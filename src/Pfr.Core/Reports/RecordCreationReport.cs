using System.Globalization;
using Pfr.Core.Nios;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;

namespace Pfr.Core.Reports;

/// <summary>Which records the Record creation report lists, and which extensible attribute it shows.</summary>
public sealed record RecordCreationOptions
{
    /// <summary>The name of the extensible attribute shown in the last column (any case). Mnemonic by default.</summary>
    public string Attribute { get; init; } = RecordCreationReport.DefaultAttribute;

    public bool IncludeARecords { get; init; } = true;

    public bool IncludeHosts { get; init; } = true;

    /// <summary>
    /// Also list host objects that are not in DNS (they only hold their addresses in IPAM and make no DNS records). Off by default,
    /// since the report is about records.
    /// </summary>
    public bool IncludeIpamOnlyHosts { get; init; }
}

/// <summary>One line of the report.</summary>
/// <param name="RecordType">"A" for an A record, "Host" for a host object.</param>
/// <param name="Fqdn">The record's or host's full name.</param>
/// <param name="IpAddress">The address; a host with several addresses has a line for each (empty for a host with none).</param>
/// <param name="Comment">The comment entered on it; empty when none.</param>
/// <param name="Created">When it was created, in UTC; null when the backup has no time for it.</param>
/// <param name="AttributeValue">The value of the chosen extensible attribute; empty when it is not set.</param>
public sealed record RecordCreationRow(
    string RecordType, string Fqdn, string IpAddress, string Comment, DateTimeOffset? Created, string AttributeValue)
{
    /// <summary>The creation date as the report shows it, yyyy-MM-dd (UTC); empty when there is none.</summary>
    public string CreatedText => Created is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
}

public sealed class RecordCreationResult(
    IReadOnlyList<RecordCreationRow> rows, string attribute, bool recordDetailsRead, IReadOnlyList<string> attributesSeen)
{
    /// <summary>The attribute the last column shows (as it was asked for).</summary>
    public string Attribute { get; } = attribute;

    /// <summary>The six columns: record type, FQDN, IP address, comment, creation date, and the attribute's name.</summary>
    public IReadOnlyList<string> Columns { get; } = ["Record type", "FQDN", "IP address", "Comment", "Creation date", attribute];

    public IReadOnlyList<RecordCreationRow> Rows { get; } = rows;

    /// <summary>
    /// False when the snapshot was saved before comments, creation dates and extensible attributes were read, so those columns are
    /// empty and the backup has to be imported again.
    /// </summary>
    public bool RecordDetailsRead { get; } = recordDetailsRead;

    /// <summary>The names of the extensible attributes that are set on the A records and hosts looked at, to choose from.</summary>
    public IReadOnlyList<string> AttributesSeen { get; } = attributesSeen;

    public int Count(string recordType) => Rows.Count(r => r.RecordType == recordType);

    /// <summary>Lines where the attribute has a value.</summary>
    public int WithAttribute => Rows.Count(r => r.AttributeValue.Length > 0);

    /// <summary>True if any listed record or host has the attribute set.</summary>
    public bool AttributeFound => WithAttribute > 0;

    /// <summary>Lines with no creation date.</summary>
    public int WithoutCreationDate => Rows.Count(r => r.Created is null);

    /// <summary>The report as a table: all text.</summary>
    public QueryResult ToResult() => new(
        Columns,
        Rows.Select(r => new object?[] { r.RecordType, r.Fqdn, r.IpAddress, r.Comment, r.CreatedText, r.AttributeValue }).ToList(),
        truncated: false, TimeSpan.Zero);
}

/// <summary>
/// The Record creation report: the A records and host objects of a grid with, for each, its full name, IP address, comment, creation
/// date and the value of one extensible attribute (Mnemonic by default).
/// <list type="bullet">
/// <item>A records are the ones entered or registered on their own; the A records a host object makes are not listed again (the
/// host is). A host with several addresses has a line for each.</item>
/// <item>An attribute a record inherits from a network is listed with the value that applies.</item>
/// <item>The creation date is blank for an object the backup has no creation time for.</item>
/// </list>
/// </summary>
public static class RecordCreationReport
{
    public const string Title = "Record creation report";

    public const string DefaultAttribute = "Mnemonic";

    public const string Description =
        "Each A record and host with its full name, IP address, comment, creation date and the value of an extensible attribute " +
        "(Mnemonic by default).";

    public static RecordCreationResult Run(GridInventory inventory, RecordCreationOptions? options = null)
    {
        options ??= new RecordCreationOptions();
        var attribute = string.IsNullOrWhiteSpace(options.Attribute) ? RecordCreationReport.DefaultAttribute : options.Attribute.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RecordCreationRow>();

        string ValueOf(Dictionary<string, string>? attributes)
        {
            if (attributes is null) return "";
            foreach (var name in attributes.Keys) seen.Add(name);
            foreach (var (name, value) in attributes)
            {
                if (string.Equals(name, attribute, StringComparison.OrdinalIgnoreCase)) return value;
            }
            return "";
        }

        if (options.IncludeARecords)
        {
            foreach (var r in inventory.Records)
            {
                if (r.Type != DnsRecordType.A || r.IsFromHost) continue; // a host object's A records are listed as the host
                rows.Add(new RecordCreationRow("A", r.Fqdn, r.Value, r.Comment ?? "", r.Created, ValueOf(r.ExtensibleAttributes)));
            }
        }

        if (options.IncludeHosts)
        {
            foreach (var h in inventory.Hosts)
            {
                if (!h.IsDns && !options.IncludeIpamOnlyHosts) continue;
                var value = ValueOf(h.ExtensibleAttributes);
                var addresses = h.Addresses.Count > 0 ? h.Addresses.Select(a => a.Address).ToList() : [""];
                foreach (var address in addresses)
                    rows.Add(new RecordCreationRow("Host", h.Name, address, h.Comment ?? "", h.Created, value));
            }
        }

        rows.Sort((a, b) =>
        {
            var c = string.Compare(a.Fqdn, b.Fqdn, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.RecordType, b.RecordType);
            return c != 0 ? c : IpSort.Compare(a.IpAddress, b.IpAddress);
        });

        return new RecordCreationResult(rows, attribute, inventory.RecordDetailsRead, seen.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
