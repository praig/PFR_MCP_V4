using Pfr.Core.Ipam;
using Pfr.Core.Nios.Model;

namespace Pfr.Gui.Tables;

/// <summary>What the IPAM details panel shows for the selected row: a title, a small table, and a note.</summary>
public sealed record IpamDetail(
    string Title, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, string Note);

/// <summary>
/// The details behind a row of the address plan (spec §13.2): for a network, what <b>each grid</b> says about it —
/// its network view, kind, comment, disabled flag and the snapshot it came from — including the grids and views the
/// row's own columns summarise or leave out; for a DHCP range, host or record, everything about it and where it sits.
/// </summary>
public static class IpamDetails
{
    /// <param name="subject">The selected row's <see cref="TreeRow.Tag"/>.</param>
    /// <returns>The details, or null when the row has nothing to show (or nothing is selected).</returns>
    public static IpamDetail? For(object? subject) => subject switch
    {
        IpamNode node => ForNetwork(node),
        IpamRangeTag range => ForRange(range),
        IpamHostTag host => ForHost(host),
        IpamRecordTag record => ForRecord(record),
        IpamView view => ForView(view),
        _ => null,
    };

    /// <summary>The most objects of a finding listed in the panel; the rest are counted (the CLI and JSON give them all).</summary>
    public const int MaxFindingRows = 500;

    /// <summary>
    /// What the panel shows for a finding: the objects it covers, or, for a finding about one network, what each grid says
    /// about that network (as when the network is selected in the tree) with the finding's summary on top.
    /// </summary>
    public static IpamDetail ForFinding(IpamPlan plan, IpamFinding finding)
    {
        if (finding.Items.Count == 0
            && plan.Views.SelectMany(v => v.AllNodes()).FirstOrDefault(n => n.Key == finding.Subject) is { } node)
        {
            var network = ForNetwork(node);
            return network with { Note = finding.Summary + (network.Note.Length > 0 ? "\n" + network.Note : "") };
        }

        var rows = finding.Items.Take(MaxFindingRows).Select(i => (IReadOnlyList<string>)[i]).ToList();
        var note = finding.Items.Count > MaxFindingRows
            ? $"{finding.Summary}\n{finding.Items.Count - MaxFindingRows:N0} more are not listed here; the CLI (pfr ipam-findings) lists them all."
            : finding.Summary;
        return new IpamDetail($"{finding.Title}: {finding.Subject}", ["Objects"], rows, note);
    }

    private static IpamDetail ForNetwork(IpamNode node)
    {
        var network = node.Network;
        var rows = network.Presences
            .OrderBy(p => p.Source.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ViewName, StringComparer.OrdinalIgnoreCase)
            .Select(p => (IReadOnlyList<string>)
            [
                p.Source.Name, p.ViewName, p.IsContainer ? "Container" : "Network",
                p.Comment ?? "", p.Disabled ? "Yes" : "", Snapshot(p.Source),
            ])
            .ToList();

        var notes = new List<string>();
        if (network.Views.Count > 1)
        {
            notes.Add($"This prefix is in {network.Views.Count} network views ({string.Join(", ", network.Views)}). " +
                      "It is one network, shown under each of them.");
        }
        if (network.Presences.Select(p => p.IsContainer).Distinct().Count() > 1)
        {
            notes.Add("The grids disagree on whether this is a container; the plan treats it as one if any grid says so.");
        }

        return new IpamDetail(
            $"{node.Key} · {(node.IsContainer ? "Container" : "Network")} · {(node.IsIpv6 ? "IPv6" : "IPv4")}",
            ["Grid", "Network view", "Kind", "Comment", "Disabled", "Snapshot"],
            rows, string.Join(" ", notes));
    }

    private static IpamDetail ForRange(IpamRangeTag tag)
    {
        var range = tag.Range;
        IReadOnlyList<string> row =
        [
            range.Source.Name, range.ViewName, range.ServedBy ?? "", range.AssociationType, range.Name ?? "",
            range.Comment ?? "", range.Disabled ? "Yes" : "", Snapshot(range.Source),
        ];

        return new IpamDetail(
            $"DHCP range {range.Start} – {range.End}",
            ["Grid", "Network view", "Served by", "Association", "Name", "Comment", "Disabled", "Snapshot"],
            [row], Placement(tag.Network, range.Source, range.ViewName, "start address"));
    }

