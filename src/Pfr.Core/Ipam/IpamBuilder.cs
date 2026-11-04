using System.Net;
using System.Net.Sockets;
using Pfr.Core.Nios;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Ipam;

/// <summary>
/// Builds a client's address plan from its grids' latest snapshots (spec §13).
/// <list type="bullet">
/// <item>A network is keyed by its prefix alone: the same prefix on several grids, or in several network views, is
/// one network with several presences.</item>
/// <item>Network views with the same name (any case) on different grids are one view in the tree.</item>
/// <item>Within a view, networks nest by containment across every grid; only containers hold other prefixes.</item>
/// <item>Address-level objects are not merged. DHCP ranges, hosts (one row per address) and A / AAAA / PTR records each
/// sit under the most specific network of their own view that contains their address, whichever grid that network is
/// on; one in no network goes under the view's "(no network)". The records a host generates are listed under the
/// host, not again as rows of their own.</item>
/// </list>
/// </summary>
public static class IpamBuilder
{
    public static IpamPlan Build(IEnumerable<IpamGridInput> grids)
    {
        var plan = new IpamPlan();
        var networks = new Dictionary<string, IpamNetwork>(StringComparer.Ordinal);
        var ranges = new List<IpamRange>();
        var hosts = new List<IpamHost>();
        var records = new List<IpamRecord>();

        foreach (var grid in grids.OrderBy(g => g.GridName, StringComparer.OrdinalIgnoreCase))
        {
            var source = new IpamSource(IpamSourceKind.Grid, grid.GridName, grid.AsOf, grid.SnapshotId);
            plan.Sources.Add(source);
            var inventory = grid.Inventory;

            var viewNames = new Dictionary<int, string>();
            foreach (var v in inventory.NetworkViews) viewNames.TryAdd(v.Id, v.Name);
            string ViewName(int? id) => id is { } known
                ? viewNames.TryGetValue(known, out var name) && name.Length > 0 ? name : $"network view {known}"
                : "network view (unknown)";

            foreach (var n in inventory.Networks)
            {
                if (!IpamKeys.TryPrefix(n.Address, n.Cidr, out var prefix, out var key))
                {
                    plan.Problems.Add($"{grid.GridName}: \"{n.Address}/{n.Cidr}\" is not a valid prefix and was left out.");
                    continue;
                }

                if (!networks.TryGetValue(key, out var network)) networks[key] = network = new IpamNetwork(key, prefix);
                network.Presences.Add(new IpamPresence(source, ViewName(n.NetworkViewId), n.IsContainer, n.Comment, n.Disabled));
            }

            foreach (var r in inventory.DhcpRanges)
            {
                ranges.Add(new IpamRange
                {
                    Source = source,
                    ViewName = ViewName(r.NetworkViewId),
                    Start = r.StartAddress,
                    End = r.EndAddress,
                    IsIpv6 = r.IsIpv6,
                    AssociationType = r.AssociationType,
                    ServedBy = ServedBy(inventory, r),
                    Name = r.Name,
                    Comment = r.Comment,
                    Disabled = r.Disabled,
                });
            }

            // Hosts: one row per address, so a host with three addresses is listed under each. The records a DNS host
            // generates are found again below by (DNS view, host name, address) and listed under its row.
            var hostRows = new Dictionary<string, IpamHost>(StringComparer.Ordinal);
            foreach (var h in inventory.Hosts)
            {
                foreach (var a in h.Addresses)
                {
                    if (IpamKeys.Canonical(a.Address) is not { } address) continue;
                    var row = new IpamHost
                    {
                        Source = source,
                        ViewName = ViewName(h.NetworkViewId),
                        Address = address,
                        IsIpv6 = a.IsIpv6,
                        Name = h.Name,
                        IsDns = h.IsDns,
                        ConfigureForDhcp = a.ConfigureForDhcp,
                        MacAddress = a.MacAddress,
                        Aliases = h.Aliases,
                        OtherAddresses = h.Addresses.Where(o => !ReferenceEquals(o, a)).Select(o => o.Address).ToList(),
                        TtlSeconds = h.IsDns ? h.Ttl.Seconds : null,
                        TtlSource = h.IsDns && h.Ttl.Seconds is not null ? h.Ttl.Source.ToString() : "",
                        Disabled = h.Disabled,
                        Comment = h.Comment,
                    };
                    hosts.Add(row);
                    if (h.IsDns) hostRows.TryAdd(HostKey(h.ViewKey, h.Name, address), row);
                }
            }

            // Address records: A and AAAA by the address in the record, PTR by the address its reverse name stands for.
            var dnsViews = new Dictionary<string, DnsView>(StringComparer.Ordinal);
            foreach (var v in inventory.DnsViews) dnsViews.TryAdd(v.Key, v);
            var zones = new Dictionary<string, DnsZone>(StringComparer.Ordinal);
            foreach (var z in inventory.Zones) zones.TryAdd(z.Key, z);

            var unreadablePtrs = 0;
            foreach (var r in inventory.Records)
            {
                if (r.Type is not (DnsRecordType.A or DnsRecordType.AAAA or DnsRecordType.PTR)) continue;

                var address = r.Type == DnsRecordType.PTR ? IpamKeys.AddressFromReverseName(r.Fqdn) : IpamKeys.Canonical(r.Value);
                if (address is null)
                {
                    if (r.Type == DnsRecordType.PTR) unreadablePtrs++;
                    continue;
                }

                dnsViews.TryGetValue(r.ViewKey, out var dnsView);
                var row = new IpamRecord
                {
                    Source = source,
                    ViewName = ViewName(dnsView?.NetworkViewId),
                    DnsViewName = dnsView?.Name ?? r.ViewKey,
                    Zone = zones.TryGetValue(r.ZoneKey, out var zone) ? zone.Fqdn : r.ZoneKey,
                    Type = r.Type,
                    Address = address,
                    IsIpv6 = address.Contains(':'),
                    Name = r.Fqdn,
                    Value = r.Value,
                    TtlSeconds = r.Ttl.Seconds,
                    TtlSource = r.Ttl.Seconds is null ? "" : r.Ttl.Source.ToString(),
                    Disabled = r.Disabled,
                    HostName = r.HostName,
                };

                if (r.HostName is not null && hostRows.TryGetValue(HostKey(r.ViewKey, r.HostName, address), out var owner))
                    owner.Records.Add(row);
                else
                    records.Add(row);
            }

            if (unreadablePtrs > 0)
            {
                plan.Problems.Add($"{grid.GridName}: {unreadablePtrs:N0} PTR record{(unreadablePtrs == 1 ? "" : "s")} " +
                                  "are not for a single address (a classless delegation, for example) and are left out.");
            }
        }

        // One entry per prefix, IPv4 before IPv6, in address order: the order the tree is nested in.
        plan.Networks.AddRange(networks.Values
            .OrderBy(n => n.IsIpv6)
            .ThenBy(n => n.Prefix.Address.ToString(), Comparer<string>.Create(IpSort.Compare))
            .ThenBy(n => n.Prefix.Length));

        // A network goes under each network view it has a presence in, with that view's presences.
        var nodesByView = new Dictionary<string, List<IpamNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var network in plan.Networks)
        {
            foreach (var inView in network.Presences.GroupBy(p => p.ViewName, StringComparer.OrdinalIgnoreCase))
            {
                if (!nodesByView.TryGetValue(inView.Key, out var list)) nodesByView[inView.Key] = list = [];
                list.Add(new IpamNode(network, inView.Key, inView.ToList()));
            }
        }

