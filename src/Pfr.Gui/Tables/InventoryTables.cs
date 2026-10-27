using Pfr.Core.Nios.Model;

namespace Pfr.Gui.Tables;

/// <summary>Turns a parsed inventory into the tables shown in the GUI.</summary>
public static class InventoryTables
{
    /// <summary>Each section is a flat <see cref="TableDefinition"/> or a <see cref="TreeTableDefinition"/>.</summary>
    public static IReadOnlyList<object> Build(GridInventory inv)
    {
        var lookup = new Lookup(inv);
        return
        [
            Members(inv),
            Licenses(inv),
            Views(inv, lookup),
            Zones(inv, lookup),
            Networks(inv, lookup),
            DhcpRanges(inv, lookup),
            Failover(inv, lookup),
            Records(inv, lookup),
        ];
    }

    private static string YesNo(bool value) => value ? "Yes" : "";

    private static string Plural(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    private static TableDefinition Members(GridInventory inv) => new(
        "Members",
        [
            new("Host name", Width: 230), new("Role"), new("Services", Width: 260),
            new("IPv4", ColumnKind.Ip), new("IPv6", ColumnKind.Ip),
            new("Model"), new("Platform"), new("Serial number"), new("HA"), new("Nodes", ColumnKind.Number),
            new("Upgrade group"),
        ],
        inv.Members.Select(m =>
        {
            var node = m.Nodes.FirstOrDefault();
            return new TableRow(
            [
                m.HostName, m.Role, string.Join(", ", m.Services), m.Ipv4Address ?? "", m.Ipv6Address ?? "",
                string.Join(", ", m.Nodes.Select(n => n.HardwareType).Distinct()),
                node?.Hypervisor ?? node?.Platform ?? "",
                string.Join(", ", m.Nodes.Select(n => n.SerialNumber).Where(s => s is not null)),
                YesNo(m.HaEnabled), m.Nodes.Count.ToString(), m.UpgradeGroup ?? "",
            ]);
        }).ToList());

    private static TreeTableDefinition Licenses(GridInventory inv)
    {
        var now = DateTimeOffset.UtcNow;
        string Expiry(DateTimeOffset? e) => e is { } d ? d.ToString("yyyy-MM-dd") : "Permanent";
        string Node(GridLicense l, GridMember m) =>
            m.Nodes.Count > 1 ? $"Node {l.NodePosition + 1} · {l.NodeSerial}" : l.NodeSerial ?? "";

        TreeRow LicenseRow(GridLicense l, string node) => new(
        [
            l.Name, l.Type, node, l.Kind ?? "", Expiry(l.Expires), l.Status(now),
        ]);

        var roots = new List<TreeRow>();

        var gridWide = inv.Licenses.Where(l => l.IsGridWide).ToList();
        if (gridWide.Count > 0)
        {
            var group = new TreeRow(
                [$"Grid-wide   ({Plural(gridWide.Count, "license")})", "", "Applies to all members", "", "", ""],
                isGroup: true) { IsExpanded = true };
            foreach (var l in gridWide) group.Add(LicenseRow(l, ""));
            roots.Add(group);
        }

        foreach (var m in inv.Members)
        {
            var own = inv.Licenses.Where(l => l.MemberOid == m.Oid).ToList();
            var next = m.NextLicenseExpiry;
            var memberRow = new TreeRow(
            [
                $"{m.HostName}   ({Plural(own.Count, "license")})", "", m.Role, "",
                next is null ? "" : $"Next: {Expiry(next)}", GridLicense.ExpiryStatus(next, now),
            ], isGroup: true);
            foreach (var l in own) memberRow.Add(LicenseRow(l, Node(l, m)));
            roots.Add(memberRow);
        }

        return new TreeTableDefinition(
            "Licenses",
            [
                new("License", Width: 285), new("Code", Width: 100), new("Node", Width: 175),
                new("Kind", Width: 100), new("Expires", Width: 170), new("Status", ColumnKind.Status, 170),
            ],
            roots, "licenses");
    }

    private static TableDefinition Views(GridInventory inv, Lookup lk) => new(
        "DNS views",
        [
            new("View", Width: 220), new("Network view"), new("Zones", ColumnKind.Number),
            new("Records", ColumnKind.Number), new("Recursion"), new("DNSSEC validation"), new("Disabled"),
        ],
        inv.DnsViews.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(v => new TableRow(
        [
            v.Name, lk.NetworkViewName(v.NetworkViewId),
            inv.Zones.Count(z => z.ViewKey == v.Key).ToString(),
            inv.Records.Count(r => r.ViewKey == v.Key).ToString(),
            YesNo(v.RecursionEnabled), YesNo(v.DnssecValidationEnabled), YesNo(v.Disabled),
        ])).ToList());

    private static TreeTableDefinition Zones(GridInventory inv, Lookup lk)
    {
        int Records(IEnumerable<DnsZone> zones) => zones.Sum(z => lk.RecordCount(z.Key));
        const string blank = "";

        var roots = ZoneHierarchy(inv,
            zoneRow: z => new TreeRow(
            [
                z.Fqdn, z.ZoneType, z.PrimaryType ?? "",
                lk.MemberNames(z.GridPrimaryOids), lk.MemberNames(z.GridSecondaryOids),
                string.Join(", ", z.ExternalSecondaries.Concat(z.MicrosoftPrimaries).Concat(z.DelegatedServers)
                    .Select(ns => ns.Name).Distinct()),
                lk.RecordCount(z.Key).ToString(), YesNo(z.DnssecEnabled), YesNo(z.Disabled),
            ]),
            viewRow: (v, zones) => new TreeRow(
            [
                v.Name, $"View · {Plural(zones.Count, "zone")}", blank, blank, blank, blank,
                Records(zones).ToString(), blank, YesNo(v.Disabled),
            ], isGroup: true),
            groupRow: (name, label, zones) => new TreeRow(
            [
                name, $"{label} · {Plural(zones.Count, "zone")}", blank, blank, blank, blank,
                Records(zones).ToString(), blank, blank,
            ], isGroup: true),
            out _);

        return new TreeTableDefinition(
            "Zones",
            [
                new("Zone", Width: 320), new("Type", Width: 250), new("Primary"),
                new("Grid primaries"), new("Grid secondaries"), new("Other name servers", Width: 240),
                new("Records", ColumnKind.Number), new("DNSSEC"), new("Disabled"),
            ],
            roots, "zones");
    }

    /// <summary>
    /// The shared DNS tree: view → top-level domain (or IPv4/IPv6 reverse) → zone → sub-zone.
    /// Callers supply the row for each level; <paramref name="zoneNodes"/> returns each
    /// zone's row by zone key so callers can hang more rows (e.g. records) under it.
    /// </summary>
    private static List<TreeRow> ZoneHierarchy(
        GridInventory inv,
        Func<DnsZone, TreeRow> zoneRow,
        Func<DnsView, List<DnsZone>, TreeRow> viewRow,
        Func<string, string, List<DnsZone>, TreeRow> groupRow,
        out Dictionary<string, TreeRow> zoneNodes)
    {
        var parents = InventoryHierarchy.ParentZones(inv.Zones);
        var nodes = inv.Zones.ToDictionary(z => z.Key, zoneRow, StringComparer.Ordinal);
        zoneNodes = nodes;

        // Nest each zone under its nearest ancestor zone (inv.Zones is already sorted by name).
        foreach (var z in inv.Zones)
        {
            if (parents[z.Key] is { } parent) nodes[parent.Key].Add(nodes[z.Key]);
        }

        var roots = new List<TreeRow>();
        foreach (var view in inv.DnsViews.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var viewZones = inv.Zones.Where(z => z.ViewKey == view.Key).ToList();
            var vRow = viewRow(view, viewZones);
            vRow.IsExpanded = true;

            // Top-level zones go under their TLD (or in-addr.arpa / ip6.arpa) group.
            var tops = viewZones.Where(z => parents[z.Key] is null)
                .GroupBy(z => InventoryHierarchy.TopLevelGroup(z.Fqdn))
                .OrderBy(g => g.Key.EndsWith("arpa", StringComparison.Ordinal)) // reverse groups last
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in tops)
            {
                // A zone that *is* the TLD (e.g. a zone named "com") stands in for the group.
                if (group.Count() == 1 && string.Equals(group.First().Fqdn.TrimEnd('.'), group.Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    vRow.Add(nodes[group.First().Key]);
                    continue;
                }

                var label = group.Key switch
                {
                    "in-addr.arpa" => "IPv4 reverse",
                    "ip6.arpa" => "IPv6 reverse",
                    _ => "Top-level domain",
                };
                // Every zone in this view under the same TLD, including nested ones.
                var groupZones = viewZones
                    .Where(z => InventoryHierarchy.TopLevelGroup(z.Fqdn) == group.Key).ToList();
                var gRow = vRow.Add(groupRow(group.Key, label, groupZones));
                foreach (var z in group) gRow.Add(nodes[z.Key]);
            }

            roots.Add(vRow);
        }
        return roots;
    }

    private static TreeTableDefinition Networks(GridInventory inv, Lookup lk)
    {
        var parents = InventoryHierarchy.ParentNetworks(inv.Networks);

        // DHCP range counts: a network shows its own; a container the total beneath it.
        var rangeTotals = new Dictionary<IpNetwork, int>(ReferenceEqualityComparer.Instance);
        foreach (var n in inv.Networks.Where(n => !n.IsContainer))
        {
            var count = lk.RangeCount(n.Key);
            if (count == 0) continue;
            for (IpNetwork? p = n; p is not null; p = parents[p])
            {
                rangeTotals[p] = rangeTotals.GetValueOrDefault(p) + count;
            }
        }

        var nodes = new Dictionary<IpNetwork, TreeRow>(ReferenceEqualityComparer.Instance);
        foreach (var n in inv.Networks)
        {
            var ranges = rangeTotals.GetValueOrDefault(n);
            nodes[n] = new TreeRow(
            [
                n.Cidrs, n.IsContainer ? "Container" : "Network", n.IsIpv6 ? "IPv6" : "IPv4",
                ranges == 0 ? "" : ranges.ToString(), n.Comment ?? "", YesNo(n.Disabled),
            ]);
        }

        // inv.Networks is sorted by view and address, so children are added in order.
        var roots = new List<TreeRow>();
        foreach (var view in inv.Networks.GroupBy(n => n.NetworkViewId).OrderBy(g => g.Key))
        {
            var viewRanges = inv.DhcpRanges.Count(r => r.NetworkViewId == view.Key);
            var viewRow = new TreeRow(
            [
                lk.NetworkViewName(view.Key) is { Length: > 0 } name ? name : $"Network view {view.Key}",
                $"Network view · {Plural(view.Count(n => n.IsContainer), "container")}, {Plural(view.Count(n => !n.IsContainer), "network")}",
                "", viewRanges == 0 ? "" : viewRanges.ToString(), "", "",
            ], isGroup: true) { IsExpanded = true };

            foreach (var n in view)
            {
                (parents[n] is { } parent ? nodes[parent] : viewRow).Add(nodes[n]);
            }
            roots.Add(viewRow);
        }

        return new TreeTableDefinition(
            "Networks",
            [
                new("Network", ColumnKind.Ip, 320), new("Kind", Width: 330), new("IP version"),
                new("DHCP ranges", ColumnKind.Number), new("Comment", Width: 260), new("Disabled"),
            ],
            roots, "networks and containers");
    }

    private static TreeTableDefinition DhcpRanges(GridInventory inv, Lookup lk)
    {
        string ServedBy(DhcpRange r) => r.AssociationType switch
        {
            "Member" => lk.MemberName(r.MemberOid),
            "Failover" or "MsFailover" => r.FailoverName ?? "",
            _ => r.IsMicrosoft ? "Microsoft DHCP" : "",
        };

        var networks = inv.Networks.Where(n => !n.IsContainer)
            .ToDictionary(n => n.Key, StringComparer.Ordinal);

        var roots = new List<TreeRow>();
        foreach (var view in inv.DhcpRanges.GroupBy(r => r.NetworkViewId).OrderBy(g => g.Key))
        {
            var byNetwork = view.GroupBy(r => r.NetworkKey).ToList();
            var viewRow = new TreeRow(
            [
                $"{(lk.NetworkViewName(view.Key) is { Length: > 0 } name ? name : $"Network view {view.Key}")}"
                    + $"   ({Plural(byNetwork.Count, "network")}, {Plural(view.Count(), "range")})",
                "", "", "", "", "",
            ], isGroup: true) { IsExpanded = true };

            // Networks in address order; ranges in start-address order (inv.DhcpRanges is sorted).
            foreach (var net in byNetwork.OrderBy(g => g.Key.Contains(':')).ThenBy(g => g.Key,
                         Comparer<string>.Create((a, b) => new ColumnComparer(0, ColumnKind.Ip)
                             .CompareCells([StripView(a)], [StripView(b)]))))
            {
                networks.TryGetValue(net.Key, out var network);
                var netRow = viewRow.Add(new TreeRow(
                [
                    $"{StripView(net.Key)}   ({Plural(net.Count(), "range")})",
                    "", "", "", network?.Comment ?? "", "",
                ], isGroup: true));

                foreach (var r in net)
                {
                    var span = r.StartAddress.Length == 0 && r.EndAddress.Length == 0
                        ? "(prefix delegation)"
                        : $"{r.StartAddress} – {r.EndAddress}";
                    netRow.Add(new TreeRow(
                    [
                        span, ServedBy(r), r.AssociationType, r.Name ?? "", r.Comment ?? "", YesNo(r.Disabled),
                    ]));
                }
            }
            roots.Add(viewRow);
        }

        return new TreeTableDefinition(
            "DHCP ranges",
            [
                new("Range", ColumnKind.Ip, 360), new("Served by", Width: 240), new("Association"),
                new("Name"), new("Comment", Width: 240), new("Disabled"),
            ],
            roots, "ranges");
    }

    private static TableDefinition Failover(GridInventory inv, Lookup lk) => new(
        "DHCP failover",
        [
            new("Name", Width: 180), new("Type"), new("Primary", Width: 220), new("Secondary", Width: 220),
            new("Mode"), new("Ranges", ColumnKind.Number),
        ],
        inv.FailoverAssociations.Select(f => new TableRow(
        [
            f.Name, f.IsMicrosoft ? "Microsoft" : "Infoblox",
            f.IsMicrosoft ? f.MsServerAddress ?? "" : lk.MemberName(f.PrimaryOid),
            f.IsMicrosoft ? f.MsPartnerAddress ?? "" : lk.MemberName(f.SecondaryOid),
            f.MsMode?.ToLowerInvariant() ?? "",
            inv.DhcpRanges.Count(r => r.FailoverName == f.Name && r.IsMicrosoft == f.IsMicrosoft).ToString(),
        ])).ToList());

    private static TreeTableDefinition Records(GridInventory inv, Lookup lk)
    {
        const string blank = "";
        int Count(IEnumerable<DnsZone> zones) => zones.Sum(z => lk.RecordCount(z.Key));

        var roots = ZoneHierarchy(inv,
            zoneRow: z => new TreeRow(
                [$"{z.Fqdn}   ({Plural(lk.RecordCount(z.Key), "record")})", z.ZoneType, blank, blank, blank, YesNo(z.Disabled)],
                isGroup: true),
            viewRow: (v, zones) => new TreeRow(
                [$"{v.Name}   ({Plural(Count(zones), "record")})", "View", blank, blank, blank, YesNo(v.Disabled)],
                isGroup: true),
            groupRow: (name, label, zones) => new TreeRow(
                [$"{name}   ({Plural(Count(zones), "record")})", label, blank, blank, blank, blank],
                isGroup: true),
            out var zoneNodes);

        // Records sit under their zone, after any sub-zones: apex ("@") first, then by name and type.
        // Records generated by a host object are flagged in the Host column.
        var ordered = inv.Records
            .OrderBy(r => r.Name.Length > 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Type);
        foreach (var r in ordered)
        {
            if (!zoneNodes.TryGetValue(r.ZoneKey, out var zoneRow)) continue;
            zoneRow.Add(new TreeRow(
            [
                r.Name.Length == 0 ? "@" : r.Name, r.Type.ToString(), r.Value, YesNo(r.IsFromHost),
                r.Fqdn, YesNo(r.Disabled),
            ]));
        }

        return new TreeTableDefinition(
            "DNS records",
            [
                new("Name", Width: 340), new("Type", Width: 150), new("Value", Width: 320), new("Host"),
                new("FQDN", Width: 300), new("Disabled"),
            ],
            roots, "records");
    }

    /// <summary>"10.0.0.0/24/0" → "10.0.0.0/24".</summary>
    private static string StripView(string networkKey)
    {
        var last = networkKey.LastIndexOf('/');
        return last > networkKey.IndexOf('/') ? networkKey[..last] : networkKey;
    }

    /// <summary>Precomputed lookups so table building stays fast on large grids.</summary>
    private sealed class Lookup
    {
        private readonly Dictionary<int, string> _members;
        private readonly Dictionary<string, string> _views;
        private readonly Dictionary<int, string> _networkViews;
        private readonly Dictionary<string, string> _zoneFqdns;
        private readonly Dictionary<string, int> _recordCounts;
        private readonly Dictionary<string, int> _rangeCounts;

        public Lookup(GridInventory inv)
        {
            _members = inv.Members.ToDictionary(m => m.Oid, m => m.HostName);
            _views = inv.DnsViews.ToDictionary(v => v.Key, v => v.Name);
            _networkViews = inv.NetworkViews.ToDictionary(v => v.Id, v => v.Name);
            _zoneFqdns = inv.Zones.ToDictionary(z => z.Key, z => z.Fqdn);
            _recordCounts = inv.Records.GroupBy(r => r.ZoneKey).ToDictionary(g => g.Key, g => g.Count());
            _rangeCounts = inv.DhcpRanges.GroupBy(r => r.NetworkKey).ToDictionary(g => g.Key, g => g.Count());
        }

        public string MemberName(int? oid) =>
            oid is { } o && _members.TryGetValue(o, out var n) ? n : "";

        public string MemberNames(IEnumerable<int> oids) =>
            string.Join(", ", oids.Select(o => MemberName(o)).Where(n => n.Length > 0));

        public string ViewName(string key) => _views.GetValueOrDefault(key, key);

        public string NetworkViewName(int? id) =>
            id is { } i && _networkViews.TryGetValue(i, out var n) ? n : "";

        public string ZoneFqdn(string key) => _zoneFqdns.GetValueOrDefault(key, key);

        public int RecordCount(string zoneKey) => _recordCounts.GetValueOrDefault(zoneKey);

        public int RangeCount(string networkKey) => _rangeCounts.GetValueOrDefault(networkKey);
    }
}
