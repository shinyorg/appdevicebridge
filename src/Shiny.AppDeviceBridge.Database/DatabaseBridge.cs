using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.AppDeviceBridge.Database.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Database;

public static class DatabaseBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/database</c>: SQLite files in the app's file roots, and whatever database servers the drivers
    /// added with <see cref="AddDatabaseDriver{TDriver}"/> reach.
    /// <code>
    /// bridge.AddDatabaseBridge(o => o.StatementTimeout = TimeSpan.FromSeconds(10));
    /// </code>
    /// <para>
    /// A page reaches only the files it could already reach through the files bridge — the same <c>{ root, path }</c>,
    /// resolved by the same rules — and SQLite's authorizer refuses <c>ATTACH</c> and <c>load_extension</c>, so a query
    /// cannot open a file outside them either. Query history is kept in the bridge's data directory, outside every root.
    /// </para>
    /// </summary>
    public static TBuilder AddDatabaseBridge<TBuilder>(this TBuilder bridge, Action<DatabaseBridgeOptions>? configure = null)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

        var services = bridge.Services;
        var options = services.FirstOrDefault(x => x.ServiceType == typeof(DatabaseBridgeOptions))?.ImplementationInstance as DatabaseBridgeOptions;
        if (options is null)
        {
            options = new DatabaseBridgeOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        options.Validate();

        bridge.AddBridge<DatabaseBridge>();
        return bridge;
    }

    /// <summary>
    /// Adds a driver for another engine — a database server the page names by connection. The driver keeps its connections
    /// and their secrets; the page only sees <see cref="IDatabaseDriver.GetConnectionsAsync"/>.
    /// <code>
    /// bridge.AddDatabaseBridge();
    /// bridge.Services.AddDatabaseDriver&lt;PostgresDriver&gt;();
    /// </code>
    /// </summary>
    public static IServiceCollection AddDatabaseDriver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDriver>(this IServiceCollection services)
        where TDriver : class, IDatabaseDriver
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDatabaseDriver, TDriver>());
        return services;
    }
}

/// <summary>
/// <c>/_bridge/database</c>. Every route but <c>connections</c> is a POST with a JSON body naming the database by
/// <c>root</c> and <c>path</c>, or by <c>connection</c> and <c>database</c>.
/// <code>
/// GET  /_bridge/database/connections
/// POST /_bridge/database/databases        { "connection": "…" }
/// POST /_bridge/database/create           { "root": "data", "path": "app.db" }
/// POST /_bridge/database/schema           { "root": "data", "path": "app.db" }
/// POST /_bridge/database/query            { "root": "data", "path": "app.db", "sql": "SELECT …", "runId": "…" }
/// POST /_bridge/database/query/cancel     { "runId": "…" }
/// POST /_bridge/database/rows             { …, "table": "orders", "offset": 0, "maxRows": 200, "sort": [], "filters": [], "search": "…" }
/// POST /_bridge/database/rows/count | rows/totals | rows/insert | rows/update | rows/delete
/// POST /_bridge/database/value | design/preview | object
/// POST /_bridge/database/import/preview | import | export
/// POST /_bridge/database/history | history/clear | saved | saved/save      DELETE /_bridge/database/saved/{id}
/// </code>
/// <para>
/// What the database decided comes back in the answer's <c>error</c>. A request that names no database, or both kinds,
/// is 400; a root or connection nobody has is 404.
/// </para>
/// </summary>
public sealed class DatabaseBridge : IWebAppBridge
{
    /// <summary>How much of a CSV is sampled for its column kinds.</summary>
    const int InferRows = 1000;

    static DatabaseJsonContext Json => DatabaseJsonContext.Default;

    readonly WebAppFileRoots roots;
    readonly AppDeviceBridgeOptions bridgeOptions;
    readonly IReadOnlyList<IDatabaseDriver> drivers;
    readonly DatabaseRuns runs = new();
    readonly DatabaseQueryStore queries;
    readonly ILogger logger;

    /// <param name="drivers">The drivers for other engines. SQLite's is always first, and serves every file.</param>
    public DatabaseBridge(
        WebAppFileRoots roots,
        AppDeviceBridgeOptions bridgeOptions,
        IEnumerable<IDatabaseDriver> drivers,
        DatabaseBridgeOptions? options = null,
        TimeProvider? time = null,
        ILoggerFactory? loggers = null
    )
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(bridgeOptions);
        ArgumentNullException.ThrowIfNull(drivers);

