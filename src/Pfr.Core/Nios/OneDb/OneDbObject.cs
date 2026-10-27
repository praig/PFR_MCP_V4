namespace Pfr.Core.Nios.OneDb;

/// <summary>
/// One &lt;OBJECT&gt; from onedb.xml: its type (e.g. "dns.zone", with the
/// ".com.infoblox." prefix removed) and its non-sensitive properties.
/// </summary>
public sealed class OneDbObject
{
    public OneDbObject(string type, IReadOnlyDictionary<string, string> properties)
    {
        Type = type;
        Properties = properties;
    }

    /// <summary>Short type name, e.g. "dns.zone" or "one.virtual_node".</summary>
    public string Type { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public string? Get(string name) =>
        Properties.TryGetValue(name, out var value) ? value : null;

    public string GetOrEmpty(string name) => Get(name) ?? string.Empty;

    public bool GetBool(string name) =>
        Get(name) is { } v && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

    public int? GetInt(string name) =>
        int.TryParse(Get(name), out var i) ? i : null;

    public override string ToString() => $"{Type} ({Properties.Count} properties)";
}
