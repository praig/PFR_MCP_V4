using System.Net;

namespace Pfr.Core.Wapi;

/// <summary>
/// A WAPI problem. Messages are written for the engineer and never contain passwords or other secrets.
/// </summary>
public class WapiException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>The grid answered, but with an error status (wrong password, no permission, unknown object...).</summary>
public sealed class WapiRequestException(HttpStatusCode statusCode, string message) : WapiException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public enum CertificateProblem
{
    /// <summary>No certificate has been pinned for this grid yet.</summary>
    NotPinned,

    /// <summary>The grid presented a different certificate from the pinned one.</summary>
    Mismatch,
}

/// <summary>
/// The grid's certificate is not the pinned one (or none is pinned yet). The connection was dropped during
/// the TLS handshake, before any user id or password was sent.
/// </summary>
public sealed class WapiCertificateException(CertificateProblem problem, string observedFingerprint, string? pinnedFingerprint, string host)
    : WapiException(Describe(problem, observedFingerprint, pinnedFingerprint, host))
{
    public CertificateProblem Problem { get; } = problem;

    /// <summary>The certificate the grid presented (64 lowercase hex characters).</summary>
    public string ObservedFingerprint { get; } = observedFingerprint;

    /// <summary>The certificate pinned for the grid, or null if none is.</summary>
    public string? PinnedFingerprint { get; } = pinnedFingerprint;

    private static string Describe(CertificateProblem problem, string observed, string? pinned, string host) =>
        problem == CertificateProblem.NotPinned
            ? $"{host} presented a certificate that has not been trusted yet (SHA-256 {CertificatePin.Format(observed)}). " +
              "Compare it with the grid's certificate, then trust it to continue."
            : $"{host} presented a different certificate from the one pinned for this grid. " +
              $"Pinned: {CertificatePin.Format(pinned ?? "")}. Presented: {CertificatePin.Format(observed)}. " +
              "This can be a certificate renewal or an attack: verify it before trusting the new certificate.";
}