        options ??= new DatabaseBridgeOptions();

        this.roots = roots;
        this.bridgeOptions = bridgeOptions;
        this.Sqlite = new SqliteDatabaseDriver(options);
        this.drivers = [this.Sqlite, .. drivers];
        this.logger = loggers?.CreateLogger<DatabaseBridge>() ?? NullLogger<DatabaseBridge>.Instance;
        this.queries = new DatabaseQueryStore(
            Path.Combine(bridgeOptions.ResolveDataDirectory(), "database", "queries.db"),
            options.MaxHistory,
            time ?? TimeProvider.System,
            this.logger
        );
    }

    public string Name => "database";

    public bool IsSupported => true;

    /// <summary>The driver behind every SQLite file.</summary>
    public SqliteDatabaseDriver Sqlite { get; }

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapGet("/connections", this.GetConnectionsAsync)
        .MapPost("/databases", ctx => this.HandleAsync(ctx, Json.GetDatabaseNames, Json.DatabaseNames,
            r => new(null, null, r.Connection, null), r => String.IsNullOrWhiteSpace(r.Connection) ? "Expected { \"connection\": \"…\" }." : null,
            (d, t, _, ct) => d.GetDatabaseNamesAsync(t, ct)))
        .MapPost("/create", this.CreateAsync)
        .MapPost("/schema", ctx => this.HandleAsync(ctx, Json.GetDatabaseSchema, Json.DatabaseSchema,
            r => new(r.Root, r.Path, r.Connection, r.Database), null,
            (d, t, r, ct) => d.GetSchemaAsync(t, ct)))
        .MapPost("/query", ctx => this.HandleAsync(ctx, Json.RunDatabaseQuery, Json.DatabaseScriptResult,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => r.Sql is null ? "Expected { \"sql\": \"…\" }." : null,
            this.QueryAsync))
        .MapPost("/query/cancel", this.CancelAsync)
        .MapPost("/rows", ctx => this.HandleAsync(ctx, Json.GetDatabaseTableRows, Json.DatabaseTableRows,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table),
            (d, t, r, ct) => d.GetRowsAsync(t, r, ct)))
        .MapPost("/rows/count", ctx => this.HandleAsync(ctx, Json.CountDatabaseTableRows, Json.DatabaseRowCount,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table),
            (d, t, r, ct) => d.CountAsync(t, r, ct)))
        .MapPost("/rows/totals", ctx => this.HandleAsync(ctx, Json.GetDatabaseTotals, Json.DatabaseTotals,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table) ?? (r.Totals is null ? "Expected { \"totals\": [] }." : null),
            (d, t, r, ct) => d.GetTotalsAsync(t, r, ct)))
        .MapPost("/rows/insert", ctx => this.HandleAsync(ctx, Json.InsertDatabaseRow, Json.DatabaseQueryResult,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table) ?? (r.Values is null ? "Expected { \"values\": [] }." : null),
            (d, t, r, ct) => d.InsertRowAsync(t, r, ct)))
        .MapPost("/rows/update", ctx => this.HandleAsync(ctx, Json.UpdateDatabaseRow, Json.DatabaseQueryResult,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table) ?? (r.Key is null || r.Changes is null ? "Expected { \"key\": [], \"changes\": [] }." : null),
            (d, t, r, ct) => d.UpdateRowAsync(t, r, ct)))
        .MapPost("/rows/delete", ctx => this.HandleAsync(ctx, Json.DeleteDatabaseRows, Json.DatabaseQueryResult,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table) ?? (r.Keys is null ? "Expected { \"keys\": [] }." : null),
            (d, t, r, ct) => d.DeleteRowsAsync(t, r, ct)))
        .MapPost("/value", ctx => this.HandleAsync(ctx, Json.GetDatabaseValue, Json.DatabaseValue,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table) ?? (r.Key is null || r.Column is null ? "Expected { \"key\": [], \"column\": \"…\" }." : null),
            (d, t, r, ct) => d.GetValueAsync(t, r, ct)))
        .MapPost("/design/preview", ctx => this.HandleAsync(ctx, Json.PreviewDatabaseDesign, Json.DatabaseDesignScript,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => r.Design is null ? "Expected { \"design\": { … } }." : null,
            (d, t, r, ct) => d.PreviewDesignAsync(t, r, ct)))
        .MapPost("/object", ctx => this.HandleAsync(ctx, Json.ChangeDatabaseObject, Json.DatabaseObjectResult,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => r.Kind is null || r.Name is null ? "Expected { \"action\": \"Drop\", \"kind\": \"table\", \"name\": \"…\" }." : null,
            (d, t, r, ct) => d.ChangeObjectAsync(t, r, ct)))
        .MapPost("/import/preview", ctx => this.HandleAsync(ctx, Json.PreviewDatabaseImport, Json.DatabaseImportPreview,
            r => new(r.Root, r.Path, r.Connection, r.Database), null,
            this.PreviewImportAsync))
        .MapPost("/import", ctx => this.HandleAsync(ctx, Json.ImportDatabaseCsv, Json.DatabaseImportResult,
            r => new(r.Root, r.Path, r.Connection, r.Database), r => NeedsTable(r.Table) ?? (r.Columns is null ? "Expected { \"columns\": [] }." : null),
            this.ImportAsync))
        .MapPost("/export", this.ExportAsync)
        .MapPost("/history", ctx => this.HandleAsync(ctx, Json.GetDatabaseHistory, Json.IReadOnlyListDatabaseHistoryEntry,
            r => new(r.Root, r.Path, r.Connection, r.Database), null,
            (_, t, r, ct) => this.queries.GetHistoryAsync(t.Key, r.Take, ct)))
        .MapPost("/history/clear", ctx => this.HandleAsync(ctx, Json.ClearDatabaseHistory, null,
            r => new(r.Root, r.Path, r.Connection, r.Database), null,
            async (_, t, _, ct) =>
            {
                await this.queries.ClearHistoryAsync(t.Key, ct);
                return true;
            }))
        .MapPost("/saved", ctx => this.HandleAsync(ctx, Json.GetSavedDatabaseQueries, Json.IReadOnlyListSavedDatabaseQuery,
            r => new(r.Root, r.Path, r.Connection, r.Database), null,
            (_, t, _, ct) => this.queries.GetSavedAsync(t.Key, ct)))
        .MapPost("/saved/save", ctx => this.HandleAsync(ctx, Json.SaveDatabaseQuery, Json.SavedDatabaseQueryResult,
            r => new(r.Root, r.Path, r.Connection, r.Database), null,
            (_, t, r, ct) => this.queries.SaveAsync(t.Key, r.Id, r.Name ?? "", r.Sql ?? "", ct)))
        .MapDelete("/saved/{id}", this.RemoveSavedAsync);

    // ---- routing ----

    readonly record struct Where(string? Root, string? Path, string? Connection, string? Database);

    static string? NeedsTable(string? table) => String.IsNullOrWhiteSpace(table) ? "Expected { \"table\": \"…\" }." : null;

    /// <summary>
    /// Reads the request, resolves its database and the driver that serves it, and answers with what the work returns. A
    /// null <paramref name="resultInfo"/> answers 204.
    /// </summary>
    async ValueTask HandleAsync<TRequest, TResult>(
        HttpContext context,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResult>? resultInfo,
        Func<TRequest, Where> where,
        Func<TRequest, string?>? invalid,
        Func<IDatabaseDriver, DatabaseTarget, TRequest, CancellationToken, Task<TResult>> work
    )
    {
        var request = await WebAppBridgeResults.ReadBodyAsync(context, requestInfo);

        if (request is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected a JSON body naming the database: { \"root\": \"data\", \"path\": \"app.db\" }.");
            return;
        }

        if (invalid?.Invoke(request) is { } wrong)
        {
            await WebAppBridgeResults.BadRequest(context, wrong);
            return;
        }

        var (target, driver) = await this.TryResolveAsync(context, where(request));

        if (target is null || driver is null)
            return;

        var result = await work(driver, target, request, context.RequestAborted);

        if (resultInfo is null)
            await WebAppBridgeResults.NoContent(context);
        else
            await WebAppBridgeResults.Json(context, result, resultInfo);
    }

    /// <summary>The database a request names, and its driver - or the failure already written to the response.</summary>
    async ValueTask<(DatabaseTarget? Target, IDatabaseDriver? Driver)> TryResolveAsync(HttpContext context, Where where)
    {
        var hasFile = !String.IsNullOrWhiteSpace(where.Root) || !String.IsNullOrWhiteSpace(where.Path);
        var hasConnection = !String.IsNullOrWhiteSpace(where.Connection);

        if (hasFile == hasConnection)
        {
            await WebAppBridgeResults.BadRequest(context, "Name the database by root and path, or by connection — one of the two.");
            return default;
        }

        DatabaseTarget target;

        if (hasFile)
        {
            if (await this.TryResolveFileAsync(context, where.Root, where.Path) is not { } file)
                return default;

            target = file;
        }
        else
        {
            target = new DatabaseConnectionTarget(where.Connection!, String.IsNullOrWhiteSpace(where.Database) ? null : where.Database);
        }

        var driver = this.drivers.FirstOrDefault(x => x.Serves(target));

        if (driver is null)
        {
            await WebAppBridgeResults.NotFound(context, $"No database driver serves the connection '{where.Connection}'.");
            return default;
        }

        return (target, driver);
    }

    async ValueTask<DatabaseFileTarget?> TryResolveFileAsync(HttpContext context, string? root, string? path)
    {
        if (!this.roots.TryGet(root, out var store))
        {
            await WebAppBridgeResults.NotFound(context, $"There is no file root called '{root}'.");
            return null;
        }

        if (WebAppFilePath.Normalize(path) is not { Length: > 0 } normalized)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "invalid_path", "The path must be a relative path to a file, with no '.' or '..' segments.");
            return null;
        }

        if (store.GetLocalPath(normalized) is not { } fullPath)
        {
            // A folder picked through Android's Storage Access Framework has no paths, and a link out of the root has none
            // that are safe; SQLite can open neither.
            await WebAppBridgeResults.BadRequest(context, $"'{normalized}' in '{store.Name}' is not a file SQLite can open.");
            return null;
        }

        return new DatabaseFileTarget(store.Name, normalized, fullPath);
    }

    // ---- databases ----

    async ValueTask GetConnectionsAsync(HttpContext context)
    {
        var connections = new List<DatabaseConnection>();

        foreach (var driver in this.drivers)
            connections.AddRange(await driver.GetConnectionsAsync(context.RequestAborted));

        await WebAppBridgeResults.Json(context, (IReadOnlyList<DatabaseConnection>)connections, Json.IReadOnlyListDatabaseConnection);
    }

    async ValueTask CreateAsync(HttpContext context)
    {
        var request = await WebAppBridgeResults.ReadBodyAsync(context, Json.CreateDatabase);

        if (request is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"root\": \"data\", \"path\": \"app.db\" }.");
            return;
        }

        if (await this.TryResolveFileAsync(context, request.Root, request.Path) is not { } file)
            return;

        if (File.Exists(file.FullPath) || Directory.Exists(file.FullPath))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "exists", $"Something is already at '{file.Path}'.");
            return;
        }

        try
        {
            await Task.Run(() => this.Sqlite.Create(file), CancellationToken.None);
        }
        catch (IOException) when (File.Exists(file.FullPath))
        {
            // made by somebody else between the check and the create
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "exists", $"Something is already at '{file.Path}'.");
            return;
        }

        await WebAppBridgeResults.NoContent(context);
    }

    // ---- scripts ----

    async Task<DatabaseScriptResult> QueryAsync(IDatabaseDriver driver, DatabaseTarget target, RunDatabaseQuery request, CancellationToken cancellationToken)
    {
        DatabaseScriptResult result;

        using (var run = this.runs.Start(request.RunId, cancellationToken))
            result = await driver.QueryAsync(target, request, run.Token);

        // Recorded after it ran, whatever it did - a statement that failed is one somebody will want to find again and
        // fix. Not what a page asks on nobody's behalf (Record is off for those), and not an Explain, which is a question
        // about a statement rather than a run of it.
        if (request.Record && !request.Explain)
            await this.queries.RecordAsync(target.Key, request.Sql, result, CancellationToken.None);

        return result;
    }

    async ValueTask CancelAsync(HttpContext context)
    {
        var request = await WebAppBridgeResults.ReadBodyAsync(context, Json.CancelDatabaseQuery);

        if (request is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"runId\": \"…\" }.");
            return;
        }

        // Nothing to say when it has already finished: that is what the cancel wanted.
        this.runs.Cancel(request.RunId);
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask RemoveSavedAsync(HttpContext context)
    {
        if (!Guid.TryParse(context.Request.RouteValues["id"], out var id))
        {
            await WebAppBridgeResults.BadRequest(context, "A saved query's id is a GUID.");
            return;
        }

        await this.queries.RemoveSavedAsync(id, context.RequestAborted);
        await WebAppBridgeResults.NoContent(context);
    }

    // ---- CSV ----
    //
    // Here rather than in a driver because the file is the file roots' and the rows are the engine's: this reads and writes
    // the file, and asks the driver for rows one at a time or hands it rows one at a time. No driver knows what a CSV is,
    // and the parsing is written once.

    async Task<DatabaseImportPreview> PreviewImportAsync(IDatabaseDriver driver, DatabaseTarget target, PreviewDatabaseImport request, CancellationToken cancellationToken)
    {
        try
        {
            char delimiter;

            using (var probe = this.OpenCsv(request.CsvRoot, request.CsvPath, request.CsvText))
            {
                var buffer = new char[64 * 1024];
                var read = await probe.ReadBlockAsync(buffer, cancellationToken);
                delimiter = Delimiter(request.Delimiter) ?? DatabaseCsv.DetectDelimiter(new string(buffer, 0, read));
            }

            using var reader = this.OpenCsv(request.CsvRoot, request.CsvPath, request.CsvText);

            var sample = new List<string[]>();
            var total = 0L;

            foreach (var (fields, _) in DatabaseCsv.Read(reader, delimiter))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sample.Count <= InferRows)
                    sample.Add(fields);

                total++;
            }

            if (sample.Count == 0)
                return new DatabaseImportPreview(DelimiterText(delimiter), false, [], [], [], [], 0, "That file has nothing in it.");

            var hasHeader = request.HasHeader ?? DatabaseCsv.DetectHeader(sample.Take(50).ToList());
            var data = hasHeader ? sample.Skip(1).ToList() : sample;
            var width = sample.Max(x => x.Length);
            var names = DatabaseCsv.ColumnNames(hasHeader ? sample[0] : null, width);
            var kinds = Enumerable.Range(0, width).Select(i => DatabaseCsv.Infer(data.Select(r => i < r.Length ? r[i] : null))).ToArray();
            var types = new string[width];

            for (var i = 0; i < width; i++)
                types[i] = await driver.GetColumnTypeAsync(target, kinds[i], cancellationToken);

            return new DatabaseImportPreview(
                DelimiterText(delimiter),
                hasHeader,
                names,
                kinds,
                types,
                data.Take(Math.Clamp(request.SampleRows, 1, 500)).Select(r => Enumerable.Range(0, width).Select(i => i < r.Length ? r[i] : null).ToArray()).ToArray(),
                hasHeader ? total - 1 : total,
                null
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DatabaseRefusal)
        {
            return new DatabaseImportPreview(",", false, [], [], [], [], 0, ex.Message);
        }
    }

    async Task<DatabaseImportResult> ImportAsync(IDatabaseDriver driver, DatabaseTarget target, ImportDatabaseCsv request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        if (Delimiter(request.Delimiter) is not { } delimiter)
            return new DatabaseImportResult(0, 0, "Say which character separates the fields.");

        var table = request.Table.Trim();
        var mapping = request.Columns.Where(x => x.Source >= 0 && !String.IsNullOrWhiteSpace(x.Target)).ToArray();

        if (mapping.Length == 0)
            return new DatabaseImportResult(0, 0, "Map at least one column of the file to the table.");

        var created = false;

        try
        {
            // opened before anything is made, so a file that is not there does not leave an empty table behind
            using var reader = this.OpenCsv(request.CsvRoot, request.CsvPath, request.CsvText);

            if (request.CreateTable)
            {
                var columns = mapping
                    .Select(x => new DatabaseColumnDesign(x.Target.Trim(), String.IsNullOrWhiteSpace(x.Type) ? "TEXT" : x.Type.Trim(), false, false))
                    .ToArray();

                if (await driver.CreateTableAsync(target, table, request.Schema, columns, cancellationToken) is { } refused)
                    return new DatabaseImportResult(0, watch.ElapsedMilliseconds, refused);

                created = true;
            }

            var records = DatabaseCsv.Read(reader, delimiter)
                .Skip(request.HasHeader ? 1 : 0)
                .Select(x => ((string?[])x.Fields, x.Line));

            var result = await driver.InsertManyAsync(target, table, request.Schema, mapping, records, request.EmptyIsNull, cancellationToken);

            if (result.Error is not null && created)
            {
                // The rows went in as one transaction and none of them did; a table made a moment ago for them would be an
                // empty one nobody asked for.
                await driver.ChangeObjectAsync(target, new ChangeDatabaseObject(DatabaseObjectAction.Drop, "table", table, Schema: request.Schema), CancellationToken.None);
            }

            return result with { ElapsedMs = watch.ElapsedMilliseconds };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DatabaseRefusal)
        {
            return new DatabaseImportResult(0, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    async ValueTask ExportAsync(HttpContext context)
    {
        var request = await WebAppBridgeResults.ReadBodyAsync(context, Json.ExportDatabaseCsv);

        if (request is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"targetRoot\": \"data\", \"targetPath\": \"orders.csv\", \"table\": \"orders\", … }.");
            return;
        }

        if (request.Table is null == request.Sql is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Export a table or a statement — one of the two.");
            return;
        }

        if (!this.roots.TryGet(request.TargetRoot, out var store))
        {
            await WebAppBridgeResults.NotFound(context, $"There is no file root called '{request.TargetRoot}'.");
            return;
        }

        if (WebAppFilePath.Normalize(request.TargetPath) is not { Length: > 0 } path)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status400BadRequest, "invalid_path", "The target must be a relative path to a file, with no '.' or '..' segments.");
            return;
        }

        var (target, driver) = await this.TryResolveAsync(context, new Where(request.Root, request.Path, request.Connection, request.Database));

        if (target is null || driver is null)
            return;

        var result = await this.ExportAsync(driver, target, request, store, path, context.RequestAborted);
        await WebAppBridgeResults.Json(context, result, Json.DatabaseExportResult);
    }

    async Task<DatabaseExportResult> ExportAsync(IDatabaseDriver driver, DatabaseTarget target, ExportDatabaseCsv request, WebAppFileStore store, string path, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        // Staged and handed to the root whole, the way an upload is: a half-written export sitting under its real name while
        // a million rows stream into it is a file somebody can open, copy or sync in the middle.
        var staging = Path.Combine(Path.GetTempPath(), $"appdevicebridge-export-{Guid.NewGuid():N}.csv");

        try
        {
            long rows;

            await using (var stream = File.Create(staging))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                // The BOM is on purpose: it is how Excel knows a CSV is UTF-8 rather than the machine's ANSI code page, and
                // every other reader skips it.
                rows = await driver.ReadAllAsync(
                    target,
                    request,
                    columns =>
                    {
                        DatabaseCsv.WriteRecord(writer, columns);
                        return Task.CompletedTask;
                    },
                    values =>
                    {
                        DatabaseCsv.WriteRecord(writer, values);
                        return Task.CompletedTask;
                    },
                    cancellationToken
                );
            }

            await using (var staged = File.OpenRead(staging))
                await store.WriteAsync(path, staged, FileWriteMode.Replace, this.bridgeOptions.MaxFileWriteBytes, cancellationToken);

            return new DatabaseExportResult(path, rows, watch.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DatabaseRefusal or WebAppFileException
            or InvalidOperationException or System.Data.Common.DbException or OperationCanceledException)
        {
            this.logger.LogDebug(ex, "An export did not finish");
            return new DatabaseExportResult(null, 0, watch.ElapsedMilliseconds, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch (IOException)
            {
                // the temp directory is the temp directory
            }
        }
    }

    TextReader OpenCsv(string? csvRoot, string? csvPath, string? csvText)
    {
        var fromFile = !String.IsNullOrWhiteSpace(csvRoot) || !String.IsNullOrWhiteSpace(csvPath);

        if (csvText is not null == fromFile)
            throw new DatabaseRefusal("Import a file in a file root or text from the page — one of the two.");

        if (csvText is not null)
            return new StringReader(csvText);

        if (!this.roots.TryResolve(csvRoot, csvPath, out var fullPath))
            throw new DatabaseRefusal($"'{csvPath}' in '{csvRoot}' is not a file that can be read here.");

        if (!File.Exists(fullPath))
            throw new DatabaseRefusal($"There is no file at '{csvPath}' in '{csvRoot}'.");

        // detectEncodingFromByteOrderMarks, and UTF-8 otherwise: what every spreadsheet writes today
        return new StreamReader(fullPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    }

    /// <summary>The delimiter as the page sends it - one character, or <c>\t</c> spelled out.</summary>
    static char? Delimiter(string? text)
        => text switch
        {
            null or "" => null,
            "\\t" or "tab" => '\t',
            _ => text[0]
        };

    static string DelimiterText(char delimiter) => delimiter == '\t' ? "\\t" : delimiter.ToString();
}
