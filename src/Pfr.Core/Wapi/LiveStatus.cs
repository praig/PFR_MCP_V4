namespace Pfr.Core.Wapi;

/// <summary>A service's state as the grid reports it.</summary>
public enum ServiceState { Unknown, Working, Warning, Failed, Inactive, Offline }

/// <summary>A roll-up of states, ordered so the worst wins. <c>Unknown</c> means nothing was reported.</summary>
public enum Health { Unknown, Ok, Warning, Failed }

/// <summary>One service on one node (DNS, DHCP, NTP, a network port, disk usage...).</summary>
public sealed record LiveService(string Name, ServiceState State, string Description)
{
    /// <summary>Failed and offline services are problems, warnings are warnings; inactive (switched off) is not a problem.</summary>
    public Health Health => State switch
    {
        ServiceState.Failed or ServiceState.Offline => Health.Failed,
        ServiceState.Warning => Health.Warning,
        ServiceState.Working or ServiceState.Inactive => Health.Ok,
        _ => Health.Unknown,
    };
}

/// <summary>One physical node of a member (an HA member has two).</summary>
public sealed class LiveNode
{
    /// <summary>1-based, in the order the grid lists the nodes.</summary>
    public int Position { get; init; }

    /// <summary>"Active", "Passive", "Not configured", or empty if the grid did not say.</summary>
    public string HaStatus { get; init; } = "";

    public string Model { get; init; } = "";

    /// <summary>Problems first, then by name.</summary>
    public IReadOnlyList<LiveService> Services { get; init; } = [];

    public Health Health => Services.Count == 0 ? Health.Unknown : Services.Max(s => s.Health);
}

/// <summary>A grid member with its live state.</summary>
public sealed class LiveMember
{
    public string HostName { get; init; } = "";

    public bool HaEnabled { get; init; }

    /// <summary>NIOS version, or null if the grid did not report it.</summary>
    public string? Version { get; init; }

    public IReadOnlyList<string> Hotfixes { get; init; } = [];

    public IReadOnlyList<LiveNode> Nodes { get; init; } = [];

    /// <summary>"-" for a single node, otherwise the nodes' HA states, e.g. "Active / Passive".</summary>
    public string HaSummary => !HaEnabled
        ? "-"
        : Nodes.Count == 0 ? "HA" : string.Join(" / ", Nodes.Select(n => n.HaStatus.Length > 0 ? n.HaStatus : "?"));

    /// <summary>Set when an HA pair does not have exactly one active node (when the grid reports HA states at all).</summary>
    public string? HaProblem
    {
        get
        {
            if (!HaEnabled || Nodes.Count < 2 || Nodes.All(n => n.HaStatus.Length == 0)) return null;
            var active = Nodes.Count(n => n.HaStatus == "Active");
            return active == 1 ? null : $"HA pair has {active} active nodes (expected 1)";
        }
    }

    public Health Health
    {
        get
        {
            var worst = Nodes.Count == 0 ? Health.Unknown : Nodes.Max(n => n.Health);
            return HaProblem is null ? worst : (Health)Math.Max((int)worst, (int)Health.Warning);
        }
    }
}

/// <summary>What the grid reported about itself right now.</summary>
public sealed class LiveGridStatus
{
    public string? GridName { get; init; }

    public string ApiVersion { get; init; } = "";

    public DateTimeOffset CollectedUtc { get; init; }

    /// <summary>By host name.</summary>
    public IReadOnlyList<LiveMember> Members { get; init; } = [];

    /// <summary>Things the grid did not offer or answer, so the engineer knows what is missing.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public Health Health => Members.Count == 0 ? Health.Unknown : Members.Max(m => m.Health);

    /// <summary>The one NIOS version, "mixed: a, b" if members differ, or "unknown".</summary>
    public string VersionSummary
    {
        get
        {
            var versions = Members.Select(m => m.Version).Where(v => !string.IsNullOrEmpty(v)).Distinct().Order().ToList();
            return versions.Count switch
            {
                0 => "unknown",
                1 => versions[0]!,
                _ => "mixed: " + string.Join(", ", versions),
            };
        }
    }

    /// <summary>One line per problem: an HA fault, or a service that is failed, offline or warning.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var lines = new List<string>();
            foreach (var member in Members)
            {
                if (member.HaProblem is { } ha) lines.Add($"{member.HostName}: {ha}");
                foreach (var node in member.Nodes)
                {
                    var location = member.Nodes.Count > 1
                        ? $"{member.HostName} (node {node.Position}{(node.HaStatus.Length > 0 ? ", " + node.HaStatus : "")})"
                        : member.HostName;
                    foreach (var service in node.Services.Where(s => s.Health is Health.Failed or Health.Warning))
                    {
                        var detail = service.Description.Length > 0 ? $" - {service.Description}" : "";
                        lines.Add($"{location}: {service.Name} {service.State}{detail}");
                    }
                }
            }
            return lines;
        }
    }
}
