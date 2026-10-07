using System.Globalization;
using System.Text;
using Shiny.AppDeviceBridge.Database.Client;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// CSV in and out of a database: reading a file record by record, guessing what it is, and writing a
/// table as one.
/// </summary>
/// <remarks>
/// <para>
/// RFC 4180 as spreadsheets actually write it: quoted fields may hold the delimiter, doubled quotes and
/// line breaks; a bare quote in the middle of an unquoted field is taken as text rather than refused,
/// because Excel writes those and nobody can fix the file from here. A byte-order mark is skipped.
/// </para>
/// <para>
/// Hand-written rather than a package: it is a hundred lines, and every CSV library brings a
/// type-mapping layer this has no use for - values go into a database as text and the engine reads
/// them through the column's own type, exactly as an edit does.
/// </para>
/// <para>
/// Pure, so detection, inference and quoting are tested without a database.
/// </para>
/// </remarks>
public static class DatabaseCsv
{
    static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>Records one at a time, each with the line of the file it started on.</summary>
    public static IEnumerable<(string[] Fields, long Line)> Read(TextReader reader, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var atFieldStart = true;
        var line = 1L;
        var recordLine = 1L;
        var any = false;

        int c;

        while ((c = reader.Read()) >= 0)
        {
            var ch = (char)c;

            if (ch == '﻿' && !any && field.Length == 0 && fields.Count == 0)
                continue;

            any = true;

            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                        line++;

                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"' && atFieldStart)
            {
                quoted = true;
                atFieldStart = false;
                continue;
            }

            if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
                continue;
            }

            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && reader.Peek() == '\n')
                    reader.Read();

                fields.Add(field.ToString());
                field.Clear();

                // a blank line is not a record of one empty field
                if (!(fields.Count == 1 && fields[0].Length == 0))
                    yield return (fields.ToArray(), recordLine);

                fields.Clear();
                atFieldStart = true;
                line++;
                recordLine = line;
                continue;
            }

            atFieldStart = false;
            field.Append(ch);
        }

        if (field.Length > 0 || fields.Count > 0 || quoted)
        {
            fields.Add(field.ToString());

            if (!(fields.Count == 1 && fields[0].Length == 0))
                yield return (fields.ToArray(), recordLine);
        }
    }

    /// <summary>
    /// The delimiter a sample was written with: whichever candidate splits its first lines into the
    /// same number of fields, most of them.
    /// </summary>
    public static char DetectDelimiter(string sample)
    {
        var best = ',';
        var bestScore = -1;

        foreach (var candidate in Candidates)
        {
            var counts = Read(new StringReader(sample), candidate).Take(20).Select(x => x.Fields.Length).ToList();

            if (counts.Count == 0 || counts[0] < 2)
                continue;

            // consistent beats plentiful: a comma inside prose splits lines unevenly
            var consistent = counts.Count(x => x == counts[0]);
            var score = consistent * 100 + counts[0];

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether the first record is a header: every field in it is text, they are all different, and
    /// in at least one column the records under it are something other than text - or, where
    /// everything is text, it has no empty field and nothing under it repeats it.
    /// </summary>
    public static bool DetectHeader(IReadOnlyList<string[]> records)
    {
        if (records.Count == 0)
            return false;

        var first = records[0];

        if (first.Any(String.IsNullOrWhiteSpace) || first.Distinct(StringComparer.OrdinalIgnoreCase).Count() != first.Length)
            return false;

        if (first.Any(x => Infer([x]) is not DatabaseValueKind.Text and not DatabaseValueKind.LongText))
            return false;

        if (records.Count == 1)
            return true;

        var rest = records.Skip(1).ToList();

        for (var i = 0; i < first.Length; i++)
        {
            var column = rest.Select(x => i < x.Length ? x[i] : null).ToList();

            if (Infer(column) is not DatabaseValueKind.Text and not DatabaseValueKind.LongText)
                return true;
        }

        // all text: a header does not reappear as data
        return !rest.Any(r => r.SequenceEqual(first, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What every value in a column can be read as - the narrowest kind they all fit, ignoring empty
    /// ones. Invariant culture throughout: a CSV is data, and "1,5" is two fields long before it is a
    /// number anywhere.
    /// </summary>
    public static DatabaseValueKind Infer(IEnumerable<string?> values)
    {
        var present = values.Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();

        if (present.Count == 0)
            return DatabaseValueKind.Text;

        if (present.All(IsBoolean))
            return DatabaseValueKind.Boolean;

        if (present.All(x => Int64.TryParse(x, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
        {
            // a leading zero is an identifier - a zip code, a phone number - and numbering it loses the zero
            return present.Any(x => x.Length > 1 && x.TrimStart('-', '+').StartsWith('0'))
                ? DatabaseValueKind.Text
                : DatabaseValueKind.Integer;
        }

        if (present.All(x => Decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                || Double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return DatabaseValueKind.Decimal;

        if (present.All(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            return DatabaseValueKind.Date;

        if (present.All(IsDateTime))
            return DatabaseValueKind.DateTime;

        if (present.All(x => Guid.TryParseExact(x, "D", out _)))
            return DatabaseValueKind.Guid;

        return present.Any(x => x.Length > 255 || x.Contains('\n')) ? DatabaseValueKind.LongText : DatabaseValueKind.Text;
    }

    static bool IsBoolean(string value)
        => value.ToLowerInvariant() is "true" or "false" or "yes" or "no";

    static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.FFFFFFFZ", "yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz"
    ];

    static bool IsDateTime(string value)
        => DateTimeOffset.TryParseExact(value, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);

    /// <summary>A value as an import writes it - booleans as 1/0, which every engine's boolean reads.</summary>
    public static string? Normalize(string? value, DatabaseValueKind kind, bool emptyIsNull)
    {
        if (value is null || (emptyIsNull && value.Length == 0))
            return null;

        if (kind == DatabaseValueKind.Boolean)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "yes" => "1",
                "false" or "no" => "0",
                _ => value
            };
        }

        return kind is DatabaseValueKind.Integer or DatabaseValueKind.Decimal ? value.Trim() : value;
    }

    /// <summary>
    /// A name for each column: the header's, made unique; or <c>column1</c>… when there is no header.
    /// </summary>
    public static string[] ColumnNames(string[]? header, int count)
    {
        var names = new string[count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < count; i++)
        {
            var name = header is not null && i < header.Length && !String.IsNullOrWhiteSpace(header[i])
                ? header[i].Trim()
                : $"column{i + 1}";

            var candidate = name;

            for (var n = 2; !used.Add(candidate); n++)
                candidate = $"{name}_{n}";

            names[i] = candidate;
        }

        return names;
    }

    // ---- writing ----

    /// <summary>One record, quoted where it has to be, ended with CRLF as RFC 4180 says.</summary>
    public static void WriteRecord(TextWriter writer, IEnumerable<string?> fields, char delimiter = ',')
    {
        var first = true;

        foreach (var field in fields)
        {
            if (!first)
                writer.Write(delimiter);

            first = false;

            // NULL is an empty field - the one spelling every reader agrees on
            if (field is null)
                continue;

            if (field.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 || field.StartsWith(' ') || field.EndsWith(' '))
                writer.Write('"' + field.Replace("\"", "\"\"") + '"');
            else
                writer.Write(field);
        }

        writer.Write("\r\n");
    }
}
