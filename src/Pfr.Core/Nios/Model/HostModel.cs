namespace Pfr.Core.Nios.Model;

/// <summary>
/// A host object: a name with one or more IP addresses. A <b>DNS host</b> sits in a DNS zone and generates A / AAAA
/// (and, where the grid hosts the reverse zone, PTR) records and CNAMEs for its aliases. An <b>IPAM-only</b> host sits
/// under a <c>non_DNS_host_root</c>: it occupies its addresses in IPAM but has no DNS records at all.
/// </summary>
public sealed class HostObject
{
    /// <summary>
    /// The host's name: its full DNS name for a DNS host (server1.lab.test), or the name as entered for an
    /// IPAM-only host.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The zone it sits in, or the non_DNS_host_root it sits under when it is IPAM-only.</summary>
    public string ZoneKey { get; set; } = "";

    /// <summary>The DNS view's key, or the key of the root that holds an IPAM-only host.</summary>
    public string ViewKey { get; set; } = "";

    /// <summary>The network view the host's addresses are in.</summary>
    public int? NetworkViewId { get; set; }

    /// <summary>True for a host configured for DNS; false for an IPAM-only host.</summary>
    public bool IsDns { get; set; }

    public bool Disabled { get; set; }
    public string? Comment { get; set; }

    /// <summary>For a DNS host, its own TTL or the one it inherits from its zone; no seconds for an IPAM-only host.</summary>
    public Ttl Ttl { get; set; } = new(null, TtlSource.Grid);

    public List<HostAddress> Addresses { get; } = [];

    /// <summary>The full names of the host's aliases (they are CNAMEs for the host), for a DNS host.</summary>
    public List<string> Aliases { get; } = [];
}

/// <summary>One IP address of a host, with its DHCP settings.</summary>
public sealed class HostAddress
{
    public string Address { get; set; } = "";
    public bool IsIpv6 { get; set; }

    /// <summary>True if the address is also configured for DHCP (the host has a MAC address and a reservation).</summary>
    public bool ConfigureForDhcp { get; set; }

    public string? MacAddress { get; set; }

    /// <summary>The reverse zone the grid links the address to, if it hosts one (this is where the PTR record lives).</summary>
    public string? ReverseZoneKey { get; set; }
}
