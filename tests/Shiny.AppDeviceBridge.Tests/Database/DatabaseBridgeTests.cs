using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Database;
using Shiny.AppDeviceBridge.Database.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests.Database;

/// <summary>
/// The database bridge over a real SQLite file in the <c>data</c> root, through the typed client: the datasheet's paging,
/// sort, filter, search, count and totals; writes and the keys they hand back; scripts with several result sets, errors
/// with lines, Explain as a tree, cancel and the timeout; the schema; the designer's scripts run for real; history and
/// saved queries; CSV in and out of file roots; the fence around the file; and a driver for another engine.
/// </summary>
public class DatabaseBridgeTests
{
    const string Db = "shop.db";

    const string Seed =
        """
        CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, city TEXT DEFAULT 'Nowhere', CHECK (length(name) > 0));
        CREATE TABLE orders (
            id INTEGER PRIMARY KEY,
            customer INTEGER NOT NULL REFERENCES customers (id) ON DELETE CASCADE,
            total REAL,
            CONSTRAINT positive CHECK (total >= 0)
        );
        CREATE INDEX orders_customer ON orders (customer);
        CREATE TABLE plain (k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID;
        CREATE TRIGGER stamp AFTER INSERT ON orders BEGIN UPDATE orders SET total = coalesce(total, 0) WHERE id = new.id; END;
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000)
            INSERT INTO customers (name, city) SELECT 'customer ' || i, CASE WHEN i % 10 = 0 THEN NULL ELSE 'city ' || (i % 7) END FROM n;
        INSERT INTO orders (customer, total) VALUES (1, 10.5), (1, 20), (2, 5);
        """;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string? Cell(DatabaseTableRows rows, int row, string column) => rows.Rows[row][Array.IndexOf(rows.Columns, column)];

    // ---- paging, sorting, filtering ----

    [Fact]
    public async Task A_page_starts_where_it_was_asked_and_says_whether_there_is_more()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var page = await db.RowsAsync("customers", offset: 200, take: 100);

        Assert.Null(page.Error);
        Assert.Equal(100, page.Rows.Length);
        Assert.True(page.Truncated);
        Assert.Equal(200, page.Offset);
        Assert.Equal("customer 201", Cell(page, 0, "name"));
        Assert.Equal("201", page.RowKeys[0][0]);

        var last = await db.RowsAsync("customers", offset: 950, take: 100);
        Assert.Equal(50, last.Rows.Length);
        Assert.False(last.Truncated);

