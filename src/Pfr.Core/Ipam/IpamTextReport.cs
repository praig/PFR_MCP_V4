using System.Text;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Ipam;

/// <summary>
/// The address plan as plain text, for the CLI: each network view with its networks nested by containment, the grids
/// each network is present in, and the DHCP ranges, hosts and (optionally) DNS records inside each network.
/// </summary>
public static class IpamTextReport
{
    private sealed record Line(string Label, string Kind, string PresentIn, string Detail, string? Raw);

    /// <param name="plan">The plan.</param>
    /// <param name="clientName">Shown in the heading.</param>
    /// <param name="notes">Anything worth saying about what the plan was built from (e.g. a grid with no snapshot).</param>
    /// <param name="grid">Show only networks present in this grid, and only its ranges, hosts and records.</param>
    /// <param name="show">The kinds of address-level object to list under their networks.</param>
    public static string Render(IpamPlan plan, string clientName, IReadOnlyList<string>? notes = null,
        string? grid = null, IpamLeafKinds show = IpamLeafKinds.Ranges | IpamLeafKinds.Hosts)
    {
        var filter = new IpamFilter(grid, show);
        var text = new StringBuilder();

        text.AppendLine($"Address plan for {clientName}");
        text.AppendLine("Grids: " + (plan.Sources.Count == 0
            ? "none"
            : string.Join(", ", plan.Sources.Select(s =>
                s.AsOf is { } at ? $"{s.Name} (snapshot of {at.ToUniversalTime():yyyy-MM-dd})" : s.Name))));
        text.AppendLine($"{Count(plan.NetworkCount, "network")}, {Count(plan.ContainerCount, "container")}, " +
                        $"{Count(plan.RangeCount, "DHCP range")}, {Count(plan.HostCount, "host")}, " +
                        $"{Count(plan.RecordCount, "DNS record")} in {Count(plan.Views.Count, "network view")}");
        if (grid is not null) text.AppendLine($"Showing only what is present in {grid}.");
        foreach (var note in (notes ?? []).Concat(plan.Problems)) text.AppendLine($"Note: {note}");

        foreach (var view in plan.Views.Where(filter.Shows))
        {
            text.AppendLine();
            text.AppendLine($"Network view: {view.Name}  ({Count(view.NetworkCount, "network")}, " +
                            $"{Count(view.RangeCount, "DHCP range")}, {Count(view.HostCount, "host")}, {Count(view.RecordCount, "DNS record")})");

            var lines = new List<Line>();
            foreach (var root in view.Roots) Walk(root, 1, lines, filter);

            var ranges = filter.UnplacedRanges(view).ToList();
            var hosts = filter.UnplacedHosts(view).ToList();
            var records = filter.UnplacedRecords(view).ToList();
            var outside = new[]
            {
                ranges.Count > 0 ? Count(ranges.Count, "DHCP range") : null,
                hosts.Count > 0 ? Count(hosts.Count, "host") : null,
                records.Count > 0 ? Count(records.Count, "DNS record") : null,
            }.Where(s => s is not null).ToList();
            if (outside.Count > 0)
            {
                lines.Add(new Line("", "", "", "", $"  (no network): {string.Join(", ", outside)} outside every network of this view"));
                foreach (var range in ranges) lines.Add(Raw(4, RangeText(range)));
                foreach (var host in hosts) AddHost(lines, 4, host, filter);
                foreach (var record in records) lines.Add(Raw(4, RecordText(record)));
            }

            var labelWidth = lines.Where(l => l.Raw is null).Select(l => l.Label.Length).DefaultIfEmpty(0).Max();
            var kindWidth = lines.Where(l => l.Raw is null).Select(l => l.Kind.Length).DefaultIfEmpty(0).Max();
            var presentWidth = lines.Where(l => l.Raw is null).Select(l => l.PresentIn.Length).DefaultIfEmpty(0).Max();
            foreach (var line in lines)
            {
                text.AppendLine(line.Raw ??
                                $"{line.Label.PadRight(labelWidth)}  {line.Kind.PadRight(kindWidth)}  {line.PresentIn.PadRight(presentWidth)}  {line.Detail}".TrimEnd());
            }
        }

        return text.ToString();
    }

    private static Line Raw(int indent, string text) => new("", "", "", "", new string(' ', indent) + text);

