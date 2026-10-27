using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Tests;

public class TreeTableTests
{
    private static TreeTableDefinition Section(string title) =>
        InventoryTables.Build(BackupParser.Parse(OneDbFixture.XmlStream()))
            .OfType<TreeTableDefinition>().Single(t => t.Title == title);

    private static List<string> Labels(TreeTable t) => t.VisibleRows.Select(r => r.Cells[0]).ToList();

    [Fact]
    public void Zone_tree_is_view_then_tld_then_zone_then_subzone()
    {
        var zones = Section("Zones");

        Assert.Equal(["External", "Internal"], zones.Roots.Select(r => r.Cells[0]));
        var internalView = zones.Roots.Single(r => r.Cells[0] == "Internal");
        Assert.True(internalView.IsGroup);

        // Forward TLDs first, reverse groups last.
        Assert.Equal(["test", "in-addr.arpa"], internalView.Children.Select(c => c.Cells[0]));

        var tld = internalView.Children[0];
        var lab = Assert.Single(tld.Children);
        Assert.Equal("lab.test", lab.Cells[0]);
        var aws = Assert.Single(lab.Children);
        Assert.Equal("aws.lab.test", aws.Cells[0]);
        Assert.Equal(2, lab.Depth); // view 0 → TLD 1 → zone 2 → sub-zone 3
        Assert.Equal(3, aws.Depth);
    }

    [Fact]
    public void Network_tree_nests_networks_in_containers()
    {
        var nets = Section("Networks");

        var view = Assert.Single(nets.Roots);
        Assert.Equal("default", view.Cells[0]);
        var container = view.Children.Single(c => c.Cells[0] == "10.0.0.0/8");
        Assert.Equal("10.0.0.0/24", Assert.Single(container.Children).Cells[0]);
        Assert.Equal("2", container.Cells[3]); // DHCP ranges rolled up from the /24
        Assert.Contains(view.Children, c => c.Cells[0] == "2001:db8::/64");
    }

    [Fact]
    public void Records_sit_under_their_zone_after_sub_zones()
    {
        var records = Section("DNS records");

        Assert.Equal(["External", "Internal"], records.Roots.Select(r => r.Cells[0].Split(' ')[0]));
        var internalView = records.Roots.Single(r => r.Cells[0].StartsWith("Internal"));
        Assert.Contains("(6 records)", internalView.Cells[0]); // 5 in lab.test + 1 PTR

        var lab = internalView.Children.Single(c => c.Cells[0].StartsWith("test")).Children.Single();
        Assert.StartsWith("lab.test", lab.Cells[0]);
        Assert.True(lab.IsGroup);

        // Sub-zone first, then the zone's own records by name ("@" is the apex).
        Assert.StartsWith("aws.lab.test", lab.Children[0].Cells[0]);
        var own = lab.Children.Skip(1).ToList();
        Assert.All(own, r => Assert.False(r.IsGroup));
        Assert.Contains(own, r => r.Cells[0] == "@" && r.Cells[1] == "A" && r.Cells[3] == "lab.test");
        Assert.Contains(own, r => r.Cells[0] == "www" && r.Cells[2] == "10.0.0.10" && r.Cells[3] == "www.lab.test");
        Assert.Contains(own, r => r.Cells[0] == "server1" && r.Cells[1] == "Host");
        Assert.Contains(own, r => r.Cells[0] == "old" && r.Cells[4] == "Yes");

        var reverse = internalView.Children.Single(c => c.Cells[0].StartsWith("in-addr.arpa"));
        var ptr = Assert.Single(reverse.Children.Single().Children);
        Assert.Equal(["10", "PTR", "www.lab.test"], ptr.Cells.Take(3));

        // The AAAA record is in the External view's copy of lab.test.
        var external = records.Roots.Single(r => r.Cells[0].StartsWith("External"));
        Assert.Contains(external.SelfAndDescendants(), r => r.Cells[1] == "AAAA");
    }

    [AvaloniaFact]
    public void Records_tree_counts_only_records()
    {
        var table = new TreeTable(Section("DNS records"));
        Assert.Equal(7, Section("DNS records").Roots.SelectMany(r => r.SelfAndDescendants()).Count(r => !r.IsGroup));

        table.SetAllExpanded(false);
        table.ApplyFilter("server1");
        Assert.Equal("server1", table.VisibleRows.Last().Cells[0]);
        Assert.StartsWith("lab.test", table.VisibleRows[^2].Cells[0]);
    }

