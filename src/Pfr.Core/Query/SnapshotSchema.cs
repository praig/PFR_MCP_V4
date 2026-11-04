using Pfr.Core.Nios.Model;

namespace Pfr.Core.Query;

/// <summary>A column of a queryable table.</summary>
public sealed record ColumnInfo(string Name, string SqlType, string Description);

/// <summary>A queryable table: what it holds, its columns, and (once loaded) how many rows.</summary>
public sealed record TableInfo(string Name, string Description, IReadOnlyList<ColumnInfo> Columns, int RowCount);

/// <summary>One table with its rows, ready to load.</summary>
internal sealed record TableData(string Name, string Description, IReadOnlyList<ColumnInfo> Columns, IEnumerable<object?[]> Rows);

/// <summary>
/// The tables a snapshot is turned into for SQL. Keys and ids are kept so tables can be joined:
/// members.oid = member_services.member_oid, zones.view_key = dns_views.view_key,
/// records.zone_key = zones.zone_key, dhcp_ranges.network_key = networks.network_key, and so on.
/// Administrators: admin_users.group_name = admin_groups.name, admin_group_roles link groups to roles, and
/// admin_permissions.owner_name is a group or a role (owner_type says which) whose resource_key is a zone_key or
/// a network_key.
/// Booleans are 0/1, dates are yyyy-MM-dd text, and nothing secret is ever in a snapshot.
/// </summary>
internal static class SnapshotSchema
{
    private const string Text = "TEXT";
    private const string Int = "INTEGER";

    /// <summary>The table and column descriptions (with zero rows), for browsing before anything is loaded.</summary>
    public static IReadOnlyList<TableInfo> Describe() =>
        Build(new GridInventory()).Select(t => new TableInfo(t.Name, t.Description, t.Columns, 0)).ToList();

