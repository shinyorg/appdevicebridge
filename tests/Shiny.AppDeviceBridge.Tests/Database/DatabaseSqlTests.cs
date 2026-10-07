using Shiny.AppDeviceBridge.Database;
using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Tests.Database;

/// <summary>
/// What the database bridge writes and reads without a database: the datasheet's WHERE and ORDER BY, SQLite's plan as a
/// tree, scripts cut into statements, what a CSV is taken to be, and the designer's DDL.
/// </summary>
public class DatabaseSqlTests
{
    static readonly DatabaseColumn[] Columns =
    [
        new("id", "INTEGER", true, true, Kind: DatabaseValueKind.Integer),
        new("name", "TEXT", false, false, Kind: DatabaseValueKind.Text),
        new("photo", "BLOB", false, false, Kind: DatabaseValueKind.Binary)
    ];

    // ---- the datasheet's SQL ----

    [Fact]
    public void The_key_always_ends_the_order_so_pages_are_stable()
    {
        var view = RowView.Build(SqliteSpelling.Instance, Columns, null, null, [new DatabaseSort("name", true)], ["rowid"]);

        Assert.Equal("\"name\" DESC, rowid", view.OrderBy);
        Assert.Equal("", view.Where);
    }

    [Fact]
    public void A_comparison_is_a_parameter_and_text_filters_are_escaped()
    {
        var view = RowView.Build(
            SqliteSpelling.Instance,
            Columns,
            [new DatabaseRowFilter("id", DatabaseFilterOperator.Greater, "10"), new DatabaseRowFilter("name", DatabaseFilterOperator.Contains, "50%_off")],
            null,
            null,
            []
        );

        Assert.Equal("\"id\" > @f0 AND CAST(\"name\" AS TEXT) LIKE @f1 ESCAPE '\\'", view.Where);
        Assert.Equal(("@f0", (string?)"10"), view.Parameters[0]);
        Assert.Equal("%50\\%\\_off%", view.Parameters[1].Value);
    }

    [Fact]
    public void Search_skips_binary_columns_and_shares_one_parameter()
    {
        var view = RowView.Build(SqliteSpelling.Instance, Columns, null, "smith", null, []);

        Assert.Equal("(CAST(\"id\" AS TEXT) LIKE @f0 ESCAPE '\\' OR CAST(\"name\" AS TEXT) LIKE @f0 ESCAPE '\\')", view.Where);
        Assert.Single(view.Parameters);
    }

    [Fact]
    public void Not_equal_keeps_the_nulls()
    {
        var view = RowView.Build(SqliteSpelling.Instance, Columns, [new DatabaseRowFilter("name", DatabaseFilterOperator.NotEqual, "x")], null, null, []);

        Assert.Equal("(\"name\" IS NULL OR \"name\" <> @f0)", view.Where);
    }

    [Fact]
    public void A_column_the_table_does_not_have_is_refused_not_spelled()
    {
        Assert.Throws<DatabaseRefusal>(() => RowView.Build(SqliteSpelling.Instance, Columns, null, null, [new DatabaseSort("id; DROP TABLE x")], []));
        Assert.Throws<DatabaseRefusal>(() => RowView.Build(SqliteSpelling.Instance, Columns, [new DatabaseRowFilter("nope", DatabaseFilterOperator.IsNull)], null, null, []));
    }

    // ---- plans ----