        // A view with only ranges, hosts or records still exists, so those can be shown.
        foreach (var viewName in ranges.Select(r => r.ViewName).Concat(hosts.Select(h => h.ViewName)).Concat(records.Select(r => r.ViewName)))
        {
            if (!nodesByView.ContainsKey(viewName)) nodesByView[viewName] = [];
        }

        var rangesByView = ranges.ToLookup(r => r.ViewName, StringComparer.OrdinalIgnoreCase);
        var hostsByView = hosts.ToLookup(h => h.ViewName, StringComparer.OrdinalIgnoreCase);
        var recordsByView = records.ToLookup(r => r.ViewName, StringComparer.OrdinalIgnoreCase);

        foreach (var (viewName, nodes) in nodesByView.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            var view = new IpamView(viewName);
            Nest(view, nodes);

            var finder = new NodeFinder(nodes);
            Place(rangesByView[viewName], r => r.Start, finder, n => n.Ranges, view.Unplaced);
            Place(hostsByView[viewName], h => h.Address, finder, n => n.Hosts, view.UnplacedHosts);
            Place(recordsByView[viewName], r => r.Address, finder, n => n.Records, view.UnplacedRecords);

            view.Unplaced.Sort(IpamRange.Compare);
            view.UnplacedHosts.Sort(IpamHost.Compare);
            view.UnplacedRecords.Sort(IpamRecord.Compare);
            foreach (var node in nodes)
            {
                node.Ranges.Sort(IpamRange.Compare);
                node.Hosts.Sort(IpamHost.Compare);
                node.Records.Sort(IpamRecord.Compare);
            }
            foreach (var host in hostsByView[viewName]) host.Records.Sort(IpamRecord.Compare);

            RollUp(view);
            plan.Views.Add(view);
        }

