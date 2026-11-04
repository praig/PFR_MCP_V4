using System.Text;
using System.Text.Json;
using Pfr.Core.Nios;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Ipam;

public enum IpamSeverity
{
    /// <summary>Worth knowing; often intended.</summary>
    Info,

    /// <summary>Probably a mistake, or something that will cause one.</summary>
    Warning,
}

public enum IpamFindingKind
{
    PresenceGap,
    MultipleViews,
    KindDisagrees,
    DisabledDisagrees,
    OverlappingNetworks,
    OutsideAnyNetwork,
    NetworkNotOnGrid,
    DuplicateHostAddress,
    ARecordWithoutPtr,
    PtrNamesOtherHost,
    NetworkWithoutDhcpRange,
}

/// <summary>
/// One thing the address plan shows that is worth a look (spec §13.7). A finding is a group: one network, one address,
/// or one grid's set of similar objects, with the objects listed in <see cref="Items"/>.
/// </summary>
/// <param name="Kind">What sort of finding it is.</param>
/// <param name="Severity">Warning or information.</param>
/// <param name="Subject">What it is about: a prefix, an address, a grid or a network view.</param>
/// <param name="Summary">A sentence saying what is wrong and where.</param>
/// <param name="Sources">The grids it involves.</param>
/// <param name="Items">The objects it covers (may be empty when the subject says it all).</param>
public sealed record IpamFinding(
    IpamFindingKind Kind, IpamSeverity Severity, string Subject, string Summary,
    IReadOnlyList<string> Sources, IReadOnlyList<string> Items)
{
    /// <summary>The short name used on the command line, e.g. "multiple-views".</summary>
    public string Id => IpamFindings.Id(Kind);

    public string Title => IpamFindings.Title(Kind);

    /// <summary>How many objects it covers (one when it is about the subject itself).</summary>
    public int Count => Items.Count == 0 ? 1 : Items.Count;
}

/// <summary>
/// Looks over a client's address plan for gaps, conflicts and untidiness. Everything it reports comes from the plan
/// (which comes from the grids' stored snapshots): nothing is contacted and nothing is changed.
/// </summary>
public static class IpamFindings
{
    private static readonly (IpamFindingKind Kind, string Id, string Title)[] Catalog =
    [
        (IpamFindingKind.OverlappingNetworks, "overlapping-networks", "Network contains another network"),
        (IpamFindingKind.MultipleViews, "multiple-views", "Same prefix in several network views"),
        (IpamFindingKind.KindDisagrees, "kind-disagrees", "Grids disagree on container or network"),
        (IpamFindingKind.OutsideAnyNetwork, "outside-any-network", "Objects in no network"),
        (IpamFindingKind.NetworkNotOnGrid, "network-not-on-grid", "Objects on a grid that does not have their network"),
        (IpamFindingKind.DuplicateHostAddress, "duplicate-host-address", "Same address held by hosts on different grids"),
        (IpamFindingKind.PresenceGap, "presence-gap", "Network on some of the client's grids but not others"),
        (IpamFindingKind.DisabledDisagrees, "disabled-disagrees", "Network disabled on some grids but not others"),
        (IpamFindingKind.ARecordWithoutPtr, "a-without-ptr", "A / AAAA records with no PTR record"),
        (IpamFindingKind.PtrNamesOtherHost, "ptr-other-name", "PTR record names a different host"),
        (IpamFindingKind.NetworkWithoutDhcpRange, "no-dhcp-range", "IPv4 networks with no DHCP range"),
    ];

    /// <summary>Every kind of finding with its command-line name and title, in the order they are listed.</summary>
    public static IReadOnlyList<(IpamFindingKind Kind, string Id, string Title)> Kinds => Catalog;

    public static string Id(IpamFindingKind kind) => Catalog.First(c => c.Kind == kind).Id;

    public static string Title(IpamFindingKind kind) => Catalog.First(c => c.Kind == kind).Title;

    /// <summary>Reads a kind from its command-line name ("multiple-views"), in any case.</summary>
    public static bool TryParseKind(string? text, out IpamFindingKind kind)
    {
        var match = Catalog.FirstOrDefault(c => string.Equals(c.Id, text?.Trim(), StringComparison.OrdinalIgnoreCase));
        kind = match.Kind;
        return match.Id is not null;
    }

