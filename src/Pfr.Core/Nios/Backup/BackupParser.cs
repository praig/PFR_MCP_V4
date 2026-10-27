using Pfr.Core.Nios.Model;
using Pfr.Core.Nios.OneDb;

namespace Pfr.Core.Nios.Backup;

/// <summary>
/// Builds a <see cref="GridInventory"/> from a NIOS grid backup.
/// </summary>
public static class BackupParser
{
    /// <summary>
    /// The only onedb.xml object types the parser reads. Everything else
    /// (certificates, keys, admin accounts, licenses, logs…) is skipped unread.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedTypes =
    [
        "one.cluster",
        "one.virtual_node",
        "one.physical_node",
        "dns.network_view",
        "dns.view",
        "dns.zone",
        "dns.zone_grid_primary",
        "dns.zone_cluster_secondary_server",
        "dns.zone_ext_secondary_server",
        "dns.zone_ms_primary_server",
        "dns.zone_delegated_server",
        "dns.network",
        "dns.network_container",
        "dns.dhcp_range",
        "dns.dhcp_failover_association",
        "dns.bind_a",
        "dns.bind_aaaa",
        "dns.bind_cname",
        "dns.bind_ptr",
        "dns.host",
        "dns.host_address",
    ];

    /// <summary>Zone types shown to users; internal types (View, Dummy, SharedRecordGroup…) are skipped.</summary>
    public static readonly IReadOnlySet<string> UserZoneTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authoritative", "Forward", "Delegated", "Stub", "ResponsePolicy",
        };

    private const string NoAddress = "255.255.255.255";

    public static GridInventory ParseFile(string path)
    {
        using var stream = BackupSource.OpenOneDb(path);
        return Parse(stream);
    }

    /// <summary>Parses an onedb.xml stream (use <see cref="BackupSource"/> to open a backup).</summary>
    public static GridInventory Parse(Stream oneDbXml)
    {
        var reader = new OneDbReader(AllowedTypes);
        var byType = AllowedTypes.ToDictionary(t => t, _ => new List<OneDbObject>());
        foreach (var obj in reader.Read(oneDbXml))
        {
            byType[obj.Type].Add(obj);
        }

        var inv = new GridInventory
        {
            NiosVersion = reader.DatabaseVersion,
            GridName = byType["one.cluster"].FirstOrDefault()?.Get("name"),
        };

        AddMembers(inv, byType);
        AddNetworkViews(inv, byType);
        AddDnsViews(inv, byType);
        AddZones(inv, byType);
        AddNetworks(inv, byType);
        AddDhcp(inv, byType);
        AddRecords(inv, byType);
        return inv;
    }

    private static void AddMembers(GridInventory inv, Dictionary<string, List<OneDbObject>> byType)
    {
        foreach (var v in byType["one.virtual_node"])
        {
            if (v.GetInt("virtual_oid") is not { } oid) continue;
            inv.Members.Add(new GridMember
            {
                Oid = oid,
                HostName = v.GetOrEmpty("host_name"),
                Ipv4Address = NullIfEmpty(v.Get("virtual_ip")),
                Ipv6Address = NullIfEmpty(v.Get("v6_virtual_ip")),
                MemberType = NullIfEmpty(v.Get("member_type")),
                IsGridMaster = v.GetBool("is_master"),
                IsGridMasterCandidate = v.GetBool("is_potential_master") && !v.GetBool("is_master"),
                HaEnabled = v.GetBool("ha_enabled"),
                UpgradeGroup = NullIfEmpty(v.Get("upgrade_group")),
            });
        }

        foreach (var p in byType["one.physical_node"])
        {
            if (inv.FindMember(p.GetInt("virtual_node")) is not { } member) continue;
            member.Nodes.Add(new GridNode
            {
                Position = p.GetInt("position") ?? 0,
                HardwareType = NullIfEmpty(p.Get("hwtype")),
                Platform = NullIfEmpty(p.Get("hwplatform")),
                Hypervisor = NullIfEmpty(p.Get("hypervisor")),
                SerialNumber = NullIfEmpty(p.Get("serial_number")),
                Ipv4Address = NullIfEmpty(p.Get("public_ip_address")),
                Ipv6Address = NullIfEmpty(p.Get("v6_public_ip_address")),
                MgmtAddress = NullIfEmpty(p.Get("mgmt_port_address")),
            });
        }

        foreach (var m in inv.Members) m.Nodes.Sort((a, b) => a.Position.CompareTo(b.Position));
        inv.Members.Sort((a, b) => string.Compare(a.HostName, b.HostName, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddNetworkViews(GridInventory inv, Dictionary<string, List<OneDbObject>> byType)
    {
        foreach (var o in byType["dns.network_view"])
        {
            if (o.GetInt("id") is not { } id) continue;
            inv.NetworkViews.Add(new NetworkView { Id = id, Name = o.GetOrEmpty("name") });
        }
        inv.NetworkViews.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    /// <summary>View-type roots that are NIOS internals, not user DNS views (DTC root).</summary>
    private static readonly HashSet<string> InternalViewNames = new(StringComparer.Ordinal) { "idns_root" };

    private static void AddDnsViews(GridInventory inv, Dictionary<string, List<OneDbObject>> byType)
    {
        // Real views are top-level zone objects of type "View". Other top-level roots
        // (non_DNS_host_root*, srg_root, rpz_root) also get dns.view objects but aren't views.
        var viewKeys = byType["dns.zone"]
            .Where(z => z.Get("zone") == ".." && z.Get("zone_type") == "View"
                        && !InternalViewNames.Contains(z.GetOrEmpty("name")))
            .Select(z => ZoneKey("..", z.GetOrEmpty("name")))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var o in byType["dns.view"])
        {
            var key = o.GetOrEmpty("zone");
            if (!viewKeys.Contains(key)) continue;
            var display = o.GetOrEmpty("displayname").Trim();
            inv.DnsViews.Add(new DnsView
            {
                Key = key,
                Name = display.Length > 0 ? display : DefaultViewName(key),
                NetworkViewId = o.GetInt("network_view"),
                Disabled = o.GetBool("disabled"),
                RecursionEnabled = o.GetBool("recursion_enabled"),
                DnssecValidationEnabled = o.GetBool("dnssec_validation_enabled"),
            });
        }
    }

    private static string DefaultViewName(string viewKey)
    {
        var name = viewKey.TrimStart('.');
        return name == "_default" ? "default" : name;
    }

    private static void AddZones(GridInventory inv, Dictionary<string, List<OneDbObject>> byType)
    {
        var zones = new Dictionary<string, DnsZone>(StringComparer.Ordinal);
        foreach (var o in byType["dns.zone"])
        {
            var type = o.GetOrEmpty("zone_type");
            var parent = o.GetOrEmpty("zone");
            // Top-level objects (parent "..") are views and internal roots such as rpz_root.
            if (!UserZoneTypes.Contains(type) || parent is "" or "..") continue;

            var key = ZoneKey(parent, o.GetOrEmpty("name"));
            zones[key] = new DnsZone
            {
                Key = key,
                ViewKey = ViewKeyOf(key),
                Fqdn = o.GetOrEmpty("fqdn"),
                ZoneType = type,
                PrimaryType = NullIfEmpty(o.Get("primary_type")),
                IsReverse = o.GetOrEmpty("is_reverse_zone") is not ("" or "0"),
                Disabled = o.GetBool("disabled"),
                DnssecEnabled = o.GetBool("dnssec_enabled"),
            };
        }

        void ForZone(string type, Action<DnsZone, OneDbObject> apply)
        {
            foreach (var o in byType[type])
            {
                if (zones.TryGetValue(o.GetOrEmpty("zone"), out var z)) apply(z, o);
            }
        }

        ForZone("dns.zone_grid_primary", (z, o) =>
        {
            if (o.GetInt("grid_member") is { } oid) z.GridPrimaryOids.Add(oid);
        });
        ForZone("dns.zone_cluster_secondary_server", (z, o) =>
        {
            if (o.GetInt("member_address") is { } oid) z.GridSecondaryOids.Add(oid);
        });
        ForZone("dns.zone_ext_secondary_server", (z, o) =>
            z.ExternalSecondaries.Add(new NameServer(o.GetOrEmpty("ext_sec_name"), NullIfEmpty(o.Get("ext_sec_address")))));
        ForZone("dns.zone_ms_primary_server", (z, o) =>
            z.MicrosoftPrimaries.Add(new NameServer(o.GetOrEmpty("ns_name"), NullIfEmpty(o.Get("ns_ip")))));
        ForZone("dns.zone_delegated_server", (z, o) =>
        {
            var addr = o.Get("address");
            z.DelegatedServers.Add(new NameServer(o.GetOrEmpty("ds_name"), addr is null or "" or NoAddress ? null : addr));
        });

        inv.Zones.AddRange(zones.Values.OrderBy(z => z.ViewKey, StringComparer.Ordinal)
            .ThenBy(z => z.Fqdn, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Zone keys are the parent key plus the name; ".." marks a top-level (view) object.</summary>
    internal static string ZoneKey(string parent, string name) =>
        parent is "" or ".." ? "." + name : parent + "." + name;

    /// <summary>"._default.network.richard" → "._default".</summary>
    internal static string ViewKeyOf(string zoneKey)
    {
        var second = zoneKey.IndexOf('.', 1);
        return second < 0 ? zoneKey : zoneKey[..second];
    }

    private static void AddNetworks(GridInventory inv, Dictionary<string, List<OneDbObject>> byType)
    {
        void Add(OneDbObject o, bool container) => inv.Networks.Add(new IpNetwork
        {
            Address = o.GetOrEmpty("address"),
            Cidr = o.GetInt("cidr") ?? 0,
            NetworkViewId = o.GetInt("network_view") ?? 0,
            IsIpv6 = o.Get("is_ipv4") is "false",
            IsContainer = container,
            Disabled = o.GetBool("disabled"),
            Comment = NullIfEmpty(o.Get("comment")),
        });

        foreach (var o in byType["dns.network_container"]) Add(o, container: true);
        foreach (var o in byType["dns.network"]) Add(o, container: false);

        inv.Networks.Sort((a, b) =>
        {
            var c = a.NetworkViewId.CompareTo(b.NetworkViewId);
            if (c != 0) return c;
            c = a.IsIpv6.CompareTo(b.IsIpv6);
            if (c != 0) return c;
            c = IpSort.Compare(a.Address, b.Address);
            return c != 0 ? c : a.Cidr.CompareTo(b.Cidr);
        });
    }

    private static void AddDhcp(GridInventory inv, Dictionary<string, List<OneDbObject>> byType)
    {
        foreach (var o in byType["dns.dhcp_failover_association"])
        {
            var isMs = IsMsServer(o.Get("ms_server"));
            inv.FailoverAssociations.Add(new FailoverAssociation
            {
                Name = o.GetOrEmpty("name"),
                PrimaryOid = isMs ? null : o.GetInt("primary_server_vnode"),
                SecondaryOid = isMs ? null : o.GetInt("secondary_server_vnode"),
                IsMicrosoft = isMs,
                MsServerAddress = isMs ? NullIfEmpty(o.Get("ms_failover_address")) : null,
                MsPartnerAddress = isMs ? NullIfEmpty(o.Get("ms_failover_partner")) : null,
                MsMode = isMs ? NullIfEmpty(o.Get("ms_failover_mode")) : null,
            });
        }
        inv.FailoverAssociations.Sort((a, b) =>
        {
            var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : IpSort.Compare(a.MsServerAddress, b.MsServerAddress);
        });

        foreach (var o in byType["dns.dhcp_range"])
        {
            var assoc = o.GetOrEmpty("server_association_type");
            var start = o.GetOrEmpty("start_address");
            var network = o.GetOrEmpty("network");
            inv.DhcpRanges.Add(new DhcpRange
            {
                NetworkKey = network,
                StartAddress = start,
                EndAddress = o.GetOrEmpty("end_address"),
                NetworkViewId = o.GetInt("network_view") ?? 0,
                IsIpv6 = start.Contains(':') || network.Contains(':'),
                AssociationType = assoc,
                MemberOid = assoc == "Member" ? o.GetInt("member") : null,
                FailoverName = FailoverNameOf(o.Get("failover_association")),
                IsMicrosoft = IsMsServer(o.Get("ms_server")),
                Disabled = o.GetBool("disabled"),
                Name = NullIfEmpty(o.Get("name")),
                Comment = NullIfEmpty(o.Get("comment")),
            });
        }

        inv.DhcpRanges.Sort((a, b) =>
        {
            var c = a.NetworkViewId.CompareTo(b.NetworkViewId);
            return c != 0 ? c : IpSort.Compare(a.StartAddress, b.StartAddress);
        });
    }

    private static bool IsMsServer(string? msServer) => msServer is not (null or "" or ".");

    /// <summary>Range references look like "name.&lt;ms server id&gt;", e.g. "gm-gmc." or "dc1-dc2.12".</summary>
    private static string? FailoverNameOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var dot = reference.LastIndexOf('.');
        var name = dot >= 0 ? reference[..dot] : reference;
        return name.Length == 0 ? null : name;
    }

    private static void AddRecords(GridInventory inv, Dictionary<string, List<OneDbObject>> byType)
    {
        var zones = inv.Zones.ToDictionary(z => z.Key, StringComparer.Ordinal);

        void Add(OneDbObject o, DnsRecordType type, string value)
        {
            var zoneKey = o.GetOrEmpty("zone");
            if (!zones.TryGetValue(zoneKey, out var zone)) return; // e.g. RPZ/blacklist internals
            var name = o.GetOrEmpty("name");
            inv.Records.Add(new DnsRecord
            {
                Type = type,
                ZoneKey = zoneKey,
                ViewKey = zone.ViewKey,
                Name = name,
                Fqdn = name is "" or "@" ? zone.Fqdn : $"{name}.{zone.Fqdn}",
                Value = value,
                Disabled = o.GetBool("disabled"),
            });
        }

        foreach (var o in byType["dns.bind_a"]) Add(o, DnsRecordType.A, o.GetOrEmpty("address"));
        foreach (var o in byType["dns.bind_aaaa"]) Add(o, DnsRecordType.AAAA, o.GetOrEmpty("address"));
        foreach (var o in byType["dns.bind_cname"]) Add(o, DnsRecordType.CNAME, o.GetOrEmpty("canonical_name"));
        foreach (var o in byType["dns.bind_ptr"]) Add(o, DnsRecordType.PTR, o.GetOrEmpty("dname"));

        // Host records: addresses live in separate host_address objects keyed by "<zone key>.<host name>".
        var hostAddresses = byType["dns.host_address"]
            .GroupBy(a => a.GetOrEmpty("host"), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(a => a.GetOrEmpty("address"))),
                StringComparer.Ordinal);

        foreach (var o in byType["dns.host"])
        {
            var hostKey = $"{o.GetOrEmpty("zone")}.{o.GetOrEmpty("name")}";
            Add(o, DnsRecordType.Host, hostAddresses.GetValueOrDefault(hostKey, ""));
        }

        inv.Records.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.ViewKey, b.ViewKey);
            if (c != 0) return c;
            c = string.Compare(a.Fqdn, b.Fqdn, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Type.CompareTo(b.Type);
        });
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
