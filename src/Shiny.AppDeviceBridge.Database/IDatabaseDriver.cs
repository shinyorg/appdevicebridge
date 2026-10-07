using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// The database a request names, resolved by the bridge before a driver sees it: a SQLite file in a file root, or a
/// connection a driver serves.
/// </summary>
public abstract record DatabaseTarget
{
    /// <summary>
    /// One spelling of "which database" for both kinds, which the query history and saved queries are kept under. A file
    /// is keyed by its root and path, so moving it starts a fresh history.
    /// </summary>
    public abstract string Key { get; }
}

/// <param name="Root">The file root, as the page named it.</param>
/// <param name="Path">The normalized path inside the root.</param>
/// <param name="FullPath">The file on disk. Already checked to be inside the root; it need not exist yet.</param>
public sealed record DatabaseFileTarget(string Root, string Path, string FullPath) : DatabaseTarget
{
    public override string Key => $"file:{this.Root}/{this.Path}";
}

/// <param name="Connection">The connection's id, as <see cref="IDatabaseDriver.GetConnectionsAsync"/> gave it.</param>
/// <param name="Database">Which database on the server; null is the connection's default.</param>
public sealed record DatabaseConnectionTarget(string Connection, string? Database) : DatabaseTarget
{
    public override string Key => $"connection:{this.Connection}/{this.Database}";
}

/// <summary>
/// An engine behind the database bridge. The bridge resolves the request's database to a <see cref="DatabaseTarget"/>,
/// hands it to the first driver that <see cref="Serves"/> it, and does what is the same whichever engine it is: routing,
/// cancel, the query history, and moving CSV in and out of files.
/// <para>
/// <see cref="SqliteDatabaseDriver"/> serves every <see cref="DatabaseFileTarget"/> and is always there. A driver for a
/// database server — PostgreSQL, SQL Server — serves <see cref="DatabaseConnectionTarget"/>s, is registered with
/// <c>services.AddDatabaseDriver&lt;T&gt;()</c>, and keeps its connections and their secrets itself: the page only ever
/// names a connection by its id.
/// </para>
/// <para>
/// Nothing the database decided is thrown. A statement that will not parse, a table that is not there, a server that is
/// off all come back in the answer's <c>Error</c>. Throw <see cref="DatabaseRefusal"/> from the helpers here — row views,
/// designs — and catch it into the answer the same way.
/// </para>
/// </summary>
public interface IDatabaseDriver
{
    /// <summary>Whether this driver answers for the database. The first that does gets every request naming it.</summary>
    bool Serves(DatabaseTarget target);

    /// <summary>The connections a page can name. A driver for files has none.</summary>
    Task<IReadOnlyList<DatabaseConnection>> GetConnectionsAsync(CancellationToken cancellationToken);

    Task<DatabaseSchema> GetSchemaAsync(DatabaseTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a script. <paramref name="cancellationToken"/> fires on the page's cancel as well as when the request goes away;
    /// a run it stops answers with <see cref="DatabaseScriptResult.Cancelled"/>.
    /// </summary>
    Task<DatabaseScriptResult> QueryAsync(DatabaseTarget target, RunDatabaseQuery request, CancellationToken cancellationToken);

    Task<DatabaseTableRows> GetRowsAsync(DatabaseTarget target, GetDatabaseTableRows request, CancellationToken cancellationToken);

    Task<DatabaseRowCount> CountAsync(DatabaseTarget target, CountDatabaseTableRows request, CancellationToken cancellationToken);

    Task<DatabaseTotals> GetTotalsAsync(DatabaseTarget target, GetDatabaseTotals request, CancellationToken cancellationToken);

    Task<DatabaseQueryResult> InsertRowAsync(DatabaseTarget target, InsertDatabaseRow request, CancellationToken cancellationToken);

    Task<DatabaseQueryResult> UpdateRowAsync(DatabaseTarget target, UpdateDatabaseRow request, CancellationToken cancellationToken);

    /// <summary>Deletes every keyed record in one transaction, or none.</summary>
    Task<DatabaseQueryResult> DeleteRowsAsync(DatabaseTarget target, DeleteDatabaseRows request, CancellationToken cancellationToken);

    Task<DatabaseValue> GetValueAsync(DatabaseTarget target, GetDatabaseValue request, CancellationToken cancellationToken);

    Task<DatabaseDesignScript> PreviewDesignAsync(DatabaseTarget target, PreviewDatabaseDesign request, CancellationToken cancellationToken);

    Task<DatabaseObjectResult> ChangeObjectAsync(DatabaseTarget target, ChangeDatabaseObject request, CancellationToken cancellationToken);

    /// <summary>The engine's type for a kind of value, for a table made from a CSV: <c>INTEGER</c>, <c>bigint</c>, <c>nvarchar(255)</c>.</summary>
    Task<string> GetColumnTypeAsync(DatabaseTarget target, DatabaseValueKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the table an import was asked to create, with these columns. Null when it was made, or why it was not. A
    /// server driver may add a key column of its own, so the imported rows can be edited.
    /// </summary>
    Task<string?> CreateTableAsync(DatabaseTarget target, string table, string? schema, IReadOnlyList<DatabaseColumnDesign> columns, CancellationToken cancellationToken);

    /// <summary>Inserts every record in one transaction — or, when one is refused, none — reading each value through its column's type.</summary>
    /// <param name="records">Each record's fields, with the line of the file it started on.</param>
    Task<DatabaseImportResult> InsertManyAsync(
        DatabaseTarget target,
        string table,
        string? schema,
        IReadOnlyList<DatabaseImportColumn> mapping,
        IEnumerable<(string?[] Fields, long Line)> records,
        bool emptyIsNull,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Every row of a table view, or of one statement's answer, one at a time and uncapped — what an export writes. Answers
    /// with how many rows there were; throws <see cref="DatabaseRefusal"/> for what the database refused.
    /// </summary>
    Task<long> ReadAllAsync(
        DatabaseTarget target,
        ExportDatabaseCsv request,
        Func<string[], Task> header,
        Func<string?[], Task> row,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// A refusal worded for the person reading it — no such table, a filter on a column that is not there — rather than a
/// fault. Drivers catch it into the answer's <c>Error</c>.
/// </summary>
public sealed class DatabaseRefusal(string message) : Exception(message);