    public static IReadOnlyList<TableData> Build(GridInventory inventory)
    {
        var nodes = inventory.Members.SelectMany(m => m.Nodes.Select(n => (Member: m, Node: n)));
        var services = inventory.Members.SelectMany(m => m.Services.Select(s => (Member: m, Service: s)));

        return
        [
            Table("grid", "The grid itself (one row).", [inventory],
                Col<GridInventory>("grid_name", Text, "Grid name", g => g.GridName),
                Col<GridInventory>("nios_version", Text, "NIOS version from the backup header", g => g.NiosVersion),
                Col<GridInventory>("default_ttl", Int, "Grid default TTL in seconds", g => g.DefaultTtl),
                Col<GridInventory>("host_objects", Int, "Number of host objects in DNS zones", g => g.HostCount)),

            Table("members", "Grid members.", inventory.Members,
                Col<GridMember>("oid", Int, "Internal member id (other tables refer to it as member_oid)", m => m.Oid),
                Col<GridMember>("host_name", Text, "Host name", m => m.HostName),
                Col<GridMember>("role", Text, "Grid Master, Grid Master Candidate or Member", m => m.Role),
                Col<GridMember>("member_type", Text, "e.g. VNIOS, INFOBLOX", m => m.MemberType),
                Col<GridMember>("is_grid_master", Int, "1 for the Grid Master", m => Flag(m.IsGridMaster)),
                Col<GridMember>("is_gm_candidate", Int, "1 for a Grid Master candidate", m => Flag(m.IsGridMasterCandidate)),
                Col<GridMember>("ha_enabled", Int, "1 for an HA pair", m => Flag(m.HaEnabled)),
                Col<GridMember>("ipv4", Text, "IPv4 address", m => m.Ipv4Address),
                Col<GridMember>("ipv6", Text, "IPv6 address", m => m.Ipv6Address),
                Col<GridMember>("upgrade_group", Text, "Upgrade group", m => m.UpgradeGroup),
                Col<GridMember>("node_count", Int, "Number of nodes (2 for HA)", m => m.Nodes.Count),
                Col<GridMember>("services", Text, "Enabled services, comma separated", m => string.Join(", ", m.Services)),
                Col<GridMember>("licenses", Text, "Installed license types, comma separated", m => string.Join(", ", m.Licenses)),
                Col<GridMember>("next_license_expiry", Text, "Earliest license expiry (yyyy-MM-dd)", m => Date(m.NextLicenseExpiry))),

            Table("nodes", "Appliances: one per member, two for an HA pair.", nodes,
                Col<(GridMember Member, GridNode Node)>("member_oid", Int, "The member", r => r.Member.Oid),
                Col<(GridMember Member, GridNode Node)>("member_host_name", Text, "The member's host name", r => r.Member.HostName),
                Col<(GridMember Member, GridNode Node)>("position", Int, "0 or 1 within an HA pair", r => r.Node.Position),
                Col<(GridMember Member, GridNode Node)>("physical_oid", Int, "Internal node id", r => r.Node.PhysicalOid),
                Col<(GridMember Member, GridNode Node)>("hardware_type", Text, "Hardware type", r => r.Node.HardwareType),
                Col<(GridMember Member, GridNode Node)>("platform", Text, "Platform", r => r.Node.Platform),
                Col<(GridMember Member, GridNode Node)>("hypervisor", Text, "Hypervisor, for virtual appliances", r => r.Node.Hypervisor),
                Col<(GridMember Member, GridNode Node)>("serial_number", Text, "Serial number", r => r.Node.SerialNumber),
                Col<(GridMember Member, GridNode Node)>("ipv4", Text, "IPv4 address", r => r.Node.Ipv4Address),
                Col<(GridMember Member, GridNode Node)>("ipv6", Text, "IPv6 address", r => r.Node.Ipv6Address),
                Col<(GridMember Member, GridNode Node)>("mgmt_address", Text, "Management address", r => r.Node.MgmtAddress)),

            Table("member_services", "One row per service enabled on a member.", services,
                Col<(GridMember Member, string Service)>("member_oid", Int, "The member", r => r.Member.Oid),
                Col<(GridMember Member, string Service)>("member_host_name", Text, "The member's host name", r => r.Member.HostName),
                Col<(GridMember Member, string Service)>("service", Text, "Service, e.g. DNS, DHCP, NTP", r => r.Service)),

            Table("licenses", "Installed licenses (never the keys).", inventory.Licenses,
                Col<GridLicense>("type", Text, "License code, e.g. dns, dtc", l => l.Type),
                Col<GridLicense>("name", Text, "Friendly license name", l => l.Name),
                Col<GridLicense>("is_grid_wide", Int, "1 if it applies to every member", l => Flag(l.IsGridWide)),
                Col<GridLicense>("member_oid", Int, "The member (empty for grid-wide)", l => l.MemberOid),
                Col<GridLicense>("node_position", Int, "Node within an HA pair", l => l.NodePosition),
                Col<GridLicense>("node_serial", Text, "Node serial number", l => l.NodeSerial),
                Col<GridLicense>("kind", Text, "e.g. STATIC, DYNAMIC, PAYG", l => l.Kind),
                Col<GridLicense>("expires", Text, "Expiry date (yyyy-MM-dd); empty if permanent", l => Date(l.Expires))),

            Table("network_views", "Network views.", inventory.NetworkViews,
                Col<NetworkView>("id", Int, "Network view id", v => v.Id),
                Col<NetworkView>("name", Text, "Name", v => v.Name)),

            Table("dns_views", "DNS views.", inventory.DnsViews,
                Col<DnsView>("view_key", Text, "View key, e.g. ._default", v => v.Key),
                Col<DnsView>("name", Text, "Name", v => v.Name),
                Col<DnsView>("network_view_id", Int, "The network view", v => v.NetworkViewId),
                Col<DnsView>("disabled", Int, "1 if disabled", v => Flag(v.Disabled)),
                Col<DnsView>("recursion_enabled", Int, "1 if recursion is on", v => Flag(v.RecursionEnabled)),
                Col<DnsView>("dnssec_validation_enabled", Int, "1 if DNSSEC validation is on", v => Flag(v.DnssecValidationEnabled))),

            Table("zones", "DNS zones.", inventory.Zones,
                Col<DnsZone>("zone_key", Text, "Zone key, e.g. ._default.example.com", z => z.Key),
                Col<DnsZone>("view_key", Text, "The DNS view", z => z.ViewKey),
                Col<DnsZone>("fqdn", Text, "Zone name", z => z.Fqdn),
                Col<DnsZone>("zone_type", Text, "Authoritative, Forward, Delegated, Stub or ResponsePolicy", z => z.ZoneType),
                Col<DnsZone>("primary_type", Text, "Grid, External, Microsoft or None", z => z.PrimaryType),
                Col<DnsZone>("is_reverse", Int, "1 for a reverse zone", z => Flag(z.IsReverse)),
                Col<DnsZone>("disabled", Int, "1 if disabled", z => Flag(z.Disabled)),
                Col<DnsZone>("dnssec_enabled", Int, "1 if signed", z => Flag(z.DnssecEnabled)),
                Col<DnsZone>("default_ttl", Int, "Default TTL in seconds", z => z.DefaultTtl.Seconds),
                Col<DnsZone>("default_ttl_source", Text, "Where the TTL is set: Zone or Grid", z => z.DefaultTtl.Source.ToString())),

            Table("zone_servers", "The servers of each zone, one row per role.", ZoneServers(inventory),
                Col<ZoneServer>("zone_key", Text, "The zone", s => s.Zone.Key),
                Col<ZoneServer>("fqdn", Text, "The zone's name", s => s.Zone.Fqdn),
                Col<ZoneServer>("role", Text,
                    "grid_primary, grid_secondary, external_secondary, microsoft_primary or delegated", s => s.Role),
                Col<ZoneServer>("member_oid", Int, "The member, for grid roles", s => s.MemberOid),
                Col<ZoneServer>("name", Text, "Host name of the member or server", s => s.Name),
                Col<ZoneServer>("address", Text, "Server address, if known", s => s.Address)),

            Table("networks", "Networks and network containers.", inventory.Networks,
                Col<IpNetwork>("network_key", Text, "Network key, e.g. 10.0.0.0/24/0 (address/length/network view)", n => n.Key),
                Col<IpNetwork>("address", Text, "Network address", n => n.Address),
                Col<IpNetwork>("cidr", Int, "Prefix length", n => n.Cidr),
                Col<IpNetwork>("prefix", Text, "Address/length, e.g. 10.0.0.0/24", n => n.Cidrs),
                Col<IpNetwork>("network_view_id", Int, "The network view", n => n.NetworkViewId),
                Col<IpNetwork>("is_ipv6", Int, "1 for IPv6", n => Flag(n.IsIpv6)),
                Col<IpNetwork>("is_container", Int, "1 for a network container", n => Flag(n.IsContainer)),
                Col<IpNetwork>("disabled", Int, "1 if disabled", n => Flag(n.Disabled)),
                Col<IpNetwork>("comment", Text, "Comment", n => n.Comment)),

            Table("dhcp_ranges", "DHCP ranges.", inventory.DhcpRanges,
                Col<DhcpRange>("network_key", Text, "The network", r => r.NetworkKey),
                Col<DhcpRange>("start_address", Text, "First address", r => r.StartAddress),
                Col<DhcpRange>("end_address", Text, "Last address", r => r.EndAddress),
                Col<DhcpRange>("network_view_id", Int, "The network view", r => r.NetworkViewId),
                Col<DhcpRange>("is_ipv6", Int, "1 for IPv6", r => Flag(r.IsIpv6)),
                Col<DhcpRange>("association_type", Text, "None, Member, Failover, MsFailover or MsServer", r => r.AssociationType),
                Col<DhcpRange>("member_oid", Int, "The serving member", r => r.MemberOid),
                Col<DhcpRange>("failover_name", Text, "The failover association", r => r.FailoverName),
                Col<DhcpRange>("is_microsoft", Int, "1 if served by Microsoft DHCP", r => Flag(r.IsMicrosoft)),
                Col<DhcpRange>("disabled", Int, "1 if disabled", r => Flag(r.Disabled)),
                Col<DhcpRange>("name", Text, "Name", r => r.Name),
                Col<DhcpRange>("comment", Text, "Comment", r => r.Comment)),

            Table("failover", "DHCP failover associations.", inventory.FailoverAssociations,
                Col<FailoverAssociation>("name", Text, "Name", f => f.Name),
                Col<FailoverAssociation>("primary_oid", Int, "Primary member", f => f.PrimaryOid),
                Col<FailoverAssociation>("secondary_oid", Int, "Secondary member", f => f.SecondaryOid),
                Col<FailoverAssociation>("is_microsoft", Int, "1 for Microsoft failover", f => Flag(f.IsMicrosoft)),
                Col<FailoverAssociation>("ms_server_address", Text, "Microsoft: this server", f => f.MsServerAddress),
                Col<FailoverAssociation>("ms_partner_address", Text, "Microsoft: the partner", f => f.MsPartnerAddress),
                Col<FailoverAssociation>("ms_mode", Text, "Microsoft: LOADBALANCE or HOTSTANDBY", f => f.MsMode)),

            Table("records", "DNS records (host objects expanded into their A, AAAA, PTR and CNAME records).", inventory.Records,
                Col<DnsRecord>("type", Text, "A, AAAA, CNAME, PTR, MX, SRV, TXT, NAPTR, CAA or NS", r => r.Type.ToString()),
                Col<DnsRecord>("zone_key", Text, "The zone", r => r.ZoneKey),
                Col<DnsRecord>("view_key", Text, "The DNS view", r => r.ViewKey),
                Col<DnsRecord>("name", Text, "Name relative to the zone (empty for the apex)", r => r.Name),
                Col<DnsRecord>("fqdn", Text, "Full name", r => r.Fqdn),
                Col<DnsRecord>("value", Text, "Record data in zone-file style (an address, a target...)", r => r.Value),
                Col<DnsRecord>("host_name", Text, "The host object that generates it, if any", r => r.HostName),
                Col<DnsRecord>("is_from_host", Int, "1 if generated by a host object", r => Flag(r.IsFromHost)),
                Col<DnsRecord>("ttl", Int, "Effective TTL in seconds", r => r.Ttl.Seconds),
                Col<DnsRecord>("ttl_source", Text, "Record, Host, Zone or Grid", r => r.Ttl.Source.ToString()),
                Col<DnsRecord>("disabled", Int, "1 if disabled", r => Flag(r.Disabled))),

            Table("hosts", "Host objects, one row per IP address. Includes IPAM-only hosts, which have no DNS records.", HostAddresses(inventory),
                Col<(HostObject Host, HostAddress Address)>("host_name", Text, "Full DNS name for a DNS host; the name as entered for an IPAM-only host", r => r.Host.Name),
                Col<(HostObject Host, HostAddress Address)>("address", Text, "IP address", r => r.Address.Address),
                Col<(HostObject Host, HostAddress Address)>("is_ipv6", Int, "1 for an IPv6 address", r => Flag(r.Address.IsIpv6)),
                Col<(HostObject Host, HostAddress Address)>("is_dns_host", Int, "1 if configured for DNS; 0 for an IPAM-only host", r => Flag(r.Host.IsDns)),
                Col<(HostObject Host, HostAddress Address)>("zone_key", Text, "The zone it sits in (or the non_DNS_host_root, for an IPAM-only host)", r => r.Host.ZoneKey),
                Col<(HostObject Host, HostAddress Address)>("view_key", Text, "The DNS view (or the root that holds an IPAM-only host)", r => r.Host.ViewKey),
                Col<(HostObject Host, HostAddress Address)>("network_view_id", Int, "The network view the address is in", r => r.Host.NetworkViewId),
                Col<(HostObject Host, HostAddress Address)>("configure_for_dhcp", Int, "1 if the address is also configured for DHCP", r => Flag(r.Address.ConfigureForDhcp)),
                Col<(HostObject Host, HostAddress Address)>("mac_address", Text, "MAC address, if set", r => r.Address.MacAddress),
                Col<(HostObject Host, HostAddress Address)>("reverse_zone_key", Text, "The reverse zone the grid links the address to, if it hosts one", r => r.Address.ReverseZoneKey),
                Col<(HostObject Host, HostAddress Address)>("aliases", Text, "The host's aliases, comma separated", r => string.Join(", ", r.Host.Aliases)),
                Col<(HostObject Host, HostAddress Address)>("ttl", Int, "Effective TTL in seconds (DNS hosts)", r => r.Host.Ttl.Seconds),
                Col<(HostObject Host, HostAddress Address)>("ttl_source", Text, "Host, Zone or Grid (DNS hosts)", r => r.Host.IsDns ? r.Host.Ttl.Source.ToString() : null),
                Col<(HostObject Host, HostAddress Address)>("disabled", Int, "1 if disabled", r => Flag(r.Host.Disabled)),
                Col<(HostObject Host, HostAddress Address)>("comment", Text, "Comment", r => r.Host.Comment)),

            // Administrators: names, flags and relationships only. Passwords, hashes, keys and certificates are never in a snapshot.
            Table("admin_users", "Administrator accounts (never their passwords, keys or certificates).", inventory.AdminUsers,
                Col<AdminUser>("name", Text, "User name", u => u.Name),
                Col<AdminUser>("group_name", Text, "The admin group the user belongs to", u => u.GroupName),
                Col<AdminUser>("is_superuser", Int, "1 if the user's group is a superuser group", u => Flag(u.IsSuperuser)),
                Col<AdminUser>("auth_type", Text, "LOCAL, or REMOTE for an external authentication server", u => u.AuthType),
                Col<AdminUser>("status", Text, "e.g. ACTIVE", u => u.Status),
                Col<AdminUser>("disabled", Int, "1 if disabled", u => Flag(u.Disabled)),
                Col<AdminUser>("is_internal", Int, "1 for accounts NIOS created for its own use", u => Flag(u.IsInternal)),
                Col<AdminUser>("uses_ssh_keys", Int, "1 if SSH public key authentication is on for the account", u => Flag(u.UsesSshKeys)),
                Col<AdminUser>("last_login", Text, "Last sign-in (yyyy-MM-dd); empty if never or not recorded", u => Date(u.LastLogin)),
                Col<AdminUser>("time_zone", Text, "Time zone", u => u.TimeZone),
                Col<AdminUser>("comment", Text, "Comment", u => u.Comment)),

            Table("admin_groups", "Admin groups: what permissions and access are granted to.", inventory.AdminGroups,
                Col<AdminGroup>("name", Text, "Group name", g => g.Name),
                Col<AdminGroup>("comment", Text, "Comment", g => g.Comment),
                Col<AdminGroup>("is_superuser", Int, "1 for a superuser group (full access whatever the permissions say)", g => Flag(g.IsSuperuser)),
                Col<AdminGroup>("disabled", Int, "1 if disabled", g => Flag(g.Disabled)),
                Col<AdminGroup>("ui_model", Text, "e.g. FULL_UI", g => g.UiModel),
                Col<AdminGroup>("restricted_user_access", Int, "1 if restricted user access is on", g => Flag(g.RestrictedUserAccess)),
                Col<AdminGroup>("user_count", Int, "Number of accounts in the group",
                    g => inventory.AdminUsers.Count(u => u.GroupName == g.Name)),
                Col<AdminGroup>("role_count", Int, "Number of roles given to the group",
                    g => inventory.AdminGroupRoles.Count(r => r.GroupName == g.Name))),

            Table("admin_roles", "Roles: named sets of permissions that groups are given.", inventory.AdminRoles,
                Col<AdminRole>("name", Text, "Role name", r => r.Name),
                Col<AdminRole>("comment", Text, "Comment", r => r.Comment),
                Col<AdminRole>("is_predefined", Int, "1 for the roles NIOS ships with (DNS Admin, DHCP Admin...)", r => Flag(r.IsPredefined)),
                Col<AdminRole>("disabled", Int, "1 if disabled", r => Flag(r.Disabled)),
                Col<AdminRole>("group_count", Int, "Number of groups that have the role",
                    r => inventory.AdminGroupRoles.Count(l => l.RoleName == r.Name))),

            Table("admin_group_roles", "Which roles each admin group has.", inventory.AdminGroupRoles,
                Col<AdminGroupRole>("group_name", Text, "The admin group", l => l.GroupName),
                Col<AdminGroupRole>("role_name", Text, "The role", l => l.RoleName),
                Col<AdminGroupRole>("position", Int, "Order within the group's roles", l => l.Position)),

            Table("admin_permissions", "What admin groups and roles may do with zones, networks and other resources.", inventory.AdminPermissions,
                Col<AdminPermission>("owner_type", Text, "group or role", p => p.OwnerType),
                Col<AdminPermission>("owner_name", Text, "Name of the admin group or role (see owner_type)", p => p.OwnerName),
                Col<AdminPermission>("resource_type", Text, "Kind of resource, e.g. dns.zone, dns.network_container, one.cluster", p => p.ResourceType),
                Col<AdminPermission>("resource_key", Text,
                    "The resource: a zone_key for dns.zone, a network_key for dns.network and dns.network_container", p => p.ResourceKey),
                Col<AdminPermission>("sub_type", Text,
                    "Limits the permission to one kind of object under the resource, e.g. dns.host_address;is_ipv4=true; empty for all", p => p.SubType),
                Col<AdminPermission>("can_read", Int, "1 if reading is allowed", p => Flag(p.CanRead)),
                Col<AdminPermission>("can_modify", Int, "1 if changing is allowed", p => Flag(p.CanModify)),
                Col<AdminPermission>("can_delete", Int, "1 if deleting is allowed", p => Flag(p.CanDelete)),
                Col<AdminPermission>("can_create", Int, "1 if creating is allowed", p => Flag(p.CanCreate)),
                Col<AdminPermission>("permission", Text, "Read/Write, Read-only, Deny, or Custom for any other mix", p => p.Level),
                Col<AdminPermission>("unmanaged", Int, "1 for rules NIOS flags as unmanaged: ones it adds itself rather than an administrator", p => Flag(p.Unmanaged))),

            Table("admin_access", "Which interfaces (GUI, CLI, API...) each admin group may use.", inventory.AdminAccessRights,
                Col<AdminAccess>("owner_type", Text, "group or role", a => a.OwnerType),
                Col<AdminAccess>("owner_name", Text, "Name of the admin group or role (see owner_type)", a => a.OwnerName),
                Col<AdminAccess>("interface", Text, "gui, cli, papi (the API), cloud_api or taxii", a => a.Interface),
                Col<AdminAccess>("allowed", Int, "1 if the interface may be used", a => Flag(a.Allowed))),

            Table("admin_auth_methods", "The order the grid tries authentication methods for administrators.", inventory.AdminAuthMethods,
                Col<AdminAuthMethod>("position", Int, "Order in which the methods are tried (0 first)", m => m.Position),
                Col<AdminAuthMethod>("type", Text, "e.g. Local, TACACS+, RADIUS, Active Directory, SAML", m => m.Type),
                Col<AdminAuthMethod>("name", Text, "Name of the method or server definition", m => m.Name)),
        ];
    }

