using Pfr.Core.Ipam;
using Pfr.Core.Nios.Model;

namespace Pfr.Gui.Tables;

/// <summary>A DHCP range row's subject: the range, and the network it sits in (null under "(no network)").</summary>
public sealed record IpamRangeTag(IpamRange Range, IpamNode? Network);

/// <summary>A host row's subject: the host at one address, and the network it sits in (null under "(no network)").</summary>
public sealed record IpamHostTag(IpamHost Host, IpamNode? Network);

/// <summary>
/// An A / AAAA / PTR record row's subject: the record, the network it sits in, and the host that generates it (null for
/// a record on its own).
/// </summary>
public sealed record IpamRecordTag(IpamRecord Record, IpamNode? Network, IpamHost? Host);

/// <summary>
/// Turns the client's address plan into the tree the IPAM tab shows (spec §13.3): network view → containers →
/// networks, nested by containment, with each network's DHCP ranges, hosts and DNS records below it. Like the Networks
/// tree in Inventory, with a <b>Present in</b> column naming the grids a network is in. The A / AAAA / PTR records a
/// host generates are rows under the host, not repeated beside it. Each row's <see cref="TreeRow.Tag"/> is the plan
/// object it shows (an <see cref="IpamView"/>, <see cref="IpamNode"/>, <see cref="IpamRangeTag"/>,
/// <see cref="IpamHostTag"/> or <see cref="IpamRecordTag"/>), for the details panel.
/// </summary>
public static class IpamTables
{
    /// <param name="plan">The plan.</param>
    /// <param name="filter">Which networks and which kinds of object to show.</param>
    public static TreeTableDefinition Build(IpamPlan plan, IpamFilter filter)
    {
        var roots = new List<TreeRow>();
        foreach (var view in plan.Views.Where(filter.Shows))
        {
            var nodes = view.AllNodes().ToList();
            var containers = nodes.Count(n => n.IsContainer);
            var viewRow = new TreeRow(Cells(
                view.Name,
                $"Network view · {Plural(containers, "container")}, {Plural(nodes.Count - containers, "network")}",
                "", "", "",
                Counted(filter, IpamLeafKinds.Ranges, view.RangeCount),
                Counted(filter, IpamLeafKinds.Hosts, view.HostCount),
                Counted(filter, IpamLeafKinds.Records, view.RecordCount),
                "", ""), isGroup: true) { IsExpanded = true, Tag = view };

            foreach (var root in view.Roots) AddNode(viewRow, root, filter);

            var ranges = filter.UnplacedRanges(view).ToList();
            var hosts = filter.UnplacedHosts(view).ToList();
            var records = filter.UnplacedRecords(view).ToList();
            if (ranges.Count + hosts.Count + records.Count > 0)
            {
                var generated = hosts.Sum(h => filter.HostRecordsShown(h).Count());
                var outside = string.Join(", ", new[]
                {
                    ranges.Count > 0 ? Plural(ranges.Count, "DHCP range") : null,
                    hosts.Count > 0 ? Plural(hosts.Count, "host") : null,
                    records.Count > 0 ? Plural(records.Count, "DNS record") : null,
                }.Where(s => s is not null));

                var none = viewRow.Add(new TreeRow(Cells(
                    "(no network)", $"{outside} outside every network of this view",
                    "", "", "",
                    ranges.Count > 0 ? ranges.Count.ToString() : "",
                    hosts.Count > 0 ? hosts.Count.ToString() : "",
                    records.Count + generated > 0 ? (records.Count + generated).ToString() : "",
                    "", ""), isGroup: true));

                foreach (var range in ranges) none.Add(RangeRow(range, null));
                foreach (var host in hosts) none.Add(HostRow(host, null, filter));
                foreach (var record in records) none.Add(RecordRow(record, null, null));
            }

            roots.Add(viewRow);
        }

        return new TreeTableDefinition(
            "Address plan",
            [
                new("Network", ColumnKind.Ip, 300), new("Kind", Width: 330), new("Name", Width: 240),
                new("Present in", Width: 200), new("IP version"),
                new("DHCP ranges", ColumnKind.Number), new("Hosts", ColumnKind.Number), new("Records", ColumnKind.Number),
                new("Comment", Width: 260), new("Disabled"),
            ],
            roots, "networks, containers and objects");
    }

