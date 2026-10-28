using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pfr.Core.Wapi;

/// <summary>
/// Certificate fingerprints for pinning a grid's TLS certificate (SSH-host-key style):
/// the SHA-256 of the DER certificate, kept as 64 lowercase hex characters.
/// </summary>
public static class CertificatePin
{
    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();

    /// <summary>
    /// Accepts a fingerprint as people write it (upper or lower case, with or without colons, spaces or
    /// dashes) and returns the 64 lowercase hex characters, or null for empty input.
    /// </summary>
    /// <exception cref="ArgumentException">Not a SHA-256 fingerprint.</exception>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var hex = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray()).ToLowerInvariant();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new ArgumentException("A certificate fingerprint is 64 hexadecimal characters (SHA-256), with or without colons.");
        return hex;
    }

    /// <summary>The fingerprint as colon-separated upper-case pairs, the way Grid Manager and browsers show it.</summary>
    public static string Format(string hex) =>
        string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
}
