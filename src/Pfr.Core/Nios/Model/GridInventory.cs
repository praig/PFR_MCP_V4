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
}
