using Pfr.Core.Nios.Model;

namespace Pfr.Gui.Tables;

/// <summary>Turns a parsed inventory into the tables shown in the GUI.</summary>
public static class InventoryTables
{
    public static IReadOnlyList<TableDefinition> Build(GridInventory inv)
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

    private static TableDefinition Zones(GridInventory inv, Lookup lk) => new(
        "Zones",
        [
            new("Zone", Width: 260), new("View"), new("Type"), new("Primary"), new("Reverse"),
            new("Grid primaries"), new("Grid secondaries"), new("Other name servers", Width: 260),
            new("Records", ColumnKind.Number), new("DNSSEC"), new("Disabled"),
        ],
        inv.Zones.Select(z => new TableRow(
        [
            z.Fqdn, lk.ViewName(z.ViewKey), z.ZoneType, z.PrimaryType ?? "", YesNo(z.IsReverse),
            lk.MemberNames(z.GridPrimaryOids), lk.MemberNames(z.GridSecondaryOids),
            string.Join(", ", z.ExternalSecondaries.Concat(z.MicrosoftPrimaries).Concat(z.DelegatedServers)
                .Select(ns => ns.Name).Distinct()),
            lk.RecordCount(z.Key).ToString(), YesNo(z.DnssecEnabled), YesNo(z.Disabled),
        ])).ToList());

    private static TableDefinition Networks(GridInventory inv, Lookup lk) => new(
        "Networks",
        [
            new("Network", ColumnKind.Ip, 200), new("Kind"), new("IP version"), new("Network view"),
            new("DHCP ranges", ColumnKind.Number), new("Comment", Width: 260), new("Disabled"),
        ],
        inv.Networks.Select(n => new TableRow(
        [
            n.Cidrs, n.IsContainer ? "Container" : "Network", n.IsIpv6 ? "IPv6" : "IPv4",
            lk.NetworkViewName(n.NetworkViewId),
            n.IsContainer ? "" : lk.RangeCount(n.Key).ToString(),
            n.Comment ?? "", YesNo(n.Disabled),
        ])).ToList());

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
