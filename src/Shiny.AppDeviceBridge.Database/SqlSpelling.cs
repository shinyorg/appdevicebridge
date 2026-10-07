using System.Text;
using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// How one engine spells the handful of things the datasheet's SQL is built from - an identifier, a
/// value read back through a column's type, a case-insensitive LIKE, a page.
/// </summary>
/// <remarks>
/// <para>
/// The base of <see cref="SqliteSpelling"/> and of any other driver's, and it exists so that
/// <see cref="RowView"/> - the WHERE and ORDER BY behind paging, sorting, filtering and search - is
/// written once for every engine rather than once per driver. Drivers still have almost nothing in
/// common below the contract; what they share is the shape of a statement, and that is all this is.
/// </para>
/// <para>
/// Every name that reaches <see cref="Quote"/> has already been looked up in the catalogue - the
/// doubling is for the table genuinely called <c>my"table</c>, not a defence against a caller.
/// </para>
/// </remarks>
public abstract class SqlSpelling
{
    public abstract DatabaseEngineKind Kind { get; }

    /// <summary>An identifier, quoted - with its closing quote doubled inside it.</summary>
    public abstract string Quote(string identifier);

    public string QualifiedName(string? schema, string name)
        => schema is null ? this.Quote(name) : $"{this.Quote(schema)}.{this.Quote(name)}";

    /// <summary>An expression giving the engine's own text for a column, as the grid shows it.</summary>
    /// <param name="column">The column, already quoted.</param>
    /// <param name="type">Its declared type, as <see cref="DatabaseColumn.Type"/> has it.</param>
    public abstract string TextOf(string column, string type);

    /// <summary>An expression reading a text parameter back as a value of the column's type.</summary>
    public abstract string FromText(string parameter, string type);

    /// <summary>
    /// A case-insensitive LIKE with a backslash escape - what a filter box and the search box mean.
    /// </summary>
    public virtual string Like(string text, string parameter) => $"{text} LIKE {parameter} ESCAPE '\\'";

    /// <summary>
    /// One page of a SELECT - <c>LIMIT … OFFSET</c>, or SQL Server's <c>OFFSET … FETCH</c>, which needs
    /// an ORDER BY to exist at all.
    /// </summary>
    public virtual string Page(string selectList, string from, string where, string orderBy, string skip, string take)
    {
        var sql = new StringBuilder($"SELECT {selectList} FROM {from}");

        if (where.Length > 0)
            sql.Append(" WHERE ").Append(where);

        if (orderBy.Length > 0)
            sql.Append(" ORDER BY ").Append(orderBy);

        return sql.Append($" LIMIT {take} OFFSET {skip}").ToString();
    }

    /// <summary>
    /// An aggregate over a column for the totals row, adjusted where the engine's plain spelling
    /// answers a different question - SQL Server's <c>AVG</c> of an int is an int.
    /// </summary>
    public virtual string Aggregate(DatabaseAggregate aggregate, string column, DatabaseValueKind kind)
        => aggregate switch
        {
            DatabaseAggregate.Count => $"COUNT({column})",
            DatabaseAggregate.Sum => $"SUM({column})",
            DatabaseAggregate.Average => $"AVG({column})",
            DatabaseAggregate.Min => $"MIN({column})",
            _ => $"MAX({column})"
        };

    /// <summary>
    /// The type name without its length or precision - <c>varchar</c> of <c>varchar(50)</c> - and
    /// lower-cased, for deciding how a value is spelled.
    /// </summary>
    public static string BaseType(string declared)
    {
        var bracket = declared.IndexOf('(');
        return (bracket < 0 ? declared : declared[..bracket]).Trim().ToLowerInvariant();
    }
}

/// <summary>
/// SQLite's spelling. Values need no reading back - SQLite applies the column's affinity to a bound
/// parameter in a comparison, so <c>'42'</c> against an INTEGER column is 42 - and its LIKE is
/// already case-insensitive for ASCII.
/// </summary>
public sealed class SqliteSpelling : SqlSpelling
{
    public static readonly SqliteSpelling Instance = new();

    public override DatabaseEngineKind Kind => DatabaseEngineKind.Sqlite;

    public override string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public override string TextOf(string column, string type) => $"CAST({column} AS TEXT)";

    public override string FromText(string parameter, string type) => parameter;
}

/// <summary>
/// The WHERE and ORDER BY behind one view of a table: its filters, its search and its sort.
/// </summary>
/// <remarks>
/// <para>
/// Every column a filter or a sort names is checked against the table's own columns before it is
/// quoted into the statement - a name the catalogue has never heard of is refused, not spelled - and
/// every value is a parameter. That is the whole reason the page sends a list of filters rather than
/// a WHERE clause.
/// </para>
/// <para>
/// <b>The key always ends the ORDER BY.</b> OFFSET paging is only paging if the order is total: sorted
/// on a column full of duplicates, two pages asked a second apart could each put a different one of
/// the ties on the boundary, and a row would be shown twice while another was never shown. The key
/// (SQLite's rowid, a server's primary key) breaks every tie.
/// </para>
/// <para>
/// Pure, so it is tested without a database.
/// </para>
/// </remarks>
public static class RowView
{
    public sealed record Built(string Where, string OrderBy, IReadOnlyList<(string Name, string? Value)> Parameters);

