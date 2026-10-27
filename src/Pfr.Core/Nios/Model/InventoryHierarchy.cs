namespace Pfr.Core.Nios.Model;

/// <summary>
/// Builds parent/child relationships for display: zones by DNS name within a view,
/// and IPAM prefixes by containment within a network view.
/// </summary>
public static class InventoryHierarchy
{
    /// <summary>
    /// The grouping label a zone sits under at the top of its view: the top-level
    /// domain ("com", "network"), or "in-addr.arpa" / "ip6.arpa" for reverse zones.
    /// </summary>
    public static string TopLevelGroup(string fqdn)
    {
        var name = fqdn.TrimEnd('.').ToLowerInvariant();
        if (name.EndsWith("in-addr.arpa", StringComparison.Ordinal)) return "in-addr.arpa";
        if (name.EndsWith("ip6.arpa", StringComparison.Ordinal)) return "ip6.arpa";
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>
    /// For each zone, the nearest ancestor zone in the same view (e.g. aws.lab.test → lab.test),
    /// or null if it has none. Keyed by zone key.
    /// </summary>
    public static Dictionary<string, DnsZone?> ParentZones(IEnumerable<DnsZone> zones)
    {
        var result = new Dictionary<string, DnsZone?>(StringComparer.Ordinal);
        foreach (var view in zones.GroupBy(z => z.ViewKey))
        {
            var byName = new Dictionary<string, DnsZone>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in view)
            {
                byName.TryAdd(z.Fqdn.TrimEnd('.'), z);
            }

            foreach (var z in view)
            {
                DnsZone? parent = null;
                var name = z.Fqdn.TrimEnd('.');
                for (var dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
                {
                    if (byName.TryGetValue(name[(dot + 1)..], out var candidate) && candidate != z)
                    {
                        parent = candidate;
                        break; // first suffix found is the nearest ancestor
                    }
                }
                result[z.Key] = parent;
            }
        }
        return result;
    }

    /// <summary>
    /// For each network or container, its smallest enclosing container in the same
    /// network view, or null at the top. Keyed by the item itself.
    /// </summary>
    public static Dictionary<IpNetwork, IpNetwork?> ParentNetworks(IEnumerable<IpNetwork> networks)
    {
        var result = new Dictionary<IpNetwork, IpNetwork?>(ReferenceEqualityComparer.Instance);
        foreach (var view in networks.GroupBy(n => n.NetworkViewId))
        {
            var items = view
                .Select(n => (Net: n, Ok: IpPrefix.TryParse(n.Address, n.Cidr, out var p), Prefix: p))
                .ToList();

            foreach (var bad in items.Where(i => !i.Ok)) result[bad.Net] = null;

            // Sorted by family, address, then shorter prefix first, a stack of open
            // ancestors finds each item's tightest enclosing prefix in one pass.
            var ordered = items.Where(i => i.Ok)
                .OrderBy(i => i.Prefix.IsIpv6)
                .ThenBy(i => i.Net.Address, Comparer<string>.Create(IpSort.Compare))
                .ThenBy(i => i.Prefix.Length)
                .ThenBy(i => i.Net.IsContainer ? 0 : 1) // a container wraps a same-size network
                .ToList();

            var stack = new Stack<(IpNetwork Net, IpPrefix Prefix)>();
            foreach (var (net, _, prefix) in ordered)
            {
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    var encloses = top.Prefix.StrictlyContains(prefix)
                                   || (top.Net.IsContainer && !net.IsContainer && top.Prefix == prefix);
                    if (encloses) break;
                    stack.Pop();
                }

                result[net] = stack.Count > 0 ? stack.Peek().Net : null;

                // In NIOS only containers hold other prefixes; networks are always leaves.
                if (net.IsContainer) stack.Push((net, prefix));
            }
        }
        return result;
    }
}
