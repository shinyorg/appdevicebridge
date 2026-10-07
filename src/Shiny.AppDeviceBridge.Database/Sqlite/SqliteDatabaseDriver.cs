using System.Diagnostics;
using System.Globalization;
using Shiny.AppDeviceBridge.Database.Client;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// The SQLite driver: a schema, a paged and editable datasheet, and a query pane that can change what it reads, over a
/// file in one of the app's file roots, opened where it lies. Always registered; it serves every
/// <see cref="DatabaseFileTarget"/>.
/// </summary>
/// <remarks>
/// <para>
/// The query runs on the device instead of in the page because of size: asking a 4GB file for twenty rows should cost
/// twenty rows, not the file.
/// </para>
/// <para>
/// <b>Writes are on, deliberately.</b> It is a database client and it can UPDATE, DELETE and run DDL, with no
/// confirmation step and no setting guarding it - as the files bridge can already overwrite the same file.
/// </para>
/// <para>
/// <b>What it cannot do is leave the file.</b> That is a different promise from the one above, and the one thing here that
/// is not negotiable by a query: the bridges hand out nothing outside the file roots, and <c>ATTACH</c> would walk straight
/// past that to any file the process can open. So the connection carries an authorizer, which is SQLite's own hook and
/// runs during preparation, before a statement can do anything. String-matching the SQL for "attach" would be the other
/// way to try this, and it is the way that loses to a comment and a line break.
/// </para>
/// <para>
/// SQLite is synchronous, and the work is moved off the request's thread rather than rewritten: its interrupt-based
/// cancellation is built around a synchronous reader, and a file on the device has nothing to wait for that async would
/// give back.
/// </para>
/// </remarks>
public sealed class SqliteDatabaseDriver(DatabaseBridgeOptions options) : IDatabaseDriver
{
    /// <summary>
    /// How much of a blob to describe rather than send. A cell is a table cell; nobody reads a
    /// megabyte of jpeg in one, and shipping it would cost the row it is in.
    /// </summary>
    const int BlobPreview = 32;

    /// <summary>The most a single value may weigh to be handed to the page whole.</summary>
    const long MaxValueBytes = 64L * 1024 * 1024;

    /// <summary>The one key column a SQLite table has - see <see cref="DatabaseTable.KeyColumns"/>.</summary>
    const string RowIdColumn = "rowid";

    static SqliteSpelling Spelling => SqliteSpelling.Instance;

    /// <summary>
    /// How long any one statement gets before it is interrupted - <see cref="DatabaseBridgeOptions.StatementTimeout"/>.
    /// </summary>
    /// <remarks>
    /// This is a phone, often. A cartesian join typed by accident is not a hung request to be waited out - it is a warm
    /// device with a flat battery, and the connection holding a write lock on a file something else is also showing.
    /// </remarks>
    TimeSpan Timeout => options.StatementTimeout;

    public bool Serves(DatabaseTarget target) => target is DatabaseFileTarget;

    public Task<IReadOnlyList<DatabaseConnection>> GetConnectionsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DatabaseConnection>>([]);

