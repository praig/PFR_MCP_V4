using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pfr.Core.Query;

/// <summary>The outcome of a query: column names, rows (long, double, string, byte[] or null), and how it went.</summary>
public sealed class QueryResult(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, bool truncated, TimeSpan elapsed)
{
    public IReadOnlyList<string> Columns { get; } = columns;

    public IReadOnlyList<object?[]> Rows { get; } = rows;

    /// <summary>True if there were more rows than the limit; only the first rows are here.</summary>
    public bool Truncated { get; } = truncated;

    public TimeSpan Elapsed { get; } = elapsed;

    /// <summary>A value as shown to people: NULL as "NULL", numbers without exponent noise, blobs by size.</summary>
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        byte[] b => $"(blob, {b.Length} bytes)",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>An aligned text table followed by a one-line summary.</summary>
    public string ToText()
    {
        var cells = Rows.Select(r => r.Select(Format).ToArray()).ToList();
        var widths = Columns.Select((c, i) => Math.Max(c.Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length))).ToArray();

        string Line(IEnumerable<string> values) =>
            string.Join("  ", values.Select((v, i) => v.PadRight(widths[i]))).TrimEnd();

        var text = new StringBuilder();
        if (Columns.Count > 0)
        {
            text.AppendLine(Line(Columns));
            text.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
            foreach (var row in cells) text.AppendLine(Line(row));
        }
        text.Append(Summary());
        return text.ToString();
    }

    /// <summary>"12 rows in 3 ms", with a note when the row limit cut the result short.</summary>
    public string Summary()
    {
        var rows = $"{Rows.Count:N0} row{(Rows.Count == 1 ? "" : "s")}";
        var time = Elapsed.TotalMilliseconds < 1 ? "<1 ms" : $"{Elapsed.TotalMilliseconds:N0} ms";
        return Truncated ? $"{rows} shown (more were cut off by the row limit) in {time}" : $"{rows} in {time}";
    }

    /// <summary>
    /// CSV with a header row; NULL is written as an empty field.
    /// </summary>
    /// <param name="spreadsheetSafe">See <see cref="WriteCsv"/>.</param>
    public string ToCsv(bool spreadsheetSafe = false)
    {
        using var writer = new StringWriter();
        WriteCsv(writer, spreadsheetSafe);
        return writer.ToString();
    }

    /// <summary>
    /// Writes CSV with a header row. With <paramref name="spreadsheetSafe"/>, text that starts with =, +, - or @ (or a
    /// tab or carriage return) gets a leading apostrophe: DNS names and record data come from outside, and a
    /// spreadsheet would otherwise run such a cell as a formula when the file is opened. Numbers are left alone.
    /// </summary>
    public void WriteCsv(TextWriter writer, bool spreadsheetSafe = false)
    {
        static string Field(string value) =>
            value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        string Cell(object? value)
        {
            if (value is null) return "";
            var text = Format(value);
            if (spreadsheetSafe && value is string s && s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
                text = "'" + text;
            return Field(text);
        }

        writer.WriteLine(string.Join(",", Columns.Select(Field)));
        foreach (var row in Rows) writer.WriteLine(string.Join(",", row.Select(Cell)));
    }

    /// <summary>A JSON array with one object per row. Repeated column names get _2, _3... added.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        WriteJson(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the JSON array (UTF-8, no byte-order mark) to a stream, leaving it open.</summary>
    public void WriteJson(Stream stream)
    {
        var names = new List<string>();
        foreach (var column in Columns)
        {
            var name = column;
            for (var n = 2; names.Contains(name); n++) name = $"{column}_{n}";
            names.Add(name);
        }

        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var row in Rows)
        {
            writer.WriteStartObject();
            for (var i = 0; i < names.Count; i++)
            {
                writer.WritePropertyName(names[i]);
                switch (row[i])
                {
                    case null: writer.WriteNullValue(); break;
                    case long l: writer.WriteNumberValue(l); break;
                    case double d: writer.WriteNumberValue(d); break;
                    case byte[] b: writer.WriteStringValue(Convert.ToBase64String(b)); break;
                    default: writer.WriteStringValue(Format(row[i])); break;
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
