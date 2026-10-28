using System.Text.Json;

namespace Pfr.Core.Wapi;

/// <summary>
/// Reads a grid's live state over WAPI: members, their nodes, HA state, services, NIOS versions and hotfixes.
/// Read-only. The grid is asked which fields each object offers and only those are requested, so a field a
/// NIOS or WAPI version lacks becomes a note on the result instead of a failed request.
/// </summary>
public static class LiveStatusCollector
{
    private static readonly string[] MemberFields = ["host_name", "enable_ha", "node_info"];
    private static readonly string[] UpgradeFields = ["member", "current_version", "hotfixes"];

    /// <summary>
    /// Connects (certificate, account, WAPI version) and reads the members and their versions. Members are
    /// essential and their failure is thrown; versions and hotfixes are best effort and failures become notes.
    /// </summary>
    public static async Task<LiveGridStatus> CollectAsync(WapiClient wapi, CancellationToken ct = default)
    {
        var info = await wapi.ConnectAsync(ct).ConfigureAwait(false);
        var notes = new List<string>();

        var members = await ReadAsync(wapi, "member", MemberFields, null, essential: true, notes, ct).ConfigureAwait(false);
        var upgrades = await ReadAsync(wapi, "upgradestatus", UpgradeFields, "type=VNODE", essential: false, notes, ct)
            .ConfigureAwait(false);
        if (upgrades.Count == 0 && !notes.Any(n => n.StartsWith("upgradestatus", StringComparison.Ordinal)))
            notes.Add("upgradestatus: the grid returned no version information, so NIOS versions and hotfixes are unavailable.");

        return Build(info.GridName, info.ApiVersion, DateTimeOffset.UtcNow, members, upgrades, notes);
    }

    private static async Task<List<JsonElement>> ReadAsync(
        WapiClient wapi, string objectType, string[] wanted, string? filter, bool essential,
        List<string> notes, CancellationToken ct)
    {
        HashSet<string>? offered = null;
        try
        {
            offered = await wapi.GetSchemaFieldsAsync(objectType, ct).ConfigureAwait(false);
        }
        catch (WapiRequestException)
        {
            // No schema to check against: ask for what we want and see.
        }

        var fields = offered is { Count: > 0 } ? wanted.Where(offered.Contains).ToArray() : wanted;
        if (offered is { Count: > 0 })
        {
            var missing = wanted.Except(fields).ToList();
            if (missing.Count > 0)
                notes.Add($"{objectType}: this grid does not offer {string.Join(", ", missing)}, so that detail is unavailable.");
        }

        if (fields.Length == 0)
        {
            if (essential) throw new WapiException($"This grid does not offer any of the fields needed from {objectType}.");
            return [];
        }

        try
        {
            return await wapi.GetListAsync(objectType, string.Join(",", fields), filter, ct).ConfigureAwait(false);
        }
        catch (WapiRequestException ex) when (!essential)
        {
            notes.Add($"{objectType}: {ex.Message}");
            return [];
        }
    }

    /// <summary>Builds the status from what the grid returned. Separate from the requests so it can be tested on its own.</summary>
    internal static LiveGridStatus Build(
        string? gridName, string apiVersion, DateTimeOffset collectedUtc,
        IReadOnlyList<JsonElement> members, IReadOnlyList<JsonElement> upgrades, IReadOnlyList<string> notes)
    {
        var versions = new Dictionary<string, (string? Version, List<string> Hotfixes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var upgrade in upgrades)
        {
            var name = Text(upgrade, "member");
            if (name.Length == 0) continue;

            var hotfixes = new List<string>();
            if (upgrade.ValueKind == JsonValueKind.Object
                && upgrade.TryGetProperty("hotfixes", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var hotfix in list.EnumerateArray())
                {
                    var text = HotfixText(hotfix);
                    if (text.Length > 0) hotfixes.Add(text);
                }
            }

            var version = Text(upgrade, "current_version");
            versions[name] = (version.Length > 0 ? version : null, hotfixes);
        }

        var result = new List<LiveMember>();
        foreach (var member in members)
        {
            var host = Text(member, "host_name");
            var nodes = new List<LiveNode>();
            if (member.ValueKind == JsonValueKind.Object
                && member.TryGetProperty("node_info", out var nodeInfo) && nodeInfo.ValueKind == JsonValueKind.Array)
            {
                var position = 0;
                foreach (var node in nodeInfo.EnumerateArray()) nodes.Add(ParseNode(node, ++position));
            }

            versions.TryGetValue(host, out var known);
            result.Add(new LiveMember
            {
                HostName = host,
                HaEnabled = Flag(member, "enable_ha") || nodes.Count > 1,
                Version = known.Version,
                Hotfixes = known.Hotfixes ?? [],
                Nodes = nodes,
            });
        }

        return new LiveGridStatus
        {
            GridName = gridName,
            ApiVersion = apiVersion,
            CollectedUtc = collectedUtc,
            Members = result.OrderBy(m => m.HostName, StringComparer.OrdinalIgnoreCase).ToList(),
            Notes = notes,
        };
    }

