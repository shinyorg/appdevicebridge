using System.Text;
using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// Turns a table as a designer wants it into the DDL that makes it so, compared with the table as it stands - SQLite's here;
/// a driver for another engine writes its own from the same <see cref="DatabaseTableDesign"/>, and can share
/// <see cref="Wrong"/> and <see cref="DesignOf"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure.</b> Everything it needs to know about the table as it is arrives as arguments - the catalogue's
/// <see cref="DatabaseTable"/> and SQLite's dependents out of <c>sqlite_master</c> - so the SQL is tested without a file,
/// and the driver only has to gather those facts and run what comes back.
/// </para>
/// <para>
/// <b>SQLite rebuilds.</b> It cannot alter a column in place, so any change to its columns or constraints is SQLite's own
/// documented rebuild (copy into a new table, drop, rename, put the indexes and triggers back). Two cheap cases are
/// recognised first so they never cost a rebuild: a change only to the indexes, and a rename and nothing else.
/// </para>
/// <para>
/// <b>What the reader approves is what runs.</b> The answer is text, previewed before it is applied, and applying it is
/// running that text - see <see cref="PreviewDatabaseDesign"/>.
/// </para>
/// </remarks>
public static class TableDesigner
{
    /// <summary>The SQLite catalogue rows the rebuild needs: every index and trigger on the table, in the words that made them.</summary>
    public sealed record SqliteDependent(string Type, string Name, string Sql);

    static DatabaseDesignScript Refuse(string why) => new("", [], why);

    // ---- what is wrong with a form ----

    /// <summary>What makes a design not a table yet, or null.</summary>
    public static string? Wrong(DatabaseTableDesign design, DatabaseEngineKind engine)
    {
        if (String.IsNullOrWhiteSpace(design.Name))
            return "The table needs a name.";

        if (design.Columns.Length == 0)
            return "A table needs at least one column.";

        if (design.Columns.Any(x => String.IsNullOrWhiteSpace(x.Name)))
            return "Every column needs a name.";

        if (Duplicate(design.Columns.Select(x => x.Name)) is { } twice)
            return $"There are two columns called '{twice}'.";

        if (engine != DatabaseEngineKind.Sqlite && design.Columns.FirstOrDefault(x => String.IsNullOrWhiteSpace(x.Type)) is { } untyped)
            return $"'{untyped.Name.Trim()}' needs a type - only SQLite takes a column without one.";

        var names = design.Columns.Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = design.Columns.Where(x => x.PrimaryKey).ToArray();

        if (design.Columns.Count(x => x.AutoIncrement) > 1)
            return "Only one column can be numbered automatically.";

        if (design.Columns.FirstOrDefault(x => x.AutoIncrement) is { } numbered)
        {
            switch (engine)
            {
                // SQLite's AUTOINCREMENT exists only on the rowid alias, which is exactly one
                // INTEGER PRIMARY KEY column - spelled INTEGER, not INT
                case DatabaseEngineKind.Sqlite when keys.Length != 1 || keys[0] != numbered
                    || !String.Equals(numbered.Type.Trim(), "INTEGER", StringComparison.OrdinalIgnoreCase):
                    return $"SQLite numbers only a table's one INTEGER PRIMARY KEY column - make '{numbered.Name.Trim()}' that, or untick autoincrement.";

                case not DatabaseEngineKind.Sqlite when !IsWholeNumber(numbered.Type):
                    return $"'{numbered.Name.Trim()}' is numbered automatically, so it needs a whole-number type.";
            }
        }

        if (Duplicate(design.Indexes.Select(x => x.Name)) is { } index)
            return $"There are two indexes called '{index}'.";

        foreach (var ix in design.Indexes)
        {
            if (String.IsNullOrWhiteSpace(ix.Name))
                return "Every index needs a name.";

            if (ix.Columns.Length == 0)
                return $"'{ix.Name.Trim()}' has no columns - tick at least one, or remove it.";

            if (ix.Columns.FirstOrDefault(x => !names.Contains(x)) is { } missing)
                return $"'{ix.Name.Trim()}' names '{missing}', which is not a column of this table.";
        }

        foreach (var fk in design.ForeignKeys)
        {
            if (fk.Columns.Length == 0 || String.IsNullOrWhiteSpace(fk.ReferencedTable))
                return "A foreign key needs its columns and the table it points at.";

            if (fk.Columns.FirstOrDefault(x => !names.Contains(x)) is { } missing)
                return $"A foreign key names '{missing}', which is not a column of this table.";

            if (fk.ReferencedColumns.Length > 0 && fk.ReferencedColumns.Length != fk.Columns.Length)
                return $"The foreign key on {String.Join(", ", fk.Columns)} names {fk.Columns.Length} column(s) here and {fk.ReferencedColumns.Length} in {fk.ReferencedTable}.";

            if (engine != DatabaseEngineKind.Sqlite && fk.ReferencedColumns.Length == 0)
                return $"The foreign key on {String.Join(", ", fk.Columns)} needs the columns of {fk.ReferencedTable} it points at.";
        }

        if (design.Checks.Any(x => String.IsNullOrWhiteSpace(x.Expression)))
            return "A check needs an expression.";

        return null;
    }

