using System.Globalization;
using Pfr.Core.Query;

namespace Pfr.Gui.Tables;

/// <summary>Turns a table shown on screen into a result that can be exported (Excel, CSV or JSON).</summary>
public static class TableExport
{
    /// <summary>
    /// The table's columns and rows, as shown. A cell of a <see cref="ColumnKind.Number"/> column that holds a plain number
    /// becomes a number (so Excel can add it up and sort it); an empty cell becomes NULL; everything else stays text.
    /// </summary>
    public static QueryResult ToResult(TableDefinition table)
    {
        var columns = table.Columns.Select(c => c.Header).ToList();
        var rows = new List<object?[]>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var values = new object?[columns.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var text = i < row.Cells.Count ? row.Cells[i] : "";
                values[i] = Value(text, table.Columns[i].Kind);
            }
            rows.Add(values);
        }
        return new QueryResult(columns, rows, truncated: false, TimeSpan.Zero);
    }

    private static object? Value(string text, ColumnKind kind)
    {
        if (text.Length == 0) return null;
        if (kind == ColumnKind.Number)
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return whole;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)) return fraction;
        }
        return text;
    }
}
