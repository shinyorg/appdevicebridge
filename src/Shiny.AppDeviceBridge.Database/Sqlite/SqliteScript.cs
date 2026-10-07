using System.Text;
using SQLitePCL;

namespace Shiny.AppDeviceBridge.Database;

/// <summary>
/// A SQLite script cut into its statements, each with the line it starts on - and the few things the
/// app has to read out of a table's own CREATE statement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Split by SQLite itself.</b> <c>sqlite3_complete</c> is the engine's own answer to "is this a
/// whole statement", and it is the only splitter that gets a trigger right: a <c>CREATE TRIGGER</c>
/// has semicolons inside its <c>BEGIN … END</c>, and every hand-rolled splitter cuts it into pieces
/// that are each a syntax error. So the text is walked to each semicolon outside a string or a comment
/// and SQLite is asked whether what has been gathered is complete.
/// </para>
/// <para>
/// Running statements one at a time rather than the whole script as one command changes nothing about
/// what runs - Microsoft.Data.Sqlite prepares a multi-statement command one statement at a time too -
/// and gains three things: each result set and each statement's row count is its own, an error names
/// the statement (and so the line) it came from, and Explain explains every statement instead of
/// explaining the first and running the rest.
/// </para>
/// </remarks>
public static class SqliteScript
{
    public sealed record Statement(string Text, int Line);

