using Pfr.Gui.Tables;

namespace Pfr.Gui.Tests;

public class ColumnComparerTests
{
    private static List<string> Sort(ColumnKind kind, params string[] values)
    {
        var rows = values.Select(v => new TableRow([v])).ToList();
        rows.Sort((a, b) => new ColumnComparer(0, kind).Compare(a, b));
        return rows.Select(r => r.Cells[0]).ToList();
    }

    [Fact]
    public void Ip_columns_sort_numerically_with_prefixes_and_blanks_last() =>
        Assert.Equal(
            ["10.0.0.0/8", "10.0.0.0/24", "10.0.0.2", "10.0.0.10", "192.168.1.1", "2001:db8::1", ""],
            Sort(ColumnKind.Ip, "", "2001:db8::1", "192.168.1.1", "10.0.0.10", "10.0.0.0/24", "10.0.0.2", "10.0.0.0/8"));

    [Fact]
    public void Number_columns_sort_numerically() =>
        Assert.Equal(["2", "10", "100"], Sort(ColumnKind.Number, "100", "2", "10"));

    [Fact]
    public void Search_matches_all_terms_in_any_column()
    {
        var row = new TableRow(["gm.lab.test", "Grid Master", "10.0.0.1"]);
        Assert.True(row.Matches(["grid", "10.0.0"]));
        Assert.False(row.Matches(["grid", "candidate"]));
    }
}