    /// <summary>
    /// Makes a new, empty database - a real one, with the header a file needs before other tools will open it as a
    /// database.
    /// </summary>
    /// <exception cref="IOException">Something is already at the path.</exception>
    public void Create(DatabaseFileTarget target)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target.FullPath)!);

        // CreateNew, so a name already taken is refused rather than opened - and a database somebody already has is not
        // quietly handed back as the empty one they asked for.
        using (new FileStream(target.FullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        try
        {
            using var connection = this.Open(target);
            using var command = connection.CreateCommand();

            // Done for its side effect, not for the setting. SQLite lays down page one - the
            // header, and an empty schema - the first time anything writes to a new file, and
            // setting the user version to the zero it already is is the smallest thing that counts
            // as a write. Without it the file stays zero bytes: still a database to SQLite, and not
            // one to anything that reads the first sixteen bytes before deciding.
            command.CommandText = "PRAGMA user_version = 0";
            command.ExecuteNonQuery();
        }
        catch
        {
            // The empty file is already where the page asked for it. Leaving it there would be a
            // database that is not one, failing to open every time it is tried - so the half-made
            // thing goes rather than the error being reported over the top of it.
            try
            {
                File.Delete(target.FullPath);
            }
            catch (IOException)
            {
                // and if even that will not go, the original failure is still the one to report
            }

            throw;
        }
    }

    // ---- the driver ----
    //
    // Off the request's thread: the engine blocks while a statement runs, and a thirty-second statement should not hold a
    // server thread doing nothing but wait. The request's token still reaches the statement, through the interrupt.

    public Task<DatabaseSchema> GetSchemaAsync(DatabaseTarget target, CancellationToken cancellationToken)
        => Run(() => this.GetSchema(FileOf(target)));

    public Task<DatabaseScriptResult> QueryAsync(DatabaseTarget target, RunDatabaseQuery request, CancellationToken cancellationToken)
        => Run(() => this.Query(FileOf(target), request, cancellationToken));

    public Task<DatabaseTableRows> GetRowsAsync(DatabaseTarget target, GetDatabaseTableRows request, CancellationToken cancellationToken)
        => Run(() => this.GetRows(FileOf(target), request, cancellationToken));

    public Task<DatabaseRowCount> CountAsync(DatabaseTarget target, CountDatabaseTableRows request, CancellationToken cancellationToken)
        => Run(() => this.Count(FileOf(target), request, cancellationToken));

    public Task<DatabaseTotals> GetTotalsAsync(DatabaseTarget target, GetDatabaseTotals request, CancellationToken cancellationToken)
        => Run(() => this.Totals(FileOf(target), request, cancellationToken));

    public Task<DatabaseQueryResult> InsertRowAsync(DatabaseTarget target, InsertDatabaseRow request, CancellationToken cancellationToken)
        => Run(() => this.InsertRow(FileOf(target), request, cancellationToken));

    public Task<DatabaseQueryResult> UpdateRowAsync(DatabaseTarget target, UpdateDatabaseRow request, CancellationToken cancellationToken)
        => Run(() => this.UpdateRow(FileOf(target), request, cancellationToken));

    public Task<DatabaseQueryResult> DeleteRowsAsync(DatabaseTarget target, DeleteDatabaseRows request, CancellationToken cancellationToken)
        => Run(() => this.DeleteRows(FileOf(target), request, cancellationToken));

    public Task<DatabaseValue> GetValueAsync(DatabaseTarget target, GetDatabaseValue request, CancellationToken cancellationToken)
        => Run(() => this.GetValue(FileOf(target), request, cancellationToken));

    public Task<DatabaseDesignScript> PreviewDesignAsync(DatabaseTarget target, PreviewDatabaseDesign request, CancellationToken cancellationToken)
        => Run(() => this.PreviewDesign(FileOf(target), request));

    public Task<DatabaseObjectResult> ChangeObjectAsync(DatabaseTarget target, ChangeDatabaseObject request, CancellationToken cancellationToken)
        => Run(() => this.ChangeObject(FileOf(target), request));

    public Task<string> GetColumnTypeAsync(DatabaseTarget target, DatabaseValueKind kind, CancellationToken cancellationToken)
        => Task.FromResult(kind switch
        {
            DatabaseValueKind.Integer => "INTEGER",
            DatabaseValueKind.Decimal => "REAL",
            DatabaseValueKind.Boolean => "BOOLEAN",
            DatabaseValueKind.Date => "DATE",
            DatabaseValueKind.DateTime => "DATETIME",
            _ => "TEXT"
        });

    /// <summary>
    /// The table through the designer - the same DDL a table designed by hand gets. No key column is added: SQLite names
    /// its rows by rowid, so an imported table is editable as it is.
    /// </summary>
    public Task<string?> CreateTableAsync(DatabaseTarget target, string table, string? schema, IReadOnlyList<DatabaseColumnDesign> columns, CancellationToken cancellationToken)
    {
        var script = TableDesigner.Sqlite(new DatabaseTableDesign(table, null, [.. columns], [], [], []), null, []);
        return script.Error is { } refused ? Task.FromResult<string?>(refused) : Run(() => this.Apply(FileOf(target), script.Sql));
    }

    public Task<DatabaseImportResult> InsertManyAsync(
        DatabaseTarget target,
        string table,
        string? schema,
        IReadOnlyList<DatabaseImportColumn> mapping,
        IEnumerable<(string?[] Fields, long Line)> records,
        bool emptyIsNull,
        CancellationToken cancellationToken
    ) => Run(() => this.InsertMany(FileOf(target), table, mapping, records, emptyIsNull, cancellationToken));

    public Task<long> ReadAllAsync(
        DatabaseTarget target,
        ExportDatabaseCsv request,
        Func<string[], Task> header,
        Func<string?[], Task> row,
        CancellationToken cancellationToken
    ) => Task.Run(() => this.ReadAll(FileOf(target), request, header, row, cancellationToken), CancellationToken.None);

    static Task<T> Run<T>(Func<T> work) => Task.Run(work, CancellationToken.None);

    static DatabaseFileTarget FileOf(DatabaseTarget target)
        => target as DatabaseFileTarget ?? throw new ArgumentException("The SQLite driver opens files, not connections.", nameof(target));

    // ---- the schema ----

    /// <summary>
    /// The tables and views in the file.
    /// </summary>
    /// <remarks>
    /// A file that will not open - not there, or not a database - comes back as a schema carrying
    /// <see cref="DatabaseSchema.Error"/> rather than as a throw. It is the most ordinary outcome of
    /// double-clicking a file with .db on the end, and thrown it would be a 500 and a crash report for
    /// a thumbnail cache.
    /// </remarks>
    DatabaseSchema GetSchema(DatabaseFileTarget file)
    {
        try
        {
            return this.ReadSchema(file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or SqliteException)
        {
            return new DatabaseSchema([], null, DatabaseEngineKind.Sqlite, "", ex.Message);
        }
    }

    DatabaseSchema ReadSchema(DatabaseFileTarget file)
    {
        using var connection = this.Open(file);

        var names = new List<(string Name, string Type)>();

        // sqlite_master rather than the pragma_table_list of newer SQLite: this has to answer for
        // whatever version wrote the file, and the two internal prefixes are the whole difference.
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT name, type
                FROM sqlite_master
                WHERE type IN ('table', 'view')
                  AND name NOT LIKE 'sqlite\_%' ESCAPE '\'
                ORDER BY type, name COLLATE NOCASE
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
                names.Add((reader.GetString(0), reader.GetString(1)));
        }

        var tables = names.Select(x => ReadTable(connection, x.Name, x.Type)).ToArray();
        var size = new FileInfo(file.FullPath).Length;


        return new DatabaseSchema(tables, size, DatabaseEngineKind.Sqlite, connection.ServerVersion);
    }

    /// <summary>One table or view, with everything the navigator, the designer and the diagram read.</summary>
    static DatabaseTable ReadTable(SqliteConnection connection, string name, string kind)
    {
        var sql = kind == "table" ? DefinitionOf(connection, name) : null;

        return new DatabaseTable(
            name,
            kind,
            ReadColumns(connection, name, sql),

            // A view has none and cannot be given any, and PRAGMA index_list answers for one
            // with an empty list rather than an error - so this is skipped for the answer it
            // would give rather than for the error it would raise.
            kind == "table" ? ReadIndexes(connection, name) : [],

            // SQLite's answer to "which record" is the rowid, so it is the one key column -
            // see DatabaseTable for why the table's own primary key will not do
            HasRowId(connection, name) ? [RowIdColumn] : [],
            null,
            kind == "table" ? ReadForeignKeys(connection, name) : [],
            SqliteScript.Checks(sql).Select(x => new DatabaseCheck(x.Name, x.Expression)).ToArray(),
            kind == "table" ? ReadTriggers(connection, name) : []
        );
    }

    static string? DefinitionOf(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = @name AND type IN ('table', 'view')";
        command.Parameters.AddWithValue("@name", name);
        return command.ExecuteScalar() as string;
    }

    // ---- scripts ----

    DatabaseScriptResult Query(DatabaseFileTarget file, RunDatabaseQuery request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var connection = this.Open(file);
            return this.Execute(connection, request, watch, cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or FileNotFoundException)
        {
            return new DatabaseScriptResult([], [new DatabaseMessage(DatabaseMessageKind.Error, ex.Message)], -1, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    DatabaseScriptResult Execute(SqliteConnection connection, RunDatabaseQuery request, Stopwatch watch, CancellationToken cancellationToken)
    {
        var statements = SqliteScript.Split(request.Sql);

        if (statements.Count == 0)
            return new DatabaseScriptResult([], [], -1, watch.ElapsedMilliseconds, "There is nothing to run.");

        var cap = Math.Clamp(request.MaxRows, 1, 100_000);
        var results = new List<DatabaseResultSet>();
        var messages = new List<DatabaseMessage>();
        var plan = new List<DatabasePlanNode>();
        var affected = 0;
        var anyWrite = false;
        string? error = null;
        int? errorLine = null;
        var stopped = false;
        var timedOut = false;

        var schemaBefore = SchemaVersion(connection);

        // Two independent registrations, not one linked source, and the difference matters.
        // Interrupt only stops a statement that is running at the time - called before one starts,
        // it is discarded. Link the deadline to the caller's token and an already-cancelled caller
        // fires the one registration immediately, on nothing, and the deadline that would have
        // caught it never arms: a runaway statement then runs to completion, which for a recursive
        // CTE means forever. Kept apart, the deadline always arms and always fires while the
        // statement is running.
        var handle = connection.Handle;
        void Interrupt() => raw.sqlite3_interrupt(handle);

        using var deadline = new CancellationTokenSource(Timeout);
        using var fromCaller = cancellationToken.Register(Interrupt);
        using var fromDeadline = deadline.Token.Register(Interrupt);

        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];

            // Before anything is prepared, because the interrupt cannot help here. A run that was
            // stopped between two statements must not start the next.
            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            var statementWatch = Stopwatch.StartNew();

            try
            {
                using var command = connection.CreateCommand();

                // EXPLAIN QUERY PLAN prepares the statement and reports the plan it would run without
                // running it, which is what makes Explain safe to press on a DELETE - and, one
                // statement at a time, on every statement of a script rather than the first.
                command.CommandText = request.Explain ? $"EXPLAIN QUERY PLAN {statement.Text}" : statement.Text;

                var changesBefore = raw.sqlite3_total_changes(handle);

                using var reader = Interruptible(() => command.ExecuteReader());

                var answered = reader.FieldCount > 0;

                if (answered)
                {
                    var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                    var declared = Enumerable.Range(0, reader.FieldCount).Select(i => SafeDeclared(reader, i)).ToArray();
                    var rows = new List<string?[]>();
                    var kinds = declared.Select(x => x.Length > 0 ? DatabaseValueKinds.Sqlite(x) : DatabaseValueKind.Other).ToArray();
                    var truncated = false;

                    while (Interruptible(reader.Read))
                    {
                        if (rows.Count == cap)
                        {
                            // asked for, not read: there is one more row than the cap, which is what
                            // the client needs to say "first 1,000 of more" rather than "1,000"
                            truncated = true;
                            break;
                        }

                        // an expression column has no declared type; the first value it holds says
                        for (var i = 0; i < kinds.Length; i++)
                        {
                            if (kinds[i] == DatabaseValueKind.Other && !reader.IsDBNull(i))
                                kinds[i] = DatabaseValueKinds.FromClr(reader.GetFieldType(i));
                        }

                        rows.Add(ReadRow(reader));
                    }

                    var set = new DatabaseResultSet(columns, kinds, rows.ToArray(), truncated, index, declared);
                    results.Add(set);

                    if (request.Explain && QueryPlans.FromSqlite(set.Rows) is { } explained)
                        plan.Add(new DatabasePlanNode(Abbreviate(statement.Text), null, explained));

                    if (truncated)
                        messages.Add(new DatabaseMessage(DatabaseMessageKind.Info, $"Statement {index + 1}: showing the first {cap:N0} rows.", statement.Line));
                }

                reader.Close();

                if (!answered && !request.Explain)
                {
                    // Not RecordsAffected alone, which is sqlite3_changes() for any statement that is
                    // not read-only - and sqlite3_changes() is the count of the last INSERT, UPDATE
                    // or DELETE on the connection, which DDL does not reset. So a CREATE TRIGGER after
                    // an UPDATE of 100,000 rows reported 100,000 rows affected of its own (measured).
                    // The connection's running total says whether this statement changed any rows at
                    // all; when it did, sqlite3_changes() is this statement's own count (a trigger's
                    // rows are in the total, not in that). When it did not, a DML statement changed
                    // none - a true and useful answer - and anything else was not a write.
                    var changed = raw.sqlite3_total_changes(handle) != changesBefore;
                    var count = changed ? raw.sqlite3_changes(handle) : IsWrite(statement.Text) ? 0 : -1;

                    if (count >= 0)
                    {
                        anyWrite = true;
                        affected += count;
                        messages.Add(new DatabaseMessage(DatabaseMessageKind.Rows, $"{Plural(count, "row")} affected", statement.Line, null, statementWatch.ElapsedMilliseconds));
                    }
                    else
                    {
                        messages.Add(new DatabaseMessage(DatabaseMessageKind.Info, $"Done: {Abbreviate(statement.Text)}", statement.Line, null, statementWatch.ElapsedMilliseconds));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // the caller's Stop, or the deadline - told apart by which fired
                stopped = cancellationToken.IsCancellationRequested;
                timedOut = !stopped;
                break;
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                // Everything before this statement has run - SQLite commits each statement outside a
                // transaction as it goes - and nothing after it will. That is what a multi-statement
                // command did too; the difference is that the error now says which statement.
                error = ex.Message;
                errorLine = statement.Line;
                messages.Add(new DatabaseMessage(DatabaseMessageKind.Error, $"Statement {index + 1}: {ex.Message}", statement.Line));
                break;
            }
        }

        var schemaChanged = SchemaVersion(connection) != schemaBefore;

        if (stopped || timedOut)
        {
            var said = timedOut
                ? $"The statement was still running after {Timeout.TotalSeconds:0} seconds and was stopped."
                : "The query was stopped.";

            messages.Add(new DatabaseMessage(DatabaseMessageKind.Info, said));
            return new DatabaseScriptResult(results.ToArray(), messages.ToArray(), anyWrite ? affected : -1, watch.ElapsedMilliseconds, said, null, null, null, true, schemaChanged);
        }

        return new DatabaseScriptResult(
            results.ToArray(),
            messages.ToArray(),
            anyWrite ? affected : -1,
            watch.ElapsedMilliseconds,
            error,
            errorLine,
            null,
            request.Explain ? plan.ToArray() : null,
            false,
            schemaChanged
        );
    }

    static long SchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA schema_version";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static string SafeDeclared(SqliteDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDataTypeName(ordinal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
        {
            return "";
        }
    }

    /// <summary>
    /// Whether a statement is one that changes rows - the four verbs, or a WITH that answered with no
    /// columns, which can only have been one of them.
    /// </summary>
    static bool IsWrite(string sql)
    {
        var first = sql.TrimStart().Split([' ', '\t', '\r', '\n', '('], 2)[0].ToUpperInvariant();
        return first is "INSERT" or "UPDATE" or "DELETE" or "REPLACE" or "WITH";
    }

    static string Abbreviate(string sql)
    {
        var line = sql.ReplaceLineEndings(" ").Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    /// <summary>
    /// Runs one step of a reader, turning an interrupt back into the cancellation it was.
    /// </summary>
    /// <remarks>
    /// <c>SqliteCommand.Cancel</c> is <c>sqlite3_interrupt</c>, and what comes back out of the
    /// statement is a plain SQLite error 9 - so without this a timeout would be reported to the
    /// reader as <c>SQLite Error 9: 'interrupted'</c>, which describes the mechanism and not one
    /// word of what happened or why.
    /// </remarks>
    static T Interruptible<T>(Func<T> step)
    {
        try
        {
            return step();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == raw.SQLITE_INTERRUPT)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Interrupts whatever the connection is running if the request goes away or the deadline passes -
    /// the same fence a script has, for the datasheet's reads.
    /// </summary>
    IDisposable Guard(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var handle = connection.Handle;
        var deadline = new CancellationTokenSource(Timeout);
        var a = cancellationToken.Register(() => raw.sqlite3_interrupt(handle));
        var b = deadline.Token.Register(() => raw.sqlite3_interrupt(handle));

        return new Disposer(() =>
        {
            a.Dispose();
            b.Dispose();
            deadline.Dispose();
        });
    }

    sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <param name="from">
    /// The first column to read. Non-zero for the editable grid, whose select list carries the rowid
    /// in front of the table's own columns.
    /// </param>
    static string?[] ReadRow(SqliteDataReader reader, int from = 0)
    {
        var values = new string?[reader.FieldCount - from];

        for (var i = 0; i < values.Length; i++)
        {
            var ordinal = i + from;

            if (reader.IsDBNull(ordinal))
                continue;

            values[i] = reader.GetFieldType(ordinal) == typeof(byte[])
                ? DescribeBlob(reader, ordinal)
                : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        return values;
    }

    /// <summary>
    /// A blob as a length and a few bytes of hex, because a cell cannot show one and a row should
    /// not cost what one weighs. Enough of it is shown to recognise a PNG header or a UUID.
    /// </summary>
    static string DescribeBlob(SqliteDataReader reader, int ordinal)
    {
        var length = reader.GetBytes(ordinal, 0, null, 0, 0);
        var take = (int)Math.Min(length, BlobPreview);
        var buffer = new byte[take];
        reader.GetBytes(ordinal, 0, buffer, 0, take);

        var hex = Convert.ToHexString(buffer);
        var ellipsis = length > take ? "…" : "";

        return $"BLOB[{length}] {hex}{ellipsis}";
    }

    // ---- one table, a page at a time ----

    DatabaseTableRows GetRows(DatabaseFileTarget file, GetDatabaseTableRows request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var connection = this.Open(file);
            using var guard = this.Guard(connection, cancellationToken);

            // A view is selectable but not editable, and this route is what feeds the grid in both
            // cases - so it takes either, and it is the absence of rowids in the answer that tells
            // the client which it got.
            var name = RequireSelectable(connection, request.Table);
            var table = ReadTable(connection, name, KindOf(connection, name));
            var addressable = table.KeyColumns.Length > 0;
            var view = RowView.Build(Spelling, table.Columns, request.Filters, request.Search, request.Sort, addressable ? [RowIdColumn] : []);
            var take = Math.Clamp(request.MaxRows, 1, 5_000);

            using var command = connection.CreateCommand();

            // rowid first and by itself, rather than folded into the * that follows it. A table is
            // allowed a column of its own called "rowid", and then the select list has two columns
            // by that name and the ordinal is the only thing that still tells them apart - which is
            // exactly why this reads column zero by position and never by name.
            command.CommandText = Spelling.Page(addressable ? "rowid, *" : "*", Quote(name), view.Where, view.OrderBy, "@skip", "@take");

            foreach (var (parameter, value) in view.Parameters)
                command.Parameters.AddWithValue(parameter, (object?)value ?? DBNull.Value);

            command.Parameters.AddWithValue("@skip", Math.Max(0, request.Offset));
            command.Parameters.AddWithValue("@take", take + 1);

            using var reader = Interruptible(() => command.ExecuteReader());

            var offset = addressable ? 1 : 0;
            var columns = Enumerable.Range(offset, reader.FieldCount - offset).Select(reader.GetName).ToArray();
            var keys = new List<string?[]>();
            var rows = new List<string?[]>();
            var truncated = false;

            while (Interruptible(reader.Read))
            {
                if (rows.Count == take)
                {
                    truncated = true;
                    break;
                }

                // as text, because that is the one shape a key has on every engine - a server's is a
                // primary key of any type, and the client carries it back without reading it
                if (addressable)
                    keys.Add([reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)]);

                rows.Add(ReadRow(reader, offset));
            }

            reader.Close();

            return new DatabaseTableRows(columns, keys.ToArray(), rows.ToArray(), truncated, watch.ElapsedMilliseconds, null, request.Offset);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or DatabaseRefusal or FileNotFoundException)
        {
            return new DatabaseTableRows([], [], [], false, watch.ElapsedMilliseconds, Describe(ex), request.Offset);
        }
    }

    DatabaseRowCount Count(DatabaseFileTarget file, CountDatabaseTableRows request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var connection = this.Open(file);
            using var guard = this.Guard(connection, cancellationToken);

            var name = RequireSelectable(connection, request.Table);
            var table = ReadTable(connection, name, KindOf(connection, name));
            var view = RowView.Build(Spelling, table.Columns, request.Filters, request.Search, null, []);

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {Quote(name)}" + (view.Where.Length > 0 ? $" WHERE {view.Where}" : "");

            foreach (var (parameter, value) in view.Parameters)
                command.Parameters.AddWithValue(parameter, (object?)value ?? DBNull.Value);

            var count = Convert.ToInt64(Interruptible(() => command.ExecuteScalar()), CultureInfo.InvariantCulture);

            return new DatabaseRowCount(count, watch.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or DatabaseRefusal or FileNotFoundException)
        {
            return new DatabaseRowCount(null, watch.ElapsedMilliseconds, Describe(ex));
        }
    }

    DatabaseTotals Totals(DatabaseFileTarget file, GetDatabaseTotals request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        if (request.Totals.Length == 0)
            return new DatabaseTotals([], 0, null);

        try
        {
            using var connection = this.Open(file);
            using var guard = this.Guard(connection, cancellationToken);

            var name = RequireSelectable(connection, request.Table);
            var table = ReadTable(connection, name, KindOf(connection, name));
            var view = RowView.Build(Spelling, table.Columns, request.Filters, request.Search, null, []);

            var aggregates = request.Totals.Select(t =>
            {
                var column = ColumnOf(table, t.Column);
                return Spelling.Aggregate(t.Aggregate, Quote(column.Name), column.Kind);
            });

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {String.Join(", ", aggregates)} FROM {Quote(name)}" + (view.Where.Length > 0 ? $" WHERE {view.Where}" : "");

            foreach (var (parameter, value) in view.Parameters)
                command.Parameters.AddWithValue(parameter, (object?)value ?? DBNull.Value);

            using var reader = Interruptible(() => command.ExecuteReader());

            var values = Interruptible(reader.Read) ? ReadRow(reader) : new string?[request.Totals.Length];

            return new DatabaseTotals(values, watch.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or DatabaseRefusal or FileNotFoundException)
        {
            return new DatabaseTotals([], watch.ElapsedMilliseconds, Describe(ex));
        }
    }

    // ---- writing records ----

    DatabaseQueryResult InsertRow(DatabaseFileTarget file, InsertDatabaseRow request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var connection = this.Open(file);

            // The same bargain UpdateRow makes: the table and every column name are taken from the
            // database's own schema rather than from the request, because an identifier cannot be a
            // parameter and the only safe identifier is one the caller never chose.
            var table = RequireTable(connection, request.Table);
            var columns = ReadColumns(connection, table, null);

            using var command = connection.CreateCommand();

            var names = new List<string>();
            var parameters = new List<string>();

            for (var i = 0; i < request.Values.Length; i++)
            {
                var value = request.Values[i];
                var column = ColumnOf(columns, table, value.Column);

                var parameter = $"@v{i}";
                names.Add(Quote(column.Name));
                parameters.Add(parameter);

                command.Parameters.AddWithValue(parameter, (object?)value.Value ?? DBNull.Value);
            }

            // DEFAULT VALUES rather than an empty column list, which is not valid SQL. It is the
            // honest statement for "one more record, all of it whatever the table says" - and it is
            // what a table of nothing but defaults and an autoincrementing key wants.
            command.CommandText = names.Count == 0
                ? $"INSERT INTO {Quote(table)} DEFAULT VALUES"
                : $"INSERT INTO {Quote(table)} ({String.Join(", ", names)}) VALUES ({String.Join(", ", parameters)})";

            var affected = command.ExecuteNonQuery();


            // Where the record landed, asked of the connection rather than worked out: the value is
            // the rowid of the last insert on this connection, and this connection has done exactly
            // one thing. It is also the only way to find a record whose key the table chose.
            var rowId = raw.sqlite3_last_insert_rowid(connection.Handle);

            // A WITHOUT ROWID table has nothing to read back by - the insert worked, and there is no
            // id that names what it wrote. The count is the whole answer, and the grid re-reads.
            return HasRowId(connection, table)
                ? ReadBack(connection, table, rowId, affected, watch)
                : new DatabaseQueryResult([], [], affected, false, watch.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or DatabaseRefusal or FileNotFoundException)
        {
            return Failed(watch, Describe(ex));
        }
    }

    DatabaseQueryResult UpdateRow(DatabaseFileTarget file, UpdateDatabaseRow request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        if (request.Changes.Length == 0)
            return Failed(watch, "Nothing was changed.");

        // The key is the rowid as the rows route wrote it. Anything else is not a key this table
        // hands out, and guessing at one is how an UPDATE lands on the wrong record.
        if (RowIdOf(request.Key) is not { } rowId)
            return Failed(watch, "That record's key is not one this table uses. Refresh the table.");

        try
        {
            using var connection = this.Open(file);

            // Both names come back from the database's own schema rather than from the request, so
            // what is interpolated below is what SQLite says exists - not what a caller sent. That
            // is the whole reason this route exists instead of the client composing an UPDATE: an
            // identifier cannot be a parameter, so the only safe identifier is one that was never
            // chosen by the caller in the first place.
            var table = RequireTable(connection, request.Table);
            var columns = ReadColumns(connection, table, null);

            var assignments = new List<string>();

            using var command = connection.CreateCommand();

            for (var i = 0; i < request.Changes.Length; i++)
            {
                var change = request.Changes[i];
                var column = ColumnOf(columns, table, change.Column);

                var parameter = $"@v{i}";
                assignments.Add($"{Quote(column.Name)} = {parameter}");

                // The text goes in as text and SQLite applies the column's affinity to it, so "42"
                // typed into an INTEGER column is stored as the number 42 - which is why the row is
                // read back below rather than assumed.
                command.Parameters.AddWithValue(parameter, (object?)change.Value ?? DBNull.Value);
            }

            command.CommandText =
                $"UPDATE {Quote(table)} SET {String.Join(", ", assignments)} WHERE rowid = @rowid";
            command.Parameters.AddWithValue("@rowid", rowId);

            var affected = command.ExecuteNonQuery();


            if (affected == 0)
            {
                // The row was deleted, or never existed. Saying so beats a silent success on a grid
                // that would then be showing a value nothing in the file agrees with.
                return Failed(watch, "That record is no longer there. Refresh the table.");
            }

            // A column declared INTEGER PRIMARY KEY is the rowid under another name, so writing to it
            // moves the record: read it back by the new id when that is what was edited.
            var alias = columns.FirstOrDefault(x => x.PrimaryKey && columns.Count(c => c.PrimaryKey) == 1
                && String.Equals(x.Type, "INTEGER", StringComparison.OrdinalIgnoreCase));

            if (alias is not null && request.Changes.LastOrDefault(x => x.Column == alias.Name) is { Value: { } moved }
                && Int64.TryParse(moved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var movedTo))
                rowId = movedTo;

            return ReadBack(connection, table, rowId, affected, watch);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or DatabaseRefusal or FileNotFoundException)
        {
            return Failed(watch, Describe(ex));
        }
    }

    DatabaseQueryResult DeleteRows(DatabaseFileTarget file, DeleteDatabaseRows request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        if (request.Keys.Length == 0)
            return Failed(watch, "Nothing was picked to delete.");

        var rowIds = request.Keys.Select(RowIdOf).ToArray();

        if (rowIds.Any(x => x is null))
            return Failed(watch, "One of those records' keys is not one this table uses. Refresh the table.");

        try
        {
            using var connection = this.Open(file);

            var table = RequireTable(connection, request.Table);

            if (!HasRowId(connection, table))
                return Failed(watch, $"'{table}' has no rowid, so nothing names one of its rows - they cannot be deleted here.");

            // one transaction: see DeleteDatabaseRows for why a half-done delete is the worst outcome
            using var transaction = connection.BeginTransaction();
            var affected = 0;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {Quote(table)} WHERE rowid = @rowid";
                var parameter = command.Parameters.Add("@rowid", SqliteType.Integer);

                foreach (var rowId in rowIds)
                {
                    parameter.Value = rowId!.Value;
                    affected += command.ExecuteNonQuery();
                }
            }

            transaction.Commit();

            return new DatabaseQueryResult([], [], affected, false, watch.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or DatabaseRefusal or FileNotFoundException)
        {
            return Failed(watch, Describe(ex));
        }
    }

    DatabaseValue GetValue(DatabaseFileTarget file, GetDatabaseValue request, CancellationToken cancellationToken)
    {
        if (RowIdOf(request.Key) is not { } rowId)
            return new DatabaseValue(null, null, 0, false, "That record's key is not one this table uses. Refresh the table.");

        try
        {
            using var connection = this.Open(file);

            var table = RequireTable(connection, request.Table);
            var column = ColumnOf(ReadColumns(connection, table, null), table, request.Column);

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {Quote(column.Name)} FROM {Quote(table)} WHERE rowid = @rowid";
            command.Parameters.AddWithValue("@rowid", rowId);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
                return new DatabaseValue(null, null, 0, false, "That record is no longer there. Refresh the table.");

            if (reader.IsDBNull(0))
                return new DatabaseValue(null, null, 0, true, null);

            if (reader.GetFieldType(0) != typeof(byte[]))
            {
                var text = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                return new DatabaseValue(null, text, text.Length, false, null);
            }

            var length = reader.GetBytes(0, 0, null, 0, 0);

            if (length > MaxValueBytes)
                return new DatabaseValue(null, null, length, false, $"That value is {length:N0} bytes - more than the bridge hands to a page in one piece.");

            var bytes = new byte[length];
            reader.GetBytes(0, 0, bytes, 0, (int)length);

            return new DatabaseValue(Convert.ToBase64String(bytes), null, length, false, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or DatabaseRefusal or FileNotFoundException)
        {
            return new DatabaseValue(null, null, 0, false, Describe(ex));
        }
    }

    static long? RowIdOf(string?[] key)
        => key is [{ } text] && Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowId) ? rowId : null;

    /// <summary>
    /// The record as it stands after a write - see <see cref="UpdateDatabaseRow"/> for why it is
    /// read rather than assumed - with the rowid it now has.
    /// </summary>
    static DatabaseQueryResult ReadBack(
        SqliteConnection connection,
        string table,
        long rowId,
        int affected,
        Stopwatch watch
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid, * FROM {Quote(table)} WHERE rowid = @rowid";
        command.Parameters.AddWithValue("@rowid", rowId);

        using var reader = command.ExecuteReader();

        var columns = Enumerable.Range(1, reader.FieldCount - 1).Select(reader.GetName).ToArray();
        var found = reader.Read();
        var rows = found ? new[] { ReadRow(reader, 1) } : [];
        string?[]? key = found ? [reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)] : null;

        reader.Close();

        return new DatabaseQueryResult(columns, rows, affected, false, watch.ElapsedMilliseconds, null, key);
    }

    // ---- designing and changing objects ----

    DatabaseDesignScript PreviewDesign(DatabaseFileTarget file, PreviewDatabaseDesign request)
    {
        try
        {
            using var connection = this.Open(file);

            DatabaseTable? current = null;
            var dependents = new List<TableDesigner.SqliteDependent>();

            if (request.Design.OriginalName is { } original)
            {
                var name = RequireTable(connection, original);
                current = ReadTable(connection, name, "table");

                using var command = connection.CreateCommand();

                // Everything the rebuild's DROP takes with it, in the words that made it. An index a
                // constraint made has no statement and comes back with the definition instead.
                command.CommandText =
                    "SELECT type, name, sql FROM sqlite_master WHERE type IN ('index', 'trigger') AND tbl_name = @name AND sql IS NOT NULL ORDER BY type, name";
                command.Parameters.AddWithValue("@name", name);

                using var reader = command.ExecuteReader();

                while (reader.Read())
                    dependents.Add(new TableDesigner.SqliteDependent(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            return TableDesigner.Sqlite(request.Design, current, dependents);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or DatabaseRefusal or FileNotFoundException)
        {
            return new DatabaseDesignScript("", [], Describe(ex));
        }
    }

    DatabaseObjectResult ChangeObject(DatabaseFileTarget file, ChangeDatabaseObject request)
    {
        try
        {
            if (request.Action == DatabaseObjectAction.Rename && String.IsNullOrWhiteSpace(request.NewName))
                throw new DatabaseRefusal("A rename needs the new name.");

            using var connection = this.Open(file);

            var name = Find(connection, request.Name, $"type = {SqliteScript.Literal(request.Kind)}")
                ?? throw new DatabaseRefusal($"There is no {request.Kind} called '{request.Name}'.");

            if (request.Kind == "index" && IsAutomaticIndex(name))
                throw new DatabaseRefusal($"'{name}' was made by a constraint, and goes when the constraint does.");

            var sql = (request.Action, request.Kind) switch
            {
                (DatabaseObjectAction.Drop, "table") => $"DROP TABLE {Quote(name)};",
                (DatabaseObjectAction.Drop, "view") => $"DROP VIEW {Quote(name)};",
                (DatabaseObjectAction.Drop, "index") => $"DROP INDEX {Quote(name)};",
                (DatabaseObjectAction.Drop, "trigger") => $"DROP TRIGGER {Quote(name)};",
                (DatabaseObjectAction.Rename, "table") => $"ALTER TABLE {Quote(name)} RENAME TO {Quote(request.NewName!.Trim())};",

                // SQLite has ALTER TABLE and nothing else: a view, an index or a trigger is renamed by
                // being made again under the new name, which is a statement somebody should write
                // knowing that is what it is
                (DatabaseObjectAction.Rename, _) => throw new DatabaseRefusal(
                    $"SQLite cannot rename a {request.Kind} - drop it and create it again under the new name in a query tab."),

                _ => throw new DatabaseRefusal($"'{request.Kind}' is not something this app can change.")
            };

            if (!request.Preview)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();

            }

            return new DatabaseObjectResult(sql, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or DatabaseRefusal or FileNotFoundException)
        {
            return new DatabaseObjectResult("", Describe(ex));
        }
    }

    static bool IsAutomaticIndex(string name) => name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal);

    // ---- CSV in and out ----

    /// <summary>Every row of a table view, or of a statement's answer, one at a time - see <see cref="IDatabaseDriver.ReadAllAsync"/>.</summary>
    async Task<long> ReadAll(
        DatabaseFileTarget file,
        ExportDatabaseCsv request,
        Func<string[], Task> header,
        Func<string?[], Task> row,
        CancellationToken cancellationToken
    )
    {
        using var connection = this.Open(file);
        using var guard = this.Guard(connection, cancellationToken);
        using var command = connection.CreateCommand();

        if (request.Table is { } requested)
        {
            var name = RequireSelectable(connection, requested);
            var table = ReadTable(connection, name, KindOf(connection, name));
            var view = RowView.Build(Spelling, table.Columns, request.Filters, request.Search, request.Sort, table.KeyColumns.Length > 0 ? [RowIdColumn] : []);

            command.CommandText = $"SELECT * FROM {Quote(name)}"
                + (view.Where.Length > 0 ? $" WHERE {view.Where}" : "")
                + (view.OrderBy.Length > 0 ? $" ORDER BY {view.OrderBy}" : "");

            foreach (var (parameter, value) in view.Parameters)
                command.Parameters.AddWithValue(parameter, (object?)value ?? DBNull.Value);
        }
        else
        {
            // the first statement with an answer is the file; anything before it is run, as a script would
            var statements = SqliteScript.Split(request.Sql ?? "");

            if (statements.Count != 1)
                throw new DatabaseRefusal("Export one statement at a time.");

            command.CommandText = statements[0].Text;
        }

        using var reader = Interruptible(() => command.ExecuteReader());

        if (reader.FieldCount == 0)
            throw new DatabaseRefusal("That statement answers with no columns, so there is nothing to export.");

        await header(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());

        var count = 0L;

        while (Interruptible(reader.Read))
        {
            var values = new string?[reader.FieldCount];

            // blobs whole, as hex - an export is the one place a value is not described
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i)
                    ? null
                    : reader.GetValue(i) is byte[] bytes
                        ? "0x" + Convert.ToHexString(bytes)
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            await row(values);
            count++;
        }

        return count;
    }

    /// <summary>Runs DDL that is plumbing rather than somebody's query - an import's CREATE TABLE.</summary>
    string? Apply(DatabaseFileTarget file, string sql)
        => this.Query(file, new RunDatabaseQuery(sql, 1, Record: false), CancellationToken.None).Error;

    DatabaseImportResult InsertMany(
        DatabaseFileTarget file,
        string tableName,
        IReadOnlyList<DatabaseImportColumn> mapping,
        IEnumerable<(string?[] Fields, long Line)> records,
        bool emptyIsNull,
        CancellationToken cancellationToken
    )
    {
        var watch = Stopwatch.StartNew();
        long? line = null;

        try
        {
            using var connection = this.Open(file);

            var table = RequireTable(connection, tableName);
            var columns = ReadColumns(connection, table, null);
            var targets = mapping.Where(x => x.Source >= 0).Select(x => (x.Source, Column: ColumnOf(columns, table, x.Target))).ToArray();

            if (targets.Length == 0)
                throw new DatabaseRefusal("No column of the file is mapped to a column of the table.");

            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"INSERT INTO {Quote(table)} ({String.Join(", ", targets.Select(x => Quote(x.Column.Name)))}) " +
                $"VALUES ({String.Join(", ", targets.Select((_, i) => $"@p{i}"))})";

            var parameters = targets.Select((_, i) => command.Parameters.Add($"@p{i}", SqliteType.Text)).ToArray();
            var inserted = 0L;

            // one prepared statement, re-bound per record: SQLite inside one transaction does tens of
            // thousands of these a second, so there is nothing to gain from batching VALUES here
            foreach (var (fields, at) in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                line = at;

                for (var i = 0; i < targets.Length; i++)
                {
                    var (source, column) = targets[i];
                    var value = DatabaseCsv.Normalize(source < fields.Length ? fields[source] : null, column.Kind, emptyIsNull);
                    parameters[i].Value = (object?)value ?? DBNull.Value;
                }

                inserted += command.ExecuteNonQuery();
            }

            transaction.Commit();

            return new DatabaseImportResult(inserted, watch.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or OperationCanceledException or DatabaseRefusal or FileNotFoundException)
        {
            return new DatabaseImportResult(0, watch.ElapsedMilliseconds,
                line is { } at ? $"Nothing was imported - line {at} was refused: {Describe(ex)}" : Describe(ex),
                line);
        }
    }

    // ---- the catalogue ----

    /// <summary>
    /// The name of a real table, as the database spells it, or an error naming what was asked for.
    /// </summary>
    /// <remarks>
    /// Looked up rather than trusted, and the answer used in place of the request's own string -
    /// which is what makes it safe to put in a statement. Views are refused as well as missing
    /// tables, because this is the check a write goes through: a view has no rowid, so there is no
    /// record for an UPDATE to name.
    /// </remarks>
    static string RequireTable(SqliteConnection connection, string requested)
        => Find(connection, requested, "type = 'table'")
            ?? throw new InvalidOperationException($"There is no table called '{requested}'.");

    /// <summary>The same, for reading, where a view is a perfectly good thing to be pointed at.</summary>
    static string RequireSelectable(SqliteConnection connection, string requested)
        => Find(connection, requested, "type IN ('table', 'view')")
            ?? throw new InvalidOperationException($"There is nothing called '{requested}' in this database.");

    static string? Find(SqliteConnection connection, string requested, string kinds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM sqlite_master WHERE {kinds} AND name = @name";
        command.Parameters.AddWithValue("@name", requested);

        return command.ExecuteScalar() as string;
    }

    static string KindOf(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name = @name AND type IN ('table', 'view')";
        command.Parameters.AddWithValue("@name", name);

        return command.ExecuteScalar() as string ?? "table";
    }

    /// <summary>
    /// Whether rows in this table can be named individually.
    /// </summary>
    /// <remarks>
    /// Asked by preparing a statement rather than by reading the schema: a WITHOUT ROWID table and a
    /// view both fail to compile <c>SELECT rowid</c>, and there is no single flag that covers both.
    /// <c>LIMIT 0</c> so this costs a prepare and never a scan.
    /// </remarks>
    static bool HasRowId(SqliteConnection connection, string table)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT rowid FROM {Quote(table)} LIMIT 0";
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>The message to show for something the database decided, rather than a stack trace.</summary>
    string Describe(Exception ex)
        => ex is OperationCanceledException
            ? $"The statement was still running after {Timeout.TotalSeconds:0} seconds and was stopped."
            : ex.Message;

    /// <summary>
    /// An identifier, quoted. Every name that reaches this came out of <c>sqlite_master</c> or
    /// <c>PRAGMA table_info</c> a moment earlier rather than from a caller - the doubling is for
    /// the table genuinely called <c>my"table</c>, not for a caller trying to end the statement.
    /// </summary>
    static string Quote(string identifier) => Spelling.Quote(identifier);

    static DatabaseColumn ColumnOf(DatabaseTable table, string name)
        => table.Columns.FirstOrDefault(x => String.Equals(x.Name, name, StringComparison.Ordinal))
            ?? throw new DatabaseRefusal($"'{table.Name}' has no column called '{name}'.");

    static DatabaseColumn ColumnOf(IReadOnlyList<DatabaseColumn> columns, string table, string name)
        => columns.FirstOrDefault(x => String.Equals(x.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{table}' has no column called '{name}'.");

    /// <summary>
    /// The indexes on a table, each with the columns it is over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two pragmas rather than a join on <c>sqlite_master</c>: the SQL of an index is null for the
    /// ones SQLite made itself, and <c>index_list</c> is the only thing that answers for unique,
    /// partial and automatic in one place.
    /// </para>
    /// <para>
    /// Read into a list before the second pragma runs. A pragma is a statement like any other, and
    /// asking one for a row while the reader of another is still open is a second reader on the
    /// same connection - which this provider does not allow.
    /// </para>
    /// </remarks>
    static DatabaseIndex[] ReadIndexes(SqliteConnection connection, string table)
    {
        var found = new List<(string Name, bool Unique, bool Automatic, bool Partial)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({Quote(table)})";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // seq, name, unique, origin, partial - origin is "c" for a CREATE INDEX and "u" or
                // "pk" for the index a UNIQUE or PRIMARY KEY constraint brought with it
                found.Add((
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    !String.Equals(reader.GetString(3), "c", StringComparison.Ordinal),
                    reader.GetBoolean(4)
                ));
            }
        }

        return found
            .Select(x => new DatabaseIndex(
                x.Name,
                ReadIndexColumns(connection, x.Name),
                x.Unique,
                x.Automatic,
                x.Partial
            ))
            .ToArray();
    }

    static string[] ReadIndexColumns(SqliteConnection connection, string index)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info({Quote(index)})";

        using var reader = command.ExecuteReader();
        var columns = new List<string>();

        while (reader.Read())
        {
            // seqno, cid, name - and the name is null where the index is over an expression rather
            // than a column, which is a position in the index that has to be accounted for
            columns.Add(reader.IsDBNull(2) ? "(expression)" : reader.GetString(2));
        }

        return columns.ToArray();
    }

    /// <param name="createTable">
    /// The table's own CREATE statement, when the caller has it - it is the only place
    /// <c>AUTOINCREMENT</c> is written down.
    /// </param>
    static DatabaseColumn[] ReadColumns(SqliteConnection connection, string table, string? createTable)
    {
        using var command = connection.CreateCommand();

        command.CommandText = $"PRAGMA table_info({Quote(table)})";

        using var reader = command.ExecuteReader();
        var columns = new List<DatabaseColumn>();
        var autoIncrement = SqliteScript.HasAutoIncrement(createTable);

        while (reader.Read())
        {
            // a column in a view, or in a table declared without types, has none - and "" reads
            // as a missing value in the header rather than as the answer it is
            var type = reader.IsDBNull(2) || reader.GetString(2).Length == 0 ? "any" : reader.GetString(2);
            var primaryKey = reader.GetInt32(5) > 0;

            columns.Add(new DatabaseColumn(
                reader.GetString(1),
                type,
                reader.GetBoolean(3),
                primaryKey,
                reader.IsDBNull(4) ? null : reader.GetString(4),

                // AUTOINCREMENT can only be on the rowid alias, the INTEGER PRIMARY KEY - so where the
                // statement has the word, that column is the one it is on
                autoIncrement && primaryKey && String.Equals(type, "INTEGER", StringComparison.OrdinalIgnoreCase),
                DatabaseValueKinds.Sqlite(type)
            ));
        }

        return columns.ToArray();
    }

    /// <summary>
    /// A table's foreign keys, grouped from <c>PRAGMA foreign_key_list</c>'s one row per column.
    /// </summary>
    static DatabaseForeignKey[] ReadForeignKeys(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({Quote(table)})";

        using var reader = command.ExecuteReader();

        // id, seq, table, from, to, on_update, on_delete, match
        var keys = new List<(long Id, string Table, List<string> From, List<string?> To, string OnUpdate, string OnDelete)>();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);

            if (keys.Count == 0 || keys[^1].Id != id)
                keys.Add((id, reader.GetString(2), [], [], reader.GetString(5), reader.GetString(6)));

            keys[^1].From.Add(reader.GetString(3));
            keys[^1].To.Add(reader.IsDBNull(4) ? null : reader.GetString(4));
        }

        // The pragma lists keys newest first; the order they were declared is what anybody reads.
        return keys
            .OrderBy(x => x.Id)
            .Reverse()
            .Select(x => new DatabaseForeignKey(
                null,
                x.From.ToArray(),
                null,
                x.Table,

                // a key written without a column list points at the other table's primary key, and
                // the pragma answers null for each - said as no columns rather than a list of nulls
                x.To.All(t => t is null) ? [] : x.To.Select(t => t ?? "").ToArray(),
                x.OnUpdate,
                x.OnDelete))
            .ToArray();
    }

    static string[] ReadTriggers(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = @name ORDER BY name";
        command.Parameters.AddWithValue("@name", table);

        using var reader = command.ExecuteReader();
        var names = new List<string>();

        while (reader.Read())
            names.Add(reader.GetString(0));

        return names.ToArray();
    }

    // ---- opening ----

    /// <summary>
    /// Opens the file, with the authorizer installed before anything can be prepared on it.
    /// </summary>
    SqliteConnection Open(DatabaseFileTarget file)
    {
        if (!File.Exists(file.FullPath))
            throw new FileNotFoundException($"There is no file at '{file.Path}' in '{file.Root}'.", file.Path);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file.FullPath,

            // ReadWrite and not ReadWriteCreate: a typo in a path should be an error, not a new
            // empty database appearing in the folder - creating one is its own request.
            Mode = SqliteOpenMode.ReadWrite,

            // Pooling keeps the handle - and on a write, the lock - alive after this connection is
            // disposed, which would leave the files bridge unable to move or delete a database anyone
            // had so much as looked at. One connection per request, closed when the request ends.
            Pooling = false
        }.ToString());

        try
        {
            connection.Open();
            Restrict(connection);

            // Open does not touch the file. SQLite reads page one when the first statement is
            // prepared, so a text file with a .db on it opens perfectly happily and only falls over
            // later - once inside a schema read or somebody's query, where the failure arrives as
            // SQLite's own wording in place of a sentence about the file being the wrong sort of
            // thing. This is the cheapest statement that forces the header to be read, so that the
            // one catch below is the one place that has to know about it.
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "PRAGMA schema_version";
                probe.ExecuteScalar();
            }
        }
        catch (SqliteException ex)
        {
            connection.Dispose();

            // SQLITE_NOTADB is what a .db that is really a Berkeley DB, a thumbnail cache or a
            // renamed zip comes back as, and it is the single most likely failure here - the
            // extension is a guess. Anything else is passed along as SQLite worded it.
            throw ex.SqliteErrorCode == 26
                ? new InvalidOperationException($"'{System.IO.Path.GetFileName(file.Path)}' is not a SQLite database.")
                : new InvalidOperationException(ex.Message, ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    /// <summary>
    /// Fences the connection to the one file it was opened on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite's own authorizer hook, called once per operation while a statement is being prepared -
    /// so a denied statement never runs at all, and there is no parsing on our side to be fooled.
    /// </para>
    /// <para>
    /// Two things are refused, and everything else - including every write - is allowed through:
    /// </para>
    /// <list type="bullet">
    /// <item><c>ATTACH</c>, which takes a second file into the same connection and is the whole
    /// file roots undone in one word. The bridges serve nothing outside the file roots; this
    /// is what keeps a query to the same rule. <c>VACUUM INTO</c> writes its copy through an
    /// ATTACH of its own, so it is refused by the same check.</item>
    /// <item><c>load_extension</c>, which loads a shared library and runs its code inside the app.
    /// That is not a database operation at any setting.</item>
    /// </list>
    /// <para>
    /// <c>DETACH</c> is left alone. Nothing can be attached, so there is never anything to detach,
    /// and refusing it would only produce a stranger error for the same outcome.
    /// </para>
    /// </remarks>
    static void Restrict(SqliteConnection connection)
    {
        var result = raw.sqlite3_set_authorizer(
            connection.Handle,
            (_, action, argument, _, _, _) => action switch
            {
                raw.SQLITE_ATTACH => raw.SQLITE_DENY,
                raw.SQLITE_FUNCTION when IsExtensionLoader(argument.utf8_to_string()) => raw.SQLITE_DENY,
                _ => raw.SQLITE_OK
            },
            null
        );

        if (result != raw.SQLITE_OK)
        {
            // Never seen in practice, and the one failure here that must not be shrugged off: an
            // authorizer that did not install is a connection with no fence around it, and carrying
            // on would mean serving queries under a guarantee that is no longer true. Disposing the
            // connection is Open's job - it is the one holding it.
            throw new InvalidOperationException("The database could not be opened safely.");
        }
    }

    static bool IsExtensionLoader(string? function)
        => String.Equals(function, "load_extension", StringComparison.OrdinalIgnoreCase);

    static string Plural(long count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    static DatabaseQueryResult Failed(Stopwatch watch, string message)
        => new([], [], -1, false, watch.ElapsedMilliseconds, message);
}
