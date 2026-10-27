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
        TreeRow ZoneRow(DnsZone z) => new(
        [
            z.Fqdn, z.ZoneType, z.PrimaryType ?? "",
            lk.MemberNames(z.GridPrimaryOids), lk.MemberNames(z.GridSecondaryOids),
            string.Join(", ", z.ExternalSecondaries.Concat(z.MicrosoftPrimaries).Concat(z.DelegatedServers)
                .Select(ns => ns.Name).Distinct()),
            lk.RecordCount(z.Key).ToString(), YesNo(z.DnssecEnabled), YesNo(z.Disabled),
        ]);

        var parents = InventoryHierarchy.ParentZones(inv.Zones);
        var nodes = inv.Zones.ToDictionary(z => z.Key, ZoneRow, StringComparer.Ordinal);

        // Nest each zone under its nearest ancestor zone (inv.Zones is already sorted by name).
        foreach (var z in inv.Zones)
        {
            if (parents[z.Key] is { } parent) nodes[parent.Key].Add(nodes[z.Key]);
        }

        var roots = new List<TreeRow>();
        foreach (var view in inv.DnsViews.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var viewZones = inv.Zones.Where(z => z.ViewKey == view.Key).ToList();
            var viewRow = new TreeRow(
            [
                view.Name, $"View · {Plural(viewZones.Count, "zone")}", "", "", "", "",
                viewZones.Sum(z => lk.RecordCount(z.Key)).ToString(), "", YesNo(view.Disabled),
            ], isGroup: true) { IsExpanded = true };

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
                    viewRow.Add(nodes[group.First().Key]);
                    continue;
                }

                var members = group.SelectMany(z => nodes[z.Key].SelfAndDescendants()).Where(r => !r.IsGroup).ToList();
                var label = group.Key switch
                {
                    "in-addr.arpa" => "IPv4 reverse",
                    "ip6.arpa" => "IPv6 reverse",
                    _ => "Top-level domain",
                };
                var groupRow = viewRow.Add(new TreeRow(
                [
                    group.Key, $"{label} · {Plural(members.Count, "zone")}", "", "", "", "",
                    members.Sum(r => int.TryParse(r.Cells[6], out var n) ? n : 0).ToString(), "", "",
                ], isGroup: true));
                foreach (var z in group) groupRow.Add(nodes[z.Key]);
            }

            roots.Add(viewRow);
        }

        return new TreeTableDefinition(
            "Zones",
            [
                new("Zone", Width: 320), new("Type", Width: 250), new("Primary"),
                new("Grid primaries"), new("Grid secondaries"), new("Other name servers", Width: 240),
                new("Records", ColumnKind.Number), new("DNSSEC"), new("Disabled"),
            ],
            roots, "zones");
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

    private static TableDefinition DhcpRanges(GridInventory inv, Lookup lk) => new(
        "DHCP ranges",
        [
            new("Start", ColumnKind.Ip, 170), new("End", ColumnKind.Ip, 170), new("Network", ColumnKind.Ip, 180),
            new("Network view"), new("Served by", Width: 220), new("Association"), new("Name"),
            new("Comment", Width: 200), new("Disabled"),
        ],
        inv.DhcpRanges.Select(r => new TableRow(
        [
            r.StartAddress, r.EndAddress, StripView(r.NetworkKey), lk.NetworkViewName(r.NetworkViewId),
            r.AssociationType switch
            {
                "Member" => lk.MemberName(r.MemberOid),
                "Failover" or "MsFailover" => r.FailoverName ?? "",
                _ => r.IsMicrosoft ? "Microsoft DHCP" : "",
            },
            r.AssociationType, r.Name ?? "", r.Comment ?? "", YesNo(r.Disabled),
        ])).ToList());

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

    private static TableDefinition Records(GridInventory inv, Lookup lk) => new(
        "DNS records",
        [
            new("Name", Width: 280), new("Type"), new("Value", Width: 280), new("Zone", Width: 220),
            new("View"), new("Disabled"),
        ],
        inv.Records.Select(r => new TableRow(
        [
            r.Fqdn, r.Type.ToString(), r.Value, lk.ZoneFqdn(r.ZoneKey), lk.ViewName(r.ViewKey), YesNo(r.Disabled),
        ])).ToList());

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
