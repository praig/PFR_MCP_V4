using System.Text.Json;

namespace Pfr.Core.Ipam;

/// <summary>
/// The address plan as JSON, for scripts: views, networks nested by containment, presences, and the DHCP ranges,
/// hosts and records inside each network.
/// </summary>
public static class IpamJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <param name="plan">The plan.</param>
    /// <param name="clientName">The client the plan is for.</param>
    /// <param name="notes">Anything worth saying about what the plan was built from.</param>
    /// <param name="grid">Include only networks present in this grid, and only its ranges, hosts and records.</param>
    /// <param name="show">The kinds of address-level object to include; the others are null.</param>
    public static string ToJson(IpamPlan plan, string clientName, IReadOnlyList<string>? notes = null,
        string? grid = null, IpamLeafKinds show = IpamLeafKinds.Ranges | IpamLeafKinds.Hosts)
    {
        var filter = new IpamFilter(grid, show);
        bool Shown(IpamLeafKinds kind) => show.HasFlag(kind);

        Dictionary<string, object?> Range(IpamRange r) => new()
        {
            ["start"] = r.Start,
            ["end"] = r.End,
            ["grid"] = r.Source.Name,
            ["view"] = r.ViewName,
            ["association"] = r.AssociationType,
            ["servedBy"] = r.ServedBy,
            ["name"] = r.Name,
            ["comment"] = r.Comment,
            ["disabled"] = r.Disabled,
        };

        Dictionary<string, object?> Record(IpamRecord r) => new()
        {
            ["type"] = r.Type.ToString(),
            ["address"] = r.Address,
            ["name"] = r.Name,
            ["value"] = r.Value,
            ["grid"] = r.Source.Name,
            ["view"] = r.ViewName,
            ["dnsView"] = r.DnsViewName,
            ["zone"] = r.Zone,
            ["ttl"] = r.TtlSeconds,
            ["ttlSource"] = r.TtlSource.Length > 0 ? r.TtlSource : null,
            ["host"] = r.HostName,
            ["disabled"] = r.Disabled,
        };

        Dictionary<string, object?> Host(IpamHost h) => new()
        {
            ["address"] = h.Address,
            ["name"] = h.Name,
            ["grid"] = h.Source.Name,
            ["view"] = h.ViewName,
            ["dns"] = h.IsDns,
            ["dhcp"] = h.ConfigureForDhcp,
            ["mac"] = h.MacAddress,
            ["aliases"] = h.Aliases,
            ["otherAddresses"] = h.OtherAddresses,
            ["ttl"] = h.TtlSeconds,
            ["ttlSource"] = h.TtlSource.Length > 0 ? h.TtlSource : null,
            ["comment"] = h.Comment,
            ["disabled"] = h.Disabled,
            ["records"] = Shown(IpamLeafKinds.Records) ? filter.HostRecordsShown(h).Select(Record).ToList() : null,
        };

        Dictionary<string, object?> Node(IpamNode n) => new()
        {
            ["prefix"] = n.Key,
            ["kind"] = n.IsContainer ? "container" : "network",
            ["ipVersion"] = n.IsIpv6 ? 6 : 4,
            ["presentIn"] = n.PresentIn,
            ["presences"] = n.Presences.Select(p => new
            {
                grid = p.Source.Name,
                view = p.ViewName,
                kind = p.IsContainer ? "container" : "network",
                comment = p.Comment,
                disabled = p.Disabled,
            }).ToList(),
            ["comment"] = n.Comment.Varies ? string.Join(", ", n.Comments) : n.Comment.Value,
            ["comments"] = n.Comments,
            ["commentVaries"] = n.Comment.Varies,
            ["disabled"] = n.Disabled.Varies ? null : n.Disabled.Value,
            ["disabledIn"] = n.DisabledIn,
            ["disabledVaries"] = n.Disabled.Varies,
            ["networkCount"] = n.NetworkCount,
            ["rangeCount"] = n.RangeCount,
            ["hostCount"] = n.HostCount,
            ["recordCount"] = n.RecordCount,
            ["ranges"] = Shown(IpamLeafKinds.Ranges) ? filter.RangesShown(n).Select(Range).ToList() : null,
            ["hosts"] = Shown(IpamLeafKinds.Hosts) ? filter.HostsShown(n).Select(Host).ToList() : null,
            ["records"] = Shown(IpamLeafKinds.Records) ? filter.RecordsShown(n).Select(Record).ToList() : null,
            ["children"] = n.Children.Where(filter.Shows).Select(Node).ToList(),
        };

        var document = new Dictionary<string, object?>
        {
            ["client"] = clientName,
            ["grids"] = plan.Sources.Select(s => new { name = s.Name, snapshotOf = s.AsOf?.ToUniversalTime().ToString("yyyy-MM-dd"), snapshotId = s.SnapshotId }).ToList(),
            ["totals"] = new
            {
                networks = plan.NetworkCount,
                containers = plan.ContainerCount,
                dhcpRanges = plan.RangeCount,
                hosts = plan.HostCount,
                dnsRecords = plan.RecordCount,
                networkViews = plan.Views.Count,
            },
            ["filter"] = grid,
            ["views"] = plan.Views.Where(filter.Shows).Select(v => new Dictionary<string, object?>
            {
                ["name"] = v.Name,
                ["networkCount"] = v.NetworkCount,
                ["rangeCount"] = v.RangeCount,
                ["hostCount"] = v.HostCount,
                ["recordCount"] = v.RecordCount,
                ["networks"] = v.Roots.Where(filter.Shows).Select(Node).ToList(),
                ["rangesInNoNetwork"] = Shown(IpamLeafKinds.Ranges) ? filter.UnplacedRanges(v).Select(Range).ToList() : null,
                ["hostsInNoNetwork"] = Shown(IpamLeafKinds.Hosts) ? filter.UnplacedHosts(v).Select(Host).ToList() : null,
                ["recordsInNoNetwork"] = Shown(IpamLeafKinds.Records) ? filter.UnplacedRecords(v).Select(Record).ToList() : null,
            }).ToList(),
            ["notes"] = (notes ?? []).ToList(),
            ["problems"] = plan.Problems,
        };

        return JsonSerializer.Serialize(document, Options);
    }
}
