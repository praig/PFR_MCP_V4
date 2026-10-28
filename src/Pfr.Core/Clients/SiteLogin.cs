namespace Pfr.Core.Clients;

/// <summary>
/// A login and/or API key for one of a client's own sites or services (a portal, VPN, cloud console,
/// ticketing system...). Any of user id, password and API key can be left empty, so this covers a plain
/// login, an API-key-only service, or both. Stored encrypted with the client (<c>logins.pfr</c>); never
/// in snapshots, reports, logs or error messages.
/// </summary>
public sealed class SiteLogin
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What the engineer calls it, e.g. "Acme VPN portal". Unique within a client.</summary>
    public string Title { get; set; } = "";

    /// <summary>Address of the site or service; free text (a URL, host name, or app name).</summary>
    public string Url { get; set; } = "";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string ApiKey { get; set; } = "";

    public string Notes { get; set; } = "";

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Trims the text fields and checks a title is present. A password is kept exactly as typed
    /// (spaces can be part of one); an API key is trimmed because pasted keys often carry stray whitespace.
    /// </summary>
    /// <exception cref="ArgumentException">There is no title.</exception>
    public void Normalize()
    {
        Title = Title.Trim();
        if (Title.Length == 0) throw new ArgumentException("A login needs a title, e.g. the name of the site.");
        Url = Url.Trim();
        Username = Username.Trim();
        ApiKey = ApiKey.Trim();
        Notes = Notes.Trim();
    }

    /// <summary>Only the title, so a secret can't leak through logging or an exception message.</summary>
    public override string ToString() => Title;
}
