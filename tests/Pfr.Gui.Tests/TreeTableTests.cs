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
        Assert.Contains("(16 records)", internalView.Cells[0]);

        var lab = internalView.Children.Single(c => c.Cells[0].StartsWith("test")).Children.Single();
        Assert.StartsWith("lab.test", lab.Cells[0]);
        Assert.True(lab.IsGroup);

        // Sub-zone first (with its delegation NS), then the zone's own records ("@" is the apex).
        var aws = lab.Children[0];
        Assert.StartsWith("aws.lab.test", aws.Cells[0]);
        Assert.Equal(["@", "NS", "ns-1.awsdns.test"], Assert.Single(aws.Children).Cells.Take(3));

        var own = lab.Children.Skip(1).ToList();
        Assert.All(own, r => Assert.False(r.IsGroup));
        Assert.Contains(own, r => r.Cells[0] == "@" && r.Cells[1] == "A" && r.Cells[3] == "" && r.Cells[4] == "lab.test");
        Assert.Contains(own, r => r.Cells[0] == "@" && r.Cells[1] == "MX" && r.Cells[2] == "10 mail.lab.test");
        Assert.Contains(own, r => r.Cells[0] == "old" && r.Cells[5] == "Yes");

        // Host object records: real types, flagged in the Host column.
        var server1 = own.Where(r => r.Cells[0] == "server1").ToList();
        Assert.Equal(["A", "A", "AAAA"], server1.Select(r => r.Cells[1]));
        Assert.All(server1, r => Assert.Equal("Yes", r.Cells[3]));
        Assert.Contains(own, r => r.Cells[0] == "files" && r.Cells[1] == "CNAME" && r.Cells[3] == "Yes");

        // Reverse zone: the standalone PTR and the host's PTR.
        var reverse = internalView.Children.Single(c => c.Cells[0].StartsWith("in-addr.arpa")).Children.Single();
        Assert.Equal(
            [("10", "www.lab.test", ""), ("20", "server1.lab.test", "Yes")],
            reverse.Children.Select(r => (r.Cells[0], r.Cells[2], r.Cells[3])));
    }

    [AvaloniaFact]
    public void Records_tree_counts_only_records()
    {
        var section = Section("DNS records");
        Assert.Equal(17, section.Roots.SelectMany(r => r.SelfAndDescendants()).Count(r => !r.IsGroup));

        var table = new TreeTable(section);
        table.SetAllExpanded(false);
        table.ApplyFilter("files");
        Assert.Equal("files", table.VisibleRows.Last().Cells[0]);
        Assert.StartsWith("lab.test", table.VisibleRows[^2].Cells[0]);
    }

    [Fact]
    public void Licenses_tree_is_grid_wide_then_members()
    {
        var licenses = Section("Licenses");

        var gridWide = licenses.Roots[0];
        Assert.StartsWith("Grid-wide", gridWide.Cells[0]);
        Assert.Equal("Reporting Subscription", Assert.Single(gridWide.Children).Cells[0]);

        // Then one row per member, in host name order.
        Assert.Equal(["gm.lab.test", "gmc.lab.test", "ns1.lab.test"],
            licenses.Roots.Skip(1).Select(r => r.Cells[0].Split(' ')[0]));

        var gm = licenses.Roots[1];
        Assert.Equal("Next: 2100-01-01", gm.Cells[4]);
        var dtc = gm.Children.Single(r => r.Cells[1] == "dtc");
        Assert.Equal(["DNS Traffic Control", "dtc", "SN-GM", "Static", "2100-01-01", ""], dtc.Cells);
        Assert.Equal("Permanent", gm.Children.Single(r => r.Cells[1] == "dns").Cells[4]);

        var gmc = licenses.Roots[2];
        Assert.Equal("Expired", gmc.Children.Single(r => r.Cells[1] == "sw_tp").Cells[5]);
        Assert.Equal(("Next: 2020-01-01", "Expired"), (gmc.Cells[4], gmc.Cells[5])); // member row flags it too

        // HA member: one row per node, labelled with position and serial.
        var ha = licenses.Roots[3];
        Assert.Equal(["Node 1 · SN-A", "Node 2 · SN-B"], ha.Children.Select(r => r.Cells[2]));
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
    public void Status_cells_are_colored_by_expiry()
    {
        Assert.NotNull(TableCells.StatusBrush("Expired"));
        Assert.NotNull(TableCells.StatusBrush("Expires in 3 days"));
        Assert.NotEqual(TableCells.StatusBrush("Expired"), TableCells.StatusBrush("Expires in 3 days"));
        Assert.Null(TableCells.StatusBrush(""));

        var cell = TableCells.Text(0, ColumnKind.Status);
        cell.DataContext = new TreeRow(["Expired"]);
        Assert.Equal(TableCells.StatusBrush("Expired"), cell.Foreground);
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
