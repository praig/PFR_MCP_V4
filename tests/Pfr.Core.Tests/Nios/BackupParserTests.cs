using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Nios;

public class BackupParserTests
{
    private static GridInventory Parse() => BackupParser.Parse(OneDbFixture.XmlStream());

    [Fact]
    public void Reads_grid_header()
    {
        var inv = Parse();
        Assert.Equal("Lab Grid", inv.GridName);
        Assert.Equal("9.0.8-12345-abc", inv.NiosVersion);
    }

    [Fact]
    public void Joins_members_and_nodes()
    {
        var inv = Parse();
        Assert.Equal(3, inv.Members.Count);

        var gm = inv.Members.Single(m => m.Oid == 0);
        Assert.Equal("gm.lab.test", gm.HostName);
        Assert.Equal("Grid Master", gm.Role);
        Assert.Equal("SN-GM", Assert.Single(gm.Nodes).SerialNumber);

        Assert.Equal("Grid Master Candidate", inv.Members.Single(m => m.Oid == 7).Role);

        var ha = inv.Members.Single(m => m.Oid == 16);
        Assert.True(ha.HaEnabled);
        Assert.Equal(["SN-A", "SN-B"], ha.Nodes.Select(n => n.SerialNumber));
    }

    [Fact]
    public void Builds_views_with_display_names()
    {
        var inv = Parse();
        Assert.Equal(["External", "Internal"], inv.DnsViews.Select(v => v.Name).Order());
        Assert.True(inv.DnsViews.Single(v => v.Key == "._default").RecursionEnabled);
    }

    [Fact]
    public void Keeps_only_user_visible_zones_with_correct_keys()
    {
        var inv = Parse();

        Assert.Equal(4, inv.Zones.Count); // Dummy, View and RPZ root are internal
        var lab = inv.Zones.Single(z => z.Key == "._default.test.lab");
        Assert.Equal("lab.test", lab.Fqdn);
        Assert.Equal("._default", lab.ViewKey);
        Assert.Equal([0], lab.GridPrimaryOids);
        Assert.Equal([16], lab.GridSecondaryOids);

        var reverse = inv.Zones.Single(z => z.Fqdn == "0.0.10.in-addr.arpa");
        Assert.True(reverse.IsReverse);

        var delegated = inv.Zones.Single(z => z.ZoneType == "Delegated");
        Assert.Equal("._default.test.lab.aws", delegated.Key);
        var ns = Assert.Single(delegated.DelegatedServers);
        Assert.Equal("ns-1.awsdns.test", ns.Name);
        Assert.Null(ns.Address); // 255.255.255.255 means "no address"

        var external = inv.Zones.Single(z => z.Key == ".1.test.lab");
        Assert.Equal("192.0.2.53", Assert.Single(external.ExternalSecondaries).Address);
    }

    [Fact]
    public void Builds_records_with_fqdns_and_skips_internal_zones()
    {
        var inv = Parse();

        Assert.Contains(inv.Records, r => r.Type == DnsRecordType.A && r.Fqdn == "www.lab.test" && r.Value == "10.0.0.10");
        Assert.Contains(inv.Records, r => r.Type == DnsRecordType.A && r.Fqdn == "lab.test" && r.Name == "");
        Assert.Contains(inv.Records, r => r.Type == DnsRecordType.AAAA && r.ViewKey == ".1");
        Assert.Contains(inv.Records, r => r.Type == DnsRecordType.PTR && r.Fqdn == "10.0.0.10.in-addr.arpa" && r.Value == "www.lab.test");

        var cnames = inv.Records.Where(r => r.Type == DnsRecordType.CNAME).ToList();
        Assert.Equal(2, cnames.Count); // blacklist pseudo-record excluded
        Assert.True(cnames.Single(c => c.Name == "old").Disabled);

        var host = inv.Records.Single(r => r.Type == DnsRecordType.Host);
        Assert.Equal("server1.lab.test", host.Fqdn);
        Assert.Equal("10.0.0.20,10.0.0.21", host.Value);
    }

    [Fact]
    public void Builds_ipam_and_dhcp()
    {
        var inv = Parse();

        Assert.Single(inv.NetworkViews);
        Assert.Equal(1, inv.Networks.Count(n => n.IsContainer));
        Assert.Equal(2, inv.Networks.Count(n => !n.IsContainer));
        Assert.Equal("10.0.0.0/24/0", inv.Networks.Single(n => !n.IsContainer && !n.IsIpv6).Key);

        Assert.Equal(3, inv.DhcpRanges.Count);
        Assert.Equal("10.0.0.50", inv.DhcpRanges[0].StartAddress); // numeric sort

        var failover = inv.DhcpRanges.Single(r => r.AssociationType == "Failover");
        Assert.Equal("gm-gmc", failover.FailoverName);
        Assert.Null(failover.MemberOid);

        Assert.Equal(16, inv.DhcpRanges.Single(r => r.AssociationType == "Member").MemberOid);
        Assert.True(inv.DhcpRanges.Single(r => r.AssociationType == "None").IsIpv6);

        var assoc = Assert.Single(inv.FailoverAssociations);
        Assert.Equal("gm.lab.test", inv.FindMember(assoc.PrimaryOid)!.HostName);
        Assert.Equal("gmc.lab.test", inv.FindMember(assoc.SecondaryOid)!.HostName);
    }

    [Theory]
    [InlineData("..", "_default", "._default")]
    [InlineData("._default", "network.richard", "._default.network.richard")]
    public void Zone_keys(string parent, string name, string expected) =>
        Assert.Equal(expected, BackupParser.ZoneKey(parent, name));

    [Theory]
    [InlineData("._default.network.richard", "._default")]
    [InlineData(".1", ".1")]
    public void View_key_of_zone(string zoneKey, string expected) =>
        Assert.Equal(expected, BackupParser.ViewKeyOf(zoneKey));

    [Fact]
    public void Summary_renders_counts()
    {
        var text = InventorySummary.Render(Parse());
        Assert.Contains("Lab Grid", text);
        Assert.Contains("Members (3)", text);
        Assert.Contains("gm.lab.test", text);
        Assert.Contains("Zones (4)", text);
    }
}
