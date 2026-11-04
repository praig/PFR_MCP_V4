using System.Text;

namespace Pfr.Core.Query;

public enum ColumnAddOutcome
{
    /// <summary>The editor was empty, so a new query was started.</summary>
    Started,

    /// <summary>The column was added to the SELECT list (and its table to FROM if it wasn't there).</summary>
    Added,

    /// <summary>The column was already selected; nothing changed.</summary>
    AlreadyThere,

    /// <summary>The query selected everything (<c>*</c>); that was replaced by just this column.</summary>
    ReplacedStar,

    /// <summary>The query is too involved to edit safely; <see cref="ColumnAddResult.Fallback"/> is for inserting at the cursor.</summary>
    NotEditable,
}

/// <param name="Sql">The query after the change (unchanged for AlreadyThere and NotEditable).</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">A sentence saying what was done, for the status line.</param>
/// <param name="Fallback">"table.column", for inserting at the cursor when the query can't be edited.</param>
public sealed record ColumnAddResult(string Sql, ColumnAddOutcome Outcome, string Message, string Fallback);

/// <summary>
/// Edits the text of a simple SELECT query to include a column, as when a column is double-clicked in the table
/// browser: the column goes into the SELECT list unless it is already there, and its table goes into FROM unless it
/// is already there (joined on a known key when there is one, otherwise comma-separated). Everything else in the
/// query, including comments, strings, WHERE, ORDER BY and LIMIT, is left exactly as it was.
/// <para>
/// Only a plain <c>SELECT … FROM …</c> is edited. A query that starts with WITH, uses UNION, INTERSECT or EXCEPT,
/// has more than one statement, or reads from something other than the grid's tables (a subquery or a CTE) is
/// reported as not editable, so the caller can insert the column name at the cursor instead.
/// </para>
/// </summary>
public static class QueryBuilder
{
    private static readonly IReadOnlyList<TableInfo> Schema = SnapshotSchema.Describe();

    /// <summary>How the tables relate: the child's column holds the parent's key.</summary>
    private static readonly (string Child, string ChildColumn, string Parent, string ParentColumn)[] Relations =
    [
        ("nodes", "member_oid", "members", "oid"),
        ("member_services", "member_oid", "members", "oid"),
        ("licenses", "member_oid", "members", "oid"),
        ("dhcp_ranges", "member_oid", "members", "oid"),
        ("zone_servers", "member_oid", "members", "oid"),
        ("failover", "primary_oid", "members", "oid"),
        ("failover", "secondary_oid", "members", "oid"),
        ("zones", "view_key", "dns_views", "view_key"),
        ("records", "view_key", "dns_views", "view_key"),
        ("records", "zone_key", "zones", "zone_key"),
        ("zone_servers", "zone_key", "zones", "zone_key"),
        ("dhcp_ranges", "network_key", "networks", "network_key"),
        ("dns_views", "network_view_id", "network_views", "id"),
        ("networks", "network_view_id", "network_views", "id"),
        ("dhcp_ranges", "network_view_id", "network_views", "id"),
        ("admin_users", "group_name", "admin_groups", "name"),
        ("admin_group_roles", "group_name", "admin_groups", "name"),
        ("admin_group_roles", "role_name", "admin_roles", "name"),
    ];

