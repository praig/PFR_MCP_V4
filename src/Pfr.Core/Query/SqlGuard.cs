namespace Pfr.Core.Query;

/// <summary>
/// Checks that a piece of SQL is a single read-only statement before it reaches the database.
/// Strings, quoted names and comments are set aside first, so a word inside a string literal
/// (WHERE comment = 'update later') is fine. The database itself is also opened read-only
/// (<c>query_only</c>); this guard adds a clear message and blocks what that doesn't, such as ATTACH.
/// </summary>
public static class SqlGuard
{
    private static readonly HashSet<string> Allowed = ["SELECT", "WITH", "VALUES", "EXPLAIN"];

    private static readonly HashSet<string> Forbidden =
    [
        "ATTACH", "DETACH", "PRAGMA", "VACUUM", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER",
        "REINDEX", "ANALYZE", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "LOAD_EXTENSION",
    ];

    /// <summary>
    /// Returns the statement ready to run (trailing semicolons removed).
    /// </summary>
    /// <exception cref="QueryException">Empty, more than one statement, or not a read-only query.</exception>
    public static string Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new QueryException("Type a query first.");

        var words = new List<string>();
        var end = -1; // where the first top-level semicolon is
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
            }
            else if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i, c);
            }
            else if (c == '[')
            {
                var close = sql.IndexOf(']', i + 1);
                i = close < 0 ? sql.Length : close + 1;
            }
            else if (c == ';')
            {
                if (end < 0) end = i;
                i++;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                if (end >= 0) throw OneStatement();
                words.Add(sql[start..i].ToUpperInvariant());
            }
            else
            {
                // Anything other than whitespace after the first semicolon is a second statement.
                if (end >= 0 && !char.IsWhiteSpace(c)) throw OneStatement();
                i++;
            }
        }

        if (words.Count == 0) throw new QueryException("Type a query first.");
        if (!Allowed.Contains(words[0]))
            throw new QueryException("Only read-only queries can be run here: start with SELECT (or WITH, VALUES, EXPLAIN).");

        var blocked = words.FirstOrDefault(Forbidden.Contains);
        if (blocked is not null)
            throw new QueryException($"{blocked} is not allowed: queries here are read-only.");

        return (end < 0 ? sql : sql[..end]).Trim();
    }

    /// <summary>Index just past a quoted section; a doubled quote inside is an escaped quote.</summary>
    private static int SkipQuoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    private static QueryException OneStatement() => new("Only one statement can be run at a time.");
}
