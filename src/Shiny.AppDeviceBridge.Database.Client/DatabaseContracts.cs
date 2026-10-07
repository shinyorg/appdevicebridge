using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Database.Client;

// Every request names its database one of two ways, and exactly one of them: Root and Path, for a SQLite file in one of
// the app's file roots; or Connection (and optionally Database), for a database server a driver the app registered serves.
// Flat fields rather than a nested source, so a page writes { "root": "data", "path": "app.db", "table": "orders" }.
//
// What the database decided — a statement that will not parse, a constraint refusing a value, a server that is off — comes
// back in the answer's Error, not as a failed request: each is an ordinary answer to typing SQL into a box. A request the
// bridge cannot route at all — no database named, an unknown root, a connection nobody serves — fails with 400 or 404.

/// <summary>What is on the other end of a request. SQLite ships with the bridge; the servers come from drivers an app registers.</summary>
public enum DatabaseEngineKind
{
    Sqlite = 0,
    SqlServer,
    PostgreSql
}

/// <summary>
/// What sort of value a column holds, decided on the device from the engine's own type names, so the page can pick an
/// editor and an alignment without knowing that <c>bit</c>, <c>boolean</c> and SQLite's <c>BOOLEAN</c> are one idea. The
/// value itself still travels as the engine's own text.
/// </summary>
public enum DatabaseValueKind
{
    Text = 0,
    LongText,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    DateTimeOffset,
    Time,
    Binary,
    Guid,
    Other
}

// ---- databases ----

/// <summary>A database server connection a driver serves, by the id requests name it with.</summary>
/// <param name="Database">The database it opens on when a request names none.</param>
public sealed record DatabaseConnection(string Id, string Name, DatabaseEngineKind Engine, string? Database = null);

/// <summary>
/// Writes a new, empty SQLite database — with the header a file needs before other tools will admit it is a database — at
/// a path in a file root. Refused when something is already there.
/// </summary>
public sealed record CreateDatabase(string Root, string Path);

