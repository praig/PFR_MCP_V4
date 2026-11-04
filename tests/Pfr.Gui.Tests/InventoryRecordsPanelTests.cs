using Pfr.Core.Nios.Model;
using Pfr.Core.Reports;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Tests;

/// <summary>
/// The Inventory tab's DNS records panel and the Row count report show a record the same way, because they build its row from
/// the same definition (<see cref="DnsRecordPanel"/>). These tests keep it that way.
/// </summary>
public class InventoryRecordsPanelTests
{
    private static GridInventory Grid()
    {
        var inventory = new GridInventory { GridName = "Lab" };
        inventory.DnsViews.Add(new DnsView { Key = "._default", Name = "default" });
        var zone = new DnsZone { Key = "._default.lab.test", ViewKey = "._default", Fqdn = "lab.test", ZoneType = "Authoritative" };
        inventory.Zones.Add(zone);

        DnsRecord Make(DnsRecordType type, string name, string value, Ttl ttl, string? host = null, bool disabled = false) => new()
        {
            Type = type, ZoneKey = zone.Key, ViewKey = zone.ViewKey, Name = name,
            Fqdn = name.Length == 0 ? zone.Fqdn : $"{name}.{zone.Fqdn}", Value = value, Ttl = ttl, HostName = host, Disabled = disabled,
        };

        inventory.Records.AddRange(
        [
            Make(DnsRecordType.CNAME, "www", "web1.lab.test", new Ttl(300, TtlSource.Record), disabled: true),
            Make(DnsRecordType.A, "web1", "10.0.0.20", new Ttl(28800, TtlSource.Host), host: "web1.lab.test"),
            Make(DnsRecordType.NS, "", "ns1.lab.test.", new Ttl(3600, TtlSource.Zone)),
            Make(DnsRecordType.A, "app", "10.0.0.21", new Ttl(null, TtlSource.Grid)),
            Make(DnsRecordType.TXT, "", "\"v=spf1 -all\"", new Ttl(86400, TtlSource.Grid)),
        ]);
        return inventory;
    }

    private static TreeTableDefinition Panel(GridInventory inventory) =>
        InventoryTables.Build(inventory).OfType<TreeTableDefinition>().Single(t => t.Title == "DNS records");

    [Fact]
    public void The_panels_columns_are_the_shared_ones()
    {
        Assert.Equal(DnsRecordPanel.Columns, Panel(Grid()).Columns.Select(c => c.Header));
    }

    [Fact]
    public void Each_record_is_the_shared_row_and_they_come_in_the_shared_order()
    {
        var inventory = Grid();

        var leaves = Panel(inventory).Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsGroup).Select(r => r.Cells).ToList();

        Assert.Equal(DnsRecordPanel.Order(inventory.Records).Select(DnsRecordPanel.Cells).ToList(), leaves);
        Assert.Equal(5, leaves.Count);
        Assert.Equal(["@", "TXT"], leaves[0].Take(2));   // the zone's own records come first (types are in the enum's order: TXT before NS)
        Assert.Equal(["@", "NS"], leaves[1].Take(2));
    }

    [Fact]
    public void The_wording_of_the_ttl_columns_is_the_panels()
    {
        var leaves = Panel(Grid()).Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsGroup).Select(r => r.Cells).ToList();

        var web1 = leaves.Single(c => c[0] == "web1");
        Assert.Equal(["28800 (8h)", "Host object", "Yes"], web1.Skip(3).Take(3));
        var ns = leaves.Single(c => c[1] == "NS");
        Assert.Equal(["3600 (1h)", "Inherited: zone"], ns.Skip(3).Take(2));
        var app = leaves.Single(c => c[0] == "app");
        Assert.Equal(["", ""], app.Skip(3).Take(2));
        var www = leaves.Single(c => c[0] == "www");
        Assert.Equal("Yes", www[7]);
    }
}
