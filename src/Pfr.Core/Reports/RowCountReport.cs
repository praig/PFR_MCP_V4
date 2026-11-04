using Pfr.Core.Nios.Model;
using Pfr.Core.Query;

namespace Pfr.Core.Reports;

/// <summary>Which zones the Row count report covers.</summary>
public sealed record RowCountOptions
{
    /// <summary>Include reverse-mapping zones (in-addr.arpa and ip6.arpa).</summary>
    public bool IncludeReverseZones { get; init; } = true;

    /// <summary>Include zones that hold no records (forward, stub and delegated zones, and empty authoritative ones).</summary>
    public bool IncludeEmptyZones { get; init; } = true;
}

/// <summary>One zone of the report: where it is, how many records it holds, and the records.</summary>
/// <param name="View">The DNS view's name.</param>
/// <param name="Zone">The zone's name.</param>
/// <param name="ZoneKey">The zone's key in the backup (what its records point at).</param>
/// <param name="ZoneType">Authoritative, Forward, Delegated, Stub or ResponsePolicy.</param>
/// <param name="IsReverse">A reverse-mapping zone.</param>
/// <param name="Records">Every record of the zone the backup holds, in the Inventory panel's order (the zone's own records first, then by name and type).</param>
/// <param name="SheetName">The name of the zone's sheet in the workbook: "view.zone", made to fit Excel's rules.</param>
public sealed record RowCountZone(
    string View, string Zone, string ZoneKey, string ZoneType, bool IsReverse,
    IReadOnlyList<DnsRecord> Records, string SheetName)
{
    public int Count => Records.Count;
}

public sealed class RowCountResult(IReadOnlyList<RowCountZone> zones)
{
    /// <summary>The zones, by view and then name.</summary>
    public IReadOnlyList<RowCountZone> Zones { get; } = zones;

    public long TotalRecords => Zones.Sum(z => (long)z.Count);

    public int EmptyZones => Zones.Count(z => z.Count == 0);

    /// <summary>
    /// The workbook's sheets: <b>Home</b> (a row for every zone: view, zone, record count, with the zone linking to its
    /// sheet) and then one sheet per zone listing its records, each with a header row.
    /// </summary>
    public IReadOnlyList<XlsxSheet> ToSheets()
    {
        var home = new QueryResult(
            ["View", "Zone", "Records"],
            Zones.Select(z => new object?[] { z.View, z.Zone, (long)z.Count }).ToList(),
            truncated: false, TimeSpan.Zero);
        var links = Zones.Select((z, i) => new XlsxLink(i, 1, z.SheetName)).ToList();

        var sheets = new List<XlsxSheet>(Zones.Count + 1) { new(RowCountReport.HomeSheet, home, links) };
        sheets.AddRange(Zones.Select(z => new XlsxSheet(z.SheetName, RecordsOf(z))));
        return sheets;
    }

    private static QueryResult RecordsOf(RowCountZone zone) => new(
        DnsRecordPanel.Columns,
        zone.Records.Select(r => DnsRecordPanel.Cells(r).Cast<object?>().ToArray()).ToList(),
        truncated: false, TimeSpan.Zero);
}

/// <summary>
/// The Row count report: how many DNS records each zone of the grid holds, as an Excel workbook. The first sheet, Home,
/// lists every zone (view, zone, record count) and each zone name links to a sheet of its own listing the zone's records.
/// A zone's sheet has the columns, wording and order of the Inventory tab's DNS records panel (see <see cref="DnsRecordPanel"/>).
/// Each sheet has a header row and is named <c>view.zone</c> (cut to Excel's 31 characters, with a ~2, ~3... if two would
/// clash). It counts the records the backup holds, which is every type this tool reads (A, AAAA, CNAME, PTR, MX, SRV, TXT,
/// NAPTR, CAA and NS); the zone's SOA record is not among them.
/// </summary>
public static class RowCountReport
{
    public const string Title = "Row count";

    public const string HomeSheet = "Home";

    public const string Description =
        "How many DNS records each zone holds, as an Excel workbook: a Home sheet lists every zone with its count and links " +
        "to a sheet of that zone's records.";

    /// <summary>Counts each zone's records and names the sheets of its workbook.</summary>
    public static RowCountResult Run(GridInventory inventory, RowCountOptions? options = null)
    {
        options ??= new RowCountOptions();

        var viewNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var view in inventory.DnsViews) viewNames[view.Key] = view.Name;

        var recordsByZone = inventory.Records.ToLookup(r => r.ZoneKey, StringComparer.Ordinal);

        var chosen = inventory.Zones
            .Where(z => options.IncludeReverseZones || !z.IsReverse)
            .Select(z => (Zone: z, Records: recordsByZone[z.Key].ToList()))
            .Where(x => options.IncludeEmptyZones || x.Records.Count > 0)
            .Select(x => (
                x.Zone,
                x.Records,
                View: viewNames.TryGetValue(x.Zone.ViewKey, out var name) ? name : x.Zone.ViewKey,
                Name: x.Zone.Fqdn.Length == 0 ? "." : x.Zone.Fqdn))
            .OrderBy(x => x.View, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sheetNames = XlsxWriter.UniqueSheetNames(chosen.Select(x => $"{x.View}.{x.Name}"), reserved: [HomeSheet]);

        var zones = chosen.Select((x, i) => new RowCountZone(
            x.View, x.Name, x.Zone.Key, x.Zone.ZoneType, x.Zone.IsReverse,
            DnsRecordPanel.Order(x.Records).ToList(),
            sheetNames[i])).ToList();

        return new RowCountResult(zones);
    }

    /// <summary>Writes the workbook to a stream (left open). Returns what Excel's limits made it cut or change; empty when nothing.</summary>
    public static IReadOnlyList<string> WriteWorkbook(Stream output, RowCountResult result, string? gridName = null, DateTimeOffset? exportedAt = null) =>
        XlsxWriter.WriteWorkbook(output, result.ToSheets(), string.IsNullOrWhiteSpace(gridName) ? Title : $"{Title}: {gridName}", exportedAt);

    /// <summary>
    /// Writes the workbook to a file. It is written beside its final name and moved into place, so a failure never leaves
    /// a half-written file. Missing folders are created.
    /// </summary>
    public static IReadOnlyList<string> WriteFile(string path, RowCountResult result, string? gridName = null)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder) Directory.CreateDirectory(folder);

        var temp = full + ".tmp";
        try
        {
            IReadOnlyList<string> notes;
            using (var stream = File.Create(temp)) notes = WriteWorkbook(stream, result, gridName);
            File.Move(temp, full, overwrite: true);
            return notes;
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }
}
