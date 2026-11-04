using Pfr.Core.Nios;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;

namespace Pfr.Core.Reports;

/// <summary>Which ranges the DHCP range report lists.</summary>
public sealed record DhcpRangeReportOptions
{
    /// <summary>
    /// Also list a range that has no options, on one line with the option columns empty. Without this a range with no options
    /// would not appear at all, which hides it rather than reporting it.
    /// </summary>
    public bool IncludeRangesWithoutOptions { get; init; } = true;

    public bool IncludeIpv6 { get; init; } = true;

    /// <summary>
    /// List the options a range inherits as well as those set on it: from its network, each network container above that, and
    /// the grid. Where the same option is set at more than one level, only the nearest setting is listed (it is the one that
    /// applies).
    /// </summary>
    public bool IncludeInheritedOptions { get; init; } = true;
}

/// <summary>One line of the report: a range and one of its options (or just the range, when it has none).</summary>
/// <param name="Network">The network, its prefix length and its network view's name, as <c>10.9.16.0/24/default</c>.</param>
/// <param name="Start">The range's first address (a DHCPv6 prefix range: its first prefix).</param>
/// <param name="End">The range's last address (or last prefix).</param>
/// <param name="LeaseSeconds">The range's lease time in seconds: its own, or the one it inherits.</param>
/// <param name="OptionCode">The option's number, or null on the line of a range with no options.</param>
/// <param name="OptionValue">The option's value as entered.</param>
/// <param name="OptionName">The option's name when the backup defines it (not part of the report's columns).</param>
/// <param name="SetOn">Where the option is set: "Range", "Network 10.9.16.0/24", "Network container 10.0.0.0/8" or "Grid"; empty with no option.</param>
public sealed record DhcpRangeRow(
    string Network, string Start, string End, int? LeaseSeconds, int? OptionCode, string? OptionValue, string? OptionName, string SetOn)
{
    /// <summary>True for an option the range inherits rather than one set on it.</summary>
    public bool IsInherited => SetOn.Length > 0 && SetOn != DhcpRangeReport.RangeScope;
}

public sealed class DhcpRangeResult(
    IReadOnlyList<DhcpRangeRow> rows, int rangeCount, int rangesWithOptions, bool optionsRead, int unmatchedOptions)
{
    /// <summary>The six columns that were asked for, then where each option is set.</summary>
    public static readonly IReadOnlyList<string> Columns =
        ["Network/CIDR/View", "Start address", "End address", "Default lease", "Option number", "Option value", "Set on"];

    public IReadOnlyList<DhcpRangeRow> Rows { get; } = rows;

    public int RangeCount { get; } = rangeCount;

    /// <summary>Ranges with at least one line for an option (set on them or, when asked for, inherited).</summary>
    public int RangesWithOptions { get; } = rangesWithOptions;

    public int RangesWithoutOptions => RangeCount - RangesWithOptions;

    /// <summary>The lines that are an option (not the line of a range with none).</summary>
    public int OptionCount => Rows.Count(r => r.OptionCode is not null);

    /// <summary>The option lines that are inherited rather than set on the range.</summary>
    public int InheritedOptionCount => Rows.Count(r => r.IsInherited);

    /// <summary>
    /// False when the snapshot was saved before lease times and options were read, so the report has neither and the backup has
    /// to be imported again.
    /// </summary>
    public bool OptionsRead { get; } = optionsRead;

    /// <summary>Options the backup attaches to a range, network or the grid that could not be found, so they are not listed.</summary>
    public int UnmatchedOptions { get; } = unmatchedOptions;

    /// <summary>The report as a table: the lease and option number as numbers, everything else text.</summary>
    public QueryResult ToResult() => new(
        Columns,
        Rows.Select(r => new object?[]
        {
            r.Network, r.Start, r.End,
            r.LeaseSeconds is { } lease ? (long)lease : null,
            r.OptionCode is { } code ? (long)code : null,
            r.OptionValue,
            r.SetOn,
        }).ToList(),
        truncated: false, TimeSpan.Zero);
}

/// <summary>
/// The DHCP range report: every DHCP range with its lease time and one line per option. Columns: network/CIDR/view, start
/// address, end address, default lease (seconds), option number, option value, and where the option is set.
/// <list type="bullet">
/// <item>The lease time is the one the range really has: its own if it sets one, otherwise its network's, a container's above
/// that, or the grid's.</item>
/// <item>Options are those set on the range and, unless turned off, those it inherits from its network, each network container
/// above that, and the grid. Where one option is set at several levels the nearest wins and only it is listed.</item>
/// </list>
/// </summary>
public static class DhcpRangeReport
{
    public const string Title = "DHCP range report";

    public const string RangeScope = "Range";

    public const string Description =
        "Every DHCP range with its lease time, and one line for each option that applies to it, set on the range or inherited: " +
        "network/CIDR/view, start and end address, default lease, option number, option value and where it is set.";

