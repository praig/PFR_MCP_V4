using System.Globalization;
using System.Net;
using Pfr.Core.Nios;

namespace Pfr.Core.Ipam;

/// <summary>
/// The IP address is always the key (spec §13.2): a network is its prefix, an address-level object its address.
/// These make keys one canonical string, so "10.0.0.5/24" and "10.0.0.0/24" are the same network and
/// "2001:0DB8::1" the same address as "2001:db8::1".
/// </summary>
public static class IpamKeys
{
    /// <summary>Parses a prefix, clearing any bits past its length, and gives its key (e.g. "10.0.0.0/24").</summary>
    public static bool TryPrefix(string address, int length, out IpPrefix prefix, out string key)
    {
        key = "";
        if (!IpPrefix.TryParse(address, length, out prefix)) return false;
        var masked = Mask(prefix.Address, prefix.Length);
        prefix = new IpPrefix(masked, prefix.Length);
        key = Key(masked, prefix.Length);
        return true;
    }

    public static string Key(IPAddress address, int length) => $"{address}/{length}";

    /// <summary>The address with every bit after the first <paramref name="length"/> cleared.</summary>
    public static IPAddress Mask(IPAddress address, int length)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(length - i * 8, 0, 8);
            bytes[i] &= bits == 0 ? (byte)0 : (byte)(0xFF << (8 - bits));
        }
        return new IPAddress(bytes);
    }

    /// <summary>The address in its canonical text form, or null if the text is not an address.</summary>
    public static string? Canonical(string? address) =>
        IPAddress.TryParse(address, out var ip) ? ip.ToString() : null;

    /// <summary>
    /// The address a reverse name stands for: "20.0.0.10.in-addr.arpa" is 10.0.0.20, and the 32 nibbles under
    /// ip6.arpa are an IPv6 address. Null when the name is not one plain address, such as a zone apex
    /// ("0.0.10.in-addr.arpa") or a classless delegation ("5.0-25.0.0.10.in-addr.arpa").
    /// </summary>
    public static string? AddressFromReverseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Trim().TrimEnd('.').ToLowerInvariant();

        const string v4Suffix = ".in-addr.arpa";
        if (text.EndsWith(v4Suffix, StringComparison.Ordinal))
        {
            var labels = text[..^v4Suffix.Length].Split('.');
            if (labels.Length != 4) return null;
            var octets = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                // The name lists the octets last first.
                if (!byte.TryParse(labels[3 - i], NumberStyles.None, CultureInfo.InvariantCulture, out octets[i])) return null;
            }
            return new IPAddress(octets).ToString();
        }

        const string v6Suffix = ".ip6.arpa";
        if (text.EndsWith(v6Suffix, StringComparison.Ordinal))
        {
            var labels = text[..^v6Suffix.Length].Split('.');
            if (labels.Length != 32 || labels.Any(l => l.Length != 1 || !Uri.IsHexDigit(l[0]))) return null;
            var bytes = new byte[16];
            for (var i = 0; i < 16; i++)
            {
                // One nibble per label, the last nibble of the address first.
                var high = Convert.ToInt32(labels[31 - 2 * i], 16);
                var low = Convert.ToInt32(labels[30 - 2 * i], 16);
                bytes[i] = (byte)((high << 4) | low);
            }
            return new IPAddress(bytes).ToString();
        }

        return null;
    }
}
