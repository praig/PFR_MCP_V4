namespace Pfr.Core.Clients;

/// <summary>
/// An SSH host key the engineer has confirmed for one of a client's hosts (spec §11: "host keys pinned"). Connecting
/// to that host again only goes ahead if it presents this key, and the key is checked before any password is sent, so
/// a different machine at the same address (or someone in the middle) never receives the account's password.
/// </summary>
public sealed class SshPinnedKey
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;

    /// <summary>The key type, e.g. ssh-ed25519 or ecdsa-sha2-nistp256.</summary>
    public string Algorithm { get; set; } = "";

    /// <summary>OpenSSH's form, as shown by "ssh-keygen -lf": SHA256: and 43 characters.</summary>
    public string Fingerprint { get; set; } = "";

    public DateTimeOffset PinnedUtc { get; set; } = DateTimeOffset.UtcNow;
}
