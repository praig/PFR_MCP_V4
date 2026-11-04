namespace Pfr.Core.Nios.Model;

/// <summary>
/// Everything parsed from one grid backup. Contains no secrets: administrator accounts are included by name,
/// group and permissions, but never their passwords, hashes, keys or certificates.
/// </summary>
public sealed class GridInventory
{
    public string? GridName { get; set; }

    /// <summary>Grid default TTL for zones that don't override it (Grid DNS properties).</summary>
    public int? DefaultTtl { get; set; }

    /// <summary>NIOS version from the backup header, e.g. "9.0.8-54134-924d94e0dcd0".</summary>
    public string? NiosVersion { get; set; }

    public List<GridMember> Members { get; } = [];
    public List<NetworkView> NetworkViews { get; } = [];
    public List<DnsView> DnsViews { get; } = [];
    public List<DnsZone> Zones { get; } = [];
    public List<IpNetwork> Networks { get; } = [];
    public List<DhcpRange> DhcpRanges { get; } = [];
    public List<FailoverAssociation> FailoverAssociations { get; } = [];
    public List<DnsRecord> Records { get; } = [];
    public List<GridLicense> Licenses { get; } = [];

    /// <summary>
    /// True when the backup's DHCP lease times and the options set on ranges were read (<see cref="DhcpRange.LeaseTimeSeconds"/>,
    /// <see cref="DhcpRange.Options"/>). A snapshot saved by an older version has neither, and needs the backup imported again.
    /// </summary>
    public bool DhcpOptionsRead { get; set; }

    /// <summary>
    /// True when each DNS record's creator and last-queried time were read (<see cref="DnsRecord.Creator"/>,
    /// <see cref="DnsRecord.LastQueried"/>). A snapshot saved by an older version has neither, and needs the backup imported again.
    /// </summary>
    public bool RecordOriginsRead { get; set; }

    /// <summary>
    /// True when each A record's and host's comment, creation time and extensible attributes were read (<see cref="DnsRecord.Comment"/>,
    /// <see cref="DnsRecord.Created"/>, <see cref="DnsRecord.ExtensibleAttributes"/>, <see cref="HostObject.Created"/>). A snapshot
    /// saved by an older version has none of them, and needs the backup imported again.
    /// </summary>
    public bool RecordDetailsRead { get; set; }

    /// <summary>The DHCP options set at the grid level, which every range inherits unless a nearer setting overrides them.</summary>
    public List<DhcpOption> GridDhcpOptions { get; } = [];

    /// <summary>DHCP options the backup says belong to a range that could not be found or read, so they are not listed.</summary>
    public int UnmatchedRangeOptions { get; set; }

    // Administrators. Names, flags and relationships only; never passwords, hashes, keys or certificates.
    public List<AdminGroup> AdminGroups { get; } = [];
    public List<AdminUser> AdminUsers { get; } = [];
    public List<AdminRole> AdminRoles { get; } = [];
    public List<AdminGroupRole> AdminGroupRoles { get; } = [];
    public List<AdminPermission> AdminPermissions { get; } = [];
    public List<AdminAccess> AdminAccessRights { get; } = [];
    public List<AdminAuthMethod> AdminAuthMethods { get; } = [];

    /// <summary>
    /// Every host object with its addresses, including IPAM-only hosts (which have no DNS records, so they appear
    /// nowhere else). A DNS host's records are also in <see cref="Records"/>.
    /// </summary>
    public List<HostObject> Hosts { get; } = [];

    /// <summary>Host objects in DNS zones (each also appears as A/AAAA/PTR/CNAME records).</summary>
    public int HostCount { get; set; }

    public GridMember? FindMember(int? oid) =>
        oid is null ? null : Members.FirstOrDefault(m => m.Oid == oid);
}

public sealed class GridMember
{
    /// <summary>Internal member id used for references inside onedb.xml.</summary>
    public int Oid { get; set; }
    public string HostName { get; set; } = "";
    public string? Ipv4Address { get; set; }
    public string? Ipv6Address { get; set; }
    /// <summary>e.g. VNIOS, INFOBLOX.</summary>
    public string? MemberType { get; set; }
    public bool IsGridMaster { get; set; }
    public bool IsGridMasterCandidate { get; set; }
    public bool HaEnabled { get; set; }
    public string? UpgradeGroup { get; set; }
    public List<GridNode> Nodes { get; } = [];

