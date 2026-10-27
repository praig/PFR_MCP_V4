using System.Collections;
using Pfr.Core.Nios;

namespace Pfr.Gui.Tables;

public enum ColumnKind
{
    Text,
    /// <summary>IP address or prefix ("10.0.0.0/24"); sorts numerically.</summary>
    Ip,
    Number,
    /// <summary>Colored text: red for "Expired"/"High", amber for "Expires…"/"Expiring"/"Medium".</summary>
    Status,
}

public sealed record TableColumn(string Header, ColumnKind Kind = ColumnKind.Text, double? Width = null);

/// <summary>One display row: pre-formatted cells plus a lower-case search string.</summary>
public sealed class TableRow
{
    public TableRow(IReadOnlyList<string> cells)
    {
        Cells = cells;
        SearchText = string.Join("\u001f", cells).ToLowerInvariant();
    }

    public IReadOnlyList<string> Cells { get; }

    public string SearchText { get; }

    public bool Matches(string[] terms) => terms.All(t => SearchText.Contains(t, StringComparison.Ordinal));
}

public sealed class TableDefinition
{
    public TableDefinition(string title, IReadOnlyList<TableColumn> columns, IReadOnlyList<TableRow> rows)
    {
        Title = title;
        Columns = columns;
        Rows = rows;
    }

    public string Title { get; }
    public IReadOnlyList<TableColumn> Columns { get; }
    public IReadOnlyList<TableRow> Rows { get; }
}

/// <summary>Sorts rows by one column, using numeric/IP order where the column calls for it.</summary>
public sealed class ColumnComparer(int index, ColumnKind kind) : IComparer
{
    public int Compare(object? x, object? y) => CompareCells(CellsOf(x), CellsOf(y));

    private static IReadOnlyList<string>? CellsOf(object? row) => row switch
    {
        TableRow t => t.Cells,
        TreeRow t => t.Cells,
        _ => null,
    };

    public int CompareCells(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
    {
        var a = x?[index] ?? "";
        var b = y?[index] ?? "";

        // Blank cells always sort last.
        if (a.Length == 0 || b.Length == 0)
        {
            return (a.Length == 0).CompareTo(b.Length == 0);
        }

        return kind switch
        {
            ColumnKind.Number when LeadingNumber(a) is { } ia && LeadingNumber(b) is { } ib => ia.CompareTo(ib),
            ColumnKind.Ip => ComparePrefix(a, b),
            _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>"28800 (8h)" → 28800; "12" → 12; non-numeric → null.</summary>
    private static long? LeadingNumber(string value)
    {
        var end = 0;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        return end > 0 && long.TryParse(value.AsSpan(0, end), out var n) ? n : null;
    }

    private static int ComparePrefix(string a, string b)
    {
        var (addrA, lenA) = Split(a);
        var (addrB, lenB) = Split(b);
        var c = IpSort.Compare(addrA, addrB);
        return c != 0 ? c : lenA.CompareTo(lenB);
    }

    private static (string Address, int Length) Split(string value)
    {
        // Ranges are shown as "start – end"; sort by the start address.
        var space = value.IndexOf(' ');
        if (space > 0) value = value[..space];

        var slash = value.IndexOf('/');
        return slash < 0
            ? (value, -1)
            : (value[..slash], int.TryParse(value[(slash + 1)..], out var len) ? len : -1);
    }
}
