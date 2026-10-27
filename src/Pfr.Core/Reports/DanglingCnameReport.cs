using Pfr.Core.Nios.Model;

namespace Pfr.Core.Reports;

/// <summary>How a CNAME chain ends, worked out from the grid's own data (no DNS queries).</summary>
public enum CnameOutcome
{
    /// <summary>Ends at a name with A/AAAA records in a grid-hosted zone.</summary>
    Resolves,
    /// <summary>The target name doesn't exist in its grid-hosted zone.</summary>
    TargetMissing,
    /// <summary>The target exists but has no A/AAAA records (e.g. only TXT/MX).</summary>
    NoAddress,
    /// <summary>The target (or the zone holding it) is disabled.</summary>
    TargetDisabled,
    /// <summary>The chain points back at itself.</summary>
    Loop,
    /// <summary>More CNAMEs in a row than the limit.</summary>
    ChainTooLong,
    /// <summary>The target is outside the grid's zones in this view: needs a live lookup.</summary>
    External,
    /// <summary>The target is in a zone the grid delegates or forwards: needs a live lookup.</summary>
    Delegated,
    /// <summary>The CNAME itself is disabled, so NIOS doesn't serve it.</summary>
    CnameDisabled,
}

public enum ReportSeverity { High, Medium, Low, Info, None }

public sealed record DanglingCnameRow(
    string View,
    string Zone,
    string Name,            // FQDN of the CNAME
    string Target,          // its direct target
    string Chain,           // "a → b → c"
    CnameOutcome Outcome,
    ReportSeverity Severity,
    string Detail,
    bool FromHost,
    string? TakeoverProvider);

public sealed record DanglingCnameResult(IReadOnlyList<DanglingCnameRow> Rows, int MaxChain)
{
    public int Total => Rows.Count;
    public int Dangling => Rows.Count(r => DanglingCnameReport.IsDangling(r.Outcome));
    public int NotVerified => Rows.Count(r => r.Outcome is CnameOutcome.External or CnameOutcome.Delegated);
    public int TakeoverRisks => Rows.Count(r => r.TakeoverProvider is not null);
    public int Resolving => Rows.Count(r => r.Outcome == CnameOutcome.Resolves);
}

/// <summary>
/// Finds CNAMEs that don't lead anywhere, using only the stored grid data. Each chain is
/// followed through the grid's authoritative zones in the same DNS view. Targets outside
/// those zones can't be checked offline and are marked "not verified"; ones on cloud or
/// SaaS hosts are flagged, since a dangling CNAME there can allow a subdomain takeover.
/// </summary>
public static class DanglingCnameReport
{
    public const string Title = "Dangling CNAMEs";

    public const string Description =
        "Follows every CNAME through the grid's own zones (per DNS view, from the stored data, " +
        "without DNS queries) and flags ones whose target doesn't exist, has no address records, " +
        "is disabled, loops, or chains too long. Targets outside the grid can't be checked offline " +
        "and are listed as not verified; those on cloud/SaaS hosts are flagged as possible " +
        "subdomain-takeover risks to check.";

    public const int DefaultMaxChain = 8;

    /// <summary>
    /// Hostname suffixes of services where a dangling CNAME can let someone else claim
    /// the name. (To become an editable rule file with the rules engine.)
    /// </summary>
    public static readonly IReadOnlyList<(string Suffix, string Provider)> TakeoverProviders =
    [
        ("azurewebsites.net", "Azure App Service"),
        ("cloudapp.net", "Azure Cloud Services"),
        ("cloudapp.azure.com", "Azure"),
        ("trafficmanager.net", "Azure Traffic Manager"),
        ("blob.core.windows.net", "Azure Storage"),
        ("azureedge.net", "Azure CDN"),
        ("azurefd.net", "Azure Front Door"),
        ("s3.amazonaws.com", "AWS S3"),
        ("s3-website", "AWS S3 website"),
        ("elasticbeanstalk.com", "AWS Elastic Beanstalk"),
        ("cloudfront.net", "AWS CloudFront"),
        ("herokuapp.com", "Heroku"),
        ("herokudns.com", "Heroku"),
        ("github.io", "GitHub Pages"),
        ("netlify.app", "Netlify"),
        ("vercel.app", "Vercel"),
        ("pages.dev", "Cloudflare Pages"),
        ("firebaseapp.com", "Firebase"),
        ("web.app", "Firebase"),
        ("myshopify.com", "Shopify"),
        ("ghost.io", "Ghost"),
        ("wordpress.com", "WordPress.com"),
        ("zendesk.com", "Zendesk"),
        ("readthedocs.io", "Read the Docs"),
        ("fastly.net", "Fastly"),
        ("pantheonsite.io", "Pantheon"),
        ("surge.sh", "Surge"),
    ];