    /// <summary>Services enabled on this member, in display order (e.g. DNS, DHCP, DTC, NTP).</summary>
    public List<string> Services { get; } = [];

    /// <summary>License types installed on this member's nodes (e.g. dns, dhcp, dtc). Never the license keys.</summary>
    public List<string> Licenses { get; } = [];

    /// <summary>Earliest expiry among licenses that apply to this member, if any expire.</summary>
    public DateTimeOffset? NextLicenseExpiry { get; set; }

    public string Role =>
        IsGridMaster ? "Grid Master"
        : IsGridMasterCandidate ? "Grid Master Candidate"
        : "Member";
}

/// <summary>A physical or virtual appliance; an HA member has two.</summary>
public sealed class GridNode
{
    /// <summary>Internal node id; licenses reference it.</summary>
    public int? PhysicalOid { get; set; }
    public int Position { get; set; }
    public string? HardwareType { get; set; }
    public string? Platform { get; set; }
    public string? Hypervisor { get; set; }
    public string? SerialNumber { get; set; }
    public string? Ipv4Address { get; set; }
    public string? Ipv6Address { get; set; }
    public string? MgmtAddress { get; set; }
}

public sealed class NetworkView
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DnsView
{
    /// <summary>onedb key, e.g. "._default".</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int? NetworkViewId { get; set; }
    public bool Disabled { get; set; }
    public bool RecursionEnabled { get; set; }
    public bool DnssecValidationEnabled { get; set; }
}

public sealed class DnsZone
{
    /// <summary>onedb key, e.g. "._default.network.richard".</summary>
    public string Key { get; set; } = "";
    public string ViewKey { get; set; } = "";
    public string Fqdn { get; set; } = "";
    /// <summary>Authoritative, Forward, Delegated, Stub, ResponsePolicy.</summary>
    public string ZoneType { get; set; } = "";
    /// <summary>Grid, External, Microsoft, None.</summary>
    public string? PrimaryType { get; set; }
    public bool IsReverse { get; set; }
    public bool Disabled { get; set; }
    public bool DnssecEnabled { get; set; }

    /// <summary>Default TTL for records in this zone: the zone's override or the grid default.</summary>
    public Ttl DefaultTtl { get; set; } = new(null, TtlSource.Grid);
    public List<int> GridPrimaryOids { get; } = [];
    public List<int> GridSecondaryOids { get; } = [];
    public List<NameServer> ExternalSecondaries { get; } = [];
    public List<NameServer> MicrosoftPrimaries { get; } = [];
    public List<NameServer> DelegatedServers { get; } = [];
}

public sealed record NameServer(string Name, string? Address);

public sealed class IpNetwork
{
    public string Address { get; set; } = "";
    public int Cidr { get; set; }
    public int NetworkViewId { get; set; }
    public bool IsIpv6 { get; set; }
    public bool IsContainer { get; set; }
    public bool Disabled { get; set; }
    public string? Comment { get; set; }
    /// <summary>onedb key, e.g. "10.9.16.0/24/0" (address/cidr/network view).</summary>
    public string Key => $"{Address}/{Cidr}/{NetworkViewId}";
    public string Cidrs => $"{Address}/{Cidr}";

    /// <summary>The key of the network container this one sits in; null when it is directly in its network view.</summary>
    public string? ParentKey { get; set; }

    /// <summary>The DHCP options set directly on this network or container (a range inherits them).</summary>
    public List<DhcpOption> Options { get; } = [];
}

public sealed class DhcpRange
{
    /// <summary>Parent network key, e.g. "10.9.16.0/24/0".</summary>
    public string NetworkKey { get; set; } = "";
    public string StartAddress { get; set; } = "";
    public string EndAddress { get; set; } = "";
    public int NetworkViewId { get; set; }
    public bool IsIpv6 { get; set; }
    /// <summary>None, Member, Failover, MsFailover, MsServer.</summary>
    public string AssociationType { get; set; } = "";
    public int? MemberOid { get; set; }
    public string? FailoverName { get; set; }
    public bool IsMicrosoft { get; set; }
    public bool Disabled { get; set; }
    public string? Name { get; set; }
    public string? Comment { get; set; }

    /// <summary>ADDRESS for a range of addresses; PREFIX for a DHCPv6 range that hands out prefixes.</summary>
    public string AddressType { get; set; } = "ADDRESS";

