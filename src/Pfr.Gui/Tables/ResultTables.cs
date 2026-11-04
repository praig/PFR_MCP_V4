using System.Net;
using Pfr.Core.Query;

namespace Pfr.Gui.Tables;

/// <summary>Turns a query result into a table definition, choosing sort behaviour per column from what it holds.</summary>
public static class ResultTables
{
    public static TableDefinition Build(QueryResult result)
    {
        var columns = result.Columns.Select((name, index) => new TableColumn(name, KindOf(result, index))).ToList();
        var rows = result.Rows.Select(row => new TableRow(row.Select(QueryResult.Format).ToArray())).ToList();
        return new TableDefinition("results", columns, rows);
    }

    /// <summary>Numbers sort numerically and addresses or prefixes sort as IPs; everything else sorts as text.</summary>
    private static ColumnKind KindOf(QueryResult result, int index)
    {
        var values = result.Rows.Select(row => row[index]).Where(value => value is not null).ToList();
        if (values.Count == 0) return ColumnKind.Text;
        if (values.All(value => value is long or double)) return ColumnKind.Number;
        if (values.All(value => value is string text && LooksLikeAddress(text))) return ColumnKind.Ip;
        return ColumnKind.Text;
    }

    private static bool LooksLikeAddress(string text)
    {
        if (!text.Contains('.') && !text.Contains(':')) return false;
        var slash = text.IndexOf('/');
        var address = slash < 0 ? text : text[..slash];
        return IPAddress.TryParse(address, out _) && (slash < 0 || int.TryParse(text[(slash + 1)..], out _));
    }
}
