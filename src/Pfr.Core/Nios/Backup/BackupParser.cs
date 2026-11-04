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
    /// (certificates, keys, licenses' keys, logs, per-group password policy, authentication servers…) is skipped
    /// unread. Administrator accounts are read for their names, groups, roles and permissions only; their
    /// passwords and keys are dropped by <see cref="SensitiveData"/> before they reach the parser.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedTypes =
    [
        .. MemberServices.RequiredTypes,
        .. LicenseParser.RequiredTypes,
        .. AdminParser.RequiredTypes,
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
        "dns.option",
        "dns.option_definition",
        "dns.cluster_dhcp_properties",
        "dns.bind_a",
        "dns.bind_aaaa",
        "dns.bind_cname",
        "dns.bind_ptr",
        "dns.host",
        "dns.host_address",
        "dns.host_alias",
        "dns.bind_mx",
        "dns.bind_srv",
        "dns.bind_txt",
        "dns.bind_naptr",
        "dns.bind_caa",
        "dns.bind_soa",
        "dns.cluster_dns_properties",
        "one.extensible_attributes_value",
    ];

    /// <summary>Zone types shown to users; internal types (View, Dummy, SharedRecordGroup…) are skipped.</summary>
    public static readonly IReadOnlySet<string> UserZoneTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authoritative", "Forward", "Delegated", "Stub", "ResponsePolicy",
        };

    private const string NoAddress = "255.255.255.255";

    /// <summary>
    /// Every grid must have a name: it's how a client's grids are told apart.
    /// </summary>
    /// <exception cref="InvalidDataException">The inventory has no grid name.</exception>
    public static void EnsureGridName(GridInventory inventory, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(inventory.GridName))
        {
            throw new InvalidDataException(
                $"{sourceName} has no grid name, so the grid can't be identified. The backup was not loaded.");
        }
    }

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
            // DHCP options hang off fixed addresses, networks and members as well as ranges, and a big grid has very many
            // of them; only the ones a range can inherit (set on the range, its network, a container or the grid) are kept.
            if (obj.Type == "dns.option" && !IsInheritableOption(obj))
            {
                continue;
            }
            // Extensible attribute values are set on networks, zones, fixed addresses and much else; only those on A records and
            // host objects are used, so only those are kept.
            if (obj.Type == "one.extensible_attributes_value" && !IsRecordAttribute(obj))
            {
                continue;
            }
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
        var attributes = AttributesByObject(byType);
        AddRecords(inv, byType, attributes);
        AddHosts(inv, byType, attributes);
        inv.RecordDetailsRead = true;
        LicenseParser.Apply(inv, byType);
        MemberServices.Apply(inv, byType);
        AdminParser.Apply(inv, byType);
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
                PhysicalOid = p.GetInt("physical_oid"),
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

        // TTLs: the grid default applies unless the zone's SOA overrides it (ttl_option 1).
        inv.DefaultTtl = byType["dns.cluster_dns_properties"].Select(o => o.GetInt("default_ttl")).FirstOrDefault(t => t is not null);
        foreach (var z in zones.Values)
        {
            // Only zones the grid answers for have a default TTL; delegated/forward/stub zones don't.
            z.DefaultTtl = new Ttl(ServesRecords(z) ? inv.DefaultTtl : null, TtlSource.Grid);
        }
        foreach (var soa in byType["dns.bind_soa"])
        {
            if (!zones.TryGetValue(soa.GetOrEmpty("zone"), out var z) || !ServesRecords(z)) continue;
            z.DefaultTtl = soa.Get("ttl_option") == "1"
                ? new Ttl(soa.GetInt("default_ttl"), TtlSource.Zone)
                : new Ttl(inv.DefaultTtl ?? soa.GetInt("default_ttl"), TtlSource.Grid);
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

    private static bool ServesRecords(DnsZone z) => z.ZoneType is "Authoritative" or "ResponsePolicy";

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
            ParentKey = ContainerKeyOf(o.GetOrEmpty("parent")),
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

        var leaseSettings = LeaseSettings(byType);
        var gridLease = byType["dns.cluster_dhcp_properties"].FirstOrDefault()?.GetInt("lease_time");
        var built = new List<(OneDbObject Object, DhcpRange Range)>();

        foreach (var o in byType["dns.dhcp_range"])
        {
            var assoc = o.GetOrEmpty("server_association_type");
            var start = o.GetOrEmpty("start_address");
            var network = o.GetOrEmpty("network");
            var (lease, leaseSource) = EffectiveLease(o, leaseSettings, gridLease);
            var range = new DhcpRange
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
                AddressType = NullIfEmpty(o.Get("address_type")) ?? "ADDRESS",
                PrefixStart = NullIfEmpty(o.Get("v6_start_prefix")),
                PrefixEnd = NullIfEmpty(o.Get("v6_end_prefix")),
                PrefixBits = o.GetInt("v6_prefix_bits"),
                LeaseTimeSeconds = lease,
                LeaseTimeSource = leaseSource,
                LeaseTimeOverridden = o.GetBool("override_lease_time"),
            };
            inv.DhcpRanges.Add(range);
            built.Add((o, range));
        }

        AttachRangeOptions(inv, byType, built);
        inv.DhcpOptionsRead = true;

        inv.DhcpRanges.Sort((a, b) =>
        {
            var c = a.NetworkViewId.CompareTo(b.NetworkViewId);
            return c != 0 ? c : IpSort.Compare(a.StartAddress, b.StartAddress);
        });
    }

    // ---- DHCP lease times and options ------------------------------------------------

    private const string RangeParentPrefix = ".com.infoblox.dns.dhcp_range$";
    private const string ContainerParentPrefix = ".com.infoblox.dns.network_container$";
    private const string NetworkParentPrefix = ".com.infoblox.dns.network$";
    private const string GridParentPrefix = ".com.infoblox.dns.cluster_dhcp_properties$";

    /// <summary>An option a range can inherit: set on a range, a network, a network container or the grid's DHCP properties.</summary>
    private static bool IsInheritableOption(OneDbObject option)
    {
        var parent = option.GetOrEmpty("parent");
        return parent.StartsWith(RangeParentPrefix, StringComparison.Ordinal)
            || parent.StartsWith(NetworkParentPrefix, StringComparison.Ordinal)
            || parent.StartsWith(ContainerParentPrefix, StringComparison.Ordinal)
            || parent.StartsWith(GridParentPrefix, StringComparison.Ordinal);
    }

    /// <summary>The key of the container a network's "parent" names ("...network_container$10.0.0.0/8/0"), or null when it is a network view.</summary>
    private static string? ContainerKeyOf(string parent) =>
        parent.StartsWith(ContainerParentPrefix, StringComparison.Ordinal) ? parent[ContainerParentPrefix.Length..] : null;

    /// <summary>What a network or container says about lease time, and which container it sits in.</summary>
    private readonly record struct LeaseSetting(bool Overrides, int? Seconds, string? ParentKey, bool IsContainer);

    /// <summary>Every network and container by key (address/cidr/view, as ranges name them).</summary>
    private static Dictionary<string, LeaseSetting> LeaseSettings(Dictionary<string, List<OneDbObject>> byType)
    {
        var settings = new Dictionary<string, LeaseSetting>(StringComparer.Ordinal);
        foreach (var (type, container) in new[] { ("dns.network", false), ("dns.network_container", true) })
        {
            foreach (var o in byType[type])
            {
                var key = $"{o.GetOrEmpty("address")}/{o.GetInt("cidr") ?? 0}/{o.GetInt("network_view") ?? 0}";
                var parent = o.GetOrEmpty("parent");
                settings[key] = new LeaseSetting(
                    o.GetBool("override_lease_time"), o.GetInt("lease_time"),
                    parent.StartsWith(ContainerParentPrefix, StringComparison.Ordinal) ? parent[ContainerParentPrefix.Length..] : null,
                    container);
            }
        }
        return settings;
    }

    /// <summary>
    /// The lease time a range really has. A range that sets its own uses it. Otherwise it takes the nearest setting above it:
    /// its network, then each network container up the tree, then the grid. The value a range stores when it doesn't override
    /// is ignored: it can be left over from before the network changed.
    /// </summary>
    private static (int? Seconds, string? Source) EffectiveLease(
        OneDbObject range, Dictionary<string, LeaseSetting> settings, int? gridLease)
    {
        var own = range.GetInt("lease_time");
        if (range.GetBool("override_lease_time")) return (own, "Range");

        var key = NullIfEmpty(range.Get("network"));
        for (var depth = 0; key is not null && depth < 64 && settings.TryGetValue(key, out var setting); depth++)
        {
            if (setting.Overrides && setting.Seconds is not null)
                return (setting.Seconds, setting.IsContainer ? "Network container" : "Network");
            key = setting.ParentKey;
        }

        return gridLease is not null ? (gridLease, "Grid") : (own, own is null ? null : "Backup");
    }

    /// <summary>
    /// Puts each option set on a range on that range. An option names its range by start and end address, then (when several
    /// ranges share those, as with the same range on two Microsoft servers or in two network views) by member, failover
    /// association, network view and Microsoft server, where the option gives them. Options that can't be placed are counted
    /// in <see cref="GridInventory.UnmatchedRangeOptions"/>, not guessed at.
    /// </summary>
    private static void AttachRangeOptions(
        GridInventory inv, Dictionary<string, List<OneDbObject>> byType, List<(OneDbObject Object, DhcpRange Range)> built)
    {
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in byType["dns.option_definition"])
            definitions.TryAdd($"{d.GetOrEmpty("space")}.{d.GetOrEmpty("code")}", d.GetOrEmpty("name"));

        var byAddresses = new Dictionary<(string Start, string End), List<(OneDbObject Object, DhcpRange Range)>>();
        void Index(string start, string end, (OneDbObject Object, DhcpRange Range) entry)
        {
            if (start.Length == 0 && end.Length == 0) return;
            if (!byAddresses.TryGetValue((start, end), out var list)) byAddresses[(start, end)] = list = [];
            list.Add(entry);
        }
        foreach (var entry in built)
        {
            Index(entry.Range.StartAddress, entry.Range.EndAddress, entry);
            Index(entry.Range.PrefixStart ?? "", entry.Range.PrefixEnd ?? "", entry);
        }

        var networks = new Dictionary<(bool Container, string Key), IpNetwork>();
        foreach (var n in inv.Networks) networks.TryAdd((n.IsContainer, n.Key), n);

        foreach (var option in byType["dns.option"])
        {
            var reference = option.GetOrEmpty("option_definition"); // e.g. "DHCP Standard Options.12.false.150": the number is the last part
            var dot = reference.LastIndexOf('.');
            var parent = option.GetOrEmpty("parent");

            // Where it was set: a range, a network, a container above it, or the grid.
            List<DhcpOption>? list = null;
            if (parent.StartsWith(RangeParentPrefix, StringComparison.Ordinal))
                list = RangeNamedBy(parent[RangeParentPrefix.Length..], byAddresses)?.Options;
            else if (parent.StartsWith(NetworkParentPrefix, StringComparison.Ordinal))
                list = networks.GetValueOrDefault((false, parent[NetworkParentPrefix.Length..]))?.Options;
            else if (parent.StartsWith(ContainerParentPrefix, StringComparison.Ordinal))
                list = networks.GetValueOrDefault((true, parent[ContainerParentPrefix.Length..]))?.Options;
            else if (parent.StartsWith(GridParentPrefix, StringComparison.Ordinal))
                list = inv.GridDhcpOptions;

            if (list is null
                || dot < 0
                || !int.TryParse(reference[(dot + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code))
            {
                inv.UnmatchedRangeOptions++;
                continue;
            }

            list.Add(new DhcpOption
            {
                Code = code,
                Name = definitions.TryGetValue(reference, out var name) ? NullIfEmpty(name) : null,
                Space = reference[..dot],
                Value = option.GetOrEmpty("value"),
                IsIpv4 = option.Get("is_ipv4") != "false",
            });
        }

        static void ByNumber(List<DhcpOption> options)
        {
            if (options.Count > 1)
                options.Sort((a, b) => a.Code != b.Code ? a.Code.CompareTo(b.Code) : string.CompareOrdinal(a.Value, b.Value));
        }
        foreach (var (_, range) in built) ByNumber(range.Options);
        foreach (var network in inv.Networks) ByNumber(network.Options);
        ByNumber(inv.GridDhcpOptions);
    }

    private static DhcpRange? RangeNamedBy(
        string key, Dictionary<(string Start, string End), List<(OneDbObject Object, DhcpRange Range)>> byAddresses)
    {
        var parts = key.Split('/');
        if (parts.Length < 2 || !byAddresses.TryGetValue((parts[0], parts[1]), out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0].Range;

        // Several ranges with these addresses: the rest of the key (member, failover, network view, Microsoft server) says which.
        // A part the key leaves empty, or gives as ".", says nothing.
        string Part(int i) => parts.Length > i ? parts[i] : "";
        static bool Agrees(string fromKey, string? fromRange) =>
            fromKey.Length == 0 || fromKey == "." || fromRange is null || fromKey == fromRange;

        foreach (var (o, range) in candidates)
        {
            if (Agrees(Part(2), o.Get("member")) && Agrees(Part(3), o.Get("failover_association"))
                && Agrees(Part(4), o.Get("network_view")) && Agrees(Part(5), o.Get("ms_server")))
                return range;
        }
        return null;
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

    private static void AddRecords(GridInventory inv, Dictionary<string, List<OneDbObject>> byType,
        Dictionary<string, Dictionary<string, string>> attributes)
    {
        var zones = inv.Zones.ToDictionary(z => z.Key, StringComparer.Ordinal);

        void AddRecord(string zoneKey, string name, DnsRecordType type, string value, bool disabled,
            string? hostName = null, Ttl? ttl = null, RecordOrigin? origin = null, Dictionary<string, string>? extensible = null)
        {
            if (!zones.TryGetValue(zoneKey, out var zone)) return; // e.g. RPZ/blacklist internals, non-DNS hosts
            inv.Records.Add(new DnsRecord
            {
                Type = type,
                ZoneKey = zoneKey,
                ViewKey = zone.ViewKey,
                Name = name,
                Fqdn = name is "" or "@" ? zone.Fqdn : $"{name}.{zone.Fqdn}",
                Value = value,
                Disabled = disabled,
                HostName = hostName,
                Ttl = ttl ?? zone.DefaultTtl, // no TTL of its own: inherit the zone's
                Creator = origin?.Creator,
                LastQueried = origin?.LastQueried,
                Comment = origin?.Comment,
                Created = origin?.Created,
                ExtensibleAttributes = extensible,
            });
        }

        void Add(OneDbObject o, DnsRecordType type, string value, string? attributesKey = null) =>
            AddRecord(o.GetOrEmpty("zone"), o.GetOrEmpty("name"), type, value, o.GetBool("disabled"),
                ttl: OwnTtl(o, TtlSource.Record), origin: OriginOf(o),
                extensible: attributesKey is not null && attributes.TryGetValue(attributesKey, out var found) ? found : null);

        string Quote(string? s) => $"\"{s ?? ""}\"";

        foreach (var o in byType["dns.bind_a"])
            Add(o, DnsRecordType.A, o.GetOrEmpty("address"),
                attributesKey: $"{BindAPrefix}{o.GetOrEmpty("zone")},{o.GetOrEmpty("name")},{o.GetOrEmpty("address")}");
        foreach (var o in byType["dns.bind_aaaa"]) Add(o, DnsRecordType.AAAA, o.GetOrEmpty("address"));
        foreach (var o in byType["dns.bind_cname"]) Add(o, DnsRecordType.CNAME, o.GetOrEmpty("canonical_name"));
        foreach (var o in byType["dns.bind_ptr"]) Add(o, DnsRecordType.PTR, o.GetOrEmpty("dname"));
        foreach (var o in byType["dns.bind_mx"])
            Add(o, DnsRecordType.MX, $"{o.GetOrEmpty("priority")} {o.GetOrEmpty("mx")}");
        foreach (var o in byType["dns.bind_srv"])
            Add(o, DnsRecordType.SRV,
                $"{o.GetOrEmpty("priority")} {o.GetOrEmpty("weight")} {o.GetOrEmpty("port")} {o.GetOrEmpty("target")}");
        foreach (var o in byType["dns.bind_txt"]) Add(o, DnsRecordType.TXT, o.GetOrEmpty("txt_string"));
        foreach (var o in byType["dns.bind_naptr"])
            Add(o, DnsRecordType.NAPTR,
                $"{o.GetOrEmpty("order")} {o.GetOrEmpty("preference")} {Quote(o.Get("flag"))} " +
                $"{Quote(o.Get("services"))} {Quote(o.Get("regexp"))} {(o.Get("replacement") is { Length: > 0 } r ? r : ".")}");
        foreach (var o in byType["dns.bind_caa"])
            Add(o, DnsRecordType.CAA, $"{o.GetOrEmpty("ca_flag")} {o.GetOrEmpty("ca_tag")} {Quote(o.Get("ca_value"))}");

        // NS: NIOS's own NS records are auto-generated, so only delegations are listed,
        // under the delegated zone.
        // Delegation NS records are served from the parent zone, so they take its default TTL.
        var parentZones = InventoryHierarchy.ParentZones(inv.Zones);
        foreach (var z in inv.Zones.Where(z => z.ZoneType == "Delegated"))
        {
            var ttl = parentZones[z.Key]?.DefaultTtl ?? new Ttl(inv.DefaultTtl, TtlSource.Grid);
            foreach (var ns in z.DelegatedServers)
            {
                AddRecord(z.Key, "", DnsRecordType.NS, ns.Name, z.Disabled, ttl: ttl);
            }
        }

        AddHostRecords(inv, byType, zones, (zk, n, t, v, d, h, ttl, origin) => AddRecord(zk, n, t, v, d, h, ttl, origin));
        inv.RecordOriginsRead = true;

        inv.Records.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.ViewKey, b.ViewKey);
            if (c != 0) return c;
            c = string.Compare(a.Fqdn, b.Fqdn, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = a.Type.CompareTo(b.Type);
            return c != 0 ? c : IpSort.Compare(a.Value, b.Value);
        });
    }

    /// <summary>
    /// Reads every host object with its addresses and aliases, DNS hosts and IPAM-only ones alike. An IPAM-only host
    /// (under a <c>non_DNS_host_root</c>) generates no DNS records, so this is the only place it is recorded. The network
    /// view of a host comes from its zone's DNS view, or from the root that holds it: host addresses carry none.
    /// </summary>
    private static void AddHosts(GridInventory inv, Dictionary<string, List<OneDbObject>> byType,
        Dictionary<string, Dictionary<string, string>> attributes)
    {
        var zones = inv.Zones.ToDictionary(z => z.Key, StringComparer.Ordinal);

        var networkViewOfRoot = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var v in byType["dns.view"]) networkViewOfRoot[v.GetOrEmpty("zone")] = v.GetInt("network_view");

        // Addresses and aliases name their host as "zone.name" or, for a host created with a timestamp (the IPAM-only
        // ones in a real backup), "zone.creator.timestamp.name". Both forms are accepted.
        var byReference = new Dictionary<string, HostObject>(StringComparer.Ordinal);
        foreach (var h in byType["dns.host"])
        {
            var zoneKey = h.GetOrEmpty("zone");
            var hostName = h.GetOrEmpty("name");
            var viewKey = ViewKeyOf(zoneKey);
            var isDns = zones.TryGetValue(zoneKey, out var zone);

            var host = new HostObject
            {
                Name = !isDns ? hostName : hostName.Length == 0 ? zone!.Fqdn : $"{hostName}.{zone!.Fqdn}",
                ZoneKey = zoneKey,
                ViewKey = viewKey,
                NetworkViewId = networkViewOfRoot.GetValueOrDefault(viewKey),
                IsDns = isDns,
                Disabled = h.GetBool("disabled"),
                Comment = NullIfEmpty(h.Get("comment")),
                Created = CreatedOf(h),
                ExtensibleAttributes = HostAttributes(attributes, h, zoneKey, hostName),
                Ttl = isDns ? OwnTtl(h, TtlSource.Host) ?? zone!.DefaultTtl : new Ttl(null, TtlSource.Grid),
            };
            inv.Hosts.Add(host);

            byReference.TryAdd($"{zoneKey}.{hostName}", host);
            if (h.Get("created_timestamp") is { Length: > 0 } created)
                byReference[$"{zoneKey}.{h.Get("creator_member") ?? "0"}.{created}.{hostName}"] = host;
        }

        foreach (var a in byType["dns.host_address"])
        {
            if (!byReference.TryGetValue(a.GetOrEmpty("host"), out var owner)) continue;
            var address = a.GetOrEmpty("address");
            if (address.Length == 0) continue; // a delegated IPv6 prefix has no single address

            owner.Addresses.Add(new HostAddress
            {
                Address = address,
                IsIpv6 = a.Get("is_ipv4") == "false" || address.Contains(':'),
                ConfigureForDhcp = a.GetBool("configure_for_dhcp"),
                MacAddress = NullIfEmpty(a.Get("mac_address")),
                ReverseZoneKey = NullIfEmpty(a.Get("reverse_zone")),
            });
        }

        foreach (var alias in byType["dns.host_alias"])
        {
            if (!byReference.TryGetValue(alias.GetOrEmpty("host"), out var owner)) continue;
            var aliasName = alias.GetOrEmpty("alias");
            owner.Aliases.Add(zones.TryGetValue(alias.GetOrEmpty("zone"), out var aliasZone)
                ? $"{aliasName}.{aliasZone.Fqdn}"
                : aliasName);
        }

        foreach (var host in inv.Hosts)
        {
            host.Addresses.Sort((x, y) => IpSort.Compare(x.Address, y.Address));
            host.Aliases.Sort(StringComparer.OrdinalIgnoreCase);
        }
        var ordered = inv.Hosts
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.ZoneKey, StringComparer.Ordinal)
            .ToList();
        inv.Hosts.Clear();
        inv.Hosts.AddRange(ordered);
    }

    /// <summary>
    /// A host object serves several records: A/AAAA for each address, a PTR in the
    /// matching reverse zone (when the grid hosts it), and a CNAME for each alias.
    /// </summary>
    private static void AddHostRecords(
        GridInventory inv,
        Dictionary<string, List<OneDbObject>> byType,
        Dictionary<string, DnsZone> zones,
        Action<string, string, DnsRecordType, string, bool, string?, Ttl?, RecordOrigin?> addRecord)
    {
        var hosts = new Dictionary<string, (string Fqdn, bool Disabled, Ttl? Ttl, RecordOrigin Origin)>(StringComparer.Ordinal);
        var addressesByHost = byType["dns.host_address"].ToLookup(a => a.GetOrEmpty("host"), StringComparer.Ordinal);
        foreach (var h in byType["dns.host"])
        {
            var zoneKey = h.GetOrEmpty("zone");
            if (!zones.TryGetValue(zoneKey, out var zone)) continue; // non-DNS host (IPAM only)
            var name = h.GetOrEmpty("name");
            var fqdn = name.Length == 0 ? zone.Fqdn : $"{name}.{zone.Fqdn}";
            // A host may set its own TTL; its records use it, otherwise they inherit their zone's.
            var hostTtl = OwnTtl(h, TtlSource.Host);
            var origin = OriginOf(h); // its records are created by whoever created the host, and queried when it is
            hosts[$"{zoneKey}.{name}"] = (fqdn, h.GetBool("disabled"), hostTtl, origin);

            foreach (var a in addressesByHost[$"{zoneKey}.{name}"])
            {
                var address = a.GetOrEmpty("address");
                var isV4 = a.Get("is_ipv4") is not "false";
                addRecord(zoneKey, name, isV4 ? DnsRecordType.A : DnsRecordType.AAAA, address, h.GetBool("disabled"), fqdn, hostTtl, origin);

                // PTR in the reverse zone NIOS links the address to, if the grid hosts it.
                if (a.Get("reverse_zone") is { Length: > 0 } reverseKey
                    && zones.TryGetValue(reverseKey, out var reverseZone)
                    && ReverseName(address) is { } reverse
                    && RelativeTo(reverse, reverseZone.Fqdn) is { } ptrName)
                {
                    addRecord(reverseKey, ptrName, DnsRecordType.PTR, fqdn, h.GetBool("disabled"), fqdn, hostTtl, origin);
                }
            }
        }
        inv.HostCount = hosts.Count;

        foreach (var alias in byType["dns.host_alias"])
        {
            if (!hosts.TryGetValue(alias.GetOrEmpty("host"), out var host)) continue;
            addRecord(alias.GetOrEmpty("zone"), alias.GetOrEmpty("alias"), DnsRecordType.CNAME,
                host.Fqdn, host.Disabled, host.Fqdn, host.Ttl, host.Origin);
        }
    }

    /// <summary>Who made a record, when it was last queried, its comment and when it was created: what the scavenging and creation reports need.</summary>
    private sealed record RecordOrigin(string? Creator, DateTimeOffset? LastQueried, string? Comment, DateTimeOffset? Created);

    private static RecordOrigin OriginOf(OneDbObject o) => new(
        NullIfEmpty(o.Get("creator")), UnixTime(o.Get("last_queried")), NullIfEmpty(o.Get("comment")), CreatedOf(o));

    /// <summary>When an object was created: its creation_timestamp, or the created_timestamp some objects carry instead.</summary>
    private static DateTimeOffset? CreatedOf(OneDbObject o) => UnixTime(o.Get("creation_timestamp")) ?? UnixTime(o.Get("created_timestamp"));

    // ---- Extensible attributes of A records and hosts ----

    private const string BindAPrefix = ".com.infoblox.dns.bind_a$";
    private const string HostPrefix = ".com.infoblox.dns.host$";

    /// <summary>An extensible attribute value set on an A record or a host object (the only ones kept).</summary>
    private static bool IsRecordAttribute(OneDbObject value)
    {
        var target = value.GetOrEmpty("object");
        return target.StartsWith(BindAPrefix, StringComparison.Ordinal) || target.StartsWith(HostPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The extensible attributes of each object by the object's reference ("...bind_a$zone,name,address" or "...host$zone.name"),
    /// each as name → value. The tag is the attribute's name with a leading dot; a value inherited from a network is on the object
    /// itself, with the value that applies, so it is included. An attribute with no value is skipped.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> AttributesByObject(Dictionary<string, List<OneDbObject>> byType)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var v in byType["one.extensible_attributes_value"])
        {
            var tag = v.GetOrEmpty("tag");
            var name = tag.StartsWith('.') ? tag[1..] : tag;
            var value = v.GetOrEmpty("value");
            if (name.Length == 0 || value.Length == 0) continue;

            var target = v.GetOrEmpty("object");
            if (!result.TryGetValue(target, out var set)) result[target] = set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            set[name] = value;
        }
        return result;
    }

    /// <summary>
    /// A host's attributes, found by the reference NIOS uses for it: "...host$zone.name" or, for a host created with a timestamp (as
    /// the IPAM-only ones are in a real backup), "...host$zone.creator.timestamp.name". Null when it has none.
    /// </summary>
    private static Dictionary<string, string>? HostAttributes(
        Dictionary<string, Dictionary<string, string>> attributes, OneDbObject host, string zoneKey, string name)
    {
        if (attributes.TryGetValue($"{HostPrefix}{zoneKey}.{name}", out var plain)) return plain;
        return host.Get("created_timestamp") is { Length: > 0 } created
            && attributes.TryGetValue($"{HostPrefix}{zoneKey}.{host.Get("creator_member") ?? "0"}.{created}.{name}", out var stamped)
                ? stamped
                : null;
    }

    /// <summary>A time in seconds since 1970 (UTC), or null when it is absent, zero or not a time.</summary>
    private static DateTimeOffset? UnixTime(string? seconds) =>
        long.TryParse(seconds, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
        && value > 0 && value < 253_402_300_799
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : null;

    /// <summary>
    /// The object's own TTL, or null when it inherits. ttl_option 1 means "set here";
    /// some record types (NAPTR) omit ttl_option and just carry a ttl.
    /// </summary>
    internal static Ttl? OwnTtl(OneDbObject o, TtlSource source)
    {
        var option = o.Get("ttl_option");
        var own = option == "1" || (option is null && o.GetInt("ttl") is not null);
        return own ? new Ttl(o.GetInt("ttl"), source) : null;
    }

    /// <summary>"10.0.0.20" → "20.0.0.10.in-addr.arpa"; IPv6 → nibble form under ip6.arpa.</summary>
    internal static string? ReverseName(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return null;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return string.Join('.', bytes.Reverse()) + ".in-addr.arpa";
        }

        var nibbles = bytes.SelectMany(b => new[] { b >> 4, b & 0xF }).Reverse().Select(n => n.ToString("x"));
        return string.Join('.', nibbles) + ".ip6.arpa";
    }

    /// <summary>The part of <paramref name="fqdn"/> before <paramref name="zone"/>, or null if it isn't in that zone.</summary>
    internal static string? RelativeTo(string fqdn, string zone)
    {
        zone = zone.TrimEnd('.');
        if (string.Equals(fqdn, zone, StringComparison.OrdinalIgnoreCase)) return "";
        return fqdn.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase)
            ? fqdn[..^(zone.Length + 1)]
            : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
