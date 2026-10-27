using Pfr.Core.Nios;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Nios;

public class HierarchyTests
{
    [Theory]
    [InlineData("10.0.0.0", 8, "10.1.2.0", 24, true)]
    [InlineData("10.0.0.0", 24, "10.0.0.0", 24, false)] // same size is not "strictly"
    [InlineData("10.0.0.0", 24, "10.0.1.0", 25, false)]
    [InlineData("192.168.0.0", 22, "192.168.3.128", 25, true)]
    [InlineData("2001:db8::", 32, "2001:db8:5::", 48, true)]
    [InlineData("2001:db8::", 48, "2001:db9::", 64, false)]
    [InlineData("10.0.0.0", 8, "2001:db8::", 64, false)]
    public void Prefix_containment(string a, int al, string b, int bl, bool expected)
    {
        Assert.True(IpPrefix.TryParse(a, al, out var outer));
        Assert.True(IpPrefix.TryParse(b, bl, out var inner));
        Assert.Equal(expected, outer.StrictlyContains(inner));
    }

    [Theory]
    [InlineData("richard.network", "network")]
    [InlineData("a.b.example.com.", "com")]
    [InlineData("0.0.10.in-addr.arpa", "in-addr.arpa")]
    [InlineData("1.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa", "ip6.arpa")]
    [InlineData("localhost", "localhost")]
    public void Top_level_groups(string fqdn, string expected) =>
        Assert.Equal(expected, InventoryHierarchy.TopLevelGroup(fqdn));

    [Fact]
    public void Zones_nest_under_nearest_ancestor_in_same_view_only()
    {
        DnsZone Z(string view, string fqdn) => new() { Key = $"{view}.{fqdn}", ViewKey = view, Fqdn = fqdn };
        var parent = Z(".a", "lab.test");
        var child = Z(".a", "aws.lab.test");
        var grandchild = Z(".a", "x.y.aws.lab.test"); // skips the missing y.aws level
        var otherView = Z(".b", "dev.lab.test");

        var parents = InventoryHierarchy.ParentZones([parent, child, grandchild, otherView]);

        Assert.Null(parents[parent.Key]);
        Assert.Same(parent, parents[child.Key]);
        Assert.Same(child, parents[grandchild.Key]);
        Assert.Null(parents[otherView.Key]); // lab.test exists only in view .a
    }

    [Fact]
    public void Networks_nest_by_containment_within_network_view()
    {
        IpNetwork N(string addr, int cidr, bool container = false, int view = 0) =>
            new() { Address = addr, Cidr = cidr, IsContainer = container, NetworkViewId = view };

        var c16 = N("10.0.0.0", 16, container: true);
        var c20 = N("10.0.0.0", 20, container: true);
        var n24 = N("10.0.1.0", 24);
        var n24b = N("10.0.32.0", 24);       // in /16 but outside the /20
        var outside = N("192.168.1.0", 24);
        var sameSize = N("10.0.0.0", 20);     // network equal to its container
        var otherView = N("10.0.5.0", 24, view: 1);

        var p = InventoryHierarchy.ParentNetworks([n24, c16, outside, c20, n24b, sameSize, otherView]);

        Assert.Null(p[c16]);
        Assert.Same(c16, p[c20]);
        Assert.Same(c20, p[n24]);
        Assert.Same(c16, p[n24b]);
        Assert.Null(p[outside]);
        Assert.Same(c20, p[sameSize]);
        Assert.Null(p[otherView]);
    }

    [Fact]
    public void Fixture_hierarchy()
    {
        var inv = BackupParser.Parse(OneDbFixture.XmlStream());

        var zoneParents = InventoryHierarchy.ParentZones(inv.Zones);
        var aws = inv.Zones.Single(z => z.Fqdn == "aws.lab.test");
        Assert.Equal("._default.test.lab", zoneParents[aws.Key]!.Key);

        var netParents = InventoryHierarchy.ParentNetworks(inv.Networks);
        var n24 = inv.Networks.Single(n => n.Cidrs == "10.0.0.0/24");
        Assert.True(netParents[n24]!.IsContainer);
    }
}