    /// <summary>
    /// The statements, in order. Text that is only whitespace and comments is not a statement and is
    /// dropped; a last statement without its semicolon is kept.
    /// </summary>
    /// <remarks>
    /// Needs SQLite's native library loaded, which any open connection has done - the engine only
    /// splits a script once it has one.
    /// </remarks>
    public static IReadOnlyList<Statement> Split(string script)
    {
        var statements = new List<Statement>();
        var start = 0;
        var i = 0;

        while (i < script.Length)
        {
            var c = script[i];

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(script, i, c);
                continue;
            }

            if (c == '[')
            {
                var close = script.IndexOf(']', i + 1);
                i = close < 0 ? script.Length : close + 1;
                continue;
            }

            if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                var end = script.IndexOf('\n', i);
                i = end < 0 ? script.Length : end + 1;
                continue;
            }

            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? script.Length : end + 2;
                continue;
            }

            i++;

            if (c == ';' && raw.sqlite3_complete(script[start..i]) != 0)
            {
                Add(statements, script, start, i);
                start = i;
            }
        }

        if (start < script.Length)
            Add(statements, script, start, script.Length);

        return statements;
    }

    static void Add(List<Statement> statements, string script, int start, int end)
    {
        var text = script[start..end];

        if (IsBlank(text))
            return;

        // From the statement's first real character: the blank lines and comments between it and
        // the previous semicolon are not part of it, and its line is the one an error is reported
        // against - not the line of that previous semicolon.
        var first = start + FirstToken(text);
        text = script[first..end];
        var line = 1;

        for (var i = 0; i < first; i++)
        {
            if (script[i] == '\n')
                line++;
        }

        statements.Add(new Statement(text.Trim(), line));
    }

    /// <summary>Whether a piece of script is nothing but whitespace, comments and semicolons.</summary>
    public static bool IsBlank(string text) => FirstToken(text) >= text.Length;

    /// <summary>Where the first character that is not whitespace, a comment or a stray semicolon is.</summary>
    static int FirstToken(string text)
    {
        var i = 0;

        while (i < text.Length)
        {
            if (Char.IsWhiteSpace(text[i]) || text[i] == ';')
            {
                i++;
            }
            else if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                return i;
            }
        }

        return i;
    }

    static int SkipQuoted(string text, int at, char quote)
    {
        var i = at + 1;

        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                // a doubled quote is the quote character itself, not the end
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    // ---- reading a table's own CREATE statement ----

    /// <summary>
    /// The CHECK constraints in a CREATE TABLE, with their names where they were given one.
    /// </summary>
    /// <remarks>
    /// There is no pragma for these - SQLite keeps them only in the statement that made the table -
    /// so the statement is tokenised just far enough to find each <c>CHECK (</c> outside a string and
    /// take everything to its matching parenthesis. A rebuild that did not carry them would drop them
    /// silently, which is the one thing a rebuild must not do.
    /// </remarks>
    public static IReadOnlyList<(string? Name, string Expression)> Checks(string? createTable)
    {
        var found = new List<(string?, string)>();

        if (String.IsNullOrEmpty(createTable))
            return found;

        var tokens = Tokens(createTable);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Is("CHECK") || i + 1 >= tokens.Count || tokens[i + 1].Text != "(")
                continue;

            var open = tokens[i + 1].Start;
            var close = MatchParenthesis(createTable, open);

            if (close < 0)
                break;

            string? name = null;

            if (i >= 2 && tokens[i - 2].Is("CONSTRAINT"))
                name = Unquote(tokens[i - 1].Text);

            found.Add((name, createTable[(open + 1)..close].Trim()));

            while (i + 1 < tokens.Count && tokens[i + 1].Start <= close)
                i++;
        }

        return found;
    }

    /// <summary>Whether a CREATE TABLE asks for <c>AUTOINCREMENT</c> - which only its INTEGER PRIMARY KEY can have.</summary>
    public static bool HasAutoIncrement(string? createTable)
        => createTable is not null && Tokens(createTable).Any(x => x.Is("AUTOINCREMENT"));

    /// <summary>
    /// The columns a CREATE TABLE declares UNIQUE on their own - either inline on the column or as a
    /// one-column table constraint.
    /// </summary>
    public static IReadOnlyList<string> UniqueColumns(string? createTable)
    {
        var found = new List<string>();

        if (String.IsNullOrEmpty(createTable))
            return found;

        var open = createTable.IndexOf('(');
        var close = open < 0 ? -1 : MatchParenthesis(createTable, open);

        if (close < 0)
            return found;

        foreach (var part in TopLevelParts(createTable[(open + 1)..close]))
        {
            var tokens = Tokens(part);

            if (tokens.Count == 0)
                continue;

            // a table constraint: [CONSTRAINT name] UNIQUE (column)
            var at = tokens[0].Is("CONSTRAINT") ? 2 : 0;

            if (at < tokens.Count && tokens[at].Is("UNIQUE"))
            {
                var columns = part[(part.IndexOf('(') + 1)..part.LastIndexOf(')')].Split(',').Select(x => Unquote(x.Trim())).ToArray();

                if (columns.Length == 1)
                    found.Add(columns[0]);

                continue;
            }

            if (tokens[0].Is("PRIMARY") || tokens[0].Is("FOREIGN") || tokens[0].Is("CHECK") || tokens[0].Is("CONSTRAINT"))
                continue;

            // a column: name type ... UNIQUE ...
            if (tokens.Skip(1).Any(x => x.Is("UNIQUE")))
                found.Add(Unquote(tokens[0].Text));
        }

        return found;
    }

    /// <summary>A CREATE TABLE's definitions, split on the commas that are not inside parentheses or strings.</summary>
    static IEnumerable<string> TopLevelParts(string body)
    {
        var depth = 0;
        var start = 0;
        var i = 0;

        while (i < body.Length)
        {
            var c = body[i];

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(body, i, c);
                continue;
            }

            if (c == '[')
            {
                var close = body.IndexOf(']', i + 1);
                i = close < 0 ? body.Length : close + 1;
                continue;
            }

            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (c == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }

            i++;
        }

        yield return body[start..];
    }

    public static string Unquote(string identifier)
    {
        var text = identifier.Trim();

        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '`' && text[^1] == '`')))
            return text[1..^1].Replace(new string(text[0], 2), text[0].ToString());

        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']')
            return text[1..^1];

        return text;
    }

    static int MatchParenthesis(string text, int open)
    {
        var depth = 0;
        var i = open;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(text, i, c);
                continue;
            }

            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return i;

            i++;
        }

        return -1;
    }

    sealed record Token(string Text, int Start)
    {
        public bool Is(string keyword) => String.Equals(this.Text, keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Words, quoted names, strings and single punctuation, with where each starts.</summary>
    static List<Token> Tokens(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (Char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }

            var start = i;

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(text, i, c);
            }
            else if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                i = close < 0 ? text.Length : close + 1;
            }
            else if (Char.IsLetterOrDigit(c) || c == '_')
            {
                while (i < text.Length && (Char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'))
                    i++;
            }
            else
            {
                i++;
            }

            tokens.Add(new Token(text[start..i], start));
        }

        return tokens;
    }

    /// <summary>A string as a SQL literal.</summary>
    public static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>Joins statements into a script, each ended with a semicolon.</summary>
    public static string Join(IEnumerable<string> statements)
    {
        var script = new StringBuilder();

        foreach (var statement in statements)
            script.Append(statement.TrimEnd().TrimEnd(';')).Append(";\n");

        return script.ToString();
    }
}