    /// <summary>The indexes built after loading, for the joins people write most.</summary>
    public static readonly string[] Indexes =
    [
        "CREATE INDEX ix_members_oid ON members(oid)",
        "CREATE INDEX ix_nodes_member ON nodes(member_oid)",
        "CREATE INDEX ix_services_member ON member_services(member_oid)",
        "CREATE INDEX ix_zones_key ON zones(zone_key)",
        "CREATE INDEX ix_zone_servers_zone ON zone_servers(zone_key)",
        "CREATE INDEX ix_networks_key ON networks(network_key)",
        "CREATE INDEX ix_ranges_network ON dhcp_ranges(network_key)",
        "CREATE INDEX ix_records_zone ON records(zone_key)",
        "CREATE INDEX ix_records_type ON records(type)",
        "CREATE INDEX ix_records_fqdn ON records(fqdn)",
        "CREATE INDEX ix_hosts_address ON hosts(address)",
        "CREATE INDEX ix_admin_users_group ON admin_users(group_name)",
        "CREATE INDEX ix_admin_group_roles_group ON admin_group_roles(group_name)",
        "CREATE INDEX ix_admin_permissions_owner ON admin_permissions(owner_type, owner_name)",
        "CREATE INDEX ix_admin_permissions_resource ON admin_permissions(resource_type, resource_key)",
    ];