    private static LiveNode ParseNode(JsonElement node, int position)
    {
        var services = new List<LiveService>();
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("service_status", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var code = Text(item, "service");
                if (code.Length == 0) continue;
                services.Add(new LiveService(ServiceName(code), State(Text(item, "status")), Text(item, "description")));
            }
        }

        var model = Text(node, "hwmodel");
        return new LiveNode
        {
            Position = position,
            HaStatus = HaStatus(Text(node, "ha_status")),
            Model = model.Length > 0 ? model : Text(node, "hwtype"),
            Services = services.OrderByDescending(s => s.Health).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A hotfix is listed as a name, or as an object with its version and status.</summary>
    private static string HotfixText(JsonElement hotfix)
    {
        if (hotfix.ValueKind == JsonValueKind.String) return hotfix.GetString() ?? "";
        if (hotfix.ValueKind != JsonValueKind.Object) return "";

        var version = Text(hotfix, "version");
        var status = Text(hotfix, "status_text");
        return version.Length == 0 ? status : status.Length == 0 ? version : $"{version} ({status})";
    }

    private static ServiceState State(string status) => status.ToUpperInvariant() switch
    {
        "WORKING" => ServiceState.Working,
        "WARNING" => ServiceState.Warning,
        "FAILED" => ServiceState.Failed,
        "INACTIVE" => ServiceState.Inactive,
        "OFFLINE" => ServiceState.Offline,
        _ => ServiceState.Unknown,
    };

    private static string HaStatus(string status) => status.ToUpperInvariant() switch
    {
        "" => "",
        "ACTIVE" => "Active",
        "PASSIVE" => "Passive",
        "NOT_CONFIGURED" => "Not configured",
        _ => status,
    };

    private static readonly Dictionary<string, string> ServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AUTH_NAMED"] = "DNS (authoritative)",
        ["RECURSIVE_NAMED"] = "DNS (recursive)",
        ["DNS_CACHE_ACCELERATION"] = "DNS cache acceleration",
        ["DHCPD"] = "DHCP",
        ["NTP_SYNC"] = "NTP",
        ["TFTP"] = "TFTP",
        ["DISCOVERY"] = "Discovery",
        ["ENET_LAN"] = "LAN port",
        ["ENET_LAN2"] = "LAN2 port",
        ["ENET_HA"] = "HA port",
        ["ENET_MGMT"] = "MGMT port",
        ["CPU_USAGE"] = "CPU usage",
        ["MEMORY"] = "Memory",
        ["DB_USAGE"] = "Database usage",
        ["DISK_USAGE"] = "Disk usage",
        ["SWAP_USAGE"] = "Swap usage",
        ["REPLICATION"] = "Replication",
    };

    /// <summary>A readable name for a WAPI service code; unknown codes are tidied ("SOME_THING" becomes "Some thing").</summary>
    internal static string ServiceName(string code)
    {
        if (ServiceNames.TryGetValue(code, out var name)) return name;
        var words = code.Replace('_', ' ').ToLowerInvariant();
        return words.Length == 0 ? code : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
