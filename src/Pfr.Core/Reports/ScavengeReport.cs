using System.Globalization;
using Pfr.Core.Ipam;
using Pfr.Core.Nios;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;

namespace Pfr.Core.Reports;

/// <summary>Which records the Scavenge report lists.</summary>
public sealed record ScavengeReportOptions
{
    /// <summary>
    /// Only records made this way (STATIC, DYNAMIC, SYSTEM; any case). Null lists them all. A record whose creator is not known is
    /// left out when a choice is made.
    /// </summary>
    public IReadOnlySet<string>? Creators { get; init; }

    /// <summary>Which record types to list (A, CNAME, PTR). Null lists all three; any other type is ignored.</summary>
    public IReadOnlySet<DnsRecordType>? Types { get; init; }
}

/// <summary>One line of the report.</summary>
/// <param name="Creator">STATIC, DYNAMIC or SYSTEM; empty when the snapshot does not hold it.</param>
/// <param name="Fqdn">The record's full name.</param>
/// <param name="IpAddress">An A record's address; a PTR's, read from its reverse name; empty for a CNAME (it has none).</param>
/// <param name="LastQueried">When it was last queried, in UTC; null when NIOS has no time for it.</param>
/// <param name="Type">A, CNAME or PTR.</param>
public sealed record ScavengeRow(string Creator, string Fqdn, string IpAddress, DateTimeOffset? LastQueried, DnsRecordType Type)
{
    /// <summary>The date as the report shows it, yyyy-MM-dd (UTC); empty when there is none.</summary>
    public string LastQueriedText => LastQueried is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";

    public string TypeText => Type.ToString();
}

public sealed class ScavengeResult(IReadOnlyList<ScavengeRow> rows, bool recordOriginsRead)
{
    /// <summary>The four columns that were asked for, then the record's type.</summary>
    public static readonly IReadOnlyList<string> Columns = ["Creator", "FQDN", "IP address", "Last queried", "Record type"];

    public IReadOnlyList<ScavengeRow> Rows { get; } = rows;

    /// <summary>
    /// False when the snapshot was saved before creators and last-queried times were read, so those columns are empty and the
    /// backup has to be imported again.
    /// </summary>
    public bool RecordOriginsRead { get; } = recordOriginsRead;

    public int Count(string creator) => Rows.Count(r => string.Equals(r.Creator, creator, StringComparison.OrdinalIgnoreCase));

    public int Count(DnsRecordType type) => Rows.Count(r => r.Type == type);

    /// <summary>Lines with no last-queried time.</summary>
    public int NeverQueried => Rows.Count(r => r.LastQueried is null);

    /// <summary>The report as a table: all text.</summary>
    public QueryResult ToResult() => new(
        Columns,
        Rows.Select(r => new object?[] { r.Creator, r.Fqdn, r.IpAddress, r.LastQueriedText, r.TypeText }).ToList(),
        truncated: false, TimeSpan.Zero);
}

/// <summary>
/// The Scavenge report: the A, CNAME and PTR records of a grid (the <c>bind_a</c>, <c>bind_cname</c> and <c>bind_ptr</c> objects)
/// with who made each (STATIC, DYNAMIC or SYSTEM), its full name, its IP address, when it was last queried and its type, for
/// deciding what DNS scavenging could safely remove.
/// <list type="bullet">
/// <item>Only records entered or registered on their own. The records a host object makes are not <c>bind_*</c> objects and are
/// not listed.</item>
/// <item>The IP address is an A record's value, or the address a PTR's reverse name stands for (empty if it names none, like a
/// zone's own name). A CNAME has no address.</item>
/// <item>"Last queried" is blank for a record NIOS has no time for: it has not been queried since NIOS began keeping the time, or
/// the grid does not keep it. Blank does not prove a record is unused.</item>
/// </list>
/// </summary>
public static class ScavengeReport
{
    public const string Title = "Scavenge report";

    public const string Description =
        "Each A, CNAME and PTR record with who created it (static, dynamic or system), its full name, its IP address and when " +
        "it was last queried: for judging what DNS scavenging could remove.";

    /// <summary>The creator names NIOS uses.</summary>
    public static readonly IReadOnlyList<string> CreatorNames = ["DYNAMIC", "STATIC", "SYSTEM"];

    /// <summary>The record types the report covers.</summary>
    public static readonly IReadOnlyList<DnsRecordType> RecordTypes = [DnsRecordType.A, DnsRecordType.CNAME, DnsRecordType.PTR];

    public static ScavengeResult Run(GridInventory inventory, ScavengeReportOptions? options = null)
    {
        options ??= new ScavengeReportOptions();
        var creators = options.Creators is null ? null : new HashSet<string>(options.Creators, StringComparer.OrdinalIgnoreCase);
        var types = options.Types is null ? RecordTypes.ToHashSet() : RecordTypes.Where(options.Types.Contains).ToHashSet();

        var rows = new List<ScavengeRow>();
        foreach (var r in inventory.Records)
        {
            if (r.IsFromHost || !types.Contains(r.Type)) continue; // a host object's records are not bind_* objects
            if (creators is not null && (r.Creator is null || !creators.Contains(r.Creator))) continue;

            var address = r.Type switch
            {
                DnsRecordType.A => r.Value,
                DnsRecordType.PTR => IpamKeys.AddressFromReverseName(r.Fqdn) ?? "",
                _ => "",
            };
            rows.Add(new ScavengeRow(r.Creator ?? "", r.Fqdn, address, r.LastQueried, r.Type));
        }

        // Dynamic first (alphabetical: DYNAMIC, STATIC, SYSTEM), then by name, then type, then address. No known creator last.
        rows.Sort((a, b) =>
        {
            var c = (a.Creator.Length == 0).CompareTo(b.Creator.Length == 0);
            if (c != 0) return c;
            c = string.Compare(a.Creator, b.Creator, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.Fqdn, b.Fqdn, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = a.Type.CompareTo(b.Type);
            return c != 0 ? c : IpSort.Compare(a.IpAddress, b.IpAddress);
        });

        return new ScavengeResult(rows, inventory.RecordOriginsRead);
    }
}
