namespace Pfr.Core.Nios.Model;

/// <summary>
/// One installed license: its type, where it applies and when it expires.
/// The license key itself is never read from the backup.
/// </summary>
public sealed class GridLicense
{
    /// <summary>NIOS license code, e.g. "dns", "dtc", "sw_tp".</summary>
    public string Type { get; set; } = "";

    public string Name => LicenseNames.Describe(Type);

    /// <summary>Grid-wide licenses apply to every member; others to one node.</summary>
    public bool IsGridWide { get; set; }

    /// <summary>Member holding the license (null for grid-wide).</summary>
    public int? MemberOid { get; set; }

    /// <summary>Node position within an HA pair (0 or 1); null for grid-wide.</summary>
    public int? NodePosition { get; set; }

    public string? NodeSerial { get; set; }

    /// <summary>e.g. STATIC, DYNAMIC, PAYG; null if the backup doesn't say.</summary>
    public string? Kind { get; set; }

    /// <summary>Expiry, or null for a permanent license (or one whose expiry isn't recorded).</summary>
    public DateTimeOffset? Expires { get; set; }

    /// <summary>"Expired", "Expires in N days" (within <paramref name="warnDays"/>), or "" when fine.</summary>
    public string Status(DateTimeOffset now, int warnDays = 90) => ExpiryStatus(Expires, now, warnDays);

    public static string ExpiryStatus(DateTimeOffset? expires, DateTimeOffset now, int warnDays = 90)
    {
        if (expires is not { } e) return "";
        if (e <= now) return "Expired";
        var days = (int)Math.Ceiling((e - now).TotalDays);
        return days <= warnDays ? $"Expires in {days} day{(days == 1 ? "" : "s")}" : "";
    }
}

/// <summary>Friendly names for NIOS license codes.</summary>
public static class LicenseNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nios"] = "NIOS",
        ["enterprise"] = "Grid",
        ["dns"] = "DNS",
        ["dhcp"] = "DHCP",
        ["dtc"] = "DNS Traffic Control",
        ["rpz"] = "Response Policy Zones (RPZ)",
        ["cloud"] = "Cloud Network Automation",
        ["cloud_api"] = "Cloud Platform",
        ["ms_management"] = "Microsoft Management",
        ["discovery"] = "Network Insight (Discovery)",
        ["sw_tp"] = "Threat Protection (Software)",
        ["tp"] = "Threat Protection",
        ["tp_sub"] = "Threat Protection Update",
        ["threat_anl"] = "Threat Analytics",
        ["rpt_sub"] = "Reporting Subscription",
        ["reporting"] = "Reporting",
        ["fireeye"] = "FireEye Integration",
        ["sec_eco"] = "Security Ecosystem",
        ["dca"] = "DNS Cache Acceleration",
        ["vnios"] = "vNIOS",
        ["ipam"] = "IPAM",
        ["ipam_freeware"] = "IPAM (Freeware)",
        ["unbound"] = "Unbound DNS",
        ["nw_automation"] = "Network Automation",
    };

    public static string Describe(string code) =>
        Names.TryGetValue(code, out var name) ? name : code.Replace('_', ' ').ToUpperInvariant();
}
