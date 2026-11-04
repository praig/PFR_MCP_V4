namespace Pfr.Core.Ipam;

/// <summary>
/// Narrows the address plan, the way the tree's filters do (spec §13.3). A network matches if it is present in the
/// chosen grid (any presence) and, when asked, is present in more than one grid. It stays visible when something
/// below it matches, so a match is never shown without the containers above it. DHCP ranges, hosts and records match
/// when they are the chosen grid's own.
/// </summary>
/// <param name="grid">Only networks present in this grid, and only its ranges, hosts and records. Null for every grid.</param>
/// <param name="kinds">Which kinds of address-level object are shown at all.</param>
/// <param name="sharedOnly">Only networks present in more than one grid.</param>
public sealed class IpamFilter(
    string? grid = null, IpamLeafKinds kinds = IpamLeafKinds.All, bool sharedOnly = false)
{
    private readonly Dictionary<IpamNode, bool> _shown = new();

    /// <summary>Which kinds of address-level object are shown.</summary>
    public IpamLeafKinds Kinds => kinds;

    /// <summary>True if anything is being filtered out.</summary>
    public bool Active => grid is not null || sharedOnly;

    private bool FromGrid(IpamSource source) =>
        grid is null || string.Equals(source.Name, grid, StringComparison.OrdinalIgnoreCase);

    public bool Matches(IpamRange range) => FromGrid(range.Source);
    public bool Matches(IpamHost host) => FromGrid(host.Source);
    public bool Matches(IpamRecord record) => FromGrid(record.Source);

    public bool Matches(IpamNode node) =>
        (grid is null || node.Presences.Any(p => FromGrid(p.Source)))
        && (!sharedOnly || node.Network.PresentIn.Count > 1);

    /// <summary>True if the node matches, has an object of the chosen grid directly in it, or has a node below it that is shown.</summary>
    public bool Shows(IpamNode node)
    {
        if (!Active) return true;
        if (_shown.TryGetValue(node, out var known)) return known;

        // With a grid chosen, one of its own objects is reason enough to keep the network it sits in.
        var shown = Matches(node)
                    || (grid is not null && (RangesShown(node).Any() || HostsShown(node).Any() || RecordsShown(node).Any()))
                    || node.Children.Any(Shows);
        _shown[node] = shown;
        return shown;
    }

    public bool Shows(IpamView view) =>
        !Active || view.Roots.Any(Shows)
        || UnplacedRanges(view).Any() || UnplacedHosts(view).Any() || UnplacedRecords(view).Any();

    /// <summary>The DHCP ranges to list under a network.</summary>
    public IEnumerable<IpamRange> RangesShown(IpamNode node) =>
        kinds.HasFlag(IpamLeafKinds.Ranges) ? node.Ranges.Where(Matches) : [];

    /// <summary>The hosts to list under a network.</summary>
    public IEnumerable<IpamHost> HostsShown(IpamNode node) =>
        kinds.HasFlag(IpamLeafKinds.Hosts) ? node.Hosts.Where(Matches) : [];

    /// <summary>The A / AAAA / PTR records to list under a network (not the ones a host generates).</summary>
    public IEnumerable<IpamRecord> RecordsShown(IpamNode node) =>
        kinds.HasFlag(IpamLeafKinds.Records) ? node.Records.Where(Matches) : [];

    /// <summary>The records to list under a host: the ones it generates.</summary>
    public IEnumerable<IpamRecord> HostRecordsShown(IpamHost host) =>
        kinds.HasFlag(IpamLeafKinds.Records) ? host.Records : [];

    // Objects in no network are not "shared", so asking for shared networks hides them.

    /// <summary>The ranges to list under a view's "(no network)".</summary>
    public IEnumerable<IpamRange> UnplacedRanges(IpamView view) =>
        kinds.HasFlag(IpamLeafKinds.Ranges) && !sharedOnly ? view.Unplaced.Where(Matches) : [];

    public IEnumerable<IpamHost> UnplacedHosts(IpamView view) =>
        kinds.HasFlag(IpamLeafKinds.Hosts) && !sharedOnly ? view.UnplacedHosts.Where(Matches) : [];

    public IEnumerable<IpamRecord> UnplacedRecords(IpamView view) =>
        kinds.HasFlag(IpamLeafKinds.Records) && !sharedOnly ? view.UnplacedRecords.Where(Matches) : [];
}
