using System.Globalization;

namespace Pfr.Core.Query;

/// <summary>
/// Named parameters in SQL: finding them (<c>:name</c>, <c>@name</c> or <c>$name</c>, ignoring strings, quoted
/// names and comments) and turning typed-in values into the values handed to the database.
/// Values only ever reach SQLite as bound parameters, so a value such as <c>'; DROP TABLE x --</c> is just text.
/// </summary>
public static class QueryParameters
{
    public const int MaxNameLength = 50;

    /// <summary>
    /// The parameter tokens in the SQL, each once, in order of first use, with their prefix (":zone").
    /// </summary>
    /// <exception cref="QueryException">The SQL uses a positional <c>?</c>; use a name instead.</exception>
    public static IReadOnlyList<string> FindTokens(string sql)
    {
        var tokens = new List<string>();
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
            else if (c == '?')
            {
                throw new QueryException("Use a named parameter such as :name instead of ?.");
            }
            else if (c is ':' or '@' or '$')
            {
                var start = i++;
                if (i < sql.Length && IsWordStart(sql[i]))
                {
                    while (i < sql.Length && IsWordChar(sql[i])) i++;
                    var token = sql[start..i];
                    if (!tokens.Contains(token)) tokens.Add(token);
                }
            }
            else if (IsWordStart(c))
            {
                // A whole word at once, so a "$" inside a name (a$b) isn't taken for a parameter.
                while (i < sql.Length && IsWordChar(sql[i])) i++;
            }
            else
            {
                i++;
            }
        }
        return tokens;
    }

    /// <summary>The parameter names used in the SQL, each once, in order of first use.</summary>
    /// <exception cref="QueryException">The SQL uses a positional <c>?</c>.</exception>
    public static IReadOnlyList<string> FindNames(string sql) =>
        FindTokens(sql).Select(NameOf).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>":zone" becomes "zone".</summary>
    public static string NameOf(string token) => token[1..];

    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxNameLength
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>A parameter that has only been typed into a query: text, and empty means NULL.</summary>
    public static ReportParameter AdHoc(string name) => new() { Name = name, Required = false };

    /// <summary>
    /// Checks the values typed in against the parameter definitions and returns what to pass to the database:
    /// text as text, numbers as numbers, a choice as its exact option, and an empty optional value as NULL.
    /// </summary>
    /// <exception cref="QueryException">A value is missing, not a number, not an option, or names no parameter.</exception>
    public static IReadOnlyDictionary<string, object?> Bind(
        IReadOnlyList<ReportParameter> definitions, IReadOnlyDictionary<string, string> values)
    {
        foreach (var name in values.Keys)
        {
            if (definitions.All(d => d.Name != name)) throw Unknown(name, definitions);
        }

        var bound = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var text = values.TryGetValue(definition.Name, out var typed) ? typed.Trim() : "";
            if (text.Length == 0)
            {
                if (definition.Required) throw new QueryException($"Enter a value for “{definition.DisplayLabel}”.");
                bound[definition.Name] = null;
                continue;
            }

            switch (definition.Type)
            {
                case ParameterType.Number:
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                        bound[definition.Name] = whole;
                    else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        bound[definition.Name] = number;
                    else
                        throw new QueryException($"“{definition.DisplayLabel}” must be a number.");
                    break;

                case ParameterType.Choice:
                    bound[definition.Name] = definition.Options.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase))
                        ?? throw new QueryException($"“{definition.DisplayLabel}” must be one of: {string.Join(", ", definition.Options)}.");
                    break;

                default:
                    bound[definition.Name] = text;
                    break;
            }
        }
        return bound;
    }

    /// <summary>Like <see cref="Bind"/>, but anything not supplied starts from the parameter's default.</summary>
    public static IReadOnlyDictionary<string, object?> Resolve(
        IReadOnlyList<ReportParameter> definitions, IReadOnlyDictionary<string, string> supplied)
    {
        var values = definitions.ToDictionary(d => d.Name, d => d.Default, StringComparer.Ordinal);
        foreach (var (name, value) in supplied)
        {
            if (!values.ContainsKey(name)) throw Unknown(name, definitions);
            values[name] = value;
        }
        return Bind(definitions, values);
    }

    private static QueryException Unknown(string name, IReadOnlyList<ReportParameter> definitions) =>
        new($"There is no parameter “{name}”. " + (definitions.Count == 0
            ? "This query takes none."
            : $"It takes: {string.Join(", ", definitions.Select(d => d.Name))}."));

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

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