    private static IpamDetail ForHost(IpamHostTag tag)
    {
        var host = tag.Host;
        IReadOnlyList<string> row =
        [
            host.Source.Name, host.ViewName, host.Name, host.Address, host.IsDns ? "DNS" : "IPAM only",
            host.ConfigureForDhcp ? "Yes" : "", host.MacAddress ?? "", Ttl(host.TtlSeconds, host.TtlSource),
            host.Disabled ? "Yes" : "", Snapshot(host.Source),
        ];

        var notes = new List<string>();
        if (!host.IsDns) notes.Add("An IPAM-only host: it occupies its addresses but has no DNS records.");
        if (host.Aliases.Count > 0) notes.Add($"Aliases: {string.Join(", ", host.Aliases)}.");
        if (host.OtherAddresses.Count > 0)
        {
            notes.Add($"Also has {(host.OtherAddresses.Count == 1 ? "the address" : "the addresses")} " +
                      $"{string.Join(", ", host.OtherAddresses)}; a host is listed once under each address it has.");
        }
        if (!string.IsNullOrEmpty(host.Comment)) notes.Add($"Comment: {host.Comment}.");
        if (host.Records.Count > 0)
        {
            notes.Add($"Generates {string.Join(" and ", host.Records.Select(r => r.Type.ToString()))} " +
                      $"{(host.Records.Count == 1 ? "record" : "records")} for this address, listed under it.");
        }
        notes.Add(Placement(tag.Network, host.Source, host.ViewName, "address"));

        return new IpamDetail(
            $"Host {host.Name} · {host.Address}",
            ["Grid", "Network view", "Name", "Address", "Kind", "DHCP", "MAC", "TTL", "Disabled", "Snapshot"],
            [row], string.Join(" ", notes));
    }

    private static IpamDetail ForRecord(IpamRecordTag tag)
    {
        var record = tag.Record;
        IReadOnlyList<string> row =
        [
            record.Source.Name, record.ViewName, record.DnsViewName, record.Zone, record.Type.ToString(), record.Name,
            record.Value, record.Address, Ttl(record.TtlSeconds, record.TtlSource), record.Disabled ? "Yes" : "",
            Snapshot(record.Source),
        ];

        var notes = new List<string>();
        if (record.Type == DnsRecordType.PTR)
            notes.Add($"A PTR record: its address {record.Address} is the one in its reverse name; the value is the name it points to.");
        if (tag.Host is { } host)
            notes.Add($"Generated by the host {host.Name}; it is listed under that host, not on its own.");
        notes.Add(Placement(tag.Network, record.Source, record.ViewName, "address"));

        return new IpamDetail(
            $"{record.Type} record {record.Name}",
            ["Grid", "Network view", "DNS view", "Zone", "Type", "Name", "Value", "Address", "TTL", "Disabled", "Snapshot"],
            [row], string.Join(" ", notes));
    }

    private static IpamDetail ForView(IpamView view)
    {
        var nodes = view.AllNodes().ToList();
        var ranges = nodes.SelectMany(n => n.Ranges).Concat(view.Unplaced).ToList();
        var hosts = nodes.SelectMany(n => n.Hosts).Concat(view.UnplacedHosts).ToList();
        var records = nodes.SelectMany(n => n.Records).Concat(view.UnplacedRecords)
            .Concat(hosts.SelectMany(h => h.Records)).ToList();

        var sources = nodes.SelectMany(n => n.Presences.Select(p => p.Source.Name))
            .Concat(ranges.Select(r => r.Source.Name))
            .Concat(hosts.Select(h => h.Source.Name))
            .Concat(records.Select(r => r.Source.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var rows = sources.Select(s => (IReadOnlyList<string>)
        [
            s,
            nodes.Count(n => n.Presences.Any(p => Is(p.Source.Name, s) && !p.IsContainer)).ToString(),
            nodes.Count(n => n.Presences.Any(p => Is(p.Source.Name, s) && p.IsContainer)).ToString(),
            ranges.Count(r => Is(r.Source.Name, s)).ToString(),
            hosts.Count(h => Is(h.Source.Name, s)).ToString(),
            records.Count(r => Is(r.Source.Name, s)).ToString(),
        ]).ToList();

        return new IpamDetail(
            $"Network view {view.Name}",
            ["Grid", "Networks", "Containers", "DHCP ranges", "Hosts", "DNS records"],
            rows,
            "What each grid has in this view. A network present in several grids counts for each of them.");
    }

    /// <summary>Where an address-level object sits: the network it is in, and whether its own grid has that network.</summary>
    private static string Placement(IpamNode? network, IpamSource source, string viewName, string what)
    {
        if (network is null) return $"Its {what} is in no network of the {viewName} view.";

        var text = $"Sits in {network.Key} ({(network.IsContainer ? "container" : "network")}), present in {string.Join(", ", network.PresentIn)}.";
        if (!network.PresentIn.Contains(source.Name, StringComparer.OrdinalIgnoreCase))
            text += $" {source.Name} does not have this network.";
        return text;
    }

    private static string Ttl(int? seconds, string source) =>
        seconds is { } s ? (source.Length > 0 ? $"{s} ({source})" : s.ToString()) : "";

    private static string Snapshot(IpamSource source) =>
        source.AsOf is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd") : "";
}