    private static IEnumerable<(HostObject Host, HostAddress Address)> HostAddresses(GridInventory inventory) =>
        inventory.Hosts.SelectMany(h => h.Addresses.Select(a => (Host: h, Address: a)));

    private sealed record ZoneServer(DnsZone Zone, string Role, int? MemberOid, string? Name, string? Address);

    private static IEnumerable<ZoneServer> ZoneServers(GridInventory inventory)
    {
        foreach (var zone in inventory.Zones)
        {
            foreach (var oid in zone.GridPrimaryOids)
                yield return new ZoneServer(zone, "grid_primary", oid, inventory.FindMember(oid)?.HostName, null);
            foreach (var oid in zone.GridSecondaryOids)
                yield return new ZoneServer(zone, "grid_secondary", oid, inventory.FindMember(oid)?.HostName, null);
            foreach (var server in zone.ExternalSecondaries)
                yield return new ZoneServer(zone, "external_secondary", null, server.Name, server.Address);
            foreach (var server in zone.MicrosoftPrimaries)
                yield return new ZoneServer(zone, "microsoft_primary", null, server.Name, server.Address);
            foreach (var server in zone.DelegatedServers)
                yield return new ZoneServer(zone, "delegated", null, server.Name, server.Address);
        }
    }

    private static int Flag(bool value) => value ? 1 : 0;

    private static string? Date(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd");

    private sealed record Column<T>(ColumnInfo Info, Func<T, object?> Get);

    private static Column<T> Col<T>(string name, string sqlType, string description, Func<T, object?> get) =>
        new(new ColumnInfo(name, sqlType, description), get);

    private static TableData Table<T>(string name, string description, IEnumerable<T> items, params Column<T>[] columns) =>
        new(name, description, columns.Select(c => c.Info).ToList(),
            items.Select(item => columns.Select(c => c.Get(item)).ToArray()));
}