    public static DhcpRangeResult Run(GridInventory inventory, DhcpRangeReportOptions? options = null)
    {
        options ??= new DhcpRangeReportOptions();

        var views = inventory.NetworkViews.GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var networks = new Dictionary<string, IpNetwork>(StringComparer.Ordinal);
        foreach (var n in inventory.Networks) networks.TryAdd(n.Key, n);
        var ranges = inventory.DhcpRanges.Where(r => options.IncludeIpv6 || !r.IsIpv6).ToList();

        var ordered = ranges
            .Select(r => (Range: r, Network: Network(r, views)))
            .OrderBy(x => x.Network.View, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Network.Address, Comparer<string>.Create(IpSort.Compare))
            .ThenBy(x => x.Network.Cidr)
            .ThenBy(x => StartOf(x.Range), Comparer<string>.Create(IpSort.Compare))
            .ToList();

        var rows = new List<DhcpRangeRow>();
        var withOptions = 0;
        foreach (var (range, network) in ordered)
        {
            var start = StartOf(range);
            var end = range.AddressType == "PREFIX" ? range.PrefixEnd ?? "" : range.EndAddress;
            var applying = OptionsFor(range, networks, inventory, options.IncludeInheritedOptions);

            if (applying.Count > 0)
            {
                withOptions++;
                foreach (var (option, setOn) in applying.OrderBy(x => x.Option.Code).ThenBy(x => x.Option.Value, StringComparer.Ordinal))
                    rows.Add(new DhcpRangeRow(network.Label, start, end, range.LeaseTimeSeconds, option.Code, option.Value, option.Name, setOn));
            }
            else if (options.IncludeRangesWithoutOptions)
            {
                rows.Add(new DhcpRangeRow(network.Label, start, end, range.LeaseTimeSeconds, null, null, null, ""));
            }
        }

        return new DhcpRangeResult(rows, ranges.Count, withOptions, inventory.DhcpOptionsRead, inventory.UnmatchedRangeOptions);
    }

    /// <summary>
    /// The options that apply to a range, each with where it is set. The range's own come first; then, if asked for, its
    /// network's, each container's above it, and the grid's. An option already set nearer is not listed again (it is
    /// overridden), and an inherited option for the other IP version is ignored (a DHCPv4 range does not inherit DHCPv6 options).
    /// </summary>
    private static List<(DhcpOption Option, string SetOn)> OptionsFor(
        DhcpRange range, Dictionary<string, IpNetwork> networks, GridInventory inventory, bool inherited)
    {
        var found = new List<(DhcpOption Option, string SetOn)>();
        var covered = new HashSet<(int Code, bool IsIpv4)>();

        void AddScope(IReadOnlyList<DhcpOption> scope, string label, bool inheritedScope = true)
        {
            var fromHere = new List<(int, bool)>();
            foreach (var option in scope)
            {
                if (inheritedScope && option.IsIpv4 == range.IsIpv6) continue;  // inherited, but for the other IP version
                if (covered.Contains((option.Code, option.IsIpv4))) continue;    // a nearer setting overrides it
                found.Add((option, label));
                fromHere.Add((option.Code, option.IsIpv4));
            }
            foreach (var id in fromHere) covered.Add(id); // an option set twice at one level is both listed; only a nearer level hides it
        }

        AddScope(range.Options, RangeScope, inheritedScope: false);
        if (!inherited) return found;

        var networkKey = string.IsNullOrEmpty(range.NetworkKey) ? null : range.NetworkKey;
        for (var depth = 0; networkKey is not null && depth < 64 && networks.TryGetValue(networkKey, out var network); depth++)
        {
            AddScope(network.Options, network.IsContainer ? $"Network container {network.Cidrs}" : $"Network {network.Cidrs}");
            networkKey = network.ParentKey;
        }

        AddScope(inventory.GridDhcpOptions, "Grid");
        return found;
    }

    private static string StartOf(DhcpRange range) =>
        range.AddressType == "PREFIX" ? range.PrefixStart ?? "" : range.StartAddress;

    /// <summary>"10.9.16.0/24/0" with the view's id replaced by its name: <c>10.9.16.0/24/default</c>.</summary>
    private static (string Label, string View, string Address, int Cidr) Network(DhcpRange range, Dictionary<int, string> views)
    {
        var view = views.TryGetValue(range.NetworkViewId, out var name) ? name : range.NetworkViewId.ToString();

        // The key ends with "/<network view id>"; what is before that is the network and its prefix length.
        var key = range.NetworkKey;
        var lastSlash = key.LastIndexOf('/');
        var network = lastSlash > 0 ? key[..lastSlash] : key;

        var cidrSlash = network.LastIndexOf('/');
        var address = cidrSlash > 0 ? network[..cidrSlash] : network;
        var cidr = cidrSlash > 0 && int.TryParse(network[(cidrSlash + 1)..], out var parsed) ? parsed : 0;
        return ($"{network}/{view}", view, address, cidr);
    }
}