    /// <summary>Warnings first, then information; within each, the kinds in the order of <see cref="Kinds"/>.</summary>
    public static IReadOnlyList<IpamFinding> Analyze(IpamPlan plan)
    {
        var findings = new List<IpamFinding>();
        var grids = plan.Sources.Where(s => s.Kind == IpamSourceKind.Grid).Select(s => s.Name).ToList();

        AnalyzeNetworks(plan, grids, findings);
        AnalyzeOverlaps(plan, findings);
        AnalyzePlacement(plan, findings);
        AnalyzeHostAddresses(plan, findings);
        AnalyzePtrs(plan, findings);
        AnalyzeDhcp(plan, findings);

        var order = Catalog.Select((c, i) => (c.Kind, i)).ToDictionary(x => x.Kind, x => x.i);
        return findings.OrderByDescending(f => f.Severity).ThenBy(f => order[f.Kind]).ToList();
    }

    /// <summary>The findings of the given kinds, at least this severe, that involve the grid (any when null).</summary>
    public static IReadOnlyList<IpamFinding> Filter(
        IEnumerable<IpamFinding> findings, IReadOnlySet<IpamFindingKind>? kinds = null,
        IpamSeverity? atLeast = null, string? grid = null) =>
        findings
            .Where(f => kinds is null || kinds.Contains(f.Kind))
            .Where(f => atLeast is null || f.Severity >= atLeast)
            .Where(f => grid is null || f.Sources.Contains(grid, StringComparer.OrdinalIgnoreCase))
            .ToList();

    // ---- Networks: where each prefix is, and what the grids say about it ----