    /// <summary>Whether a server type can be numbered - an integer of any width, or a decimal or numeric, which SQL Server's IDENTITY also takes.</summary>
    static bool IsWholeNumber(string type)
        => SqlSpelling.BaseType(type) is "tinyint" or "smallint" or "int" or "integer" or "bigint" or "int2" or "int4" or "int8"
            or "serial" or "bigserial" or "smallserial" or "decimal" or "numeric";

    static string? Duplicate(IEnumerable<string> names)
        => names
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1)?.Key;

    // ---- the form, seeded from what is there ----

    /// <summary>
    /// A table as a design - what the form opens on, and what "unchanged" is measured against.
    /// </summary>
    /// <remarks>
    /// A column is UNIQUE when an index the engine made for a constraint covers it and nothing else,
    /// and is not the primary key's. That is how a column-level UNIQUE reads back on all three engines,
    /// so the form and the device agree without either parsing anything.
    /// </remarks>
    public static DatabaseTableDesign DesignOf(DatabaseTable table)
    {
        var unique = UniqueColumns(table);

        return new DatabaseTableDesign(
            table.Name,
            table.Schema,
            table.Columns
                .Select(c => new DatabaseColumnDesign(
                    c.Name,
                    c.Type == "any" ? "" : c.Type,
                    c.NotNull,
                    c.PrimaryKey,
                    c.Default,
                    c.AutoIncrement,
                    unique.Contains(c.Name),
                    c.Name))
                .ToArray(),
            table.Indexes
                .Where(x => !x.Automatic)
                .Select(x => new DatabaseIndexDesign(x.Name, x.Columns, x.Unique, x.Name))
                .ToArray(),
            (table.ForeignKeys ?? [])
                .Select((x, i) => new DatabaseForeignKeyDesign(x.Name, x.Columns, x.ReferencedSchema, x.ReferencedTable, x.ReferencedColumns, x.OnDelete, x.OnUpdate, x.Name ?? $"#{i}"))
                .ToArray(),
            (table.Checks ?? [])
                .Select((x, i) => new DatabaseCheckDesign(x.Name, x.Expression, x.Name ?? $"#{i}"))
                .ToArray(),
            table.Name,
            table.Schema
        );
    }

    /// <summary>The columns an engine-made unique index covers alone - a column-level UNIQUE.</summary>
    public static HashSet<string> UniqueColumns(DatabaseTable table)
    {
        var key = table.Columns.Where(x => x.PrimaryKey).Select(x => x.Name).ToArray();

        return table.Indexes
            .Where(x => x is { Automatic: true, Unique: true, Partial: false, Columns.Length: 1 } && !(key.Length == 1 && key[0] == x.Columns[0]))
            .Select(x => x.Columns[0])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The engine-made unique index for one column, whose name is the constraint's on a server.</summary>
    static DatabaseIndex? UniqueIndexFor(DatabaseTable table, string column)
        => table.Indexes.FirstOrDefault(x => x is { Automatic: true, Unique: true, Columns.Length: 1 } && x.Columns[0] == column
            && !(table.KeyColumns.Length == 1 && table.KeyColumns[0] == column && table.Columns.Any(c => c.Name == column && c.PrimaryKey)));

    /// <summary>The index behind the primary key, whose name is the constraint's on a server.</summary>
    static DatabaseIndex? PrimaryIndexOf(DatabaseTable table)
    {
        var key = table.Columns.Where(x => x.PrimaryKey).Select(x => x.Name).ToArray();

        return key.Length == 0
            ? null
            : table.Indexes.FirstOrDefault(x => x is { Automatic: true, Unique: true } && x.Columns.SequenceEqual(key, StringComparer.Ordinal));
    }

    // ---- comparing ----

    static bool Same(string? a, string? b) => String.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);

    static bool SameType(string? a, string? b) => String.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    static bool SameColumn(DatabaseColumnDesign a, DatabaseColumnDesign b)
        => Same(a.Name, b.Name) && SameType(a.Type, b.Type) && a.NotNull == b.NotNull && a.PrimaryKey == b.PrimaryKey
            && Same(a.Default, b.Default) && a.AutoIncrement == b.AutoIncrement && a.Unique == b.Unique;

    static bool SameIndex(DatabaseIndexDesign a, DatabaseIndexDesign b)
        => Same(a.Name, b.Name) && a.Unique == b.Unique && a.Columns.Select(x => x.Trim()).SequenceEqual(b.Columns.Select(x => x.Trim()), StringComparer.Ordinal);

    static bool SameKey(DatabaseForeignKeyDesign a, DatabaseForeignKeyDesign b)
        => Same(a.Name, b.Name) && a.Columns.SequenceEqual(b.Columns, StringComparer.Ordinal)
            && Same(a.ReferencedSchema, b.ReferencedSchema) && Same(a.ReferencedTable, b.ReferencedTable)
            && a.ReferencedColumns.SequenceEqual(b.ReferencedColumns, StringComparer.Ordinal)
            && String.Equals(a.OnDelete.Trim(), b.OnDelete.Trim(), StringComparison.OrdinalIgnoreCase)
            && String.Equals(a.OnUpdate.Trim(), b.OnUpdate.Trim(), StringComparison.OrdinalIgnoreCase);

    static bool SameCheck(DatabaseCheckDesign a, DatabaseCheckDesign b) => Same(a.Name, b.Name) && Same(a.Expression, b.Expression);

    static bool ColumnsUnchanged(DatabaseTableDesign now, DatabaseTableDesign was)
        => now.Columns.Length == was.Columns.Length && now.Columns.Zip(was.Columns).All(x => Same(x.First.Original, x.Second.Original) && SameColumn(x.First, x.Second));

    static bool KeysUnchanged(DatabaseTableDesign now, DatabaseTableDesign was)
        => now.ForeignKeys.Length == was.ForeignKeys.Length && now.ForeignKeys.All(x => was.ForeignKeys.Any(y => Same(x.Original, y.Original) && SameKey(x, y)));

    static bool ChecksUnchanged(DatabaseTableDesign now, DatabaseTableDesign was)
        => now.Checks.Length == was.Checks.Length && now.Checks.All(x => was.Checks.Any(y => Same(x.Original, y.Original) && SameCheck(x, y)));

    // ---- SQLite ----

    public static DatabaseDesignScript Sqlite(DatabaseTableDesign design, DatabaseTable? current, IReadOnlyList<SqliteDependent> dependents)
    {
        if (Wrong(design, DatabaseEngineKind.Sqlite) is { } wrong)
            return Refuse(wrong);

        try
        {
            return SqliteScriptFor(design, current, dependents);
        }
        catch (DatabaseRefusal refusal)
        {
            return Refuse(refusal.Message);
        }
    }

    static DatabaseDesignScript SqliteScriptFor(DatabaseTableDesign design, DatabaseTable? current, IReadOnlyList<SqliteDependent> dependents)
    {
        var q = SqliteSpelling.Instance;
        var name = design.Name.Trim();

        if (current is null)
        {
            var script = new StringBuilder()
                .Append("CREATE TABLE ").Append(q.Quote(name)).Append(" (\n")
                .Append(SqliteDefinition(design)).Append("\n);\n");

            foreach (var index in design.Indexes)
                script.Append('\n').Append(CreateIndex(q, index, null, name)).Append('\n');

            return new DatabaseDesignScript(script.ToString(), [], null);
        }

        var was = DesignOf(current);
        var renamed = !Same(name, current.Name);
        var columnsSame = ColumnsUnchanged(design, was) && KeysUnchanged(design, was) && ChecksUnchanged(design, was);
        var indexChanges = IndexChanges(q, design, current, null, renamed ? name : current.Name);

        if (columnsSame)
        {
            var script = new StringBuilder();

            // ALTER TABLE … RENAME TO is a real statement in SQLite and moves the indexes and triggers
            // with the table - no rebuild for a new name alone
            if (renamed)
                script.Append("ALTER TABLE ").Append(q.Quote(current.Name)).Append(" RENAME TO ").Append(q.Quote(name)).Append(";\n");

            script.Append(indexChanges);

            return script.Length == 0
                ? Refuse("Nothing has changed yet.")
                : new DatabaseDesignScript(script.ToString(), renamed ? [] : ["Index changes only - the table itself is left alone."], null);
        }

        return SqliteRebuild(design, current, dependents, was);
    }

    /// <summary>The column list and table constraints of a CREATE TABLE, shared by a new table and a rebuilt one.</summary>
    static string SqliteDefinition(DatabaseTableDesign design)
    {
        var q = SqliteSpelling.Instance;
        var lines = new List<string>();
        var keys = design.Columns.Where(x => x.PrimaryKey).ToArray();

        // AUTOINCREMENT is only legal written inline, on the column: "id INTEGER PRIMARY KEY
        // AUTOINCREMENT". The table-level PRIMARY KEY clause cannot carry it.
        var inlineKey = keys.Length == 1 && keys[0].AutoIncrement;

        foreach (var column in design.Columns)
        {
            var line = new StringBuilder("    ").Append(q.Quote(column.Name.Trim()));

            if (!String.IsNullOrWhiteSpace(column.Type))
                line.Append(' ').Append(column.Type.Trim());

            if (inlineKey && column.PrimaryKey)
                line.Append(" PRIMARY KEY AUTOINCREMENT");

            if (column.NotNull)
                line.Append(" NOT NULL");

            if (column.Unique && !column.PrimaryKey)
                line.Append(" UNIQUE");

            // written as typed: a default is an expression - 0, '', CURRENT_TIMESTAMP - and quoting it
            // here would turn every one of those into the string of its own name
            if (!String.IsNullOrWhiteSpace(column.Default))
                line.Append(" DEFAULT ").Append(Parenthesised(column.Default.Trim()));

            lines.Add(line.ToString());
        }

        // a table-level clause even for one column, which still makes an INTEGER column the alias for
        // the rowid the way the inline form does - and is the only form that can name two
        if (keys.Length > 0 && !inlineKey)
            lines.Add($"    PRIMARY KEY ({String.Join(", ", keys.Select(x => q.Quote(x.Name.Trim())))})");

        foreach (var check in design.Checks)
        {
            lines.Add(String.IsNullOrWhiteSpace(check.Name)
                ? $"    CHECK ({check.Expression.Trim()})"
                : $"    CONSTRAINT {q.Quote(check.Name.Trim())} CHECK ({check.Expression.Trim()})");
        }

        foreach (var fk in design.ForeignKeys)
            lines.Add("    " + ForeignKeyClause(q, fk, sqlite: true));

        return String.Join(",\n", lines);
    }

    /// <summary>
    /// A default as SQLite will take it. SQLite accepts a literal, a signed number or a parenthesised
    /// expression after DEFAULT - so anything that is not obviously one of the first two is wrapped,
    /// which is what makes <c>CURRENT_TIMESTAMP</c> and <c>(datetime('now'))</c> both work as typed.
    /// </summary>
    static string Parenthesised(string expression)
    {
        if (expression.StartsWith('(') || expression.StartsWith('\'')
            || Double.TryParse(expression, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            || expression.ToUpperInvariant() is "NULL" or "TRUE" or "FALSE" or "CURRENT_TIMESTAMP" or "CURRENT_DATE" or "CURRENT_TIME")
            return expression;

        return $"({expression})";
    }

    static string ForeignKeyClause(SqlSpelling q, DatabaseForeignKeyDesign fk, bool sqlite)
    {
        var clause = new StringBuilder();

        if (!String.IsNullOrWhiteSpace(fk.Name))
            clause.Append("CONSTRAINT ").Append(q.Quote(fk.Name.Trim())).Append(' ');

        clause.Append("FOREIGN KEY (").Append(String.Join(", ", fk.Columns.Select(x => q.Quote(x.Trim())))).Append(") REFERENCES ");

        // SQLite's REFERENCES takes a bare table name: a schema there would be an attached database
        clause.Append(sqlite ? q.Quote(fk.ReferencedTable.Trim()) : q.QualifiedName(Blank(fk.ReferencedSchema), fk.ReferencedTable.Trim()));

        if (fk.ReferencedColumns.Length > 0)
            clause.Append(" (").Append(String.Join(", ", fk.ReferencedColumns.Select(x => q.Quote(x.Trim())))).Append(')');

        if (Action(fk.OnDelete) is { } onDelete)
            clause.Append(" ON DELETE ").Append(onDelete);

        if (Action(fk.OnUpdate) is { } onUpdate)
            clause.Append(" ON UPDATE ").Append(onUpdate);

        return clause.ToString();
    }

    static string? Blank(string? text) => String.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>A referential action as written, or null for NO ACTION, which is what saying nothing means.</summary>
    static string? Action(string? action)
    {
        var said = (action ?? "").Trim().ToUpperInvariant();

        return said switch
        {
            "" or "NO ACTION" => null,
            "CASCADE" or "SET NULL" or "SET DEFAULT" or "RESTRICT" => said,
            _ => throw new DatabaseRefusal($"'{action}' is not a referential action - use CASCADE, SET NULL, SET DEFAULT, RESTRICT or NO ACTION.")
        };
    }

    static string CreateIndex(SqlSpelling q, DatabaseIndexDesign index, string? schema, string table)
        => $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX {q.Quote(index.Name.Trim())} ON "
            + $"{q.QualifiedName(schema, table)} ({String.Join(", ", index.Columns.Select(x => q.Quote(x.Trim())))});";

    static string DropIndex(SqlSpelling q, string name, string? schema, string table)
        => q.Kind switch
        {
            DatabaseEngineKind.SqlServer => $"DROP INDEX {q.Quote(name)} ON {q.QualifiedName(schema, table)};",
            DatabaseEngineKind.PostgreSql => $"DROP INDEX {q.QualifiedName(schema, name)};",
            _ => $"DROP INDEX {q.Quote(name)};"
        };

    /// <summary>
    /// The DROPs and CREATEs that turn a table's indexes into what the form says, without touching the
    /// table. A changed index is a drop and a create - there is no ALTER INDEX that changes columns -
    /// and the drop comes first, since the name it frees is the name the create takes.
    /// </summary>
    static string IndexChanges(SqlSpelling q, DatabaseTableDesign design, DatabaseTable current, string? schema, string table)
    {
        var script = new StringBuilder();

        foreach (var index in current.Indexes.Where(x => !x.Automatic))
        {
            var kept = design.Indexes.FirstOrDefault(x => Same(x.Original, index.Name));

            if (kept is null || !SameIndex(kept, new DatabaseIndexDesign(index.Name, index.Columns, index.Unique, index.Name)))
                script.Append(DropIndex(q, index.Name, schema, table)).Append('\n');
        }

        foreach (var index in design.Indexes)
        {
            var was = current.Indexes.FirstOrDefault(x => !x.Automatic && Same(index.Original, x.Name));

            if (was is null || !SameIndex(index, new DatabaseIndexDesign(was.Name, was.Columns, was.Unique, was.Name)))
                script.Append(CreateIndex(q, index, schema, table)).Append('\n');
        }

        return script.ToString();
    }

    /// <summary>
    /// SQLite's documented procedure for changing a table's columns, as a script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every line is here because leaving it out breaks something: foreign keys off so the DROP of the
    /// old table does not cascade into other tables' rows; legacy ALTER so the rename in the middle
    /// does not trip over a view that names the table while it is briefly gone; one transaction so a
    /// failure half way leaves the table as it was; and the indexes and triggers put back afterwards,
    /// because dropping a table takes them with it and nothing else remembers what they were.
    /// </para>
    /// <para>
    /// An index the form did not touch is put back with its own text out of <c>sqlite_master</c> - it
    /// can be partial, over an expression, or collated, and only the statement that made it says so.
    /// One added or changed is written from the form; one removed is simply not put back.
    /// </para>
    /// </remarks>
    static DatabaseDesignScript SqliteRebuild(DatabaseTableDesign design, DatabaseTable current, IReadOnlyList<SqliteDependent> dependents, DatabaseTableDesign was)
    {
        var q = SqliteSpelling.Instance;
        var original = current.Name;
        var target = design.Name.Trim();
        var staging = $"{original}__rebuild";
        var notes = new List<string> { $"Rebuilds {original}: SQLite cannot change a column in place, so the table is copied into a new one, dropped and renamed." };

        var script = new StringBuilder()
            .Append("PRAGMA foreign_keys = off;\n")
            .Append("PRAGMA legacy_alter_table = on;\n\n")
            .Append("BEGIN TRANSACTION;\n\n")
            .Append("CREATE TABLE ").Append(q.Quote(staging)).Append(" (\n")
            .Append(SqliteDefinition(design)).Append("\n);\n\n");

        // only the columns that existed before can be copied; one being added takes its default
        var carried = design.Columns.Where(x => x.Original is { } o && current.Columns.Any(c => c.Name == o)).ToArray();

        if (carried.Length > 0)
        {
            script.Append("INSERT INTO ").Append(q.Quote(staging)).Append(" (")
                .Append(String.Join(", ", carried.Select(x => q.Quote(x.Name.Trim()))))
                .Append(")\n    SELECT ")
                .Append(String.Join(", ", carried.Select(x => q.Quote(x.Original!))))
                .Append(" FROM ").Append(q.Quote(original)).Append(";\n\n");
        }
        else
        {
            notes.Add($"No column here existed before, so nothing is copied: every record in {original} will be gone.");
        }

        var dropped = current.Columns.Where(c => !design.Columns.Any(d => d.Original == c.Name)).Select(c => c.Name).ToArray();

        if (dropped.Length > 0)
            notes.Add($"Drops {String.Join(", ", dropped)} and everything in {(dropped.Length == 1 ? "it" : "them")}.");

        script.Append("DROP TABLE ").Append(q.Quote(original)).Append(";\n\n")
            .Append("ALTER TABLE ").Append(q.Quote(staging)).Append(" RENAME TO ").Append(q.Quote(target)).Append(";\n\n");

        if (!Same(target, original))
            notes.Add($"Also renames the table. In legacy mode, views, triggers and foreign keys elsewhere that name {original} are NOT updated to say {target}.");

        foreach (var index in design.Indexes)
        {
            var existing = current.Indexes.FirstOrDefault(x => !x.Automatic && Same(index.Original, x.Name));
            var asItWas = existing is not null && SameIndex(index, new DatabaseIndexDesign(existing.Name, existing.Columns, existing.Unique, existing.Name))
                ? dependents.FirstOrDefault(x => x.Type == "index" && x.Name == existing.Name)?.Sql
                : null;

            // untouched: back with the words that made it; otherwise written from the form
            script.Append(asItWas is not null ? asItWas.TrimEnd().TrimEnd(';') + ";" : CreateIndex(q, index, null, target)).Append('\n');
        }

        foreach (var trigger in dependents.Where(x => x.Type == "trigger"))
            script.Append(trigger.Sql.TrimEnd().TrimEnd(';')).Append(";\n");

        script.Append("\nCOMMIT;\n\nPRAGMA legacy_alter_table = off;\nPRAGMA foreign_keys = on;\n");

        // the columns are what they were, and only a constraint moved: still a rebuild, said plainly
        if (ColumnsUnchanged(design, was))
            notes.Add("Only constraints changed, but SQLite has no ALTER for a constraint - so this is still a rebuild.");

        return new DatabaseDesignScript(script.ToString(), notes.ToArray(), null);
    }
}
