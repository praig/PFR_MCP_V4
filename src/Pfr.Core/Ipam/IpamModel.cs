using Pfr.Core.Nios;
using Pfr.Core.Nios.Model;

namespace Pfr.Core.Ipam;

// The client's address plan (spec §13). The model is source-neutral: a network knows which *sources* it is present
// in, and today the only sources are the client's NIOS grids (as of their latest snapshots). External sources
// (Forward Networks, NetBrain, CSV) will add presences and leaf objects of their own without changing any of this.

public enum IpamSourceKind
{
    /// <summary>One of the client's NIOS grids, as of its latest snapshot.</summary>
    Grid,

    /// <summary>An imported external source (not built yet).</summary>
    External,
}

/// <summary>A place addresses come from, and how old what it says is.</summary>
/// <param name="Kind">Grid or external source.</param>
/// <param name="Name">The grid's or source's name.</param>
/// <param name="AsOf">When the data was taken (the snapshot's date for a grid).</param>
/// <param name="SnapshotId">The snapshot a grid's data came from.</param>
public sealed record IpamSource(IpamSourceKind Kind, string Name, DateTimeOffset? AsOf = null, string? SnapshotId = null);

/// <summary>What one source says about a network: the view it is in, its kind, comment and disabled flag.</summary>
public sealed record IpamPresence(IpamSource Source, string ViewName, bool IsContainer, string? Comment, bool Disabled);

/// <summary>
/// A value every presence agrees on, or, when they don't (<see cref="Varies"/>), each of the different values in the
/// order of the sources.
/// </summary>
public readonly record struct IpamAgreed<T>(T? Value, bool Varies, IReadOnlyList<T> Values)
{
    public override string ToString() => string.Join(", ", Values.Select(v => v?.ToString() ?? ""));
}

public static class IpamAgreed
{
    public static IpamAgreed<T> Of<T>(IEnumerable<T> values)
    {
        var distinct = values.Distinct().ToList();
        return new IpamAgreed<T>(distinct.Count == 1 ? distinct[0] : default, distinct.Count > 1, distinct);
    }
}

/// <summary>
/// A network or container, keyed by its prefix alone. It exists once in the plan however many grids and network
/// views it is in; its presences say where.
/// </summary>
public sealed class IpamNetwork(string key, IpPrefix prefix)
{
    /// <summary>The prefix in canonical form, e.g. "10.0.0.0/8" or "2001:db8::/32".</summary>
    public string Key { get; } = key;

    public IpPrefix Prefix { get; } = prefix;
    public bool IsIpv6 => Prefix.IsIpv6;

    /// <summary>Every source's view of it (one per source and network view).</summary>
    public List<IpamPresence> Presences { get; } = [];

    /// <summary>A container if any source says so.</summary>
    public bool IsContainer => Presences.Any(p => p.IsContainer);

