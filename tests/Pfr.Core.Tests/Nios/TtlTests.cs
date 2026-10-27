using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Nios;

public class TtlTests
{
    private static GridInventory Parse() => BackupParser.Parse(OneDbFixture.XmlStream());

    private static DnsRecord Record(GridInventory inv, string fqdn, DnsRecordType type, string? viewKey = "._default") =>
        inv.Records.Single(r => r.Fqdn == fqdn && r.Type == type && (viewKey is null || r.ViewKey == viewKey));

    [Fact]
    public void Grid_default_and_zone_overrides()
    {
        var inv = Parse();
        Assert.Equal(28800, inv.DefaultTtl);

        Assert.Equal(new Ttl(3600, TtlSource.Zone), inv.Zones.Single(z => z.Key == "._default.test.lab").DefaultTtl);
        Assert.Equal(new Ttl(28800, TtlSource.Grid), inv.Zones.Single(z => z.Key == ".1.test.lab").DefaultTtl);
        // Delegated zones don't serve records, so they have no default TTL.
        Assert.Null(inv.Zones.Single(z => z.ZoneType == "Delegated").DefaultTtl.Seconds);
        // No SOA object at all: the grid default applies.
        Assert.Equal(new Ttl(28800, TtlSource.Grid), inv.Zones.Single(z => z.Fqdn == "0.0.10.in-addr.arpa").DefaultTtl);
    }

    [Fact]
    public void Records_set_their_own_ttl_or_inherit_their_zone()
    {
        var inv = Parse();

        Assert.Equal(new Ttl(300, TtlSource.Record), Record(inv, "www.lab.test", DnsRecordType.A).Ttl);
        Assert.Equal(new Ttl(3600, TtlSource.Zone), Record(inv, "lab.test", DnsRecordType.A).Ttl);
        Assert.Equal(new Ttl(3600, TtlSource.Zone), Record(inv, "lab.test", DnsRecordType.TXT).Ttl); // option 0 wins over a stale ttl
        Assert.Equal(new Ttl(7200, TtlSource.Record), Record(inv, "voip.lab.test", DnsRecordType.NAPTR).Ttl);
        Assert.Equal(new Ttl(28800, TtlSource.Grid), Record(inv, "www.lab.test", DnsRecordType.AAAA, ".1").Ttl);
        Assert.Equal(new Ttl(28800, TtlSource.Grid), Record(inv, "10.0.0.10.in-addr.arpa", DnsRecordType.PTR).Ttl);
    }

    [Fact]
    public void Host_ttl_applies_to_every_record_the_host_generates()
    {
        var inv = Parse();
        var fromHost = inv.Records.Where(r => r.HostName == "server1.lab.test").ToList();
        Assert.Equal(5, fromHost.Count);
        Assert.All(fromHost, r => Assert.Equal(new Ttl(600, TtlSource.Host), r.Ttl));
    }

    [Fact]
    public void Delegation_ns_takes_the_parent_zone_ttl()
    {
        var ns = Parse().Records.Single(r => r.Type == DnsRecordType.NS);
        Assert.Equal(new Ttl(3600, TtlSource.Zone), ns.Ttl);
    }

    [Theory]
    [InlineData(300, "5m")]
    [InlineData(3600, "1h")]
    [InlineData(28800, "8h")]
    [InlineData(86400, "1d")]
    [InlineData(604800, "1w")]
    [InlineData(740700, "1w1d13h45m")]
    [InlineData(90061, "1d1h1m1s")]
    [InlineData(0, "0s")]
    public void Human_durations(int seconds, string expected) => Assert.Equal(expected, Ttl.Human(seconds));

    [Fact]
    public void Display_and_inheritance_flags()
    {
        Assert.Equal("28800 (8h)", new Ttl(28800, TtlSource.Grid).Display);
        Assert.Equal("", new Ttl(null, TtlSource.Grid).Display);
        Assert.False(new Ttl(300, TtlSource.Record).IsInherited);
        Assert.True(new Ttl(300, TtlSource.Host).IsInherited);
        Assert.True(new Ttl(300, TtlSource.Zone).IsInherited);
    }
}