    private static void Walk(IpamNode node, int depth, List<Line> lines, IpamFilter filter)
    {
        if (!filter.Shows(node)) return;

        var detail = new List<string>();
        if (filter.Kinds.HasFlag(IpamLeafKinds.Ranges) && node.RangeCount > 0) detail.Add(Count(node.RangeCount, "range"));
        if (filter.Kinds.HasFlag(IpamLeafKinds.Hosts) && node.HostCount > 0) detail.Add(Count(node.HostCount, "host"));
        if (filter.Kinds.HasFlag(IpamLeafKinds.Records) && node.RecordCount > 0) detail.Add(Count(node.RecordCount, "record"));
        var comment = node.Comment;
        if (comment.Varies) detail.Add(string.Join(", ", node.Comments.Select(c => $"\"{c}\"")));
        else if (!string.IsNullOrEmpty(comment.Value)) detail.Add($"\"{comment.Value}\"");
        var disabled = node.Disabled;
        if (disabled.Varies) detail.Add("disabled in " + string.Join(", ", node.DisabledIn));
        else if (disabled.Value) detail.Add("disabled");

        lines.Add(new Line(new string(' ', depth * 2) + node.Key, node.IsContainer ? "container" : "network",
            string.Join(", ", node.PresentIn), string.Join("  ", detail), null));

        var indent = depth * 2 + 2;
        foreach (var range in filter.RangesShown(node)) lines.Add(Raw(indent, RangeText(range)));
        foreach (var host in filter.HostsShown(node)) AddHost(lines, indent, host, filter);
        foreach (var record in filter.RecordsShown(node)) lines.Add(Raw(indent, RecordText(record)));

        foreach (var child in node.Children) Walk(child, depth + 1, lines, filter);
    }

    /// <summary>A host, then the records it generates (when records are shown) a level below it.</summary>
    private static void AddHost(List<Line> lines, int indent, IpamHost host, IpamFilter filter)
    {
        lines.Add(Raw(indent, HostText(host)));
        foreach (var record in filter.HostRecordsShown(host)) lines.Add(Raw(indent + 2, GeneratedText(record)));
    }

    private static string RangeText(IpamRange range)
    {
        var text = new StringBuilder($"range {range.Start} - {range.End}  {range.Source.Name}");
        var serving = string.Join(" ", new[] { range.AssociationType, range.ServedBy }.Where(s => !string.IsNullOrEmpty(s)));
        if (serving.Length > 0) text.Append("  ").Append(serving);
        if (!string.IsNullOrEmpty(range.Name)) text.Append($"  \"{range.Name}\"");
        if (!string.IsNullOrEmpty(range.Comment)) text.Append($"  \"{range.Comment}\"");
        if (range.Disabled) text.Append("  disabled");
        return text.ToString();
    }

    private static string HostText(IpamHost host)
    {
        var text = new StringBuilder($"host {host.Address}  {host.Name}  {host.Source.Name}  {(host.IsDns ? "DNS" : "IPAM only")}");
        if (host.ConfigureForDhcp) text.Append("  DHCP");
        if (!string.IsNullOrEmpty(host.MacAddress)) text.Append($"  mac {host.MacAddress}");
        if (host.Aliases.Count > 0) text.Append($"  aliases {string.Join(", ", host.Aliases)}");
        if (TtlText(host.TtlSeconds, host.TtlSource) is { Length: > 0 } ttl) text.Append("  ").Append(ttl);
        if (!string.IsNullOrEmpty(host.Comment)) text.Append($"  \"{host.Comment}\"");
        if (host.Disabled) text.Append("  disabled");
        return text.ToString();
    }

    /// <summary>A record on its own: its address first, then its name and, when it isn't the address, its data.</summary>
    private static string RecordText(IpamRecord record)
    {
        var text = new StringBuilder($"record {record.Type} {record.Address}  {record.Name}");
        if (record.Type == DnsRecordType.PTR) text.Append($"  {record.Value}");
        text.Append($"  {record.Source.Name}");
        if (TtlText(record.TtlSeconds, record.TtlSource) is { Length: > 0 } ttl) text.Append("  ").Append(ttl);
        if (record.Disabled) text.Append("  disabled");
        return text.ToString();
    }

    /// <summary>A record under the host that generates it.</summary>
    private static string GeneratedText(IpamRecord record)
    {
        var text = new StringBuilder($"{record.Type}  {record.Name}  {record.Value}");
        if (TtlText(record.TtlSeconds, record.TtlSource) is { Length: > 0 } ttl) text.Append("  ").Append(ttl);
        if (record.Disabled) text.Append("  disabled");
        return text.ToString();
    }

    private static string TtlText(int? seconds, string source) =>
        seconds is { } s ? $"TTL {s}" + (source.Length > 0 ? $" ({source})" : "") : "";

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
