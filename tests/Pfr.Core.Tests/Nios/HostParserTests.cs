using System.Text.Json;
using Pfr.Core.Nios.Backup;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;

namespace Pfr.Core.Tests.Nios;

public class HostParserTests
{
    private static readonly GridInventory Inventory = BackupParser.Parse(OneDbFixture.XmlStream());

    private static HostObject Host(string name) => Inventory.Hosts.Single(h => h.Name == name);

    [Fact]
    public void Every_host_is_read_with_its_addresses_and_listed_by_name()
    {
        Assert.Equal("printer,router,server1.lab.test", string.Join(",", Inventory.Hosts.Select(h => h.Name)));
        Assert.Equal(5, Inventory.Hosts.Sum(h => h.Addresses.Count));
    }

    [Fact]
    public void A_dns_host_has_its_full_name_view_aliases_and_ttl()
    {
        var host = Host("server1.lab.test");

        Assert.True(host.IsDns);
        Assert.Equal("._default.test.lab", host.ZoneKey);
        Assert.Equal("._default", host.ViewKey);
        Assert.Equal(0, host.NetworkViewId);
        Assert.Equal("files.lab.test", Assert.Single(host.Aliases));
        Assert.Equal(new Ttl(600, TtlSource.Host), host.Ttl); // its own TTL, not the zone's

        Assert.Equal("10.0.0.20,10.0.0.21,2001:db8::20", string.Join(",", host.Addresses.Select(a => a.Address)));
        Assert.Equal("._default.arpa.in-addr.10.0.0", host.Addresses[0].ReverseZoneKey);
        Assert.True(host.Addresses[2].IsIpv6);
        Assert.False(host.Addresses[0].IsIpv6);
    }

    [Fact]
    public void An_IPAM_only_host_is_kept_and_placed_in_its_roots_network_view()
    {
        var host = Host("printer");

        Assert.False(host.IsDns);
        Assert.Equal(".non_DNS_host_root", host.ZoneKey);
        Assert.Equal(0, host.NetworkViewId); // from the dns.view of the root: its addresses carry no network view
        Assert.Null(host.Ttl.Seconds);       // no DNS, so no TTL
        Assert.Empty(host.Aliases);
        Assert.Equal("10.0.0.30", Assert.Single(host.Addresses).Address);

        // It still generates no DNS records, and does not count as a DNS host.
        Assert.DoesNotContain(Inventory.Records, r => r.HostName == "printer");
        Assert.Equal(1, Inventory.HostCount);
    }

    [Fact]
    public void A_host_created_with_a_timestamp_is_found_by_its_addresses()
    {
        var host = Host("router");

        var address = Assert.Single(host.Addresses);
        Assert.Equal("10.0.0.31", address.Address);
        Assert.True(address.ConfigureForDhcp);
        Assert.Equal("00:11:22:33:44:55", address.MacAddress);
        Assert.Equal("Edge router", host.Comment);
        Assert.False(host.IsDns);
    }

    [Fact]
    public void Hosts_survive_being_stored_and_loaded()
    {
        var json = JsonSerializer.Serialize(Inventory, StoreJson.Options);
        var loaded = JsonSerializer.Deserialize<GridInventory>(json, StoreJson.Options)!;

        Assert.Equal(Inventory.Hosts.Count, loaded.Hosts.Count);
        var host = loaded.Hosts.Single(h => h.Name == "server1.lab.test");
        Assert.Equal(new Ttl(600, TtlSource.Host), host.Ttl);
        Assert.Equal(3, host.Addresses.Count);
        Assert.Equal("files.lab.test", Assert.Single(host.Aliases));
    }

    [Fact]
    public void A_snapshot_stored_before_hosts_were_read_loads_with_none()
    {
        var loaded = JsonSerializer.Deserialize<GridInventory>("""{"GridName":"Old","Members":[]}""", StoreJson.Options)!;

        Assert.Empty(loaded.Hosts);
    }
}
