using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pfr.Core.Clients;

/// <summary>
/// How to reach one of a client's grids over WAPI, and the accounts to use.
/// Stored encrypted next to the grid (<c>connection.pfr</c>), never in snapshots,
/// reports, logs or error messages. A read-only account is the everyday one; the
/// read-write account is optional and only for changes the engineer deliberately applies.
/// </summary>
public sealed partial class GridConnection
{
    public const int DefaultPort = 443;

    /// <summary>Grid master host name or IP address (no scheme, port or path).</summary>
    public string Address { get; set; } = "";

    public int Port { get; set; } = DefaultPort;

    /// <summary>WAPI version such as "v2.12" or "2.13.1"; null means detect it on connect.</summary>
    public string? WapiVersion { get; set; }

    public Credential? ReadOnly { get; set; }

    public Credential? ReadWrite { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The WAPI root, e.g. https://gm.acme.test:8443 (the port is left out when it is 443).</summary>
    [JsonIgnore]
    public string BaseUrl
    {
        get
        {
            var host = Address.Contains(':') ? $"[{Address}]" : Address; // IPv6
            return Port == DefaultPort ? $"https://{host}" : $"https://{host}:{Port}";
        }
    }

    /// <summary>
    /// Trims and checks the values, and tidies what people paste (a leading https://, a trailing
    /// slash, "host:port"). Accounts with neither user id nor password are dropped.
    /// </summary>
    /// <exception cref="ArgumentException">Something is missing or not usable.</exception>
    public void Normalize()
    {
        var address = Address.Trim();
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address = address[8..];
        address = address.TrimEnd('/');

        // "host:port" pasted into the address box.
        var hostAndPort = HostWithPort().Match(address);
        if (hostAndPort.Success)
        {
            address = hostAndPort.Groups["host"].Value;
            Port = int.Parse(hostAndPort.Groups["port"].Value);
        }

        if (address.Length == 0)
            throw new ArgumentException("The grid needs an address (the Grid Master's host name or IP address).");
        if (address.Any(char.IsWhiteSpace) || address.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
            throw new ArgumentException("Enter the Grid Master's host name or IP address only, without https:// or a path.");
        Address = address;

        if (Port is < 1 or > 65535) throw new ArgumentException("The port must be between 1 and 65535.");

        var version = (WapiVersion ?? "").Trim();
        if (version.Length == 0 || version.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            WapiVersion = null;
        }
        else if (!WapiVersionPattern().IsMatch(version))
        {
            throw new ArgumentException("The WAPI version should look like v2.12 or 2.13.1 (leave it empty to detect it).");
        }
        else
        {
            WapiVersion = version;
        }

        ReadOnly = NormalizeAccount(ReadOnly, "read-only");
        ReadWrite = NormalizeAccount(ReadWrite, "read-write");
    }

    private static Credential? NormalizeAccount(Credential? account, string kind)
    {
        if (account is null) return null;
        account.Username = account.Username.Trim();
        // Passwords are kept exactly as typed: spaces can be part of one.
        if (account.Username.Length == 0 && account.Password.Length == 0) return null;
        if (account.Username.Length == 0 || account.Password.Length == 0)
            throw new ArgumentException($"The {kind} account needs both a user id and a password (or leave both empty).");
        return account;
    }

    /// <summary>Never includes passwords.</summary>
    public override string ToString() => BaseUrl;

    [GeneratedRegex(@"^(?<host>[^:\s\[\]]+):(?<port>\d{1,5})$")]
    private static partial Regex HostWithPort();

    [GeneratedRegex(@"^v?\d+(\.\d+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex WapiVersionPattern();
}

/// <summary>A user id and password for a grid account.</summary>
public sealed class Credential
{
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>Only the user id, so a password can't leak through logging or an exception message.</summary>
    public override string ToString() => Username;
}