        var sorted = await db.RowsAsync("customers", take: 3, sort: [new DatabaseSort("id", Descending: true)]);
        Assert.Equal(["1000", "999", "998"], sorted.Rows.Select(x => x[0]).ToArray());
    }

    [Fact]
    public async Task Filters_compare_through_the_columns_type_and_search_takes_percent_literally()
    {
        await using var db = await DatabaseFixture.StartAsync();

        // "id > 990" as a number: as text "999" > "990" but so is "991"… and "1000" is not
        var page = await db.RowsAsync("customers", filters: [new DatabaseRowFilter("id", DatabaseFilterOperator.Greater, "990")]);
        Assert.Equal(10, page.Rows.Length);
        Assert.Contains(page.Rows, x => x[0] == "1000");

        Assert.Equal(100, (await db.CountAsync("customers", filters: [new DatabaseRowFilter("city", DatabaseFilterOperator.IsNull)])).Count);

        // not-equal keeps the NULLs, as a person reading a filter means it
        Assert.True((await db.CountAsync("customers", filters: [new DatabaseRowFilter("city", DatabaseFilterOperator.NotEqual, "city 6")])).Count > 800);

        // customer 99, 990..999
        Assert.Equal(11, (await db.CountAsync("customers", search: "CUSTOMER 99")).Count);
        Assert.Equal(0, (await db.CountAsync("customers", search: "%")).Count);
    }

    [Fact]
    public async Task A_filter_on_a_column_the_table_does_not_have_is_refused()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var page = await db.RowsAsync("customers", filters: [new DatabaseRowFilter("name; DROP TABLE customers", DatabaseFilterOperator.Equal, "x")]);

        Assert.NotNull(page.Error);
        Assert.Equal(1000, (await db.CountAsync("customers")).Count);
    }

    [Fact]
    public async Task Totals_are_over_every_row_the_filters_let_through()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var totals = await db.Client.GetTotalsAsync(new GetDatabaseTotals(
            "customers",
            [
                new DatabaseTotal("id", DatabaseAggregate.Count),
                new DatabaseTotal("id", DatabaseAggregate.Sum),
                new DatabaseTotal("id", DatabaseAggregate.Average),
                new DatabaseTotal("id", DatabaseAggregate.Max)
            ],
            Filters: [new DatabaseRowFilter("id", DatabaseFilterOperator.LessOrEqual, "10")],
            Root: "data",
            Path: Db
        ), Ct);

        Assert.Null(totals.Error);
        Assert.Equal(["10", "55", "5.5", "10"], totals.Values);
    }

    // ---- writing ----

    [Fact]
    public async Task An_insert_answers_with_the_key_the_engine_chose()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var added = await db.Client.InsertRowAsync(new InsertDatabaseRow("customers", [new DatabaseCellEdit("name", "new one")], Root: "data", Path: Db), Ct);

        Assert.Null(added.Error);
        Assert.Equal(["1001"], added.Key);
        Assert.Equal("Nowhere", added.Rows[0][Array.IndexOf(added.Columns, "city")]);
    }

    [Fact]
    public async Task Editing_the_integer_primary_key_moves_the_row_and_the_new_key_comes_back()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var saved = await db.Client.UpdateRowAsync(new UpdateDatabaseRow("customers", ["5"], [new DatabaseCellEdit("id", "5005")], Root: "data", Path: Db), Ct);

        Assert.Null(saved.Error);
        Assert.Equal(["5005"], saved.Key);
        Assert.Equal("customer 5", saved.Rows[0][Array.IndexOf(saved.Columns, "name")]);
    }

    [Fact]
    public async Task Rows_are_deleted_all_or_none()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var deleted = await db.Client.DeleteRowsAsync(new DeleteDatabaseRows("customers", [["3"], ["4"]], Root: "data", Path: Db), Ct);
        Assert.Null(deleted.Error);
        Assert.Equal(2, deleted.RowsAffected);
        Assert.Equal(998, (await db.CountAsync("customers")).Count);

        // a key that is not a rowid is refused before anything runs
        var refused = await db.Client.DeleteRowsAsync(new DeleteDatabaseRows("customers", [["6"], ["not a rowid"]], Root: "data", Path: Db), Ct);
        Assert.NotNull(refused.Error);
        Assert.Equal(998, (await db.CountAsync("customers")).Count);

        var withoutRowId = await db.Client.DeleteRowsAsync(new DeleteDatabaseRows("plain", [["1"]], Root: "data", Path: Db), Ct);
        Assert.Contains("rowid", withoutRowId.Error);
    }

    [Fact]
    public async Task A_blob_is_described_in_a_page_and_read_whole_on_its_own()
    {
        await using var db = await DatabaseFixture.StartAsync();
        await db.RunAsync("CREATE TABLE pics (data BLOB); INSERT INTO pics VALUES (randomblob(1000));");

        var described = await db.RowsAsync("pics");
        Assert.StartsWith("BLOB[1000] ", Cell(described, 0, "data"));

        var whole = await db.Client.GetValueAsync(new GetDatabaseValue("pics", described.RowKeys[0], "data", Root: "data", Path: Db), Ct);
        Assert.Null(whole.Error);
        Assert.Equal(1000, Convert.FromBase64String(whole.Base64!).Length);
    }

    // ---- scripts ----

    [Fact]
    public async Task Every_result_set_of_a_script_is_kept()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var result = await db.RunAsync("SELECT 1 AS a;\nUPDATE customers SET city = 'x' WHERE id <= 3;\nSELECT 2 AS b, 'two' AS c;");

        Assert.Equal(2, result.Results.Length);
        Assert.Equal(["a"], result.Results[0].Columns);
        Assert.Equal(["b", "c"], result.Results[1].Columns);
        Assert.Equal(2, result.Results[1].Statement);
        Assert.Equal(3, result.RowsAffected);
        Assert.Contains(result.Messages, x => x.Kind == DatabaseMessageKind.Rows && x.Text.StartsWith("3 rows", StringComparison.Ordinal) && x.Line == 2);
        Assert.Equal(DatabaseValueKind.Integer, result.Results[1].Kinds[0]);
    }

    [Fact]
    public async Task Ddl_after_a_write_does_not_report_the_writes_count_as_its_own()
    {
        await using var db = await DatabaseFixture.StartAsync();

        // sqlite3_changes() is not reset by DDL: read naively, the CREATE reports the UPDATE's 1000
        var result = await db.RunAsync("UPDATE customers SET city = 'x';\nCREATE TABLE another (x);\nDELETE FROM customers WHERE id < 0;");

        Assert.Equal(1000, result.RowsAffected);
        Assert.Contains(result.Messages, x => x.Line == 2 && x.Kind == DatabaseMessageKind.Info);
        Assert.Contains(result.Messages, x => x.Line == 3 && x.Text.StartsWith("0 rows", StringComparison.Ordinal));
        Assert.True(result.SchemaChanged);
        Assert.False((await db.RunAsync("SELECT * FROM another")).SchemaChanged);
    }

    [Fact]
    public async Task An_error_says_which_line_its_statement_starts_on()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var result = await db.Client.QueryAsync(new RunDatabaseQuery("SELECT 1;\n\n  SELECT nope FROM missing;\nSELECT 3;", Root: "data", Path: Db), Ct);

        Assert.NotNull(result.Error);
        Assert.Equal(3, result.ErrorLine);
        Assert.Single(result.Results);
    }

    [Fact]
    public async Task Explain_is_a_tree_and_runs_nothing()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var result = await db.Client.QueryAsync(new RunDatabaseQuery(
            "DELETE FROM customers; SELECT * FROM orders JOIN customers ON customers.id = orders.customer WHERE customers.name = 'a'",
            Explain: true,
            Root: "data",
            Path: Db
        ), Ct);

        Assert.Null(result.Error);
        Assert.NotNull(result.Plan);
        Assert.Equal(2, result.Plan.Length);
        Assert.NotEmpty(result.Plan[1].Children);
        Assert.Equal(1000, (await db.CountAsync("customers")).Count);
    }

    [Fact]
    public async Task A_cancel_ends_a_runaway_query()
    {
        await using var db = await DatabaseFixture.StartAsync();
        var id = Guid.NewGuid();
        var watch = Stopwatch.StartNew();

        var running = db.Client.QueryAsync(new RunDatabaseQuery(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n",
            RunId: id,
            Root: "data",
            Path: Db
        ), Ct);

        await Task.Delay(300, Ct);
        await db.Client.CancelAsync(new CancelDatabaseQuery(id), Ct);

        var result = await running;

        Assert.True(result.Cancelled);
        Assert.Equal("The query was stopped.", result.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_statement_past_the_timeout_is_interrupted()
    {
        await using var db = await DatabaseFixture.StartAsync(new DatabaseBridgeOptions { StatementTimeout = TimeSpan.FromSeconds(1) });

        var result = await db.Client.QueryAsync(new RunDatabaseQuery(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n",
            Root: "data",
            Path: Db
        ), Ct);

        Assert.True(result.Cancelled);
        Assert.Contains("still running after 1 seconds", result.Error);
    }

    // ---- the schema ----

    [Fact]
    public async Task The_schema_carries_foreign_keys_checks_defaults_and_autoincrement()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var schema = await db.SchemaAsync();
        var customers = schema.Tables.Single(x => x.Name == "customers");
        var orders = schema.Tables.Single(x => x.Name == "orders");

        Assert.Equal(DatabaseEngineKind.Sqlite, schema.Engine);
        Assert.True(schema.FileSize > 0);

        var id = customers.Columns.Single(x => x.Name == "id");
        Assert.True(id.AutoIncrement);
        Assert.Equal(DatabaseValueKind.Integer, id.Kind);
        Assert.Equal("'Nowhere'", customers.Columns.Single(x => x.Name == "city").Default);
        Assert.Equal("length(name) > 0", Assert.Single(customers.Checks!).Expression);
        Assert.Contains("name", TableDesigner.UniqueColumns(customers));

        var key = Assert.Single(orders.ForeignKeys!);
        Assert.Equal(["customer"], key.Columns);
        Assert.Equal("customers", key.ReferencedTable);
        Assert.Equal(["id"], key.ReferencedColumns);
        Assert.Equal("CASCADE", key.OnDelete);
        Assert.Equal("positive", Assert.Single(orders.Checks!).Name);
        Assert.Equal(["stamp"], orders.Triggers);

        Assert.Empty(schema.Tables.Single(x => x.Name == "plain").KeyColumns);
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_says_so_rather_than_failing()
    {
        await using var db = await DatabaseFixture.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(db.DataRoot, "notes.db"), "not a database at all, just some text that is long enough", Ct);

        var schema = await db.Client.GetSchemaAsync(new GetDatabaseSchema("data", "notes.db"), Ct);

        Assert.Equal("'notes.db' is not a SQLite database.", schema.Error);
        Assert.Contains("no file at", (await db.Client.GetSchemaAsync(new GetDatabaseSchema("data", "missing.db"), Ct)).Error);
    }

    // ---- the designer, run for real ----

    [Fact]
    public async Task A_rebuild_keeps_the_rows_the_indexes_and_the_triggers()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var orders = (await db.SchemaAsync()).Tables.Single(x => x.Name == "orders");
        var design = TableDesigner.DesignOf(orders);
        design = design with { Columns = [.. design.Columns, new DatabaseColumnDesign("note", "TEXT", false, false, "'none'")] };

        var script = await db.Client.PreviewDesignAsync(new PreviewDatabaseDesign(design, "data", Db), Ct);

        Assert.Null(script.Error);
        Assert.Contains("__rebuild", script.Sql);
        Assert.NotEmpty(script.Notes);

        await db.RunAsync(script.Sql);

        var after = (await db.SchemaAsync()).Tables.Single(x => x.Name == "orders");
        Assert.Contains(after.Columns, x => x.Name == "note");
        Assert.Contains(after.Indexes, x => x.Name == "orders_customer");
        Assert.Equal(["stamp"], after.Triggers);
        Assert.Single(after.ForeignKeys!);
        Assert.Equal("positive", Assert.Single(after.Checks!).Name);
        Assert.Equal(3, (await db.CountAsync("orders")).Count);

        var indexOnly = await db.Client.PreviewDesignAsync(new PreviewDatabaseDesign(
            TableDesigner.DesignOf(after) with { Indexes = [.. TableDesigner.DesignOf(after).Indexes, new DatabaseIndexDesign("orders_total", ["total"], false)] },
            "data",
            Db
        ), Ct);
        Assert.DoesNotContain("__rebuild", indexOnly.Sql);
        Assert.Contains("CREATE INDEX \"orders_total\"", indexOnly.Sql);
    }

    [Fact]
    public async Task Renaming_and_dropping_go_through_the_catalogue()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var preview = await db.Client.ChangeObjectAsync(new ChangeDatabaseObject(DatabaseObjectAction.Drop, "table", "plain", Preview: true, Root: "data", Path: Db), Ct);
        Assert.Equal("DROP TABLE \"plain\";", preview.Sql);
        Assert.Contains((await db.SchemaAsync()).Tables, x => x.Name == "plain");

        Assert.Null((await db.Client.ChangeObjectAsync(new ChangeDatabaseObject(DatabaseObjectAction.Rename, "table", "plain", NewName: "simple", Root: "data", Path: Db), Ct)).Error);
        Assert.Contains((await db.SchemaAsync()).Tables, x => x.Name == "simple");

        Assert.NotNull((await db.Client.ChangeObjectAsync(new ChangeDatabaseObject(DatabaseObjectAction.Drop, "table", "no such", Root: "data", Path: Db), Ct)).Error);
        Assert.NotNull((await db.Client.ChangeObjectAsync(new ChangeDatabaseObject(DatabaseObjectAction.Rename, "index", "orders_customer", NewName: "x", Root: "data", Path: Db), Ct)).Error);
    }

    // ---- history and saved queries ----

    [Fact]
    public async Task Queries_are_recorded_per_database_and_a_repeat_is_one_entry()
    {
        await using var db = await DatabaseFixture.StartAsync();
        await db.Client.CreateAsync(new CreateDatabase("data", "other.db"), Ct);

        await db.RunAsync("SELECT 1");
        await db.RunAsync("SELECT 1");
        await db.RunAsync("SELECT 2");
        await db.Client.QueryAsync(new RunDatabaseQuery("SELECT 3", Record: false, Root: "data", Path: Db), Ct);
        await db.Client.QueryAsync(new RunDatabaseQuery("SELECT 4", Explain: true, Root: "data", Path: Db), Ct);
        await db.Client.QueryAsync(new RunDatabaseQuery("SELECT 'elsewhere'", Root: "data", Path: "other.db"), Ct);

        var history = await db.Client.GetHistoryAsync(new GetDatabaseHistory(Root: "data", Path: Db), Ct);

        // the seed script is the oldest entry
        Assert.Equal(["SELECT 2", "SELECT 1", Seed], history.Select(x => x.Sql).ToArray());
        Assert.Equal(["SELECT 'elsewhere'"], (await db.Client.GetHistoryAsync(new GetDatabaseHistory(Root: "data", Path: "other.db"), Ct)).Select(x => x.Sql).ToArray());

        await db.Client.ClearHistoryAsync(new ClearDatabaseHistory("data", Db), Ct);
        Assert.Empty(await db.Client.GetHistoryAsync(new GetDatabaseHistory(Root: "data", Path: Db), Ct));
        Assert.Single(await db.Client.GetHistoryAsync(new GetDatabaseHistory(Root: "data", Path: "other.db"), Ct));

        // kept in the data directory, where no file root reaches
        Assert.True(File.Exists(Path.Combine(db.DataDirectory, "database", "queries.db")));
        Assert.False(Directory.Exists(Path.Combine(db.DataRoot, "database")));
    }

    [Fact]
    public async Task The_history_keeps_only_the_newest()
    {
        await using var db = await DatabaseFixture.StartAsync(new DatabaseBridgeOptions { MaxHistory = 3 });

        for (var i = 0; i < 5; i++)
            await db.RunAsync($"SELECT {i}");

        var history = await db.Client.GetHistoryAsync(new GetDatabaseHistory(Root: "data", Path: Db), Ct);
        Assert.Equal(["SELECT 4", "SELECT 3", "SELECT 2"], history.Select(x => x.Sql).ToArray());
    }

    [Fact]
    public async Task A_saved_query_is_overwritten_by_name_and_cannot_be_renamed_onto_another()
    {
        await using var db = await DatabaseFixture.StartAsync();
        await db.Client.CreateAsync(new CreateDatabase("data", "other.db"), Ct);

        var totals = (await db.Client.SaveQueryAsync(new SaveDatabaseQuery("totals", "SELECT 1", Root: "data", Path: Db), Ct)).Query!;
        var again = (await db.Client.SaveQueryAsync(new SaveDatabaseQuery(" totals ", "SELECT 2", Root: "data", Path: Db), Ct)).Query!;
        var counts = (await db.Client.SaveQueryAsync(new SaveDatabaseQuery("counts", "SELECT 3", Root: "data", Path: Db), Ct)).Query!;

        Assert.Equal(totals.Id, again.Id);
        Assert.Equal(["counts", "totals"], (await db.Client.GetSavedQueriesAsync(new GetSavedDatabaseQueries("data", Db), Ct)).Select(x => x.Name).ToArray());
        Assert.Equal("SELECT 2", (await db.Client.GetSavedQueriesAsync(new GetSavedDatabaseQueries("data", Db), Ct)).Single(x => x.Name == "totals").Sql);

        Assert.Contains("already called", (await db.Client.SaveQueryAsync(new SaveDatabaseQuery("totals", "SELECT 3", counts.Id, "data", Db), Ct)).Refusal);
        Assert.Contains("another database", (await db.Client.SaveQueryAsync(new SaveDatabaseQuery("x", "SELECT 3", counts.Id, "data", "other.db"), Ct)).Refusal);
        Assert.Contains("needs a name", (await db.Client.SaveQueryAsync(new SaveDatabaseQuery(" ", "SELECT 3", Root: "data", Path: Db), Ct)).Refusal);

        var renamed = (await db.Client.SaveQueryAsync(new SaveDatabaseQuery("tallies", "SELECT 3", counts.Id, "data", Db), Ct)).Query!;
        Assert.Equal(counts.Id, renamed.Id);

        await db.Client.RemoveSavedQueryAsync(totals.Id, Ct);
        Assert.Equal(["tallies"], (await db.Client.GetSavedQueriesAsync(new GetSavedDatabaseQueries("data", Db), Ct)).Select(x => x.Name).ToArray());
        Assert.Empty(await db.Client.GetSavedQueriesAsync(new GetSavedDatabaseQueries("data", "other.db"), Ct));
    }

    // ---- CSV ----

    [Fact]
    public async Task A_csv_is_imported_into_a_new_table_and_exported_back_into_a_root()
    {
        await using var db = await DatabaseFixture.StartAsync();
        const string csv = "code,amount,when,active\r\n007,1.5,2026-01-02,yes\r\n\"a, b\",2,2026-02-03,no\r\n";

        var preview = await db.Client.PreviewImportAsync(new PreviewDatabaseImport(CsvText: csv, Root: "data", Path: Db), Ct);

        Assert.Null(preview.Error);
        Assert.True(preview.HasHeader);
        Assert.Equal(",", preview.Delimiter);
        Assert.Equal(2, preview.Rows);
        Assert.Equal([DatabaseValueKind.Text, DatabaseValueKind.Decimal, DatabaseValueKind.Date, DatabaseValueKind.Boolean], preview.Kinds);
        Assert.Equal(["TEXT", "REAL", "DATE", "BOOLEAN"], preview.SuggestedTypes);

        var imported = await db.Client.ImportAsync(new ImportDatabaseCsv(
            "imported",
            true,
            preview.Columns.Select((c, i) => new DatabaseImportColumn(i, c, preview.SuggestedTypes[i])).ToArray(),
            preview.Delimiter,
            preview.HasHeader,
            CsvText: csv,
            Root: "data",
            Path: Db
        ), Ct);

        Assert.Null(imported.Error);
        Assert.Equal(2, imported.Rows);

        var rows = await db.RowsAsync("imported");
        Assert.Equal("007", Cell(rows, 0, "code"));
        Assert.Equal("1", Cell(rows, 0, "active"));
        Assert.Equal("a, b", Cell(rows, 1, "code"));

        var exported = await db.Client.ExportAsync(new ExportDatabaseCsv("data", "exports/out.csv", Table: "imported", Root: "data", Path: Db), Ct);

        Assert.Null(exported.Error);
        Assert.Equal(2, exported.Rows);
        Assert.Equal("exports/out.csv", exported.Path);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(db.DataRoot, "exports", "out.csv"), Ct);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("code,amount,when,active\r\n007,1.5,2026-01-02,1\r\n\"a, b\",2,2026-02-03,0\r\n", Encoding.UTF8.GetString(bytes[3..]));

        // and the file it wrote is one a second import reads from the root
        var again = await db.Client.PreviewImportAsync(new PreviewDatabaseImport("data", "exports/out.csv", Root: "data", Path: Db), Ct);
        Assert.Null(again.Error);
        Assert.Equal(2, again.Rows);
    }

    [Fact]
    public async Task A_statement_exports_its_answer()
    {
        await using var db = await DatabaseFixture.StartAsync();

        var exported = await db.Client.ExportAsync(new ExportDatabaseCsv("data", "few.csv", Sql: "SELECT id, name FROM customers WHERE id <= 2", Root: "data", Path: Db), Ct);

        Assert.Null(exported.Error);
        Assert.Equal("id,name\r\n1,customer 1\r\n2,customer 2\r\n", (await File.ReadAllTextAsync(Path.Combine(db.DataRoot, "few.csv"), Ct)).TrimStart('﻿'));

        var two = await db.Client.ExportAsync(new ExportDatabaseCsv("data", "two.csv", Sql: "SELECT 1; SELECT 2", Root: "data", Path: Db), Ct);
        Assert.Contains("one statement", two.Error);
        Assert.False(File.Exists(Path.Combine(db.DataRoot, "two.csv")));
    }

    [Fact]
    public async Task A_failed_import_leaves_nothing_behind()
    {
        await using var db = await DatabaseFixture.StartAsync();
        const string csv = "name\r\nfine\r\nfine\r\n";

        // customers.name is UNIQUE, so the second "fine" is refused - and the first goes with it
        var failed = await db.Client.ImportAsync(new ImportDatabaseCsv("customers", false, [new DatabaseImportColumn(0, "name")], ",", true, CsvText: csv, Root: "data", Path: Db), Ct);

        Assert.NotNull(failed.Error);
        Assert.Equal(3, failed.FailedAtLine);
        Assert.Equal(1000, (await db.CountAsync("customers")).Count);

        // a table made for an import that failed is not left behind empty
        var fresh = await db.Client.ImportAsync(new ImportDatabaseCsv(
            "uniques",
            true,
            [new DatabaseImportColumn(0, "name", "TEXT NOT NULL")],
            ",",
            true,
            CsvText: "name\r\nx\r\n\r\n,\r\n",
            Root: "data",
            Path: Db
        ), Ct);

        Assert.NotNull(fresh.Error);
        Assert.DoesNotContain((await db.SchemaAsync()).Tables, x => x.Name == "uniques");
    }

    // ---- creating ----

    [Fact]
    public async Task A_new_database_is_a_real_one_and_is_not_made_over_another()
    {
        await using var db = await DatabaseFixture.StartAsync();

        await db.Client.CreateAsync(new CreateDatabase("data", "nested/fresh.db"), Ct);

        var header = (await File.ReadAllBytesAsync(Path.Combine(db.DataRoot, "nested", "fresh.db"), Ct))[..16];
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(header));
        Assert.Empty((await db.Client.GetSchemaAsync(new GetDatabaseSchema("data", "nested/fresh.db"), Ct)).Tables);

        var taken = await Assert.ThrowsAsync<BridgeException>(() => db.Client.CreateAsync(new CreateDatabase("data", Db), Ct));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("exists", taken.Code);
        Assert.Equal(1000, (await db.CountAsync("customers")).Count);
    }

    // ---- the fence ----

    [Fact]
    public async Task A_query_cannot_attach_another_file_or_load_an_extension()
    {
        await using var db = await DatabaseFixture.StartAsync();
        var outside = Path.Combine(db.DataDirectory, "outside.db");

        var attach = await db.Client.QueryAsync(new RunDatabaseQuery($"ATTACH DATABASE '{outside}' AS other", Root: "data", Path: Db), Ct);
        Assert.Contains("not authorized", attach.Error);
        Assert.False(File.Exists(outside));

        var extension = await db.Client.QueryAsync(new RunDatabaseQuery("SELECT load_extension('anything')", Root: "data", Path: Db), Ct);
        Assert.Contains("not authorized", extension.Error);

        // VACUUM INTO writes a copy through an ATTACH of its own, which the same authorizer refuses
        var vacuum = await db.Client.QueryAsync(new RunDatabaseQuery($"VACUUM INTO '{outside}'", Root: "data", Path: Db), Ct);
        Assert.Contains("authorization denied", vacuum.Error);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task A_database_is_named_by_a_root_and_a_safe_path_or_a_connection()
    {
        await using var db = await DatabaseFixture.StartAsync();

        async Task<BridgeException> Refused(GetDatabaseSchema request)
            => await Assert.ThrowsAsync<BridgeException>(() => db.Client.GetSchemaAsync(request, Ct));

        Assert.Equal(HttpStatusCode.BadRequest, (await Refused(new GetDatabaseSchema())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Refused(new GetDatabaseSchema("data", Db, "pg"))).StatusCode);
        Assert.Equal("invalid_path", (await Refused(new GetDatabaseSchema("data", "../outside.db"))).Code);
        Assert.Equal("invalid_path", (await Refused(new GetDatabaseSchema("data", ""))).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await Refused(new GetDatabaseSchema("nowhere", Db))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Refused(new GetDatabaseSchema(Connection: "pg"))).StatusCode);

        // the export target is held to the same rules
        var export = await Assert.ThrowsAsync<BridgeException>(() => db.Client.ExportAsync(new ExportDatabaseCsv("data", "../out.csv", Table: "customers", Root: "data", Path: Db), Ct));
        Assert.Equal("invalid_path", export.Code);

        var missingSql = await db.WebView.PostAsJsonAsync("/_bridge/database/query", new { root = "data", path = Db }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingSql.StatusCode);
    }

    // ---- other engines ----

    [Fact]
    public async Task A_driver_serves_the_connections_it_names()
    {
        var driver = new FakeServerDriver();
        await using var db = await DatabaseFixture.StartAsync(drivers: driver);

        var connection = Assert.Single(await db.Client.GetConnectionsAsync(Ct));
        Assert.Equal(new DatabaseConnection("pg", "Warehouse", DatabaseEngineKind.PostgreSql, "postgres"), connection);

        var schema = await db.Client.GetSchemaAsync(new GetDatabaseSchema(Connection: "pg", Database: "sales"), Ct);
        Assert.Equal(DatabaseEngineKind.PostgreSql, schema.Engine);
        Assert.Equal(new DatabaseConnectionTarget("pg", "sales"), driver.Targets.Single());

        var result = await db.Client.QueryAsync(new RunDatabaseQuery("SELECT now()", Connection: "pg", Database: "sales"), Ct);
        Assert.Equal("ran SELECT now()", result.Messages.Single().Text);
        Assert.Equal(["SELECT now()"], (await db.Client.GetHistoryAsync(new GetDatabaseHistory(Connection: "pg", Database: "sales"), Ct)).Select(x => x.Sql).ToArray());
        Assert.Empty(await db.Client.GetHistoryAsync(new GetDatabaseHistory(Connection: "pg", Database: "other"), Ct));

        // a file is still SQLite's, whatever else is registered
        Assert.Equal(DatabaseEngineKind.Sqlite, (await db.SchemaAsync()).Engine);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<BridgeException>(() => db.Client.GetSchemaAsync(new GetDatabaseSchema(Connection: "mssql"), Ct))).StatusCode);
    }

    [Fact]
    public async Task The_bridge_and_its_drivers_register_on_a_headless_server()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(
            http => http.AddAppDeviceBridge(bridge =>
            {
                bridge.Configure(o =>
                {
                    o.AppId = TestApp.AppId;
                    o.DataDirectory = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", Guid.NewGuid().ToString("n"));
                });
                bridge.AddDatabaseBridge(o => o.StatementTimeout = TimeSpan.FromSeconds(5));
                bridge.Services.AddDatabaseDriver<FakeServerDriver>();
            }),
            autoStart: false
        );

        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<AppDeviceBridgeServer>();
        _ = server.Http;

        var bridge = Assert.IsType<DatabaseBridge>(Assert.Single(server.Bridges, x => x.Name == "database"));
        Assert.True(bridge.IsSupported);
        Assert.Equal(TimeSpan.FromSeconds(5), provider.GetRequiredService<DatabaseBridgeOptions>().StatementTimeout);
        Assert.IsType<FakeServerDriver>(Assert.Single(provider.GetServices<IDatabaseDriver>()));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddShinyHttpServer(
            http => http.AddAppDeviceBridge(b => b.AddDatabaseBridge(o => o.MaxHistory = 0)),
            autoStart: false
        ));
    }

    // ---- the fixture ----

    sealed class DatabaseFixture : IAsyncDisposable
    {
        BuiltInClientTests.HostFixture host = null!;

        public DatabaseBridgeClient Client { get; private set; } = null!;
        public HttpClient WebView { get; private set; } = null!;

        /// <summary>The bridge's data directory.</summary>
        public string DataDirectory { get; private set; } = null!;

        /// <summary>Where the <c>data</c> root is on disk.</summary>
        public string DataRoot => Path.Combine(this.DataDirectory, "files");

        public static async Task<DatabaseFixture> StartAsync(DatabaseBridgeOptions? options = null, params IDatabaseDriver[] drivers)
        {
            var fixture = new DatabaseFixture();

            fixture.host = await BuiltInClientTests.HostFixture.StartAsync(
                app =>
                {
                    var bridgeOptions = app.BridgeOptions();
                    fixture.DataDirectory = bridgeOptions.ResolveDataDirectory();
                    return [new DatabaseBridge(new WebAppFileRoots(bridgeOptions), bridgeOptions, drivers, options)];
                },
                null,
                client => fixture.WebView = client
            );

            fixture.Client = new DatabaseBridgeClient(fixture.host.Transport);

            await fixture.Client.CreateAsync(new CreateDatabase("data", Db), Ct);
            await fixture.RunAsync(Seed);

            return fixture;
        }

        public async Task<DatabaseScriptResult> RunAsync(string sql)
        {
            var result = await this.Client.QueryAsync(new RunDatabaseQuery(sql, Root: "data", Path: Db), Ct);
            Assert.Null(result.Error);
            return result;
        }

        public Task<DatabaseSchema> SchemaAsync() => this.Client.GetSchemaAsync(new GetDatabaseSchema("data", Db), Ct);

        public Task<DatabaseTableRows> RowsAsync(string table, long offset = 0, int take = 100, DatabaseSort[]? sort = null, DatabaseRowFilter[]? filters = null, string? search = null)
            => this.Client.GetRowsAsync(new GetDatabaseTableRows(table, take, offset, sort, filters, search, Root: "data", Path: Db), Ct);

        public Task<DatabaseRowCount> CountAsync(string table, DatabaseRowFilter[]? filters = null, string? search = null)
            => this.Client.CountAsync(new CountDatabaseTableRows(table, filters, search, Root: "data", Path: Db), Ct);

        public ValueTask DisposeAsync() => this.host.DisposeAsync();
    }

    /// <summary>A server engine as an app would add one: it serves the connections it knows and nothing else.</summary>
    sealed class FakeServerDriver : IDatabaseDriver
    {
        public List<DatabaseTarget> Targets { get; } = [];

        public bool Serves(DatabaseTarget target) => target is DatabaseConnectionTarget { Connection: "pg" };

        public Task<IReadOnlyList<DatabaseConnection>> GetConnectionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DatabaseConnection>>([new DatabaseConnection("pg", "Warehouse", DatabaseEngineKind.PostgreSql, "postgres")]);

        public Task<DatabaseSchema> GetSchemaAsync(DatabaseTarget target, CancellationToken cancellationToken)
        {
            this.Targets.Add(target);
            return Task.FromResult(new DatabaseSchema([], null, DatabaseEngineKind.PostgreSql, "PostgreSQL 17", DefaultSchema: "public"));
        }

        public Task<DatabaseScriptResult> QueryAsync(DatabaseTarget target, RunDatabaseQuery request, CancellationToken cancellationToken)
            => Task.FromResult(new DatabaseScriptResult([], [new DatabaseMessage(DatabaseMessageKind.Info, $"ran {request.Sql}")], -1, 1, null));

        public Task<DatabaseTableRows> GetRowsAsync(DatabaseTarget target, GetDatabaseTableRows request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseRowCount> CountAsync(DatabaseTarget target, CountDatabaseTableRows request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseTotals> GetTotalsAsync(DatabaseTarget target, GetDatabaseTotals request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseQueryResult> InsertRowAsync(DatabaseTarget target, InsertDatabaseRow request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseQueryResult> UpdateRowAsync(DatabaseTarget target, UpdateDatabaseRow request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseQueryResult> DeleteRowsAsync(DatabaseTarget target, DeleteDatabaseRows request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseValue> GetValueAsync(DatabaseTarget target, GetDatabaseValue request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseDesignScript> PreviewDesignAsync(DatabaseTarget target, PreviewDatabaseDesign request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DatabaseObjectResult> ChangeObjectAsync(DatabaseTarget target, ChangeDatabaseObject request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetColumnTypeAsync(DatabaseTarget target, DatabaseValueKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> CreateTableAsync(DatabaseTarget target, string table, string? schema, IReadOnlyList<DatabaseColumnDesign> columns, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DatabaseImportResult> InsertManyAsync(DatabaseTarget target, string table, string? schema, IReadOnlyList<DatabaseImportColumn> mapping, IEnumerable<(string?[] Fields, long Line)> records, bool emptyIsNull, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<long> ReadAllAsync(DatabaseTarget target, ExportDatabaseCsv request, Func<string[], Task> header, Func<string?[], Task> row, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