/// <summary>
/// The tables and views in a database, and the columns, indexes, keys, checks and triggers of each. No row counts: a count
/// is a full scan on a table without a narrow index, so it is asked separately (<see cref="CountDatabaseTableRows"/>).
/// </summary>
public sealed record GetDatabaseSchema(
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Default">The column's default as the engine wrote it down — an expression, not a value.</param>
/// <param name="AutoIncrement">Whether the engine numbers it; a column like this is left out of a new record.</param>
public sealed record DatabaseColumn(
    string Name,
    string Type,
    bool NotNull,
    bool PrimaryKey,
    string? Default = null,
    bool AutoIncrement = false,
    DatabaseValueKind Kind = DatabaseValueKind.Text
);

/// <param name="Columns">In index order. An expression index says <c>(expression)</c> for that position.</param>
/// <param name="Automatic">Made by the engine for a constraint rather than by CREATE INDEX; it cannot be dropped on its own.</param>
/// <param name="Partial">It has a WHERE clause, so it covers only some of the rows.</param>
public sealed record DatabaseIndex(string Name, string[] Columns, bool Unique, bool Automatic, bool Partial);

/// <param name="Name">Null on SQLite, whose foreign keys have no names.</param>
/// <param name="ReferencedColumns">Parallel to <see cref="Columns"/>; empty for a SQLite key that means the other table's primary key.</param>
public sealed record DatabaseForeignKey(
    string? Name,
    string[] Columns,
    string? ReferencedSchema,
    string ReferencedTable,
    string[] ReferencedColumns,
    string OnUpdate = "NO ACTION",
    string OnDelete = "NO ACTION"
);

public sealed record DatabaseCheck(string? Name, string Expression);

/// <summary>A table or a view.</summary>
/// <param name="Kind"><c>table</c> or <c>view</c>.</param>
/// <param name="KeyColumns">
/// The columns whose values name one row, and so whether a row can be edited at all: empty means it cannot. SQLite's is
/// <c>rowid</c>, which a view and a WITHOUT ROWID table do not have; a server's is the primary key.
/// </param>
/// <param name="Schema">A server's namespace — <c>dbo</c>, <c>public</c>. Null on SQLite.</param>
/// <param name="Triggers">The names of the triggers on it.</param>
public sealed record DatabaseTable(
    string Name,
    string Kind,
    DatabaseColumn[] Columns,
    DatabaseIndex[] Indexes,
    string[] KeyColumns,
    string? Schema = null,
    DatabaseForeignKey[]? ForeignKeys = null,
    DatabaseCheck[]? Checks = null,
    string[]? Triggers = null
);

/// <param name="Tables">Tables first, then views, each alphabetically. The engine's internal ones are left out.</param>
/// <param name="FileSize">The file's size, for a SQLite file; null for a server.</param>
/// <param name="ServerVersion">The engine's own version string.</param>
/// <param name="Error">Why the database could not be opened at all — a file that is not a database, a server that is off.</param>
/// <param name="DefaultSchema">Where a table made without saying lands — <c>dbo</c>, <c>public</c>. Null on SQLite.</param>
public sealed record DatabaseSchema(
    DatabaseTable[] Tables,
    long? FileSize,
    DatabaseEngineKind Engine,
    string ServerVersion,
    string? Error = null,
    string? DefaultSchema = null
);

// ---- the SQL editor ----

/// <summary>
/// Runs SQL — one statement or a script — and answers with every result set it produced and what each statement said. This
/// can write: it is a database client. What it cannot do on SQLite is leave the file it was opened on — <c>ATTACH</c> and
/// <c>load_extension</c> are refused by SQLite's authorizer before a statement can run.
/// </summary>
/// <param name="MaxRows">The cap on each result set, 1–100,000. Each result says whether it bit.</param>
/// <param name="Explain">Ask how each statement would be answered rather than answering it, as a plan tree.</param>
/// <param name="RunId">The page's name for this run, so <see cref="CancelDatabaseQuery"/> can stop it.</param>
/// <param name="Record">Whether this run goes into the database's query history.</param>
public sealed record RunDatabaseQuery(
    string Sql,
    int MaxRows = 1000,
    bool Explain = false,
    Guid? RunId = null,
    bool Record = true,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <summary>Stops a query started with that run id, if it is still running.</summary>
public sealed record CancelDatabaseQuery(Guid RunId);

/// <param name="Types">The engine's name for each column's type, where it gave one.</param>
/// <param name="Kinds">Parallel to <see cref="Columns"/>: how each is shown.</param>
/// <param name="Statement">Which statement of the script produced it, from zero.</param>
public sealed record DatabaseResultSet(
    string[] Columns,
    DatabaseValueKind[] Kinds,
    string?[][] Rows,
    bool Truncated,
    int Statement,
    string[]? Types = null
);

public enum DatabaseMessageKind
{
    Info = 0,
    Rows,
    Error,
    Notice
}

/// <summary>What a statement did, what the server printed, what failed.</summary>
/// <param name="Line">1-based, in the text that was sent, where the engine said.</param>
public sealed record DatabaseMessage(DatabaseMessageKind Kind, string Text, int? Line = null, int? Column = null, long ElapsedMs = 0);

/// <summary>A step of a query plan, as a tree.</summary>
/// <param name="Label">What the step is: <c>SCAN orders</c>, <c>Hash Join</c>.</param>
/// <param name="Detail">The numbers — estimated rows and cost — where the engine gave any.</param>
public sealed record DatabasePlanNode(string Label, string? Detail, DatabasePlanNode[] Children);

/// <summary>Everything a script produced: each result set, each statement's outcome, and how it ended.</summary>
/// <param name="RowsAffected">What every write in it changed, added up; -1 when nothing was a write.</param>
/// <param name="ErrorLine">Where the error was, counted in the text that was sent.</param>
/// <param name="Plan">For an Explain: the plan as a tree, or null where it could not be read as one.</param>
/// <param name="Cancelled">Stopped — by a cancel, or by the device's statement timeout.</param>
/// <param name="SchemaChanged">Whether the script made, dropped or altered something, so the page reads the schema again.</param>
public sealed record DatabaseScriptResult(
    DatabaseResultSet[] Results,
    DatabaseMessage[] Messages,
    int RowsAffected,
    long ElapsedMs,
    string? Error,
    int? ErrorLine = null,
    int? ErrorColumn = null,
    DatabasePlanNode[]? Plan = null,
    bool Cancelled = false,
    bool SchemaChanged = false
);

// ---- the datasheet ----

/// <summary>
/// How a filter compares a column with its value. The comparisons read the value back through the column's own type; the
/// text ones compare the column's text, case-insensitively.
/// </summary>
public enum DatabaseFilterOperator
{
    Equal = 0,
    NotEqual,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    IsNull,
    IsNotNull
}

/// <param name="Value">Ignored by <see cref="DatabaseFilterOperator.IsNull"/> and <see cref="DatabaseFilterOperator.IsNotNull"/>.</param>
public sealed record DatabaseRowFilter(string Column, DatabaseFilterOperator Operator, string? Value = null);

public sealed record DatabaseSort(string Column, bool Descending = false);

/// <summary>
/// One page of one table's rows, each with the key that names it. Paged, sorted and filtered on the device: every column
/// a filter or sort names is checked against the table, and every value is a parameter.
/// </summary>
/// <param name="MaxRows">The page size, 1–5,000.</param>
/// <param name="Offset">Where the page starts, in the order the sort gives. The key always ends the order, so pages are stable.</param>
/// <param name="Filters">Every one must hold.</param>
/// <param name="Search">Text to look for anywhere in a row, case-insensitively.</param>
public sealed record GetDatabaseTableRows(
    string Table,
    int MaxRows = 200,
    long Offset = 0,
    DatabaseSort[]? Sort = null,
    DatabaseRowFilter[]? Filters = null,
    string? Search = null,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="RowKeys">Parallel to <see cref="Rows"/>: each row's key, carried back untouched to edit or delete it. Empty when rows cannot be named.</param>
/// <param name="Truncated">True when there are rows after this page.</param>
/// <param name="Offset">The request's, said back so a late answer can be placed.</param>
public sealed record DatabaseTableRows(
    string[] Columns,
    string?[][] RowKeys,
    string?[][] Rows,
    bool Truncated,
    long ElapsedMs,
    string? Error,
    long Offset = 0
);

/// <summary>How many rows a table has under the same filters and search as a page — exact, and asked separately so the first page never waits on it.</summary>
public sealed record CountDatabaseTableRows(
    string Table,
    DatabaseRowFilter[]? Filters = null,
    string? Search = null,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Count">Null when it could not be counted.</param>
public sealed record DatabaseRowCount(long? Count, long ElapsedMs, string? Error);

public enum DatabaseAggregate
{
    Count = 0,
    Sum,
    Average,
    Min,
    Max
}

public sealed record DatabaseTotal(string Column, DatabaseAggregate Aggregate);

/// <summary>A totals row — one aggregate per column, over every row the filters let through, worked out by the engine.</summary>
public sealed record GetDatabaseTotals(
    string Table,
    DatabaseTotal[] Totals,
    DatabaseRowFilter[]? Filters = null,
    string? Search = null,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Values">Parallel to the request's totals, each the engine's own text for it.</param>
public sealed record DatabaseTotals(string?[] Values, long ElapsedMs, string? Error);

/// <param name="Value">Null is SQL NULL.</param>
public sealed record DatabaseCellEdit(string Column, string? Value);

/// <summary>
/// Adds one record and answers with it as it stands afterwards — defaults, the number the engine gave it, whatever a
/// trigger made of it. A column left out takes the table's default. The table and column names are checked against the
/// schema and every value is a parameter.
/// </summary>
public sealed record InsertDatabaseRow(
    string Table,
    DatabaseCellEdit[] Values,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <summary>Changes some cells of one record, named by its key, and answers with the record as it now stands.</summary>
/// <param name="Key">The row's key exactly as <see cref="DatabaseTableRows.RowKeys"/> gave it.</param>
public sealed record UpdateDatabaseRow(
    string Table,
    string?[] Key,
    DatabaseCellEdit[] Changes,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <summary>Deletes records, each named by its key — all of them or, when one is refused, none.</summary>
public sealed record DeleteDatabaseRows(
    string Table,
    string?[][] Keys,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <summary>What one write did. Every value is the engine's text; null stays null.</summary>
/// <param name="Key">The record's key after the write — the only way to name a record the engine numbered. Null when it could not be read back.</param>
public sealed record DatabaseQueryResult(
    string[] Columns,
    string?[][] Rows,
    int RowsAffected,
    bool Truncated,
    long ElapsedMs,
    string? Error,
    string?[]? Key = null
);

/// <summary>One value in full — a BLOB the datasheet only describes as its length and first bytes.</summary>
public sealed record GetDatabaseValue(
    string Table,
    string?[] Key,
    string Column,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Base64">The bytes of a binary value.</param>
/// <param name="Text">Any other value, as the engine's own text.</param>
public sealed record DatabaseValue(string? Base64, string? Text, long Length, bool IsNull, string? Error);

// ---- the table designer ----

/// <param name="Original">What the column is called in the table as it stands, or null for one being added.</param>
/// <param name="Unique">A single-column UNIQUE constraint.</param>
public sealed record DatabaseColumnDesign(
    string Name,
    string Type,
    bool NotNull,
    bool PrimaryKey,
    string? Default = null,
    bool AutoIncrement = false,
    bool Unique = false,
    string? Original = null
);

/// <param name="Original">The index's name as it stands, or null for one being added.</param>
public sealed record DatabaseIndexDesign(string Name, string[] Columns, bool Unique, string? Original = null);

/// <param name="Original">The constraint's name as it stands — or, where keys have no names, any value for "was there".</param>
public sealed record DatabaseForeignKeyDesign(
    string? Name,
    string[] Columns,
    string? ReferencedSchema,
    string ReferencedTable,
    string[] ReferencedColumns,
    string OnDelete = "NO ACTION",
    string OnUpdate = "NO ACTION",
    string? Original = null
);

public sealed record DatabaseCheckDesign(string? Name, string Expression, string? Original = null);

/// <summary>A table as a designer wants it, compared by the device with the table as it is.</summary>
/// <param name="OriginalName">The table being changed, or null for a new one.</param>
public sealed record DatabaseTableDesign(
    string Name,
    string? Schema,
    DatabaseColumnDesign[] Columns,
    DatabaseIndexDesign[] Indexes,
    DatabaseForeignKeyDesign[] ForeignKeys,
    DatabaseCheckDesign[] Checks,
    string? OriginalName = null,
    string? OriginalSchema = null
);

/// <summary>
/// The DDL that would make a table what a design says, to be read before it is run. Applying it is running that text with
/// <see cref="RunDatabaseQuery"/>, so what somebody approved is what runs.
/// </summary>
public sealed record PreviewDatabaseDesign(
    DatabaseTableDesign Design,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Sql">The script, or empty when there is nothing to do.</param>
/// <param name="Notes">What a reader must know before running it — a rebuild, what cannot be kept.</param>
/// <param name="Error">Why no script could be written.</param>
public sealed record DatabaseDesignScript(string Sql, string[] Notes, string? Error);

public enum DatabaseObjectAction
{
    Drop = 0,
    Rename
}

/// <summary>Drops or renames a table, view, index or trigger — or, with <see cref="Preview"/>, only says what it would run.</summary>
/// <param name="Kind"><c>table</c>, <c>view</c>, <c>index</c> or <c>trigger</c>.</param>
/// <param name="Table">The table an index or trigger belongs to, where the engine's rename needs it.</param>
public sealed record ChangeDatabaseObject(
    DatabaseObjectAction Action,
    string Kind,
    string Name,
    string? NewName = null,
    string? Table = null,
    bool Preview = false,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

public sealed record DatabaseObjectResult(string Sql, string? Error);

// ---- CSV ----

/// <summary>
/// A first look at a CSV: its delimiter, whether it has a header, what each column seems to be, and the engine's type for
/// each. From a file in a file root (<see cref="CsvRoot"/> and <see cref="CsvPath"/>) or from text the page already has
/// (<see cref="CsvText"/>) — exactly one.
/// </summary>
/// <param name="Delimiter">Null to detect it. <c>\t</c> for a tab.</param>
/// <param name="HasHeader">Null to detect it.</param>
public sealed record PreviewDatabaseImport(
    string? CsvRoot = null,
    string? CsvPath = null,
    string? CsvText = null,
    string? Delimiter = null,
    bool? HasHeader = null,
    int SampleRows = 50,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Columns">A name for each column — the header's, or <c>column1</c>… without one.</param>
/// <param name="Kinds">What every sampled value in each column could be read as.</param>
/// <param name="SuggestedTypes">The engine's type for each, for a new table.</param>
/// <param name="Rows">How many data rows the whole file has.</param>
public sealed record DatabaseImportPreview(
    string Delimiter,
    bool HasHeader,
    string[] Columns,
    DatabaseValueKind[] Kinds,
    string[] SuggestedTypes,
    string?[][] Sample,
    long Rows,
    string? Error
);

/// <param name="Source">The CSV column, from zero.</param>
/// <param name="Target">The table's column it goes into.</param>
/// <param name="Type">For a new table, the column's type.</param>
public sealed record DatabaseImportColumn(int Source, string Target, string? Type = null);

/// <summary>
/// Reads a CSV into a table — a new one made to the mapping, or one that exists — in one transaction: an import that stops
/// part way leaves the table as it was, with the line that stopped it in the answer.
/// </summary>
/// <param name="EmptyIsNull">Whether an empty field is NULL rather than an empty string.</param>
public sealed record ImportDatabaseCsv(
    string Table,
    bool CreateTable,
    DatabaseImportColumn[] Columns,
    string Delimiter,
    bool HasHeader,
    string? CsvRoot = null,
    string? CsvPath = null,
    string? CsvText = null,
    bool EmptyIsNull = true,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="FailedAtLine">The file's line the import stopped at, when it did.</param>
public sealed record DatabaseImportResult(long Rows, long ElapsedMs, string? Error, long? FailedAtLine = null);

/// <summary>
/// Writes a table — filtered and sorted as a datasheet shows it — or one statement's answer to a CSV file in a file root,
/// streamed from the engine on the device. Exactly one of <see cref="Table"/> and <see cref="Sql"/>. The file is UTF-8 with
/// a byte-order mark, so spreadsheets read it as UTF-8, and replaces anything already at the target.
/// </summary>
public sealed record ExportDatabaseCsv(
    string TargetRoot,
    string TargetPath,
    string? Table = null,
    string? Sql = null,
    DatabaseRowFilter[]? Filters = null,
    string? Search = null,
    DatabaseSort[]? Sort = null,
    string? Schema = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Path">Where it was written, in <see cref="ExportDatabaseCsv.TargetRoot"/>.</param>
public sealed record DatabaseExportResult(string? Path, long Rows, long ElapsedMs, string? Error);

// ---- history and saved queries ----

/// <summary>The queries run against one database, newest first, as the device recorded them.</summary>
public sealed record GetDatabaseHistory(
    int Take = 200,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

public sealed record DatabaseHistoryEntry(
    Guid Id,
    string Sql,
    DateTimeOffset RanOn,
    long ElapsedMs,
    int RowsAffected,
    int ResultSets,
    string? Error
);

public sealed record ClearDatabaseHistory(
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <summary>The named queries saved against one database.</summary>
public sealed record GetSavedDatabaseQueries(
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

public sealed record SavedDatabaseQuery(Guid Id, string Name, string Sql, DateTimeOffset SavedOn);

/// <summary>Saves a query under a name. Saving under a name the database already has overwrites that one.</summary>
/// <param name="Id">Null for a new one; a saved one's id to overwrite or rename it.</param>
public sealed record SaveDatabaseQuery(
    string Name,
    string Sql,
    Guid? Id = null,
    string? Root = null,
    string? Path = null,
    string? Connection = null,
    string? Database = null
);

/// <param name="Refusal">Why it was not saved, in a sentence for the form.</param>
public sealed record SavedDatabaseQueryResult(SavedDatabaseQuery? Query, string? Refusal);

/// <summary>Serialization for every database contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(IReadOnlyList<DatabaseConnection>))]
[JsonSerializable(typeof(CreateDatabase))]
[JsonSerializable(typeof(GetDatabaseSchema))]
[JsonSerializable(typeof(DatabaseSchema))]
[JsonSerializable(typeof(RunDatabaseQuery))]
[JsonSerializable(typeof(CancelDatabaseQuery))]
[JsonSerializable(typeof(DatabaseScriptResult))]
[JsonSerializable(typeof(GetDatabaseTableRows))]
[JsonSerializable(typeof(DatabaseTableRows))]
[JsonSerializable(typeof(CountDatabaseTableRows))]
[JsonSerializable(typeof(DatabaseRowCount))]
[JsonSerializable(typeof(GetDatabaseTotals))]
[JsonSerializable(typeof(DatabaseTotals))]
[JsonSerializable(typeof(InsertDatabaseRow))]
[JsonSerializable(typeof(UpdateDatabaseRow))]
[JsonSerializable(typeof(DeleteDatabaseRows))]
[JsonSerializable(typeof(DatabaseQueryResult))]
[JsonSerializable(typeof(GetDatabaseValue))]
[JsonSerializable(typeof(DatabaseValue))]
[JsonSerializable(typeof(PreviewDatabaseDesign))]
[JsonSerializable(typeof(DatabaseDesignScript))]
[JsonSerializable(typeof(ChangeDatabaseObject))]
[JsonSerializable(typeof(DatabaseObjectResult))]
[JsonSerializable(typeof(PreviewDatabaseImport))]
[JsonSerializable(typeof(DatabaseImportPreview))]
[JsonSerializable(typeof(ImportDatabaseCsv))]
[JsonSerializable(typeof(DatabaseImportResult))]
[JsonSerializable(typeof(ExportDatabaseCsv))]
[JsonSerializable(typeof(DatabaseExportResult))]
[JsonSerializable(typeof(GetDatabaseHistory))]
[JsonSerializable(typeof(IReadOnlyList<DatabaseHistoryEntry>))]
[JsonSerializable(typeof(ClearDatabaseHistory))]
[JsonSerializable(typeof(GetSavedDatabaseQueries))]
[JsonSerializable(typeof(IReadOnlyList<SavedDatabaseQuery>))]
[JsonSerializable(typeof(SaveDatabaseQuery))]
[JsonSerializable(typeof(SavedDatabaseQueryResult))]
public partial class DatabaseJsonContext : JsonSerializerContext;