    [Fact]
    public void Sqlites_parent_ids_are_the_tree()
    {
        var plan = QueryPlans.FromSqlite(
        [
            ["2", "0", "0", "SCAN orders"],
            ["5", "0", "0", "SEARCH customers USING INTEGER PRIMARY KEY (rowid=?)"],
            ["7", "5", "0", "CORRELATED SCALAR SUBQUERY 1"]
        ]);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Length);
        Assert.Equal("SEARCH customers USING INTEGER PRIMARY KEY (rowid=?)", plan[1].Label);
        Assert.Equal("CORRELATED SCALAR SUBQUERY 1", Assert.Single(plan[1].Children).Label);
    }

    [Fact]
    public void Rows_that_are_not_a_plan_are_not_forced_into_one()
        => Assert.Null(QueryPlans.FromSqlite([["not a number", "0", "0", "x"]]));

    // ---- scripts and CREATE TABLE ----

    [Fact]
    public void A_triggers_semicolons_do_not_split_it()
    {
        SQLitePCL.Batteries_V2.Init();
        var statements = SqliteScript.Split("CREATE TRIGGER t AFTER INSERT ON x BEGIN SELECT 1; SELECT 'a;b'; END;\n-- only a comment;\nSELECT 2");

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("CREATE TRIGGER", statements[0].Text);
        Assert.Equal("SELECT 2", statements[1].Text);
        Assert.Equal(3, statements[1].Line);
    }

    [Fact]
    public void A_check_reads_out_of_a_create_table_with_its_name()
    {
        var checks = SqliteScript.Checks("CREATE TABLE t (a INT CHECK (a > 0), b TEXT, CONSTRAINT \"b ok\" CHECK (b IN ('x', ')')))");

        Assert.Equal(2, checks.Count);
        Assert.Equal((null, "a > 0"), checks[0]);
        Assert.Equal(("b ok", "b IN ('x', ')')"), checks[1]);
        Assert.True(SqliteScript.HasAutoIncrement("create table t (id integer primary key autoincrement)"));
        Assert.False(SqliteScript.HasAutoIncrement("create table t (note text default 'autoincrement')"));
    }

    // ---- CSV ----

    static List<string[]> Parse(string text, char delimiter = ',')
        => DatabaseCsv.Read(new StringReader(text), delimiter).Select(x => x.Fields).ToList();

    [Fact]
    public void Quoted_fields_hold_delimiters_quotes_and_line_breaks()
    {
        var records = Parse("﻿a,\"b, c\",\"say \"\"hi\"\"\"\r\n1,\"two\nlines\",3\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(["a", "b, c", "say \"hi\""], records[0]);
        Assert.Equal(["1", "two\nlines", "3"], records[1]);
    }

    [Fact]
    public void A_record_knows_the_line_it_started_on()
    {
        var lines = DatabaseCsv.Read(new StringReader("h\r\n\"a\nb\"\r\nc\r\n"), ',').Select(x => x.Line).ToList();

        Assert.Equal([1L, 2L, 4L], lines);
    }

    [Fact]
    public void Blank_lines_are_not_records_and_a_last_line_needs_no_break()
        => Assert.Equal(2, Parse("a,b\n\n1,2").Count);

    [Theory]
    [InlineData("a;b;c\n1;2;3\n4;5;6", ';')]
    [InlineData("a\tb\n1\t2", '\t')]
    [InlineData("name,note\nx,\"semi; colon\"\ny,plain", ',')]
    public void The_delimiter_is_the_one_that_splits_every_line_alike(string sample, char expected)
        => Assert.Equal(expected, DatabaseCsv.DetectDelimiter(sample));

    [Fact]
    public void A_header_is_text_over_something_that_is_not()
    {
        Assert.True(DatabaseCsv.DetectHeader([["id", "name"], ["1", "a"], ["2", "b"]]));
        Assert.False(DatabaseCsv.DetectHeader([["1", "a"], ["2", "b"]]));
        Assert.False(DatabaseCsv.DetectHeader([["x", "x"], ["1", "2"]]));
    }

    [Theory]
    [InlineData(DatabaseValueKind.Integer, "1", "-20", "", "300")]
    [InlineData(DatabaseValueKind.Text, "007", "12")]
    [InlineData(DatabaseValueKind.Decimal, "1.5", "2", "1e3")]
    [InlineData(DatabaseValueKind.Boolean, "true", "FALSE", "yes")]
    [InlineData(DatabaseValueKind.Date, "2026-01-02", "1999-12-31")]
    [InlineData(DatabaseValueKind.DateTime, "2026-01-02 03:04:05", "2026-01-02T03:04:05Z")]
    [InlineData(DatabaseValueKind.Guid, "7f449a2e-d45d-4f65-b565-9446aee18058")]
    [InlineData(DatabaseValueKind.Text, "1,5", "2")]
    public void A_column_is_the_narrowest_kind_every_value_fits(DatabaseValueKind expected, params string[] values)
        => Assert.Equal(expected, DatabaseCsv.Infer(values));

    [Fact]
    public void Header_names_are_made_unique_and_missing_ones_numbered()
        => Assert.Equal(["a", "A_2", "column3"], DatabaseCsv.ColumnNames(["a", "A", ""], 3));

    [Fact]
    public void Writing_quotes_only_what_needs_it()
    {
        var text = new StringWriter();
        DatabaseCsv.WriteRecord(text, ["plain", "a,b", "say \"hi\"", null, " padded"]);

        Assert.Equal("plain,\"a,b\",\"say \"\"hi\"\"\",,\" padded\"\r\n", text.ToString());

        // and it reads back as it went out, NULL as the empty field it was written as
        Assert.Equal(["plain", "a,b", "say \"hi\"", "", " padded"], Parse(text.ToString()).Single());
    }

    [Fact]
    public void Booleans_are_written_as_one_and_zero()
    {
        Assert.Equal("1", DatabaseCsv.Normalize("Yes", DatabaseValueKind.Boolean, true));
        Assert.Null(DatabaseCsv.Normalize("", DatabaseValueKind.Text, true));
        Assert.Equal("", DatabaseCsv.Normalize("", DatabaseValueKind.Text, false));
    }

    // ---- the designer ----

    static DatabaseColumnDesign Column(string name, string type, bool notNull = false, bool key = false, string? @default = null, bool auto = false, bool unique = false)
        => new(name, type, notNull, key, @default, auto, unique);

    static DatabaseTableDesign New(string name, params DatabaseColumnDesign[] columns) => new(name, null, columns, [], [], []);

    [Theory]
    [InlineData(DatabaseEngineKind.Sqlite)]
    [InlineData(DatabaseEngineKind.SqlServer)]
    [InlineData(DatabaseEngineKind.PostgreSql)]
    public void A_form_that_is_not_a_table_says_why(DatabaseEngineKind engine)
    {
        Assert.Equal("The table needs a name.", TableDesigner.Wrong(New(" ", Column("a", "int")), engine));
        Assert.Equal("A table needs at least one column.", TableDesigner.Wrong(New("t"), engine));
        Assert.Contains("two columns called", TableDesigner.Wrong(New("t", Column("a", "int"), Column("A", "int")), engine));
        Assert.Contains("not a column", TableDesigner.Wrong(New("t", Column("a", "int")) with { Indexes = [new DatabaseIndexDesign("i", ["b"], false)] }, engine));
    }

    [Fact]
    public void Only_sqlite_takes_a_column_without_a_type_and_servers_number_whole_numbers()
    {
        Assert.Null(TableDesigner.Wrong(New("t", Column("a", "")), DatabaseEngineKind.Sqlite));
        Assert.Contains("needs a type", TableDesigner.Wrong(New("t", Column("a", "")), DatabaseEngineKind.PostgreSql));
        Assert.Null(TableDesigner.Wrong(New("t", Column("id", "bigint", key: true, auto: true)), DatabaseEngineKind.SqlServer));
        Assert.Contains("whole-number", TableDesigner.Wrong(New("t", Column("id", "text", key: true, auto: true)), DatabaseEngineKind.PostgreSql));
    }

    [Fact]
    public void Sqlite_numbers_only_its_integer_primary_key()
    {
        Assert.Contains("INTEGER PRIMARY KEY", TableDesigner.Wrong(New("t", Column("id", "INT", key: true, auto: true)), DatabaseEngineKind.Sqlite));
        Assert.Null(TableDesigner.Wrong(New("t", Column("id", "INTEGER", key: true, auto: true)), DatabaseEngineKind.Sqlite));
    }

    [Fact]
    public void A_new_table_carries_every_constraint()
    {
        var design = New("orders",
                Column("id", "INTEGER", key: true, auto: true),
                Column("code", "TEXT", notNull: true, unique: true),
                Column("made", "TEXT", @default: "CURRENT_TIMESTAMP"),
                Column("customer", "INTEGER"))
            with
            {
                Checks = [new DatabaseCheckDesign("code_long", "length(code) > 2")],
                ForeignKeys = [new DatabaseForeignKeyDesign(null, ["customer"], null, "customers", ["id"], "CASCADE")],
                Indexes = [new DatabaseIndexDesign("orders_customer", ["customer"], false)]
            };

        var script = TableDesigner.Sqlite(design, null, []);

        Assert.Null(script.Error);
        Assert.Contains("\"id\" INTEGER PRIMARY KEY AUTOINCREMENT", script.Sql);
        Assert.Contains("\"code\" TEXT NOT NULL UNIQUE", script.Sql);
        Assert.Contains("DEFAULT CURRENT_TIMESTAMP", script.Sql);
        Assert.Contains("CONSTRAINT \"code_long\" CHECK (length(code) > 2)", script.Sql);
        Assert.Contains("FOREIGN KEY (\"customer\") REFERENCES \"customers\" (\"id\") ON DELETE CASCADE", script.Sql);
        Assert.Contains("CREATE INDEX \"orders_customer\" ON \"orders\" (\"customer\");", script.Sql);
        Assert.DoesNotContain("PRIMARY KEY (", script.Sql);
    }

    [Fact]
    public void A_default_that_is_an_expression_is_parenthesised()
        => Assert.Contains("DEFAULT (datetime('now'))", TableDesigner.Sqlite(New("t", Column("made", "TEXT", @default: "datetime('now')")), null, []).Sql);

    [Fact]
    public void Renaming_a_table_alone_is_one_alter_and_an_unchanged_form_has_nothing_to_do()
    {
        var current = new DatabaseTable("t", "table", [new DatabaseColumn("a", "TEXT", false, false)], [], ["rowid"]);

        Assert.Equal("ALTER TABLE \"t\" RENAME TO \"u\";\n", TableDesigner.Sqlite(TableDesigner.DesignOf(current) with { Name = "u" }, current, []).Sql);
        Assert.Equal("Nothing has changed yet.", TableDesigner.Sqlite(TableDesigner.DesignOf(current), current, []).Error);
    }

    [Fact]
    public void An_untouched_index_is_put_back_with_the_words_that_made_it()
    {
        var current = new DatabaseTable("t", "table",
            [new DatabaseColumn("a", "TEXT", false, false)],
            [new DatabaseIndex("partial_a", ["a"], false, false, true)],
            ["rowid"]);

        var design = TableDesigner.DesignOf(current);
        design = design with { Columns = [.. design.Columns, Column("b", "INTEGER")] };

        var script = TableDesigner.Sqlite(design, current,
        [
            new TableDesigner.SqliteDependent("index", "partial_a", "CREATE INDEX partial_a ON t (a) WHERE a IS NOT NULL"),
            new TableDesigner.SqliteDependent("trigger", "keep", "CREATE TRIGGER keep AFTER INSERT ON t BEGIN SELECT 1; END")
        ]);

        Assert.Contains("CREATE INDEX partial_a ON t (a) WHERE a IS NOT NULL;", script.Sql);
        Assert.Contains("CREATE TRIGGER keep AFTER INSERT ON t BEGIN SELECT 1; END;", script.Sql);
        Assert.Contains("INSERT INTO \"t__rebuild\" (\"a\")\n    SELECT \"a\" FROM \"t\";", script.Sql);
        Assert.Contains("PRAGMA foreign_keys = off;", script.Sql);
    }

    [Fact]
    public void A_referential_action_that_is_not_one_is_refused_rather_than_spelled()
    {
        var design = New("t", Column("a", "int")) with
        {
            ForeignKeys = [new DatabaseForeignKeyDesign(null, ["a"], null, "u", ["id"], "CASCADE; DROP TABLE u")]
        };

        Assert.Contains("referential action", TableDesigner.Sqlite(design, null, []).Error);
    }
}