    private static void AddNode(TreeRow parent, IpamNode node, IpamFilter filter)
    {
        if (!filter.Shows(node)) return;

        var row = parent.Add(new TreeRow(Cells(
            node.Key,
            node.IsContainer ? "Container" : "Network",
            "",
            string.Join(", ", node.PresentIn),
            node.IsIpv6 ? "IPv6" : "IPv4",
            Counted(filter, IpamLeafKinds.Ranges, node.RangeCount),
            Counted(filter, IpamLeafKinds.Hosts, node.HostCount),
            Counted(filter, IpamLeafKinds.Records, node.RecordCount),
            Comment(node),
            Disabled(node))) { Tag = node });

        foreach (var range in filter.RangesShown(node)) row.Add(RangeRow(range, node));
        foreach (var host in filter.HostsShown(node)) row.Add(HostRow(host, node, filter));
        foreach (var record in filter.RecordsShown(node)) row.Add(RecordRow(record, node, null));
        foreach (var child in node.Children) AddNode(row, child, filter);
    }

    private static TreeRow RangeRow(IpamRange range, IpamNode? network)
    {
        var serving = string.Join(" ", new[] { range.AssociationType is "None" ? "" : range.AssociationType, range.ServedBy }
            .Where(s => !string.IsNullOrEmpty(s)));

        return new TreeRow(Cells(
            $"{range.Start} – {range.End}",
            serving.Length == 0 ? "DHCP range" : $"DHCP range · {serving}",
            range.Name ?? "",
            range.Source.Name,
            range.IsIpv6 ? "IPv6" : "IPv4",
            "", "", "",
            range.Comment ?? "",
            range.Disabled ? "Yes" : "")) { Tag = new IpamRangeTag(range, network) };
    }

    /// <summary>A host at one address, with the records it generates for that address as rows beneath it.</summary>
    private static TreeRow HostRow(IpamHost host, IpamNode? network, IpamFilter filter)
    {
        var row = new TreeRow(Cells(
            host.Address,
            "Host · " + (host.IsDns ? "DNS" : "IPAM only") + (host.ConfigureForDhcp ? " · DHCP" : ""),
            host.Name,
            host.Source.Name,
            host.IsIpv6 ? "IPv6" : "IPv4",
            "", "", "",
            host.Comment ?? "",
            host.Disabled ? "Yes" : "")) { Tag = new IpamHostTag(host, network) };

        foreach (var record in filter.HostRecordsShown(host)) row.Add(RecordRow(record, network, host));
        return row;
    }

    private static TreeRow RecordRow(IpamRecord record, IpamNode? network, IpamHost? host) => new(Cells(
        record.Address,
        record.Type == DnsRecordType.PTR ? $"PTR record · {record.Value}" : $"{record.Type} record",
        record.Name,
        record.Source.Name,
        record.IsIpv6 ? "IPv6" : "IPv4",
        "", "", "",
        "",
        record.Disabled ? "Yes" : "")) { Tag = new IpamRecordTag(record, network, host) };

    /// <summary>The ten cells of a row, in column order.</summary>
    private static IReadOnlyList<string> Cells(string address, string kind, string name, string presentIn, string ipVersion,
        string ranges, string hosts, string records, string comment, string disabled) =>
        [address, kind, name, presentIn, ipVersion, ranges, hosts, records, comment, disabled];

    /// <summary>A count for a column, blank when it is zero or that kind of object is not being shown.</summary>
    private static string Counted(IpamFilter filter, IpamLeafKinds kind, int count) =>
        filter.Kinds.HasFlag(kind) && count > 0 ? count.ToString() : "";

    /// <summary>The comment when the sources agree; each different comment, comma separated, when they don't.</summary>
    private static string Comment(IpamNode node) =>
        node.Comment.Varies ? string.Join(", ", node.Comments) : node.Comment.Value ?? "";

    /// <summary>"Yes" when every source has it disabled; the sources that do ("In DR") when only some do.</summary>
    private static string Disabled(IpamNode node) =>
        node.Disabled.Varies ? "In " + string.Join(", ", node.DisabledIn) : node.Disabled.Value ? "Yes" : "";

    private static string Plural(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";
}
