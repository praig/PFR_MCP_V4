using System.Net;
using Microsoft.Data.Sqlite;

namespace Pfr.Core.Query;

/// <summary>
/// IP-aware SQL functions, because plain SQL can't order or search addresses properly:
/// <list type="bullet">
/// <item><c>ip_key(x)</c>: a text key that sorts addresses numerically (IPv4 before IPv6); for a prefix such as
/// "10.0.0.0/24" the length is added, so a network sorts before its subnets. NULL if x isn't an address.</item>
/// <item><c>cidr_contains(prefix, x)</c>: 1 if the address or prefix x lies inside prefix, else 0 (NULL if either is
/// not valid).</item>
/// <item><c>ip_in_range(x, start, end)</c>: 1 if address x is between start and end, inclusive.</item>
/// </list>
/// </summary>
internal static class IpFunctions
{
    public static void Register(SqliteConnection connection)
    {
        connection.CreateFunction<string?, string?>("ip_key", Key, true);
        connection.CreateFunction<string?, string?, int?>("cidr_contains", Contains, true);
        connection.CreateFunction<string?, string?, string?, int?>("ip_in_range", InRange, true);
    }

    public static string? Key(string? text)
    {
        if (!TryParse(text, out var bytes, out var length, out var hasLength)) return null;
        var key = (bytes.Length == 4 ? "4" : "6") + Convert.ToHexString(bytes);
        return hasLength ? $"{key}/{length:D3}" : key;
    }

    public static int? Contains(string? prefix, string? other)
    {
        if (!TryParse(prefix, out var p, out var pLength, out _) || !TryParse(other, out var o, out var oLength, out _))
            return null;
        if (p.Length != o.Length || oLength < pLength) return 0;

        var full = pLength / 8;
        for (var i = 0; i < full; i++)
        {
            if (p[i] != o[i]) return 0;
        }

        var rest = pLength % 8;
        if (rest == 0) return 1;
        var mask = (byte)(0xFF << (8 - rest));
        return (p[full] & mask) == (o[full] & mask) ? 1 : 0;
    }

    public static int? InRange(string? address, string? start, string? end)
    {
        var a = AddressKey(address);
        var s = AddressKey(start);
        var e = AddressKey(end);
        if (a is null || s is null || e is null) return null;
        return string.CompareOrdinal(a, s) >= 0 && string.CompareOrdinal(a, e) <= 0 ? 1 : 0;
    }

    /// <summary>The key of the address part only (any "/length" is ignored).</summary>
    private static string? AddressKey(string? text) =>
        TryParse(text, out var bytes, out _, out _) ? (bytes.Length == 4 ? "4" : "6") + Convert.ToHexString(bytes) : null;

    /// <summary>
    /// Reads "address" or "address/length". Without a length the whole address counts (32 or 128 bits).
    /// </summary>
    private static bool TryParse(string? text, out byte[] bytes, out int length, out bool hasLength)
    {
        bytes = [];
        length = 0;
        hasLength = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        var slash = value.IndexOf('/');
        var addressText = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(addressText, out var ip)) return false;

        bytes = ip.GetAddressBytes();
        var max = bytes.Length * 8;
        if (slash < 0)
        {
            length = max;
            return true;
        }

        if (!int.TryParse(value[(slash + 1)..], out length) || length < 0 || length > max) return false;
        hasLength = true;
        return true;
    }
}