    [Fact]
    public void Dhcp_tree_is_network_view_then_network_then_ranges()
    {
        var dhcp = Section("DHCP ranges");

        var view = Assert.Single(dhcp.Roots);
        Assert.StartsWith("default", view.Cells[0]);
        Assert.Contains("2 networks, 3 ranges", view.Cells[0]);
        Assert.True(view.IsExpanded);

        // IPv4 networks before IPv6.
        Assert.Equal(["10.0.0.0/24", "2001:db8::/64"], view.Children.Select(c => c.Cells[0].Split(' ')[0]));

        var v4 = view.Children[0];
        Assert.True(v4.IsGroup);
        Assert.Equal("Infrastructure", v4.Cells[4]); // network comment
        Assert.Equal(
            ["10.0.0.50 – 10.0.0.59", "10.0.0.100 – 10.0.0.199"],
            v4.Children.Select(r => r.Cells[0]));
        Assert.Equal("ns1.lab.test", v4.Children[0].Cells[1]); // served by member
        Assert.Equal("gm-gmc", v4.Children[1].Cells[1]);       // served by failover
        Assert.All(v4.Children, r => Assert.False(r.IsGroup));
    }

    [AvaloniaFact]
    public void Dhcp_tree_counts_and_sorts_ranges()
    {
        var table = new TreeTable(Section("DHCP ranges"));
        table.SetAllExpanded(true);
        Assert.Equal(3, table.VisibleRows.Count(r => !r.IsGroup));

        table.SortBy(0); // ascending by start address
        table.SortBy(0); // descending
        var v4 = table.VisibleRows.Single(r => r.Cells[0].StartsWith("10.0.0.0/24"));
        Assert.Equal(["10.0.0.100 – 10.0.0.199", "10.0.0.50 – 10.0.0.59"], v4.Children.Select(r => r.Cells[0]));
    }

    [AvaloniaFact]
    public void Twisties_expand_and_collapse()
    {
        var table = new TreeTable(Section("Zones"));

        // Views open by default; TLD groups closed.
        Assert.Equal(["External", "test", "Internal", "test", "in-addr.arpa"], Labels(table));

        var tld = table.VisibleRows.Last(r => r.Cells[0] == "test");
        table.Toggle(tld);
        Assert.Equal(["External", "test", "Internal", "test", "lab.test", "in-addr.arpa"], Labels(table));

        table.SetAllExpanded(true);
        Assert.Contains("aws.lab.test", Labels(table));

        table.Toggle(table.VisibleRows.Single(r => r.Cells[0] == "Internal"));
        Assert.Equal(["External", "test", "lab.test", "Internal"], Labels(table));

        table.SetAllExpanded(false);
        Assert.Equal(["External", "Internal"], Labels(table));
    }

    [AvaloniaFact]
    public void Search_keeps_ancestors_and_opens_branches()
    {
        var table = new TreeTable(Section("Zones"));
        table.SetAllExpanded(false);

        table.ApplyFilter("aws");
        Assert.Equal(["Internal", "test", "lab.test", "aws.lab.test"], Labels(table));

        table.ApplyFilter("");
        Assert.Contains("External", Labels(table));
    }

    [AvaloniaFact]
    public void Sorting_reorders_siblings_and_keeps_the_tree()
    {
        var table = new TreeTable(Section("Zones"));
        table.SetAllExpanded(true);
        var parentsBefore = table.VisibleRows.ToDictionary(r => r, r => r.Parent);
        List<string> Roots() => table.VisibleRows.Where(r => r.Depth == 0).Select(r => r.Cells[0]).ToList();

        table.SortBy(0); // ascending
        Assert.Equal(["External", "Internal"], Roots());

        table.SortBy(0); // same column again: descending
        Assert.Equal(["Internal", "External"], Roots());
        var internalTlds = table.VisibleRows.Single(r => r.Cells[0] == "Internal").Children.Select(c => c.Cells[0]);
        Assert.Equal(["test", "in-addr.arpa"], internalTlds);

        // Every row still has the same parent, and children still follow their parent.
        Assert.All(table.VisibleRows, r => Assert.Same(parentsBefore[r], r.Parent));
        for (var i = 1; i < table.VisibleRows.Count; i++)
        {
            Assert.True(table.VisibleRows[i].Depth <= table.VisibleRows[i - 1].Depth + 1);
        }
    }

    [AvaloniaFact]
    public void Text_cells_follow_their_row_when_recycled()
    {
        var cell = TableCells.Text(1);
        cell.DataContext = new TableRow(["a", "first"]);
        Assert.Equal("first", cell.Text);

        cell.DataContext = new TableRow(["b", "second"]);
        Assert.Equal("second", cell.Text);

        cell.DataContext = new TreeRow(["c", "third"]);
        Assert.Equal("third", cell.Text);
    }
}