    public static bool IsDangling(CnameOutcome o) =>
        o is CnameOutcome.TargetMissing or CnameOutcome.NoAddress or CnameOutcome.TargetDisabled
            or CnameOutcome.Loop or CnameOutcome.ChainTooLong;

    public static string Describe(CnameOutcome o) => o switch
    {
        CnameOutcome.Resolves => "Resolves",
        CnameOutcome.TargetMissing => "Target doesn't exist",
        CnameOutcome.NoAddress => "Target has no address",
        CnameOutcome.TargetDisabled => "Target disabled",
        CnameOutcome.Loop => "Loop",
        CnameOutcome.ChainTooLong => "Chain too long",
        CnameOutcome.External => "External (not verified)",
        CnameOutcome.Delegated => "Delegated/forwarded (not verified)",
        CnameOutcome.CnameDisabled => "CNAME disabled",
        _ => o.ToString(),
    };

    public static DanglingCnameResult Run(GridInventory inv, int maxChain = DefaultMaxChain)
    {
        var views = inv.DnsViews.ToDictionary(v => v.Key, v => v.Name, StringComparer.Ordinal);
        var zonesByKey = inv.Zones.ToDictionary(z => z.Key, StringComparer.Ordinal);

        // Per view: zones by name, and records by owner name.
        var zonesByView = inv.Zones.GroupBy(z => z.ViewKey)
            .ToDictionary(g => g.Key, g => g.ToDictionary(z => Norm(z.Fqdn), StringComparer.Ordinal), StringComparer.Ordinal);
        var recordsByView = inv.Records.GroupBy(r => r.ViewKey)
            .ToDictionary(g => g.Key, g => g.ToLookup(r => Norm(r.Fqdn), StringComparer.Ordinal), StringComparer.Ordinal);

        var rows = new List<DanglingCnameRow>();
        foreach (var cname in inv.Records.Where(r => r.Type == DnsRecordType.CNAME))
        {
            // CNAMEs in Response Policy zones are RPZ rules ("CNAME ." blocks, "rpz-passthru." allows),
            // not names anyone resolves, so they aren't checked here.
            if (zonesByKey.TryGetValue(cname.ZoneKey, out var z) && z.ZoneType == "ResponsePolicy") continue;

            var view = views.GetValueOrDefault(cname.ViewKey, cname.ViewKey);
            var zone = z?.Fqdn ?? cname.ZoneKey;

            if (cname.Disabled)
            {
                rows.Add(new DanglingCnameRow(view, zone, cname.Fqdn, cname.Value, $"{cname.Fqdn} → {cname.Value}",
                    CnameOutcome.CnameDisabled, ReportSeverity.Info, "Disabled, so not served; consider removing it.",
                    cname.IsFromHost, null));
                continue;
            }

            var zones = zonesByView.GetValueOrDefault(cname.ViewKey) ?? new Dictionary<string, DnsZone>();
            var records = recordsByView.GetValueOrDefault(cname.ViewKey);
            var (outcome, chain, detail, last) = Follow(cname, zones, records, maxChain);

            var provider = outcome is CnameOutcome.External or CnameOutcome.Delegated or CnameOutcome.TargetMissing
                ? TakeoverProvider(last)
                : null;
            var severity = outcome switch
            {
                _ when IsDangling(outcome) => outcome == CnameOutcome.NoAddress ? ReportSeverity.Medium : ReportSeverity.High,
                CnameOutcome.External or CnameOutcome.Delegated => provider is null ? ReportSeverity.Info : ReportSeverity.Medium,
                _ => ReportSeverity.None,
            };
            if (provider is not null)
            {
                detail += $" Target is on {provider}: if that resource no longer exists, someone else could claim it " +
                          "(subdomain takeover). Verify it.";
            }

            rows.Add(new DanglingCnameRow(view, zone, cname.Fqdn, cname.Value, string.Join(" → ", chain),
                outcome, severity, detail.Trim(), cname.IsFromHost, provider));
        }

        var ordered = rows
            .OrderBy(r => r.Severity)
            .ThenBy(r => r.View, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DanglingCnameResult(ordered, maxChain);
    }

    private static (CnameOutcome Outcome, List<string> Chain, string Detail, string Last) Follow(
        DnsRecord start, Dictionary<string, DnsZone> zones, ILookup<string, DnsRecord>? records, int maxChain)
    {
        var chain = new List<string> { start.Fqdn };
        var seen = new HashSet<string>(StringComparer.Ordinal) { Norm(start.Fqdn) };
        var target = start.Value;
        var hops = 1;

        while (true)
        {
            var name = Norm(target);
            chain.Add(target.TrimEnd('.'));

            if (name.Length == 0)
                return (CnameOutcome.TargetMissing, chain, "The CNAME has an empty target.", target);
            if (!seen.Add(name))
                return (CnameOutcome.Loop, chain, $"{target.TrimEnd('.')} leads back into the chain.", target);
            if (hops > maxChain)
                return (CnameOutcome.ChainTooLong, chain, $"More than {maxChain} CNAMEs in a row.", target);

            // The zone answering for this name in this view: the longest matching zone name.
            var zone = FindZone(name, zones);
            if (zone is null)
                return (CnameOutcome.External, chain, "Target is outside the grid's zones in this view.", target);
            if (zone.ZoneType is "Delegated" or "Forward" or "Stub")
                return (CnameOutcome.Delegated, chain,
                    $"Target is in {zone.Fqdn}, which the grid {(zone.ZoneType == "Delegated" ? "delegates" : "forwards")}; check it live.",
                    target);
            if (zone.Disabled)
                return (CnameOutcome.TargetDisabled, chain, $"The zone holding the target ({zone.Fqdn}) is disabled.", target);

            var atName = records?[name].ToList() ?? [];
            var enabled = atName.Where(r => !r.Disabled).ToList();

            if (atName.Count == 0)
                return (CnameOutcome.TargetMissing, chain, $"No records named {target.TrimEnd('.')} in {zone.Fqdn}.", target);
            if (enabled.Count == 0)
                return (CnameOutcome.TargetDisabled, chain, $"Every record named {target.TrimEnd('.')} is disabled.", target);

            if (enabled.FirstOrDefault(r => r.Type == DnsRecordType.CNAME) is { } next)
            {
                target = next.Value;
                hops++;
                continue;
            }

            if (enabled.Any(r => r.Type is DnsRecordType.A or DnsRecordType.AAAA))
                return (CnameOutcome.Resolves, chain, "", target);

            var types = string.Join(", ", enabled.Select(r => r.Type).Distinct());
            return (CnameOutcome.NoAddress, chain, $"{target.TrimEnd('.')} has only {types} records, no A or AAAA.", target);
        }
    }

    private static DnsZone? FindZone(string name, Dictionary<string, DnsZone> zones)
    {
        for (var candidate = name; candidate.Length > 0;)
        {
            if (zones.TryGetValue(candidate, out var zone)) return zone;
            var dot = candidate.IndexOf('.');
            if (dot < 0) break;
            candidate = candidate[(dot + 1)..];
        }
        return null;
    }

    internal static string? TakeoverProvider(string target)
    {
        var name = Norm(target);
        foreach (var (suffix, provider) in TakeoverProviders)
        {
            if (name == suffix || name.EndsWith("." + suffix, StringComparison.Ordinal) || name.Contains("." + suffix + ".", StringComparison.Ordinal)
                || (suffix.StartsWith("s3-", StringComparison.Ordinal) && name.Contains(suffix, StringComparison.Ordinal)))
            {
                return provider;
            }
        }
        return null;
    }

    private static string Norm(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();
}
