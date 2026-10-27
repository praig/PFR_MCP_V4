using System.Net;
using System.Net.Sockets;

namespace Pfr.Core.Nios;

/// <summary>An IPv4 or IPv6 prefix such as 10.0.0.0/8, with containment checks.</summary>
public readonly record struct IpPrefix(IPAddress Address, int Length)
{
    public bool IsIpv6 => Address.AddressFamily == AddressFamily.InterNetworkV6;

    public static bool TryParse(string address, int length, out IpPrefix prefix)
    {
        prefix = default;
        if (!IPAddress.TryParse(address, out var ip)) return false;
        var max = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (length < 0 || length > max) return false;
        prefix = new IpPrefix(ip, length);
        return true;
    }

    /// <summary>True if <paramref name="other"/> lies entirely inside this prefix (and is not the same size).</summary>
    public bool StrictlyContains(IpPrefix other) =>
        other.Length > Length && Covers(other.Address);

    /// <summary>True if <paramref name="ip"/> falls inside this prefix.</summary>
    public bool Covers(IPAddress ip)
    {
        if (ip.AddressFamily != Address.AddressFamily) return false;
        var a = Address.GetAddressBytes();
        var b = ip.GetAddressBytes();
        var full = Length / 8;
        for (var i = 0; i < full; i++)
        {
            if (a[i] != b[i]) return false;
        }

        var rem = Length % 8;
        if (rem == 0) return true;
        var mask = (byte)(0xFF << (8 - rem));
        return (a[full] & mask) == (b[full] & mask);
    }

    public override string ToString() => $"{Address}/{Length}";
}
