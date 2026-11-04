using Pfr.Core.Nios.Model;

namespace Pfr.Core.Reports;

/// <summary>
/// How a DNS record is laid out as a row: the columns, the wording and the order that the Inventory tab's DNS records panel
/// uses. The panel and the Row count report both build their rows here, so a record reads the same in either.
/// </summary>
public static class DnsRecordPanel
{
    /// <summary>The panel's columns, in order.</summary>
    public static readonly IReadOnlyList<string> Columns = ["Name", "Type", "Value", "TTL", "TTL from", "Host", "FQDN", "Disabled"];

    /// <summary>
    /// A record's cells, in the order of <see cref="Columns"/>: its name relative to the zone ("@" for the zone's own name),
    /// type, data, TTL (as "300 (5m)"), where the TTL comes from, "Yes" if a host object generates it, its full name, and
    /// "Yes" if it is disabled. A cell with nothing to say is empty.
    /// </summary>
    public static IReadOnlyList<string> Cells(DnsRecord r) =>
    [
        r.Name.Length == 0 ? "@" : r.Name, r.Type.ToString(), r.Value, r.Ttl.Display, TtlFrom(r.Ttl), YesNo(r.IsFromHost), r.Fqdn, YesNo(r.Disabled),
    ];

    /// <summary>The panel's order: the zone's own records ("@") first, then by name, then by type.</summary>
    public static IOrderedEnumerable<DnsRecord> Order(IEnumerable<DnsRecord> records) => records
        .OrderBy(r => r.Name.Length > 0)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Type);

    /// <summary>Where a TTL comes from, worded so inherited values are obvious.</summary>
    public static string TtlFrom(Ttl ttl) => ttl.Seconds is null ? "" : ttl.Source switch
    {
        TtlSource.Record => "Record",
        TtlSource.Host => "Host object",
        TtlSource.Zone => "Inherited: zone",
        _ => "Inherited: grid",
    };

    private static string YesNo(bool value) => value ? "Yes" : "";
}
