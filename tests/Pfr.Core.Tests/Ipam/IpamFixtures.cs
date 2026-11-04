using Pfr.Core.Ipam;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Tests.Ipam;

/// <summary>Small grids built in code, so each IPAM rule is tested on its own. Shared with the GUI tests.</summary>
internal static class IpamFixtures
{
    /// <summary>A grid with a "default" network view (id 0) and any extra views given.</summary>
    public static GridInventory Grid(string name, params (int Id, string Name)[] views)
    {
        var inventory = new GridInventory { GridName = name };
        inventory.NetworkViews.Add(new NetworkView { Id = 0, Name = "default" });
        foreach (var (id, viewName) in views) inventory.NetworkViews.Add(new NetworkView { Id = id, Name = viewName });
        return inventory;
    }

    public static GridInventory AddNet(this GridInventory inventory, string address, int cidr, bool container = false,
        string? comment = null, bool disabled = false, int view = 0)
    {
        inventory.Networks.Add(new IpNetwork
        {
            Address = address, Cidr = cidr, IsContainer = container, Comment = comment, Disabled = disabled,
            NetworkViewId = view, IsIpv6 = address.Contains(':'),
        });
        return inventory;
    }

    public static GridInventory AddRange(this GridInventory inventory, string start, string end, int view = 0,
        string association = "Member", int? member = null, string? failover = null, string? name = null,
        string? comment = null, bool disabled = false)
    {
        inventory.DhcpRanges.Add(new DhcpRange
        {
            StartAddress = start, EndAddress = end, NetworkViewId = view, AssociationType = association,
            MemberOid = member, FailoverName = failover, Name = name, Comment = comment, Disabled = disabled,
            IsIpv6 = start.Contains(':'),
        });
        return inventory;
    }

    public static IpamGridInput Input(this GridInventory inventory, string date = "2026-02-10") =>
        new(inventory.GridName!, DateTimeOffset.Parse(date + "T00:00:00Z"), "snap-" + inventory.GridName, inventory);

    /// <summary>
    /// A DNS view "Internal" (key ._default) in the given network view, with the zone lab.test and the reverse zone
    /// 0.0.10.in-addr.arpa, so hosts and records have somewhere to live.
    /// </summary>
    public static GridInventory WithDns(this GridInventory inventory, int networkView = 0)
    {
        inventory.DnsViews.Add(new DnsView { Key = "._default", Name = "Internal", NetworkViewId = networkView });
        inventory.Zones.Add(new DnsZone { Key = "._default.lab.test", ViewKey = "._default", Fqdn = "lab.test", ZoneType = "Authoritative" });
        inventory.Zones.Add(new DnsZone
        {
            Key = "._default.arpa.in-addr.10.0.0", ViewKey = "._default", Fqdn = "0.0.10.in-addr.arpa",
            ZoneType = "Authoritative", IsReverse = true,
        });
        return inventory;
    }

    /// <summary>
    /// A host with the given addresses. A DNS host (the default) also generates, like NIOS, an A or AAAA record for each
    /// address and a PTR record for each 10.0.0.x address; an IPAM-only host generates none.
    /// </summary>
    public static GridInventory AddHost(this GridInventory inventory, string name, string[] addresses, bool dns = true,
        int? networkView = 0, bool dhcp = false, string? mac = null, string[]? aliases = null, int? ttl = null,
        bool disabled = false, string? comment = null)
    {
        var hostTtl = !dns ? new Ttl(null, TtlSource.Grid) : ttl is { } own ? new Ttl(own, TtlSource.Host) : new Ttl(28800, TtlSource.Zone);
        var host = new HostObject
        {
            Name = name,
            ZoneKey = dns ? "._default.lab.test" : ".non_DNS_host_root",
            ViewKey = dns ? "._default" : ".non_DNS_host_root",
            NetworkViewId = networkView,
            IsDns = dns,
            Disabled = disabled,
            Comment = comment,
            Ttl = hostTtl,
        };
        foreach (var address in addresses)
        {
            host.Addresses.Add(new HostAddress { Address = address, IsIpv6 = address.Contains(':'), ConfigureForDhcp = dhcp, MacAddress = mac });
        }
        if (aliases is not null) host.Aliases.AddRange(aliases);
        inventory.Hosts.Add(host);

        if (!dns) return inventory;
        foreach (var address in addresses)
        {
            inventory.Records.Add(new DnsRecord
            {
                Type = address.Contains(':') ? DnsRecordType.AAAA : DnsRecordType.A, ZoneKey = "._default.lab.test", ViewKey = "._default",
                Name = name.Replace(".lab.test", ""), Fqdn = name, Value = address, HostName = name, Ttl = hostTtl, Disabled = disabled,
            });
            if (address.StartsWith("10.0.0.", StringComparison.Ordinal))
            {
                inventory.Records.Add(new DnsRecord
                {
                    Type = DnsRecordType.PTR, ZoneKey = "._default.arpa.in-addr.10.0.0", ViewKey = "._default",
                    Name = address[7..], Fqdn = ReverseName(address), Value = name, HostName = name, Ttl = hostTtl, Disabled = disabled,
                });
            }
        }
        return inventory;
    }

    /// <summary>A record that no host generates: an A / AAAA with the address as its value, or a PTR for a 10.0.0.x name.</summary>
    public static GridInventory AddRecord(this GridInventory inventory, DnsRecordType type, string fqdn, string value,
        int? ttl = null, bool disabled = false)
    {
        var isPtr = type == DnsRecordType.PTR;
        inventory.Records.Add(new DnsRecord
        {
            Type = type,
            ZoneKey = isPtr ? "._default.arpa.in-addr.10.0.0" : "._default.lab.test",
            ViewKey = "._default",
            Name = fqdn,
            Fqdn = fqdn,
            Value = value,
            Ttl = ttl is { } own ? new Ttl(own, TtlSource.Record) : new Ttl(28800, TtlSource.Zone),
            Disabled = disabled,
        });
        return inventory;
    }

    /// <summary>"10.0.0.20" → "20.0.0.10.in-addr.arpa".</summary>
    public static string ReverseName(string address) =>
        string.Join('.', address.Split('.').Reverse()) + ".in-addr.arpa";
}
