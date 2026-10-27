using System.Xml;

namespace Pfr.Core.Nios.OneDb;

/// <summary>
/// Streams objects out of a NIOS onedb.xml without loading the whole file.
/// Only object types in the allowlist are returned, and sensitive properties
/// are always dropped (see <see cref="SensitiveData"/>).
/// </summary>
public sealed class OneDbReader
{
    private const string TypePrefix = ".com.infoblox.";

    private readonly ISet<string> _allowedTypes;

    /// <param name="allowedTypes">Short type names to keep, e.g. "dns.zone".</param>
    public OneDbReader(IEnumerable<string> allowedTypes)
    {
        _allowedTypes = new HashSet<string>(allowedTypes, StringComparer.Ordinal);
    }

    /// <summary>NIOS version from the &lt;DATABASE VERSION=...&gt; header, once reading has started.</summary>
    public string? DatabaseVersion { get; private set; }

    public IEnumerable<OneDbObject> Read(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            CloseInput = false,
        };

        using var xml = XmlReader.Create(stream, settings);

        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (xml.Name == "DATABASE")
            {
                DatabaseVersion = xml.GetAttribute("VERSION");
                continue;
            }

            if (xml.Name != "OBJECT" || xml.IsEmptyElement)
            {
                continue;
            }

            var obj = ReadObject(xml);
            if (obj is not null)
            {
                yield return obj;
            }
        }
    }

    private OneDbObject? ReadObject(XmlReader xml)
    {
        var depth = xml.Depth;
        string? type = null;
        var props = new Dictionary<string, string>(StringComparer.Ordinal);

        while (xml.Read() && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth))
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Name != "PROPERTY")
            {
                continue;
            }

            var name = xml.GetAttribute("NAME");
            var value = xml.GetAttribute("VALUE") ?? string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name == "__type")
            {
                type = value.StartsWith(TypePrefix, StringComparison.Ordinal)
                    ? value[TypePrefix.Length..]
                    : value;

                if (!_allowedTypes.Contains(type))
                {
                    // Not needed: skip the rest of this object without reading it.
                    SkipToEndOfObject(xml, depth);
                    return null;
                }

                continue;
            }

            if (!SensitiveData.IsSensitive(name, value))
            {
                props[name] = value;
            }
        }

        // __type is normally first; if it was missing or late, apply the filter now.
        if (type is null || !_allowedTypes.Contains(type))
        {
            return null;
        }

        return new OneDbObject(type, props);
    }

    private static void SkipToEndOfObject(XmlReader xml, int depth)
    {
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth)
            {
                return;
            }
        }
    }
}