    private static void AnalyzeNetworks(IpamPlan plan, List<string> grids, List<IpamFinding> findings)
    {
        foreach (var n in plan.Networks)
        {
            var presentIn = n.PresentIn;

            // On two or more grids but not all of them. A network on one grid only is normal and is not reported.
            if (grids.Count >= 2 && presentIn.Count >= 2 && presentIn.Count < grids.Count)
            {
                var missing = grids.Where(g => !presentIn.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
                findings.Add(new IpamFinding(IpamFindingKind.PresenceGap, IpamSeverity.Info, n.Key,
                    $"{n.Key} is on {Join(presentIn)} but not on {Join(missing)}.", grids, []));
            }

            if (n.Views.Count > 1)
            {
                findings.Add(new IpamFinding(IpamFindingKind.MultipleViews, IpamSeverity.Warning, n.Key,
                    $"{n.Key} is in {n.Views.Count} network views ({Join(n.Views)}). That is fine if they are separate address spaces; " +
                    "if not, one of them is a mistake. The plan counts it as one network.", presentIn, []));
            }

            var containerOn = n.Presences.Where(p => p.IsContainer).Select(p => p.Source.Name).Distinct().ToList();
            var networkOn = n.Presences.Where(p => !p.IsContainer).Select(p => p.Source.Name).Distinct().ToList();
            if (containerOn.Count > 0 && networkOn.Count > 0)
            {
                findings.Add(new IpamFinding(IpamFindingKind.KindDisagrees, IpamSeverity.Warning, n.Key,
                    $"{n.Key} is a container on {Join(containerOn)} and a network on {Join(networkOn)}.", presentIn, []));
            }

            var disabledOn = n.Presences.Where(p => p.Disabled).Select(p => p.Source.Name).Distinct().ToList();
            var enabledOn = n.Presences.Where(p => !p.Disabled).Select(p => p.Source.Name).Distinct().ToList();
            if (disabledOn.Count > 0 && enabledOn.Count > 0)
            {
                findings.Add(new IpamFinding(IpamFindingKind.DisabledDisagrees, IpamSeverity.Info, n.Key,
                    $"{n.Key} is disabled on {Join(disabledOn)} but enabled on {Join(enabledOn)}.", presentIn, []));
            }
        }
    }

    // ---- A network that lies inside another network ----

    private static void AnalyzeOverlaps(IpamPlan plan, List<IpamFinding> findings)
    {
        foreach (var view in plan.Views)
        {
            // The tree lists a view in address order with the shorter prefix first, so a stack of the open networks finds
            // anything that lies inside one. Only containers are meant to hold other prefixes.
            var open = new Stack<IpamNode>();
            foreach (var node in view.AllNodes())
            {
                while (open.Count > 0 && !open.Peek().Network.Prefix.StrictlyContains(node.Network.Prefix)) open.Pop();

                if (open.Count > 0)
                {
                    var outer = open.Peek();
                    findings.Add(new IpamFinding(IpamFindingKind.OverlappingNetworks, IpamSeverity.Warning, node.Key,
                        $"{node.Key} lies inside the network {outer.Key}{InView(plan, view)}. A network should not contain another network; " +
                        "one of them is probably the wrong size, or should be a container.",
                        outer.PresentIn.Concat(node.PresentIn).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        [$"{outer.Key} (network on {Join(outer.PresentIn)})", $"{node.Key} ({(node.IsContainer ? "container" : "network")} on {Join(node.PresentIn)})"]));
                }

                if (!node.IsContainer) open.Push(node);
            }
        }
    }

    // ---- Ranges, hosts and records that are in no network, or in one their own grid does not have ----

    private static void AnalyzePlacement(IpamPlan plan, List<IpamFinding> findings)
    {
        foreach (var view in plan.Views)
        {
            var inView = InView(plan, view);

            var outside = view.Unplaced.Select(r => (Grid: r.Source.Name, Item: Describe(r)))
                .Concat(view.UnplacedHosts.Select(h => (Grid: h.Source.Name, Item: Describe(h))))
                .Concat(view.UnplacedRecords.Select(r => (Grid: r.Source.Name, Item: Describe(r))));
            foreach (var byGrid in outside.GroupBy(x => x.Grid, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var items = byGrid.Select(x => x.Item).ToList();
                findings.Add(new IpamFinding(IpamFindingKind.OutsideAnyNetwork, IpamSeverity.Warning, byGrid.Key,
                    $"{Count(items.Count, "object")} on {byGrid.Key} {(items.Count == 1 ? "is" : "are")} in no network{inView}: " +
                    "their addresses are not in any network or container of the plan.",
                    [byGrid.Key], items));
            }

            foreach (var node in view.AllNodes())
            {
                var present = node.PresentIn;
                var placed = node.Ranges.Select(r => (Grid: r.Source.Name, Item: Describe(r)))
                    .Concat(node.Hosts.Select(h => (Grid: h.Source.Name, Item: Describe(h))))
                    .Concat(node.Records.Select(r => (Grid: r.Source.Name, Item: Describe(r))));

                foreach (var byGrid in placed.GroupBy(x => x.Grid, StringComparer.OrdinalIgnoreCase)
                             .Where(g => !present.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var items = byGrid.Select(x => x.Item).ToList();
                    findings.Add(new IpamFinding(IpamFindingKind.NetworkNotOnGrid, IpamSeverity.Warning, node.Key,
                        $"{Count(items.Count, "object")} on {byGrid.Key} {(items.Count == 1 ? "sits" : "sit")} in {node.Key}{inView}, " +
                        $"which {byGrid.Key} does not have. The network is on {Join(present)}.",
                        present.Append(byGrid.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), items));
                }
            }
        }
    }

    // ---- The same address on hosts of different grids ----

    private static void AnalyzeHostAddresses(IpamPlan plan, List<IpamFinding> findings)
    {
        var hosts = plan.Views.SelectMany(v => v.AllNodes().SelectMany(n => n.Hosts).Concat(v.UnplacedHosts)).ToList();

        foreach (var group in hosts.GroupBy(h => h.Address, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, Comparer<string>.Create(IpSort.Compare)))
        {
            var grids = group.Select(h => h.Source.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (grids.Count < 2) continue;

            // In one network view on both grids it is almost certainly a clash; in different views it may be separate space.
            var sameView = group.GroupBy(h => h.ViewName, StringComparer.OrdinalIgnoreCase)
                .Any(v => v.Select(h => h.Source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            var showView = plan.Views.Count > 1;

            findings.Add(new IpamFinding(IpamFindingKind.DuplicateHostAddress,
                sameView ? IpamSeverity.Warning : IpamSeverity.Info, group.Key,
                $"{group.Key} is held by hosts on {grids.Count} grids ({Join(grids)})."
                + (sameView ? "" : " They are in different network views, so this may be separate address space."),
                grids,
                group.OrderBy(h => h.Source.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(h => $"{h.Name} on {h.Source.Name}" + (showView ? $" (network view {h.ViewName})" : "")).ToList()));
        }
    }

    // ---- A / AAAA records and their PTR records ----

    private static void AnalyzePtrs(IpamPlan plan, List<IpamFinding> findings)
    {
        var records = new List<IpamRecord>();
        foreach (var view in plan.Views)
        {
            foreach (var node in view.AllNodes())
            {
                records.AddRange(node.Records);
                foreach (var host in node.Hosts) records.AddRange(host.Records);
            }
            records.AddRange(view.UnplacedRecords);
            foreach (var host in view.UnplacedHosts) records.AddRange(host.Records);
        }

        // The names each address's PTR records give it, within a network view. A disabled record is not served, so it
        // does not count on either side.
        var ptrNames = records
            .Where(r => r.Type == DnsRecordType.PTR && !r.Disabled)
            .GroupBy(r => (View: r.ViewName.ToLowerInvariant(), r.Address))
            .ToDictionary(g => g.Key, g => g.Select(r => Normalize(r.Value)).ToHashSet(StringComparer.Ordinal));

        var without = new List<IpamRecord>();
        var other = new List<(IpamRecord Record, HashSet<string> Names)>();
        foreach (var r in records.Where(r => r.Type is DnsRecordType.A or DnsRecordType.AAAA && !r.Disabled))
        {
            if (!ptrNames.TryGetValue((r.ViewName.ToLowerInvariant(), r.Address), out var names)) without.Add(r);
            else if (!names.Contains(Normalize(r.Name))) other.Add((r, names));
        }

        foreach (var byGrid in without.GroupBy(r => r.Source.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var items = byGrid.OrderBy(r => r.Address, Comparer<string>.Create(IpSort.Compare)).Select(r => $"{r.Name} {r.Address}").ToList();
            findings.Add(new IpamFinding(IpamFindingKind.ARecordWithoutPtr, IpamSeverity.Info, byGrid.Key,
                $"{Count(items.Count, "A / AAAA record")} on {byGrid.Key} {(items.Count == 1 ? "has" : "have")} no PTR record for the address, on any grid.",
                [byGrid.Key], items));
        }

        foreach (var byGrid in other.GroupBy(x => x.Record.Source.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var items = byGrid.OrderBy(x => x.Record.Address, Comparer<string>.Create(IpSort.Compare))
                .Select(x => $"{x.Record.Name} {x.Record.Address} (PTR says {Join(x.Names.Order(StringComparer.Ordinal).ToList())})").ToList();
            findings.Add(new IpamFinding(IpamFindingKind.PtrNamesOtherHost, IpamSeverity.Info, byGrid.Key,
                $"{Count(items.Count, "A / AAAA record")} on {byGrid.Key} {(items.Count == 1 ? "has" : "have")} a PTR record for the address, but not one that names this host. " +
                "That is normal when one address has several names.",
                [byGrid.Key], items));
        }
    }

    // ---- IPv4 networks with nothing to hand out addresses ----

    private static void AnalyzeDhcp(IpamPlan plan, List<IpamFinding> findings)
    {
        foreach (var view in plan.Views)
        {
            var bare = view.AllNodes()
                .Where(n => !n.IsContainer && !n.IsIpv6 && n.RangeCount == 0 && !n.Presences.All(p => p.Disabled))
                .ToList();
            if (bare.Count == 0) continue;

            findings.Add(new IpamFinding(IpamFindingKind.NetworkWithoutDhcpRange, IpamSeverity.Info, view.Name,
                $"{Count(bare.Count, "IPv4 network")}{InView(plan, view)} {(bare.Count == 1 ? "has" : "have")} no DHCP range. " +
                "That is normal for networks that only have static addresses.",
                bare.SelectMany(n => n.PresentIn).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                bare.Select(n => n.Key).ToList()));
        }
    }

    // ---- Wording ----

    private static string Describe(IpamRange r) => $"DHCP range {r.Start} - {r.End}";

    private static string Describe(IpamHost h) => $"host {h.Name} {h.Address}";

    private static string Describe(IpamRecord r) => $"{r.Type} {r.Name} {r.Address}";

    private static string Join(IEnumerable<string> names) => string.Join(", ", names);

    private static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Names are compared without case or a closing dot.</summary>
    private static string Normalize(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>Names the network view, but only when the plan has more than one (otherwise it is just noise).</summary>
    private static string InView(IpamPlan plan, IpamView view) => plan.Views.Count > 1 ? $" (network view {view.Name})" : "";
}

/// <summary>Findings as plain text or JSON.</summary>
public static class IpamFindingsReport
{
    /// <param name="plan">The plan the findings came from (for the counts at the top).</param>
    /// <param name="findings">What to show (after any filtering).</param>
    /// <param name="clientName">The client.</param>
    /// <param name="notes">Anything the caller wants said, such as grids that have no snapshot.</param>
    /// <param name="itemsPerFinding">How many of a finding's objects to list; the rest are counted. Zero lists none.</param>
    public static string Text(
        IpamPlan plan, IReadOnlyList<IpamFinding> findings, string clientName, IReadOnlyList<string> notes, int itemsPerFinding = 5)
    {
        var text = new StringBuilder();
        var grids = plan.Sources.Where(s => s.Kind == IpamSourceKind.Grid).Select(s => s.Name).ToList();
        text.AppendLine($"Address plan findings: {clientName}");
        text.AppendLine($"{grids.Count} grid{(grids.Count == 1 ? "" : "s")} ({string.Join(", ", grids)}), {plan.Networks.Count:N0} networks and containers.");
        text.AppendLine();

        if (findings.Count == 0)
        {
            text.AppendLine("No findings.");
            if (grids.Count < 2) text.AppendLine("(Comparing grids needs at least two of the client's grids to have a snapshot.)");
        }
        else
        {
            var warnings = findings.Count(f => f.Severity == IpamSeverity.Warning);
            text.AppendLine($"{findings.Count:N0} finding{(findings.Count == 1 ? "" : "s")}: {warnings:N0} warning{(warnings == 1 ? "" : "s")}, {findings.Count - warnings:N0} for information.");

            foreach (var severity in new[] { IpamSeverity.Warning, IpamSeverity.Info })
            {
                var group = findings.Where(f => f.Severity == severity).ToList();
                if (group.Count == 0) continue;

                text.AppendLine();
                text.AppendLine(severity == IpamSeverity.Warning ? "WARNINGS" : "FOR INFORMATION");
                foreach (var f in group)
                {
                    text.AppendLine();
                    text.AppendLine($"[{f.Title}] {f.Subject}");
                    text.AppendLine($"  {f.Summary}");
                    if (f.Items.Count == 0 || itemsPerFinding <= 0) continue;

                    foreach (var item in f.Items.Take(itemsPerFinding)) text.AppendLine($"    - {item}");
                    if (f.Items.Count > itemsPerFinding) text.AppendLine($"    ... and {f.Items.Count - itemsPerFinding:N0} more");
                }
            }
        }

        var allNotes = notes.Concat(plan.Problems).ToList();
        if (allNotes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Notes");
            foreach (var note in allNotes) text.AppendLine($"  {note}");
        }
        return text.ToString();
    }

    public static string Json(IpamPlan plan, IReadOnlyList<IpamFinding> findings, string clientName, IReadOnlyList<string> notes) =>
        JsonSerializer.Serialize(new
        {
            client = clientName,
            sources = plan.Sources.Select(s => new { kind = s.Kind.ToString(), name = s.Name, asOf = s.AsOf }).ToList(),
            networks = plan.Networks.Count,
            counts = new
            {
                findings = findings.Count,
                warnings = findings.Count(f => f.Severity == IpamSeverity.Warning),
                information = findings.Count(f => f.Severity == IpamSeverity.Info),
            },
            findings = findings.Select(f => new
            {
                kind = f.Id,
                title = f.Title,
                severity = f.Severity == IpamSeverity.Warning ? "warning" : "info",
                subject = f.Subject,
                summary = f.Summary,
                sources = f.Sources,
                count = f.Count,
                items = f.Items,
            }).ToList(),
            notes = notes.Concat(plan.Problems).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true });
}
