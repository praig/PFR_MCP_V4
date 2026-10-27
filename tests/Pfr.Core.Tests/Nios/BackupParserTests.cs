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
    public void Detects_member_services_in_display_order()
    {
        var inv = Parse();
        Assert.Equal(["DNS", "DHCP", "DTC", "NTP"], inv.Members.Single(m => m.Oid == 0).Services);
        Assert.Equal(["DNS", "TFTP"], inv.Members.Single(m => m.Oid == 7).Services); // no DTC license
        // DTC license but DNS off -> no DTC; reporting indexer -> Reporting; ATP flag false -> not listed.
        Assert.Equal(["DHCP", "Reporting"], inv.Members.Single(m => m.Oid == 16).Services);
    }

    [Fact]
    public void Maps_node_licenses_to_members()
    {
        var inv = Parse();
        Assert.Equal(["dns", "dtc"], inv.Members.Single(m => m.Oid == 0).Licenses);
        Assert.Equal(["dtc"], inv.Members.Single(m => m.Oid == 16).Licenses); // both HA nodes, de-duplicated
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

        var cnames = inv.Records.Where(r => r.Type == DnsRecordType.CNAME && !r.IsFromHost).ToList();
        Assert.Equal(2, cnames.Count); // blacklist pseudo-record excluded
        Assert.True(cnames.Single(c => c.Name == "old").Disabled);

        Assert.Equal(1, inv.HostCount); // the non-DNS host is not counted
    }

    [Fact]
    public void Expands_host_objects_into_the_records_they_serve()
    {
        var inv = Parse();
        var fromHost = inv.Records.Where(r => r.HostName == "server1.lab.test").ToList();

        Assert.Equal(
            [
                (DnsRecordType.A, "server1.lab.test", "10.0.0.20"),
                (DnsRecordType.A, "server1.lab.test", "10.0.0.21"),
                (DnsRecordType.AAAA, "server1.lab.test", "2001:db8::20"),
                (DnsRecordType.CNAME, "files.lab.test", "server1.lab.test"),
                (DnsRecordType.PTR, "20.0.0.10.in-addr.arpa", "server1.lab.test"), // only the linked address
            ],
            fromHost.Select(r => (r.Type, r.Fqdn, r.Value)).OrderBy(x => x.Type).ThenBy(x => x.Value));

        var ptr = fromHost.Single(r => r.Type == DnsRecordType.PTR);
        Assert.Equal("._default.arpa.in-addr.10.0.0", ptr.ZoneKey);
        Assert.Equal("20", ptr.Name);

        Assert.DoesNotContain(inv.Records, r => r.Value == "10.0.0.30"); // non-DNS host
        Assert.All(inv.Records.Where(r => r.HostName is null), r => Assert.False(r.IsFromHost));
    }

    [Fact]
    public void Reads_other_record_types_in_zone_file_style()
    {
        var inv = Parse();
        string Value(DnsRecordType t) => inv.Records.Single(r => r.Type == t).Value;

        Assert.Equal("10 mail.lab.test", Value(DnsRecordType.MX));
        Assert.Equal("10 20 5060 sip.lab.test", Value(DnsRecordType.SRV));
        Assert.Equal("\"v=spf1 -all\"", Value(DnsRecordType.TXT));
        Assert.Equal("90 50 \"S\" \"SIP+D2T\" \"\" _sip._tcp.lab.test", Value(DnsRecordType.NAPTR));
        Assert.Equal("0 issue \"letsencrypt.org\"", Value(DnsRecordType.CAA));

        // NS: auto-generated NIOS NS records are skipped; delegations are listed under the delegated zone.
        var ns = Assert.Single(inv.Records, r => r.Type == DnsRecordType.NS);
        Assert.Equal("aws.lab.test", ns.Fqdn);
        Assert.Equal("ns-1.awsdns.test", ns.Value);
    }

    [Theory]
    [InlineData("10.0.0.20", "20.0.0.10.in-addr.arpa")]
    [InlineData("2001:db8::1", "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa")]
    public void Reverse_names(string address, string expected) =>
        Assert.Equal(expected, BackupParser.ReverseName(address));

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

        var assoc = Assert.Single(inv.FailoverAssociations, f => !f.IsMicrosoft);
        Assert.Equal("gm.lab.test", inv.FindMember(assoc.PrimaryOid)!.HostName);
        Assert.Equal("gmc.lab.test", inv.FindMember(assoc.SecondaryOid)!.HostName);
    }

    [Fact]
    public void Distinguishes_microsoft_failover_associations()
    {
        var ms = Parse().FailoverAssociations.Where(f => f.IsMicrosoft).ToList();
        Assert.Equal(2, ms.Count);
        Assert.Equal(["192.0.2.31", "192.0.2.32"], ms.Select(f => f.MsServerAddress)); // sorted by address
        Assert.Equal("192.0.2.32", ms[0].MsPartnerAddress);
        Assert.All(ms, f => Assert.Null(f.PrimaryOid));

        var text = InventorySummary.Render(Parse());
        Assert.Contains("192.0.2.31 <-> 192.0.2.32", text);
    }

    [Fact]
    public void Skips_internal_roots_that_look_like_views()
    {
        var names = Parse().DnsViews.Select(v => v.Key).ToList();
        Assert.Equal([".1", "._default"], names.Order(StringComparer.Ordinal));
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
