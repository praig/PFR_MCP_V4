using Pfr.Core.Nios.Model;
using Pfr.Core.Nios.OneDb;

namespace Pfr.Core.Nios.Backup;

/// <summary>
/// Works out which services each member runs, from the per-member property
/// objects in onedb.xml. To support another service, add a row to <see cref="Flags"/>.
/// </summary>
internal static class MemberServices
{
    /// <summary>A service enabled by a boolean field on a per-member object.</summary>
    private sealed record ServiceFlag(string Service, string ObjectType, string Field);

    // Display order is the order of this list.
    private static readonly ServiceFlag[] Flags =
    [
        new("DNS", "dns.member_dns_properties", "service_enabled"),
        new("DHCP", "dns.member_dhcp_properties", "service_enabled"),
        // DTC is inserted after DHCP (see Apply): it has no switch of its own.
        new("NTP", "one.vnode_time", "ntp_service_enabled"),
        new("TFTP", "one.member_tftp_properties", "tftp_enabled"),
        new("HTTP file dist.", "one.member_tftp_properties", "http_enabled"),
        new("FTP", "one.member_tftp_properties", "ftp_enabled"),
        new("Discovery", "discovery.member_discovery_properties", "enable_service"),
        new("Threat Protection", "atp.member_atp_restart", "service_enabled"),
        new("Threat Analytics", "analytics.member_analytics_restart", "service_enabled"),
        new("DFP", "one.member_dfp_properties", "dfp_enable"),
        new("Captive Portal", "one.member_captive_portal_properties", "service_enabled"),
        new("TAXII", "one.member_taxii_properties", "taxii_enabled"),
        new("Cloud DNS Sync", "one.member_cloud_dns_sync_properties", "cloud_dns_sync_enabled"),
    ];

    private const string ReportingIndexerType = "reporting.indexer_vnode";

    /// <summary>Object types the parser must read for service and license detection.</summary>
    public static IEnumerable<string> RequiredTypes =>
        Flags.Select(f => f.ObjectType).Append(ReportingIndexerType).Distinct();

    public static void Apply(GridInventory inv, IReadOnlyDictionary<string, List<OneDbObject>> byType)
    {
        // Requires LicenseParser.Apply to have run (DTC depends on member licenses).
        // Which members have each flag set: service -> member oids.
        var enabled = new Dictionary<string, HashSet<int>>();
        foreach (var flag in Flags)
        {
            var oids = enabled.TryGetValue(flag.Service, out var s) ? s : enabled[flag.Service] = [];
            foreach (var o in byType[flag.ObjectType])
            {
                if (MemberOid(o) is { } oid && o.GetBool(flag.Field)) oids.Add(oid);
            }
        }

        var reportingServers = byType[ReportingIndexerType]
            .Select(o => o.GetInt("vnode")).OfType<int>().ToHashSet();

        foreach (var member in inv.Members)
        {
            foreach (var flag in Flags.DistinctBy(f => f.Service))
            {
                if (enabled[flag.Service].Contains(member.Oid))
                {
                    member.Services.Add(flag.Service);
                }

                // DTC runs inside DNS: a DNS member with a DTC license serves DTC.
                if (flag.Service == "DHCP"
                    && member.Services.Contains("DNS")
                    && member.Licenses.Contains("dtc", StringComparer.OrdinalIgnoreCase))
                {
                    member.Services.Add("DTC");
                }
            }

            if (reportingServers.Contains(member.Oid))
            {
                member.Services.Add("Reporting");
            }
        }
    }

    /// <summary>Per-member objects link to the member through "virtual_node" or "parent".</summary>
    private static int? MemberOid(OneDbObject o) => o.GetInt("virtual_node") ?? o.GetInt("parent");
}
