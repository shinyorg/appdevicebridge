using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Database.Client;

/// <summary>
/// A database client on the device: SQLite files in the app's file roots, opened where they lie so a 4GB file costs a page
/// of rows rather than 4GB; and database servers a driver the app registered can reach, which a page cannot open a socket
/// to. Every request names its database by <c>root</c> and <c>path</c>, or by <c>connection</c> and <c>database</c>.
/// <para>
/// What the database decided — a syntax error, a constraint, a server that is off — is in the answer's <c>error</c>.
/// A request that names no database, or one that is not there, fails with 400 or 404.
/// </para>
/// </summary>
[BridgeClient("database", typeof(DatabaseJsonContext))]
public interface IDatabaseBridge
{
    /// <summary>The database server connections the app's drivers serve. Empty when it registered none.</summary>
    [BridgeGet("connections")]
    Task<IReadOnlyList<DatabaseConnection>> GetConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>The databases on a connection's server that its login may open, or why the server could not be asked.</summary>
    [BridgePost("databases")]
    Task<DatabaseNames> GetDatabaseNamesAsync(GetDatabaseNames request, CancellationToken cancellationToken = default);

    /// <summary>Makes a new, empty SQLite database. 409 when something is already at the path.</summary>
    [BridgePost("create")]
    Task CreateAsync(CreateDatabase request, CancellationToken cancellationToken = default);

    /// <summary>The tables and views in a database, or why it could not be opened.</summary>
    [BridgePost("schema")]
    Task<DatabaseSchema> GetSchemaAsync(GetDatabaseSchema request, CancellationToken cancellationToken = default);

    /// <summary>Runs SQL — every result set and every statement's outcome — or the error it failed with.</summary>
    [BridgePost("query")]
    Task<DatabaseScriptResult> QueryAsync(RunDatabaseQuery request, CancellationToken cancellationToken = default);

    /// <summary>Stops a query started with that run id. Harmless when it has already finished.</summary>
    [BridgePost("query/cancel")]
    Task CancelAsync(CancelDatabaseQuery request, CancellationToken cancellationToken = default);

    /// <summary>One page of one table's rows, with the key that names each.</summary>
    [BridgePost("rows")]
    Task<DatabaseTableRows> GetRowsAsync(GetDatabaseTableRows request, CancellationToken cancellationToken = default);

    /// <summary>How many rows pass the filters and search.</summary>
    [BridgePost("rows/count")]
    Task<DatabaseRowCount> CountAsync(CountDatabaseTableRows request, CancellationToken cancellationToken = default);

    /// <summary>An aggregate per column over every row that passes the filters and search.</summary>
    [BridgePost("rows/totals")]
    Task<DatabaseTotals> GetTotalsAsync(GetDatabaseTotals request, CancellationToken cancellationToken = default);

    /// <summary>Adds one record, and answers with it as it now stands.</summary>
    [BridgePost("rows/insert")]
    Task<DatabaseQueryResult> InsertRowAsync(InsertDatabaseRow request, CancellationToken cancellationToken = default);

    /// <summary>Changes cells of one record, and answers with it as it now stands.</summary>
    [BridgePost("rows/update")]
    Task<DatabaseQueryResult> UpdateRowAsync(UpdateDatabaseRow request, CancellationToken cancellationToken = default);

    /// <summary>Deletes records by key, all or none.</summary>
    [BridgePost("rows/delete")]
    Task<DatabaseQueryResult> DeleteRowsAsync(DeleteDatabaseRows request, CancellationToken cancellationToken = default);

    /// <summary>One value in full — a BLOB as base64, up to 64MB.</summary>
    [BridgePost("value")]
    Task<DatabaseValue> GetValueAsync(GetDatabaseValue request, CancellationToken cancellationToken = default);

    /// <summary>The DDL that would make a table what a design says. Nothing is run.</summary>
    [BridgePost("design/preview")]
    Task<DatabaseDesignScript> PreviewDesignAsync(PreviewDatabaseDesign request, CancellationToken cancellationToken = default);

    /// <summary>Drops or renames one object — or with <c>preview</c>, says what that would run.</summary>
    [BridgePost("object")]
    Task<DatabaseObjectResult> ChangeObjectAsync(ChangeDatabaseObject request, CancellationToken cancellationToken = default);

    /// <summary>A first look at a CSV before importing it.</summary>
    [BridgePost("import/preview")]
    Task<DatabaseImportPreview> PreviewImportAsync(PreviewDatabaseImport request, CancellationToken cancellationToken = default);

    /// <summary>Reads a CSV into a table in one transaction.</summary>
    [BridgePost("import")]
    Task<DatabaseImportResult> ImportAsync(ImportDatabaseCsv request, CancellationToken cancellationToken = default);

    /// <summary>Writes a table or a statement's answer to a CSV file in a file root.</summary>
    [BridgePost("export")]
    Task<DatabaseExportResult> ExportAsync(ExportDatabaseCsv request, CancellationToken cancellationToken = default);

    /// <summary>The queries run against a database, newest first.</summary>
    [BridgePost("history")]
    Task<IReadOnlyList<DatabaseHistoryEntry>> GetHistoryAsync(GetDatabaseHistory request, CancellationToken cancellationToken = default);

    /// <summary>Forgets a database's query history.</summary>
    [BridgePost("history/clear")]
    Task ClearHistoryAsync(ClearDatabaseHistory request, CancellationToken cancellationToken = default);

    /// <summary>The queries saved against a database, by name.</summary>
    [BridgePost("saved")]
    Task<IReadOnlyList<SavedDatabaseQuery>> GetSavedQueriesAsync(GetSavedDatabaseQueries request, CancellationToken cancellationToken = default);

    /// <summary>Saves a query under a name, or overwrites one.</summary>
    [BridgePost("saved/save")]
    Task<SavedDatabaseQueryResult> SaveQueryAsync(SaveDatabaseQuery request, CancellationToken cancellationToken = default);

    /// <summary>Forgets a saved query.</summary>
    [BridgeDelete("saved/{id}")]
    Task RemoveSavedQueryAsync(Guid id, CancellationToken cancellationToken = default);
}