    /// <param name="keys">The expressions that name a row, already quoted - <c>rowid</c>, or the primary key's columns.</param>
    public static Built Build(
        SqlSpelling spelling,
        IReadOnlyList<DatabaseColumn> columns,
        DatabaseRowFilter[]? filters,
        string? search,
        DatabaseSort[]? sort,
        IReadOnlyList<string> keys
    )
    {
        var parameters = new List<(string, string?)>();
        var where = new List<string>();

        string Add(string? value)
        {
            var name = $"@f{parameters.Count}";
            parameters.Add((name, value));
            return name;
        }

        foreach (var filter in filters ?? [])
        {
            var column = Find(columns, filter.Column);
            var quoted = spelling.Quote(column.Name);
            var text = spelling.TextOf(quoted, column.Type);

            where.Add(filter.Operator switch
            {
                DatabaseFilterOperator.IsNull => $"{quoted} IS NULL",
                DatabaseFilterOperator.IsNotNull => $"{quoted} IS NOT NULL",

                DatabaseFilterOperator.Equal => $"{quoted} = {spelling.FromText(Add(filter.Value), column.Type)}",

                // NULL is "not equal" to anything as a person reads a filter, though not as SQL does -
                // a filter of "not London" that also hid every row with no city would be hiding rows
                // nobody asked to hide.
                DatabaseFilterOperator.NotEqual =>
                    $"({quoted} IS NULL OR {quoted} <> {spelling.FromText(Add(filter.Value), column.Type)})",

                DatabaseFilterOperator.Greater => $"{quoted} > {spelling.FromText(Add(filter.Value), column.Type)}",
                DatabaseFilterOperator.GreaterOrEqual => $"{quoted} >= {spelling.FromText(Add(filter.Value), column.Type)}",
                DatabaseFilterOperator.Less => $"{quoted} < {spelling.FromText(Add(filter.Value), column.Type)}",
                DatabaseFilterOperator.LessOrEqual => $"{quoted} <= {spelling.FromText(Add(filter.Value), column.Type)}",

                DatabaseFilterOperator.Contains => spelling.Like(text, Add($"%{EscapeLike(filter.Value)}%")),
                DatabaseFilterOperator.NotContains =>
                    $"({quoted} IS NULL OR NOT ({spelling.Like(text, Add($"%{EscapeLike(filter.Value)}%"))}))",
                DatabaseFilterOperator.StartsWith => spelling.Like(text, Add($"{EscapeLike(filter.Value)}%")),
                DatabaseFilterOperator.EndsWith => spelling.Like(text, Add($"%{EscapeLike(filter.Value)}")),

                _ => throw new DatabaseRefusal("That is not a filter this app knows.")
            });
        }

        if (!String.IsNullOrWhiteSpace(search))
        {
            // Binary columns are left out: their text is a description or hex, and a search for "ab"
            // matching every picture whose bytes happen to spell it is noise.
            var searched = columns
                .Where(x => x.Kind != DatabaseValueKind.Binary)
                .Select(x => spelling.TextOf(spelling.Quote(x.Name), x.Type))
                .ToList();

            if (searched.Count > 0)
            {
                var parameter = Add($"%{EscapeLike(search.Trim())}%");
                where.Add("(" + String.Join(" OR ", searched.Select(x => spelling.Like(x, parameter))) + ")");
            }
        }

        var order = new List<string>();
        var ordered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var by in sort ?? [])
        {
            var column = Find(columns, by.Column);
            var quoted = spelling.Quote(column.Name);

            if (ordered.Add(quoted))
                order.Add($"{quoted} {(by.Descending ? "DESC" : "ASC")}");
        }

        foreach (var key in keys)
        {
            if (ordered.Add(key))
                order.Add(key);
        }

        return new Built(String.Join(" AND ", where), String.Join(", ", order), parameters);
    }

    static DatabaseColumn Find(IReadOnlyList<DatabaseColumn> columns, string name)
        => columns.FirstOrDefault(x => String.Equals(x.Name, name, StringComparison.Ordinal))
            ?? throw new DatabaseRefusal($"There is no column called '{name}'.");

    /// <summary>
    /// A value made literal inside a LIKE pattern: its own <c>%</c>, <c>_</c> and backslash, and SQL
    /// Server's <c>[</c>, each preceded by the backslash the pattern's ESCAPE names.
    /// </summary>
    public static string EscapeLike(string? value)
    {
        var escaped = new StringBuilder();

        foreach (var c in value ?? "")
        {
            if (c is '\\' or '%' or '_' or '[')
                escaped.Append('\\');

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
