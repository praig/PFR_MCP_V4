using System.Net;

namespace Pfr.Core.Nios;

/// <summary>Orders IP address strings numerically (IPv4 before IPv6, unparseable last).</summary>
public static class IpSort
{
    public static int Compare(string? a, string? b)
    {
        var ia = Parse(a);
        var ib = Parse(b);
        if (ia is null || ib is null)
        {
            return (ia is null, ib is null) switch
            {
                (true, true) => string.CompareOrdinal(a, b),
                (true, false) => 1,
                _ => -1,
            };
        }

        var ba = ia.GetAddressBytes();
        var bb = ib.GetAddressBytes();
        if (ba.Length != bb.Length) return ba.Length.CompareTo(bb.Length);
        return ba.AsSpan().SequenceCompareTo(bb);
    }

    private static IPAddress? Parse(string? s) =>
        IPAddress.TryParse(s, out var ip) ? ip : null;
}