    private static readonly HashSet<string> ClauseStarts =
        new(["FROM", "WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "WINDOW"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SecondQueryWords =
        new(["SELECT", "UNION", "INTERSECT", "EXCEPT"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NotAnAlias = new(
    [
        "ON", "USING", "JOIN", "LEFT", "RIGHT", "FULL", "INNER", "OUTER", "CROSS", "NATURAL", "INDEXED", "NOT", "AS",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds <paramref name="column"/> of <paramref name="table"/> to the query.
    /// </summary>
    /// <exception cref="ArgumentException">There is no such table or column.</exception>
    public static ColumnAddResult AddColumn(string sql, string table, string column)
    {
        var info = Schema.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"There is no table \"{table}\".");
        var columnInfo = info.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ArgumentException($"{info.Name} has no column \"{column}\".");
        table = info.Name;
        column = columnInfo.Name;
        var fallback = $"{table}.{column}";

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new ColumnAddResult($"SELECT {column}\nFROM {table}", ColumnAddOutcome.Started,
                $"Started a query on {table} with {column}.", fallback);
        }

        var parsed = Parse(sql);
        if (parsed is null || (parsed.HasFrom && parsed.Refs.Count == 0))
        {
            return new ColumnAddResult(sql, ColumnAddOutcome.NotEditable,
                $"This query is too involved to edit automatically, so {fallback} was inserted at the cursor.", fallback);
        }

        var tableRef = parsed.Refs.FirstOrDefault(r => r.Table == table);
        var onlyStar = parsed.Items.Count == 1 && PlainColumn(parsed.Items[0]) is { Name: "*", Qualifier: null };

        // Already selected (a lone * is handled below: it is replaced).
        if (tableRef is not null && !onlyStar)
        {
            foreach (var item in parsed.Items)
            {
                if (Covers(item, tableRef, parsed.Refs, column))
                    return new ColumnAddResult(sql, ColumnAddOutcome.AlreadyThere, $"{column} is already in the SELECT list.", fallback);
            }
        }

        var addTable = tableRef is null;
        var tablesAfter = parsed.Refs.Select(r => r.Table).Append(table).Distinct().Count();
        var qualifier = tableRef?.Qualifier ?? table;
        var text = tablesAfter > 1 ? $"{qualifier}.{column}" : column;

        var fromAddition = "";
        string? fromNote = null;
        if (addTable)
        {
            if (!parsed.HasFrom)
            {
                fromAddition = $"\nFROM {table}";
                fromNote = $"Added FROM {table}.";
            }
            else if (FindJoin(table, parsed.Refs) is { } join)
            {
                fromAddition = $"\nJOIN {table} ON {join.On}";
                fromNote = $"Added {table} to FROM, joined on {join.Column}.";
            }
            else
            {
                fromAddition = $", {table}";
                fromNote = $"Added {table} to FROM. There is no known key to join it on, so check how it relates to the others.";
            }
        }

        var edits = new List<(int Position, int Remove, string Insert)>();
        if (onlyStar)
        {
            var star = parsed.Items[0];
            edits.Add((star.Start, star.End - star.Start, text));
            if (addTable) edits.Add((parsed.HasFrom ? parsed.FromEnd : star.End, 0, fromAddition));
        }
        else
        {
            var selectAddition = parsed.Items.Count == 0 ? $" {text}" : $", {text}";
            if (addTable && !parsed.HasFrom)
            {
                edits.Add((parsed.LastItemEnd, 0, selectAddition + fromAddition));
            }
            else
            {
                edits.Add((parsed.LastItemEnd, 0, selectAddition));
                if (addTable) edits.Add((parsed.FromEnd, 0, fromAddition));
            }

            // A second table makes bare column names ambiguous: say which table the existing ones belong to.
            if (addTable && parsed.Refs.Count > 0)
            {
                foreach (var item in parsed.Items)
                {
                    if (PlainColumn(item) is not { Qualifier: null } bare || bare.Name == "*") continue;
                    var owner = parsed.Refs.FirstOrDefault(r => ColumnsOf(r.Table).Contains(bare.Name, StringComparer.OrdinalIgnoreCase));
                    if (owner is not null) edits.Add((item.Start, 0, owner.Qualifier + "."));
                }
            }
        }

        var result = new StringBuilder(sql);
        foreach (var edit in edits.OrderByDescending(e => e.Position))
        {
            result.Remove(edit.Position, edit.Remove);
            result.Insert(edit.Position, edit.Insert);
        }

        var message = onlyStar ? $"Replaced * with {text}." : $"Added {text} to the SELECT list.";
        if (fromNote is not null) message += " " + fromNote;
        return new ColumnAddResult(result.ToString(), onlyStar ? ColumnAddOutcome.ReplacedStar : ColumnAddOutcome.Added, message, fallback);
    }

    // ---- Reading the query --------------------------------------------------------------

    private enum Kind { Word, Quoted, Symbol }

    private sealed record Token(Kind Kind, string Text, int Start, int End, int Depth);

    private sealed record TableRef(string Table, string? Alias)
    {
        /// <summary>What columns of this table are called in the query: its alias, or its name.</summary>
        public string Qualifier => Alias ?? Table;
    }

    private sealed record Item(Token[] Tokens)
    {
        public int Start => Tokens[0].Start;
        public int End => Tokens[^1].End;
    }

    private sealed record Parsed(List<Item> Items, int LastItemEnd, bool HasFrom, int FromEnd, List<TableRef> Refs);

    /// <summary>A SELECT item that is just a column: <c>role</c>, <c>m.role</c>, <c>*</c> or <c>m.*</c> (Qualifier null when bare).</summary>
    private sealed record Plain(string? Qualifier, string Name);

    /// <summary>The SELECT list and the tables in FROM of a plain single SELECT, or null if it is anything more involved.</summary>
    private static Parsed? Parse(string sql)
    {
        var top = Scan(sql).Where(t => t.Depth == 0).ToList();
        if (top.Count == 0 || !IsWord(top[0], "SELECT")) return null;

        for (var k = 1; k < top.Count; k++)
        {
            var token = top[k];
            if (token.Kind == Kind.Word && SecondQueryWords.Contains(token.Text)) return null;
            if (token is { Kind: Kind.Symbol, Text: ";" } && k < top.Count - 1) return null;
        }

        var index = 1;
        var selectEnd = top[0].End;
        if (index < top.Count && (IsWord(top[index], "DISTINCT") || IsWord(top[index], "ALL")))
        {
            selectEnd = top[index].End;
            index++;
        }

        var listEnd = index;
        while (listEnd < top.Count && !IsClauseStart(top[listEnd])) listEnd++;
        var items = SplitItems(top.GetRange(index, listEnd - index));

        var hasFrom = listEnd < top.Count && IsWord(top[listEnd], "FROM");
        var refs = new List<TableRef>();
        var fromEnd = 0;
        if (hasFrom)
        {
            var end = listEnd + 1;
            while (end < top.Count && !IsClauseStart(top[end])) end++;
            var fromTokens = top.GetRange(listEnd + 1, end - listEnd - 1);
            refs = FindTableRefs(fromTokens);
            if (fromTokens.Count > 0) fromEnd = fromTokens[^1].End;
        }

        return new Parsed(items, items.Count > 0 ? items[^1].End : selectEnd, hasFrom, fromEnd, refs);
    }

    private static List<Item> SplitItems(List<Token> tokens)
    {
        var items = new List<Item>();
        var current = new List<Token>();
        foreach (var token in tokens)
        {
            if (token is { Kind: Kind.Symbol, Text: "," })
            {
                if (current.Count > 0) items.Add(new Item(current.ToArray()));
                current = [];
            }
            else
            {
                current.Add(token);
            }
        }
        if (current.Count > 0) items.Add(new Item(current.ToArray()));
        return items;
    }

    /// <summary>The grid's tables named in a FROM clause, with the alias given to each.</summary>
    private static List<TableRef> FindTableRefs(List<Token> from)
    {
        var refs = new List<TableRef>();
        for (var i = 0; i < from.Count; i++)
        {
            var token = from[i];
            if (token.Kind != Kind.Word) continue;
            var table = Schema.FirstOrDefault(t => string.Equals(t.Name, token.Text, StringComparison.OrdinalIgnoreCase))?.Name;
            if (table is null) continue;

            // "x.members" or "members.oid" (in an ON clause) is part of a column reference, not a table in FROM.
            if (i > 0 && from[i - 1] is { Kind: Kind.Symbol, Text: "." }) continue;
            if (i + 1 < from.Count && from[i + 1] is { Kind: Kind.Symbol, Text: "." }) continue;

            string? alias = null;
            if (i + 1 < from.Count && from[i + 1].Kind == Kind.Word)
            {
                var next = from[i + 1];
                if (IsWord(next, "AS"))
                {
                    if (i + 2 < from.Count && from[i + 2].Kind is Kind.Word or Kind.Quoted) alias = Unquote(from[i + 2].Text);
                }
                else if (!NotAnAlias.Contains(next.Text))
                {
                    alias = next.Text;
                }
            }
            refs.Add(new TableRef(table, alias));
        }
        return refs;
    }

    /// <summary>
    /// What a SELECT item is when it is just a column (optionally qualified, optionally renamed): ("m", "host_name"),
    /// (null, "role"), (null, "*") or ("m", "*"). Null for anything else (a function, an expression...).
    /// </summary>
    private static Plain? PlainColumn(Item item)
    {
        var tokens = item.Tokens;
        var core = tokens;
        if (tokens.Length >= 3 && IsWord(tokens[^2], "AS"))
            core = tokens[..^2];
        else if (tokens.Length >= 2 && tokens[^1].Kind is Kind.Word or Kind.Quoted && tokens[^2].Kind is Kind.Word or Kind.Quoted)
            core = tokens[..^1];

        if (core.Length == 1)
        {
            if (core[0] is { Kind: Kind.Symbol, Text: "*" }) return new Plain(null, "*");
            if (core[0].Kind is Kind.Word or Kind.Quoted) return new Plain(null, Unquote(core[0].Text));
        }
        else if (core.Length == 3 && core[1] is { Kind: Kind.Symbol, Text: "." } && core[0].Kind is Kind.Word or Kind.Quoted)
        {
            if (core[2] is { Kind: Kind.Symbol, Text: "*" }) return new Plain(Unquote(core[0].Text), "*");
            if (core[2].Kind is Kind.Word or Kind.Quoted) return new Plain(Unquote(core[0].Text), Unquote(core[2].Text));
        }
        return null;
    }

    /// <summary>True if this SELECT item already gives the column of the table (or all of its columns).</summary>
    private static bool Covers(Item item, TableRef tableRef, List<TableRef> refs, string column)
    {
        if (PlainColumn(item) is not { } plain) return false;
        var (qualifier, name) = plain;

        if (qualifier is not null)
        {
            if (!string.Equals(qualifier, tableRef.Qualifier, StringComparison.OrdinalIgnoreCase)) return false;
            return name == "*" || string.Equals(name, column, StringComparison.OrdinalIgnoreCase);
        }

        // A bare name only counts if no other table in the query has a column of that name.
        if (!string.Equals(name, column, StringComparison.OrdinalIgnoreCase)) return false;
        return !refs.Any(r => r.Table != tableRef.Table && ColumnsOf(r.Table).Contains(column, StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ColumnsOf(string table) =>
        Schema.First(t => t.Name == table).Columns.Select(c => c.Name);

    /// <summary>How to join <paramref name="table"/> to a table already in the query, if the schema knows a key for it.</summary>
    private static (string On, string Column)? FindJoin(string table, List<TableRef> refs)
    {
        foreach (var (child, childColumn, parent, parentColumn) in Relations)
        {
            if (child == table && refs.FirstOrDefault(r => r.Table == parent) is { } parentRef)
                return ($"{table}.{childColumn} = {parentRef.Qualifier}.{parentColumn}", childColumn);
            if (parent == table && refs.FirstOrDefault(r => r.Table == child) is { } childRef)
                return ($"{childRef.Qualifier}.{childColumn} = {table}.{parentColumn}", childColumn);
        }
        return null;
    }

    private static bool IsClauseStart(Token token) =>
        token is { Kind: Kind.Symbol, Text: ";" } || (token.Kind == Kind.Word && ClauseStarts.Contains(token.Text));

    private static bool IsWord(Token token, string word) =>
        token.Kind == Kind.Word && string.Equals(token.Text, word, StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && ((text[0] is '"' or '`' && text[^1] == text[0]) || (text[0] == '[' && text[^1] == ']')))
            return text[1..^1];
        return text;
    }

    // ---- Tokens ----------------------------------------------------------------------------

    /// <summary>
    /// Splits SQL into words, quoted things and single symbols, with how deeply inside parentheses each is. Comments
    /// are skipped; offsets refer to the original text, so edits can be spliced into it.
    /// </summary>
    private static List<Token> Scan(string sql)
    {
        var tokens = new List<Token>();
        var depth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
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
                var end = SkipQuoted(sql, i, c);
                tokens.Add(new Token(Kind.Quoted, sql[i..end], i, end, depth));
                i = end;
            }
            else if (c == '[')
            {
                var close = sql.IndexOf(']', i + 1);
                var end = close < 0 ? sql.Length : close + 1;
                tokens.Add(new Token(Kind.Quoted, sql[i..end], i, end, depth));
                i = end;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                tokens.Add(new Token(Kind.Word, sql[start..i], start, i, depth));
            }
            else if (char.IsDigit(c))
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.')) i++;
                tokens.Add(new Token(Kind.Word, sql[start..i], start, i, depth));
            }
            else if (c == '(')
            {
                tokens.Add(new Token(Kind.Symbol, "(", i, i + 1, depth));
                depth++;
                i++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                tokens.Add(new Token(Kind.Symbol, ")", i, i + 1, depth));
                i++;
            }
            else
            {
                tokens.Add(new Token(Kind.Symbol, c.ToString(), i, i + 1, depth));
                i++;
            }
        }
        return tokens;
    }

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
}