        plan.RangeCount = ranges.Count;
        plan.HostCount = hosts.Count;
        plan.RecordCount = records.Count + hosts.Sum(h => h.Records.Count);
        return plan;
    }

    private static string? ServedBy(GridInventory inventory, DhcpRange range)
    {
        if (range.FailoverName is { Length: > 0 } failover) return failover;
        if (range.IsMicrosoft) return "Microsoft server";
        return inventory.FindMember(range.MemberOid)?.HostName;
    }

    private static string HostKey(string viewKey, string name, string address) =>
        $"{viewKey}\u001f{name.ToLowerInvariant()}\u001f{address}";

    /// <summary>
    /// Puts each node under its tightest enclosing container. The nodes are in address order with shorter prefixes
    /// first, so a stack of open containers finds every parent in one pass. Networks are leaves: only containers
    /// hold other prefixes.
    /// </summary>
    private static void Nest(IpamView view, List<IpamNode> nodes)
    {
        var open = new Stack<IpamNode>();
        foreach (var node in nodes)
        {
            while (open.Count > 0 && !open.Peek().Network.Prefix.StrictlyContains(node.Network.Prefix)) open.Pop();

            if (open.Count > 0)
            {
                node.Parent = open.Peek();
                open.Peek().Children.Add(node);
            }
            else
            {
                view.Roots.Add(node);
            }

            if (node.IsContainer) open.Push(node);
        }
    }

    /// <summary>Puts each item under the most specific network of the view that contains its address, or in <paramref name="unplaced"/>.</summary>
    private static void Place<T>(IEnumerable<T> items, Func<T, string> addressOf, NodeFinder finder,
        Func<IpamNode, List<T>> homeOf, List<T> unplaced)
    {
        foreach (var item in items)
        {
            if (finder.Find(addressOf(item)) is { } node) homeOf(node).Add(item);
            else unplaced.Add(item);
        }
    }

    /// <summary>Finds the most specific network of a view that contains an address, by trying the prefix lengths in use.</summary>
    private sealed class NodeFinder
    {
        private readonly Dictionary<string, IpamNode> _byKey;
        private readonly List<int> _lengthsV4;
        private readonly List<int> _lengthsV6;

        public NodeFinder(List<IpamNode> nodes)
        {
            _byKey = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
            _lengthsV4 = LengthsOf(nodes, ipv6: false);
            _lengthsV6 = LengthsOf(nodes, ipv6: true);
        }

        public IpamNode? Find(string address)
        {
            if (!IPAddress.TryParse(address, out var ip)) return null;
            var lengths = ip.AddressFamily == AddressFamily.InterNetworkV6 ? _lengthsV6 : _lengthsV4;
            foreach (var length in lengths)
            {
                if (_byKey.TryGetValue(IpamKeys.Key(IpamKeys.Mask(ip, length), length), out var node)) return node;
            }
            return null;
        }

        private static List<int> LengthsOf(List<IpamNode> nodes, bool ipv6) => nodes
            .Where(n => n.IsIpv6 == ipv6).Select(n => n.Network.Prefix.Length).Distinct().OrderByDescending(l => l).ToList();
    }

    private readonly record struct Totals(int Ranges, int Hosts, int Records, int Networks);

    private static void RollUp(IpamView view)
    {
        var totals = new Totals();
        foreach (var root in view.Roots)
        {
            var t = RollUp(root);
            totals = new Totals(totals.Ranges + t.Ranges, totals.Hosts + t.Hosts, totals.Records + t.Records, totals.Networks + t.Networks);
        }

        view.NetworkCount = totals.Networks;
        view.RangeCount = totals.Ranges + view.Unplaced.Count;
        view.HostCount = totals.Hosts + view.UnplacedHosts.Count;
        view.RecordCount = totals.Records + view.UnplacedRecords.Count + view.UnplacedHosts.Sum(h => h.Records.Count);
    }

    private static Totals RollUp(IpamNode node)
    {
        var ranges = node.Ranges.Count;
        var hosts = node.Hosts.Count;
        var records = node.Records.Count + node.Hosts.Sum(h => h.Records.Count);
        var networks = node.IsContainer ? 0 : 1;
        foreach (var child in node.Children)
        {
            var t = RollUp(child);
            ranges += t.Ranges;
            hosts += t.Hosts;
            records += t.Records;
            networks += t.Networks;
        }

        node.RangeCount = ranges;
        node.HostCount = hosts;
        node.RecordCount = records;
        node.NetworkCount = networks;
        return new Totals(ranges, hosts, records, networks);
    }
}
