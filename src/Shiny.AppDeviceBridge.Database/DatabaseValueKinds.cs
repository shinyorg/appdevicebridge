using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// Which <see cref="DatabaseValueKind"/> a SQLite column's declared type is - and a result column's, from
/// the .NET type its reader hands back, which any ADO.NET driver can use.
/// </summary>
/// <remarks>
/// <para>
/// Decided on the device so the page never has to know each engine's type names. A datasheet chooses
/// its editor and its alignment from the answer and nothing else. A driver for another engine
/// classifies its own type names.
/// </para>
/// <para>
/// <b>SQLite has affinities, not types</b>, and follows its own documented rules (section 3.1 of
/// "Datatypes In SQLite"): a declared type containing INT is an integer, CHAR, CLOB or TEXT is text,
/// REAL, FLOA or DOUB is a real. Before those, the names people write for dates and booleans are
/// taken at their word - <c>BOOLEAN</c> and <c>DATETIME</c> have NUMERIC affinity to SQLite, but a
/// column somebody called a date is edited as one. None of this changes what SQLite stores.
/// </para>
/// </remarks>
public static class DatabaseValueKinds
{
    public static DatabaseValueKind Sqlite(string declared)
    {
        var type = declared.Trim().ToUpperInvariant();

        if (type is "" or "ANY")
            return DatabaseValueKind.Other;

        if (type.StartsWith("BOOL", StringComparison.Ordinal))
            return DatabaseValueKind.Boolean;

        if (type is "DATE")
            return DatabaseValueKind.Date;

        if (type.StartsWith("DATETIME", StringComparison.Ordinal) || type.StartsWith("TIMESTAMP", StringComparison.Ordinal))
            return DatabaseValueKind.DateTime;

        if (type is "TIME")
            return DatabaseValueKind.Time;

        if (type is "UUID" or "GUID" or "UNIQUEIDENTIFIER")
            return DatabaseValueKind.Guid;

        // the affinity rules, in SQLite's own order
        if (type.Contains("INT", StringComparison.Ordinal))
            return DatabaseValueKind.Integer;

        // TEXT is SQLite's ordinary string - every name and email is one - so it is Text, edited in the
        // cell; only a type that says it holds documents is edited in the zoom box
        if (type.Contains("CLOB", StringComparison.Ordinal) || type is "JSON" or "XML")
            return DatabaseValueKind.LongText;

        if (type.Contains("CHAR", StringComparison.Ordinal) || type.Contains("TEXT", StringComparison.Ordinal))
            return DatabaseValueKind.Text;

        if (type.Contains("BLOB", StringComparison.Ordinal))
            return DatabaseValueKind.Binary;

        if (type.Contains("REAL", StringComparison.Ordinal) || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal) || type.StartsWith("NUMERIC", StringComparison.Ordinal)
            || type.StartsWith("DECIMAL", StringComparison.Ordinal))
            return DatabaseValueKind.Decimal;

        return DatabaseValueKind.Other;
    }

    /// <summary>A result column's kind, from what its reader hands back - there is no declaration to read.</summary>
    public static DatabaseValueKind FromClr(Type? type)
    {
        if (type is null)
            return DatabaseValueKind.Other;

        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(bool))
            return DatabaseValueKind.Boolean;

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            return DatabaseValueKind.Integer;

        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
            return DatabaseValueKind.Decimal;

        if (type == typeof(DateTime))
            return DatabaseValueKind.DateTime;

        if (type == typeof(DateTimeOffset))
            return DatabaseValueKind.DateTimeOffset;

        if (type == typeof(DateOnly))
            return DatabaseValueKind.Date;

        if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
            return DatabaseValueKind.Time;

        if (type == typeof(Guid))
            return DatabaseValueKind.Guid;

        if (type == typeof(byte[]))
            return DatabaseValueKind.Binary;

        return type == typeof(string) ? DatabaseValueKind.Text : DatabaseValueKind.Other;
    }

    /// <summary>Whether a kind is a number, which the totals row can add up and the grid right-aligns.</summary>
    public static bool IsNumeric(DatabaseValueKind kind) => kind is DatabaseValueKind.Integer or DatabaseValueKind.Decimal;
}