    /// <summary>The sources it is present in, by name.</summary>
    public IReadOnlyList<string> PresentIn => Presences.Select(p => p.Source.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The network views it is in.</summary>
    public IReadOnlyList<string> Views => Presences.Select(p => p.ViewName)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// A network as it appears in the tree under one network view: the same network object, with only that view's
/// presences, nested by containment, with the address-level objects that sit inside it.
/// </summary>
public sealed class IpamNode(IpamNetwork network, string viewName, IReadOnlyList<IpamPresence> presences)
{
    public IpamNetwork Network { get; } = network;
    public string ViewName { get; } = viewName;

    /// <summary>The presences in this view only.</summary>
    public IReadOnlyList<IpamPresence> Presences { get; } = presences;

    public string Key => Network.Key;
    public bool IsIpv6 => Network.IsIpv6;

    /// <summary>Shown as a container if any of its presences in this view is one.</summary>
    public bool IsContainer => Presences.Any(p => p.IsContainer);

    /// <summary>The sources it is present in within this view.</summary>
    public IReadOnlyList<string> PresentIn => Presences.Select(p => p.Source.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public IpamNode? Parent { get; internal set; }
    public List<IpamNode> Children { get; } = [];

    /// <summary>DHCP ranges directly inside this network (not those of networks below it).</summary>
    public List<IpamRange> Ranges { get; } = [];

    /// <summary>Hosts (one row per address) directly inside this network.</summary>
    public List<IpamHost> Hosts { get; } = [];

    /// <summary>A / AAAA and PTR records directly inside this network that no host generates.</summary>
    public List<IpamRecord> Records { get; } = [];

    /// <summary>DHCP ranges in this network and every network below it.</summary>
    public int RangeCount { get; internal set; }

    /// <summary>Hosts (one per address) in this network and every network below it.</summary>
    public int HostCount { get; internal set; }

    /// <summary>Address records in this network and below, including the ones hosts generate.</summary>
    public int RecordCount { get; internal set; }

    /// <summary>Networks (not containers) at or below this node.</summary>
    public int NetworkCount { get; internal set; }

    public IpamAgreed<string> Comment => IpamAgreed.Of(Presences.Select(p => p.Comment ?? ""));
    public IpamAgreed<bool> Disabled => IpamAgreed.Of(Presences.Select(p => p.Disabled));

    /// <summary>Each different comment (leaving out empty ones), in the order of the sources.</summary>
    public IReadOnlyList<string> Comments => Comment.Values.Where(c => c.Length > 0).ToList();

    /// <summary>The sources that have it disabled.</summary>
    public IReadOnlyList<string> DisabledIn => Presences.Where(p => p.Disabled).Select(p => p.Source.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// A DHCP range. Ranges are not merged: one that exists on two grids is two rows, each with its own source.
/// </summary>
public sealed class IpamRange
{
    public IpamSource Source { get; init; } = new(IpamSourceKind.Grid, "");
    public string ViewName { get; init; } = "";
    public string Start { get; init; } = "";
    public string End { get; init; } = "";
    public bool IsIpv6 { get; init; }

    /// <summary>None, Member, Failover, MsFailover or MsServer.</summary>
    public string AssociationType { get; init; } = "";

    /// <summary>What serves it: the member's host name, or the failover association's name.</summary>
    public string? ServedBy { get; init; }

    public string? Name { get; init; }
    public string? Comment { get; init; }
    public bool Disabled { get; init; }

    public static int Compare(IpamRange a, IpamRange b)
    {
        var c = IpSort.Compare(a.Start, b.Start);
        if (c != 0) return c;
        c = IpSort.Compare(a.End, b.End);
        return c != 0 ? c : string.Compare(a.Source.Name, b.Source.Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Which kinds of address-level object to show under the networks.</summary>
[Flags]
public enum IpamLeafKinds
{
    None = 0,
    Ranges = 1,
    Hosts = 2,
    Records = 4,
    All = Ranges | Hosts | Records,
}

/// <summary>
/// A host at one of its addresses (spec §13.4): a host with three addresses is three of these, one under each
/// address, each with its own source. IPAM-only hosts are included. The A / AAAA / PTR records a DNS host generates
/// are listed under it (<see cref="Records"/>) instead of as separate rows.
/// </summary>
public sealed class IpamHost
{
    public IpamSource Source { get; init; } = new(IpamSourceKind.Grid, "");
    public string ViewName { get; init; } = "";

    /// <summary>The address, in canonical form.</summary>
    public string Address { get; init; } = "";

    public bool IsIpv6 { get; init; }
    public string Name { get; init; } = "";

    /// <summary>True for a host configured for DNS; false for an IPAM-only host.</summary>
    public bool IsDns { get; init; }

    public bool ConfigureForDhcp { get; init; }
    public string? MacAddress { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The host's other addresses: it is listed once under each address it has.</summary>
    public IReadOnlyList<string> OtherAddresses { get; init; } = [];

    public int? TtlSeconds { get; init; }

    /// <summary>Where the TTL is set: Host, Zone or Grid. Empty when there is none (an IPAM-only host).</summary>
    public string TtlSource { get; init; } = "";

    public bool Disabled { get; init; }
    public string? Comment { get; init; }

    /// <summary>The records this host generates for this address.</summary>
    public List<IpamRecord> Records { get; } = [];

    public static int Compare(IpamHost a, IpamHost b)
    {
        var c = IpSort.Compare(a.Address, b.Address);
        if (c != 0) return c;
        c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.Compare(a.Source.Name, b.Source.Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// An A, AAAA or PTR record: the only record types that carry an address. The address is the one in an A / AAAA record,
/// or the one decoded from a PTR record's reverse name. Not merged: one on two grids is two rows.
/// </summary>
public sealed class IpamRecord
{
    public IpamSource Source { get; init; } = new(IpamSourceKind.Grid, "");

    /// <summary>The network view of the record's DNS view.</summary>
    public string ViewName { get; init; } = "";

    public string DnsViewName { get; init; } = "";
    public string Zone { get; init; } = "";
    public DnsRecordType Type { get; init; }

    /// <summary>The address the record stands for, in canonical form.</summary>
    public string Address { get; init; } = "";

    public bool IsIpv6 { get; init; }

    /// <summary>The record's owner name: the host name for A / AAAA, the reverse name for PTR.</summary>
    public string Name { get; init; } = "";

    /// <summary>The record's data: the address for A / AAAA, the target name for PTR.</summary>
    public string Value { get; init; } = "";

    public int? TtlSeconds { get; init; }

    /// <summary>Where the TTL is set: Record, Host, Zone or Grid.</summary>
    public string TtlSource { get; init; } = "";

    public bool Disabled { get; init; }

    /// <summary>The host object that generates the record, if any.</summary>
    public string? HostName { get; init; }

    public static int Compare(IpamRecord a, IpamRecord b)
    {
        var c = IpSort.Compare(a.Address, b.Address);
        if (c != 0) return c;
        c = a.Type.CompareTo(b.Type);
        if (c != 0) return c;
        c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : string.Compare(a.Source.Name, b.Source.Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A network view: it groups the tree but is not part of any key.</summary>
public sealed class IpamView(string name)
{
    public string Name { get; } = name;

    /// <summary>Top-level networks and containers of this view; the rest are below them.</summary>
    public List<IpamNode> Roots { get; } = [];

    /// <summary>Ranges whose address is in no network of this view: the "(no network)" node.</summary>
    public List<IpamRange> Unplaced { get; } = [];

    public List<IpamHost> UnplacedHosts { get; } = [];
    public List<IpamRecord> UnplacedRecords { get; } = [];

    public int NetworkCount { get; internal set; }
    public int RangeCount { get; internal set; }
    public int HostCount { get; internal set; }
    public int RecordCount { get; internal set; }

    /// <summary>Every node of the view, parents before their children.</summary>
    public IEnumerable<IpamNode> AllNodes()
    {
        var stack = new Stack<IpamNode>(Roots.AsEnumerable().Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }
}

/// <summary>A client's whole address plan.</summary>
public sealed class IpamPlan
{
    /// <summary>The grids (and later external sources) the plan was built from.</summary>
    public List<IpamSource> Sources { get; } = [];

    /// <summary>Every distinct prefix once, whatever number of views and sources it is in.</summary>
    public List<IpamNetwork> Networks { get; } = [];

    public List<IpamView> Views { get; } = [];

    /// <summary>Things left out, and why.</summary>
    public List<string> Problems { get; } = [];

    public int ContainerCount => Networks.Count(n => n.IsContainer);
    public int NetworkCount => Networks.Count(n => !n.IsContainer);
    public int RangeCount { get; internal set; }

    /// <summary>Hosts, one per address and grid.</summary>
    public int HostCount { get; internal set; }

    /// <summary>A / AAAA and PTR records, including the ones hosts generate.</summary>
    public int RecordCount { get; internal set; }
}

/// <summary>One grid's contribution to the plan: its latest snapshot.</summary>
public sealed record IpamGridInput(string GridName, DateTimeOffset AsOf, string? SnapshotId, GridInventory Inventory);
