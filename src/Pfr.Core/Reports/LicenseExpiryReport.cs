using Pfr.Core.Nios.Model;

namespace Pfr.Core.Reports;

public enum ExpiryState
{
    Expired,
    /// <summary>Expires within the report window.</summary>
    Expiring,
    /// <summary>Expires after the report window.</summary>
    Later,
    /// <summary>Never expires.</summary>
    Permanent,
}

public sealed record LicenseExpiryRow(
    string Scope,          // "Grid-wide" or the member host name
    string License,        // friendly name
    string Code,           // NIOS license code, or "" for the summary row
    string Node,
    DateTimeOffset? Expires,
    int? DaysLeft,
    ExpiryState State,
    string Note);

public sealed record LicenseExpiryResult(
    IReadOnlyList<LicenseExpiryRow> Rows,
    int WindowDays,
    DateTimeOffset AsOf)
{
    public int Expired => Rows.Count(r => r.State == ExpiryState.Expired);
    public int Expiring => Rows.Count(r => r.State == ExpiryState.Expiring);
    public int Later => Rows.Count(r => r.State == ExpiryState.Later);
    public int Permanent => Rows.Count(r => r.State == ExpiryState.Permanent);

    /// <summary>The soonest date anything expires (past or future), if any.</summary>
    public DateTimeOffset? NextExpiry => Rows.Where(r => r.Expires is not null).Min(r => r.Expires);
}

/// <summary>
/// Every license on the grid with how long it has left, soonest first. Also lists each
/// member's next expiry from NIOS's own summary, which covers subscription licenses
/// whose dates aren't on the individual license records.
/// </summary>
public static class LicenseExpiryReport
{
    public const string Title = "License expiry";

    public const string Description =
        "Every license on the grid and when it expires, soonest first. Expired licenses and ones " +
        "expiring within the chosen window are flagged. Each member's next expiry from NIOS's own " +
        "license summary is included, since subscription licenses often carry their dates only there.";

    public static readonly IReadOnlyList<int> Windows = [30, 60, 90, 180, 365];

    public static LicenseExpiryResult Run(GridInventory inv, int windowDays = 90, DateTimeOffset? asOf = null)
    {
        var now = asOf ?? DateTimeOffset.UtcNow;
        var rows = new List<LicenseExpiryRow>();

        LicenseExpiryRow Row(string scope, string license, string code, string node, DateTimeOffset? expires, string note)
        {
            int? days = expires is { } e ? (int)Math.Floor((e - now).TotalDays) : null;
            var state = expires is null ? ExpiryState.Permanent
                : expires <= now ? ExpiryState.Expired
                : days <= windowDays ? ExpiryState.Expiring
                : ExpiryState.Later;
            return new LicenseExpiryRow(scope, license, code, node, expires, days, state, note);
        }

        foreach (var l in inv.Licenses)
        {
            var member = inv.FindMember(l.MemberOid);
            var scope = l.IsGridWide ? "Grid-wide" : member?.HostName ?? "(unknown member)";
            var node = l.IsGridWide ? "" : member is { Nodes.Count: > 1 }
                ? $"Node {l.NodePosition + 1} · {l.NodeSerial}"
                : l.NodeSerial ?? "";
            rows.Add(Row(scope, l.Name, l.Type, node, l.Expires, l.Kind ?? ""));
        }

        // NIOS's per-member summary often holds the real (subscription) expiry. Add it when it
        // tells us something the member's own license dates don't.
        foreach (var m in inv.Members.Where(m => m.NextLicenseExpiry is not null))
        {
            var ownSoonest = inv.Licenses.Where(l => l.MemberOid == m.Oid && l.Expires is not null)
                .Select(l => l.Expires).Min();
            if (ownSoonest == m.NextLicenseExpiry) continue;
            rows.Add(Row(m.HostName, "Next expiry (NIOS license summary)", "", "", m.NextLicenseExpiry,
                "Earliest expiry NIOS reports for this member, usually a subscription license"));
        }

        var ordered = rows
            .OrderBy(r => r.State switch
            {
                ExpiryState.Expired => 0,
                ExpiryState.Expiring => 1,
                ExpiryState.Later => 2,
                _ => 3,
            })
            .ThenBy(r => r.Expires ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Scope == "Grid-wide" ? 0 : 1)
            .ThenBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.License, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LicenseExpiryResult(ordered, windowDays, now);
    }
}