    /// <summary>A PREFIX range: its first and last prefix, and the length of the prefixes it hands out. (Its start and end address are empty.)</summary>
    public string? PrefixStart { get; set; }
    public string? PrefixEnd { get; set; }
    public int? PrefixBits { get; set; }

    /// <summary>
    /// The range's <b>effective</b> lease time in seconds: its own when it overrides, otherwise the one it inherits from its
    /// network, a network container above it, or the grid (see <see cref="LeaseTimeSource"/>). The value stored on a range that
    /// does not override is not used, because it can be stale (a range under a network that sets 600 seconds can still store
    /// the grid's 43200). For DHCPv6 this is the valid lifetime. Null when not read (a snapshot from an older version).
    /// </summary>
    public int? LeaseTimeSeconds { get; set; }

    /// <summary>Where <see cref="LeaseTimeSeconds"/> is set: Range, Network, Network container or Grid.</summary>
    public string? LeaseTimeSource { get; set; }

    /// <summary>True when the range sets its own lease time.</summary>
    public bool LeaseTimeOverridden { get; set; }

    /// <summary>The DHCP options set directly on this range (not those inherited from its network, member or the grid), by number.</summary>
    public List<DhcpOption> Options { get; } = [];
}

/// <summary>A DHCP option set on a range: its number, its value as entered, and its name when the backup defines it.</summary>
public sealed class DhcpOption
{
    public int Code { get; set; }

    /// <summary>The option's name from its definition, e.g. "routers"; null when the backup has no definition for it.</summary>
    public string? Name { get; set; }

    /// <summary>The option space it belongs to, e.g. "DHCP" or "DHCP Standard Options".</summary>
    public string Space { get; set; } = "";

    public string Value { get; set; } = "";
    public bool IsIpv4 { get; set; } = true;
}

public sealed class FailoverAssociation
{
    public string Name { get; set; } = "";
    public int? PrimaryOid { get; set; }
    public int? SecondaryOid { get; set; }
    public bool IsMicrosoft { get; set; }
    /// <summary>Microsoft failover only: this server's address.</summary>
    public string? MsServerAddress { get; set; }
    /// <summary>Microsoft failover only: the partner server's address.</summary>
    public string? MsPartnerAddress { get; set; }
    /// <summary>Microsoft failover only: LOADBALANCE or HOTSTANDBY.</summary>
    public string? MsMode { get; set; }
}

public enum DnsRecordType { A, AAAA, CNAME, PTR, MX, SRV, TXT, NAPTR, CAA, NS }

public sealed class DnsRecord
{
    public DnsRecordType Type { get; set; }
    public string ZoneKey { get; set; } = "";
    public string ViewKey { get; set; } = "";
    /// <summary>Name relative to the zone ("" for the zone apex).</summary>
    public string Name { get; set; } = "";
    public string Fqdn { get; set; } = "";
    /// <summary>Record data in zone-file style, e.g. an address, a target, or "10 20 80 sip.example.com" for SRV.</summary>
    public string Value { get; set; } = "";
    /// <summary>FQDN of the host object that generates this record, or null for a standalone record.</summary>
    public string? HostName { get; set; }
    public bool IsFromHost => HostName is not null;

    /// <summary>Effective TTL and where it is set (record, host, zone or grid).</summary>
    public Ttl Ttl { get; set; } = new(null, TtlSource.Grid);
    public bool Disabled { get; set; }

    /// <summary>
    /// How the record came to exist, as NIOS says: STATIC (entered by an administrator), DYNAMIC (registered by a DHCP or DDNS
    /// client) or SYSTEM (created by NIOS itself). A record made by a host object has its host's. Null when not read.
    /// </summary>
    public string? Creator { get; set; }

    /// <summary>
    /// When the record was last asked for in a DNS query, as NIOS recorded it (UTC); null when NIOS has no such time for it, which
    /// means it has not been queried since NIOS began keeping the time, or never had it switched on.
    /// </summary>
    public DateTimeOffset? LastQueried { get; set; }

    /// <summary>The comment entered on the record (a record a host object makes has the host's); null when there is none.</summary>
    public string? Comment { get; set; }

    /// <summary>When the record was created (UTC), as NIOS recorded it; null when the backup has no creation time for it.</summary>
    public DateTimeOffset? Created { get; set; }

    /// <summary>
    /// The record's extensible attributes by name (a value inherited from a network is included, as it applies); null when it has
    /// none. Read only for A records, which is all the record creation report needs.
    /// </summary>
    public Dictionary<string, string>? ExtensibleAttributes { get; set; }
}
