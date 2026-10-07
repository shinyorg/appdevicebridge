using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// The query history and saved queries of every database, kept on the device in a SQLite file of the bridge's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The device's, not the page's.</b> A second page on the same device — a second window, the same app reinstalled over
/// an update — is the same person, and what they ran yesterday, or saved as "monthly totals", should be there for it.
/// </para>
/// <para>
/// <b>Keyed by database</b> (<see cref="DatabaseTarget.Key"/>). A statement is a statement about one schema; the history
/// of every database in one list would be a list of "no such table" for all but one of them.
/// </para>
/// <para>
/// History is capped per database (<see cref="DatabaseBridgeOptions.MaxHistory"/>), trimmed as it is written, and records
/// the same statement run twice in a row once — pressing Run five times while fixing a typo elsewhere is not five things
/// anybody wants to scroll past.
/// </para>
/// <para>
/// Outside every file root, so the page reaches it only through these routes, and never through the files bridge or a
/// query of its own.
/// </para>
/// </remarks>
sealed class DatabaseQueryStore(string path, int maxHistory, TimeProvider time, ILogger logger)
{
    readonly SemaphoreSlim gate = new(1, 1);
    bool created;

    public async Task RecordAsync(string source, string sql, DatabaseScriptResult result, CancellationToken cancellationToken)
    {
        try
        {
            await this.RunAsync(connection =>
            {
                using var transaction = connection.BeginTransaction();
                var now = time.GetUtcNow();

                using (var last = Command(connection, transaction, "SELECT id, sql FROM history WHERE source = @source ORDER BY ran_on DESC, rowid DESC LIMIT 1", ("@source", source)))
                using (var reader = last.ExecuteReader())
                {
                    if (reader.Read() && reader.GetString(1) == sql)
                    {
                        var id = reader.GetString(0);
                        reader.Close();

                        using var update = Command(
                            connection,
                            transaction,
                            "UPDATE history SET ran_on = @ranOn, elapsed_ms = @elapsed, rows_affected = @rows, result_sets = @sets, error = @error WHERE id = @id",
                            ("@ranOn", now.UtcTicks),
                            ("@elapsed", result.ElapsedMs),
                            ("@rows", result.RowsAffected),
                            ("@sets", result.Results.Length),
                            ("@error", result.Error),
                            ("@id", id)
                        );
                        update.ExecuteNonQuery();
                        transaction.Commit();
                        return;
                    }
                }

                using (var insert = Command(
                    connection,
                    transaction,
                    "INSERT INTO history (id, source, sql, ran_on, elapsed_ms, rows_affected, result_sets, error) VALUES (@id, @source, @sql, @ranOn, @elapsed, @rows, @sets, @error)",
                    ("@id", Guid.NewGuid().ToString("D")),
                    ("@source", source),
                    ("@sql", sql),
                    ("@ranOn", now.UtcTicks),
                    ("@elapsed", result.ElapsedMs),
                    ("@rows", result.RowsAffected),
                    ("@sets", result.Results.Length),
                    ("@error", result.Error)
                ))
                {
                    insert.ExecuteNonQuery();
                }

                using (var trim = Command(
                    connection,
                    transaction,
                    "DELETE FROM history WHERE source = @source AND id NOT IN (SELECT id FROM history WHERE source = @source ORDER BY ran_on DESC, rowid DESC LIMIT @keep)",
                    ("@source", source),
                    ("@keep", maxHistory)
                ))
                {
                    trim.ExecuteNonQuery();
                }

                transaction.Commit();
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The query ran; failing to write it down is not a reason to report it as failed.
            logger.LogWarning(ex, "Could not record a query in the history of {Source}", source);
        }
    }

    public Task<IReadOnlyList<DatabaseHistoryEntry>> GetHistoryAsync(string source, int take, CancellationToken cancellationToken)
        => this.RunAsync<IReadOnlyList<DatabaseHistoryEntry>>(connection =>
        {
            using var command = Command(
                connection,
                null,
                "SELECT id, sql, ran_on, elapsed_ms, rows_affected, result_sets, error FROM history WHERE source = @source ORDER BY ran_on DESC, rowid DESC LIMIT @take",
                ("@source", source),
                ("@take", Math.Clamp(take, 1, maxHistory))
            );
            using var reader = command.ExecuteReader();
            var entries = new List<DatabaseHistoryEntry>();

            while (reader.Read())
            {
                entries.Add(new DatabaseHistoryEntry(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                    reader.GetInt64(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)
                ));
            }

            return entries;
        }, cancellationToken);

    public Task ClearHistoryAsync(string source, CancellationToken cancellationToken)
        => this.RunAsync(connection =>
        {
            using var command = Command(connection, null, "DELETE FROM history WHERE source = @source", ("@source", source));
            command.ExecuteNonQuery();
        }, cancellationToken);

    public Task<IReadOnlyList<SavedDatabaseQuery>> GetSavedAsync(string source, CancellationToken cancellationToken)
        => this.RunAsync<IReadOnlyList<SavedDatabaseQuery>>(connection =>
        {
            using var command = Command(connection, null, "SELECT id, name, sql, saved_on FROM saved WHERE source = @source ORDER BY name COLLATE NOCASE", ("@source", source));
            using var reader = command.ExecuteReader();
            var saved = new List<SavedDatabaseQuery>();

            while (reader.Read())
                saved.Add(ReadSaved(reader));

            return saved;
        }, cancellationToken);

    /// <summary>Saves a query under a name, or overwrites the one with that id — or that name.</summary>
    /// <remarks>
    /// A name is unique per database, and saving under a name already taken overwrites that one rather than making a second
    /// entry nobody can tell apart.
    /// </remarks>
    public Task<SavedDatabaseQueryResult> SaveAsync(string source, Guid? id, string name, string sql, CancellationToken cancellationToken)
    {
        name = name.Trim();

        if (name.Length == 0)
            return Task.FromResult(new SavedDatabaseQueryResult(null, "A saved query needs a name."));

        if (String.IsNullOrWhiteSpace(sql))
            return Task.FromResult(new SavedDatabaseQueryResult(null, "There is nothing to save."));

        return this.RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();

            (string Id, string Source)? byId = null;

            if (id is { } existing)
            {
                using var find = Command(connection, transaction, "SELECT id, source FROM saved WHERE id = @id", ("@id", existing.ToString("D")));
                using var reader = find.ExecuteReader();

                if (reader.Read())
                    byId = (reader.GetString(0), reader.GetString(1));
            }

            if (byId is { } row && row.Source != source)
                return new SavedDatabaseQueryResult(null, "That saved query belongs to another database.");

            string? byName;

            using (var named = Command(connection, transaction, "SELECT id FROM saved WHERE source = @source AND name = @name", ("@source", source), ("@name", name)))
                byName = named.ExecuteScalar() as string;

            // a rename onto a name another query already has would leave two with one name
            if (byName is not null && byId is { } renamed && byName != renamed.Id)
                return new SavedDatabaseQueryResult(null, $"Another saved query is already called '{name}'.");

            var target = byId?.Id ?? byName ?? Guid.NewGuid().ToString("D");
            var now = time.GetUtcNow();

            using (var upsert = Command(
                connection,
                transaction,
                """
                INSERT INTO saved (id, source, name, sql, saved_on) VALUES (@id, @source, @name, @sql, @savedOn)
                ON CONFLICT (id) DO UPDATE SET name = excluded.name, sql = excluded.sql, saved_on = excluded.saved_on
                """,
                ("@id", target),
                ("@source", source),
                ("@name", name),
                ("@sql", sql),
                ("@savedOn", now.UtcTicks)
            ))
            {
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();
            return new SavedDatabaseQueryResult(new SavedDatabaseQuery(Guid.Parse(target), name, sql, now), null);
        }, cancellationToken);
    }

    public Task RemoveSavedAsync(Guid id, CancellationToken cancellationToken)
        => this.RunAsync(connection =>
        {
            using var command = Command(connection, null, "DELETE FROM saved WHERE id = @id", ("@id", id.ToString("D")));
            command.ExecuteNonQuery();
        }, cancellationToken);

    static SavedDatabaseQuery ReadSaved(SqliteDataReader reader)
        => new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero));

    Task RunAsync(Action<SqliteConnection> work, CancellationToken cancellationToken)
        => this.RunAsync<object?>(connection =>
        {
            work(connection);
            return null;
        }, cancellationToken);

    /// <summary>One at a time, off the request's thread: SQLite is synchronous and the file is the bridge's alone.</summary>
    async Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken);

        try
        {
            return await Task.Run(() =>
            {
                if (!this.created)
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString());

                connection.Open();

                if (!this.created)
                {
                    using var schema = Command(
                        connection,
                        null,
                        """
                        CREATE TABLE IF NOT EXISTS history (
                            id TEXT PRIMARY KEY,
                            source TEXT NOT NULL,
                            sql TEXT NOT NULL,
                            ran_on INTEGER NOT NULL,
                            elapsed_ms INTEGER NOT NULL,
                            rows_affected INTEGER NOT NULL,
                            result_sets INTEGER NOT NULL,
                            error TEXT
                        );
                        CREATE INDEX IF NOT EXISTS history_by_source ON history (source, ran_on);
                        CREATE TABLE IF NOT EXISTS saved (
                            id TEXT PRIMARY KEY,
                            source TEXT NOT NULL,
                            name TEXT NOT NULL,
                            sql TEXT NOT NULL,
                            saved_on INTEGER NOT NULL,
                            UNIQUE (source, name)
                        );
                        """
                    );
                    schema.ExecuteNonQuery();
                    this.created = true;
                }

                return work(connection);
            }, cancellationToken);
        }
        finally
        {
            this.gate.Release();
        }
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        return command;
    }
}
