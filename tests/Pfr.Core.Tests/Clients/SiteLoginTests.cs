using System.Text;
using Pfr.Core.Clients;
using Pfr.Core.Storage;

namespace Pfr.Core.Tests.Clients;

public sealed class SiteLoginTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string SitePassword = "Portal-Sup3rSecret!";
    private const string ApiKey = "AKIA-Sup3rSecretApiKey-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-login-{Guid.NewGuid():N}");
    private readonly ClientWorkspace _ws;

    public SiteLoginTests()
    {
        var store = new SecureStore(_dir);
        store.Initialize(Password, SecureStoreTests.TestIterations);
        _ws = new ClientWorkspace(store);
    }

    public void Dispose()
    {
        _ws.Store.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static SiteLogin Portal() => new()
    {
        Title = "Acme VPN portal",
        Url = "https://vpn.acme.test",
        Username = "pfr-engineer",
        Password = SitePassword,
        ApiKey = ApiKey,
        Notes = "MFA via authenticator app.",
    };

    [Fact]
    public void A_client_has_no_logins_until_one_is_saved()
    {
        var client = _ws.CreateClient("Acme");
        Assert.Empty(_ws.GetLogins(client.Id));
    }

    [Fact]
    public void A_login_with_user_password_and_api_key_round_trips()
    {
        var client = _ws.CreateClient("Acme");
        var saved = _ws.SaveLogin(client.Id, Portal());

        var loaded = Assert.Single(_ws.GetLogins(client.Id));
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("Acme VPN portal", loaded.Title);
        Assert.Equal("https://vpn.acme.test", loaded.Url);
        Assert.Equal("pfr-engineer", loaded.Username);
        Assert.Equal(SitePassword, loaded.Password);
        Assert.Equal(ApiKey, loaded.ApiKey);
        Assert.Equal("MFA via authenticator app.", loaded.Notes);
    }

    [Fact]
    public void An_api_key_alone_or_a_login_alone_is_enough()
    {
        var client = _ws.CreateClient("Acme");
        _ws.SaveLogin(client.Id, new SiteLogin { Title = "Cloud API", ApiKey = ApiKey });
        _ws.SaveLogin(client.Id, new SiteLogin { Title = "Ticketing", Username = "pat", Password = SitePassword });

        var logins = _ws.GetLogins(client.Id);
        Assert.Equal(["Cloud API", "Ticketing"], logins.Select(l => l.Title));
        Assert.Equal("", logins[0].Username);
        Assert.Equal("", logins[1].ApiKey);
    }

    [Fact]
    public void Logins_are_listed_by_title_ignoring_case()
    {
        var client = _ws.CreateClient("Acme");
        foreach (var title in new[] { "vpn", "Cloud console", "Ansible" })
            _ws.SaveLogin(client.Id, new SiteLogin { Title = title, Username = "u" });

        Assert.Equal(["Ansible", "Cloud console", "vpn"], _ws.GetLogins(client.Id).Select(l => l.Title));
    }

    [Fact]
    public void Each_client_keeps_its_own_logins()
    {
        var acme = _ws.CreateClient("Acme");
        var globex = _ws.CreateClient("Globex");
        _ws.SaveLogin(acme.Id, new SiteLogin { Title = "Portal", Username = "acme-user" });
        _ws.SaveLogin(globex.Id, new SiteLogin { Title = "Portal", Username = "globex-user" }); // same title, other client is fine

        Assert.Equal("acme-user", Assert.Single(_ws.GetLogins(acme.Id)).Username);
        Assert.Equal("globex-user", Assert.Single(_ws.GetLogins(globex.Id)).Username);
    }

    [Fact]
    public void Secrets_never_appear_on_disk_or_in_the_client_file()
    {
        var client = _ws.CreateClient("Acme");
        _ws.SaveLogin(client.Id, Portal());

        var files = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith("logins.pfr", StringComparison.Ordinal));
        foreach (var secret in new[] { "Acme VPN portal", "vpn.acme.test", "pfr-engineer", SitePassword, ApiKey, "authenticator" })
        {
            Assert.All(files, f => Assert.DoesNotContain(secret, f));                                            // paths
            Assert.All(files, f => Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(f)))); // contents
        }

        // Listing clients reads client.pfr only, so it never touches a secret.
        var clientJson = Encoding.UTF8.GetString(_ws.Store.Read($"clients/{client.Id}/client.pfr"));
        Assert.DoesNotContain(SitePassword, clientJson);
        Assert.DoesNotContain(ApiKey, clientJson);
    }

    [Fact]
    public void Logins_are_readable_again_after_reopening_with_the_master_password()
    {
        var client = _ws.CreateClient("Acme");
        _ws.SaveLogin(client.Id, Portal());
        _ws.Store.Lock();

        var store = new SecureStore(_dir);
        Assert.True(store.Unlock(Password));
        Assert.Equal(ApiKey, Assert.Single(new ClientWorkspace(store).GetLogins(client.Id)).ApiKey);
    }

    [Fact]
    public void A_title_is_required_and_unique_per_client_ignoring_case()
    {
        var client = _ws.CreateClient("Acme");
        Assert.Throws<ArgumentException>(() => _ws.SaveLogin(client.Id, new SiteLogin { Username = "x" }));
        Assert.Throws<ArgumentException>(() => _ws.SaveLogin(client.Id, new SiteLogin { Title = "   " }));
        Assert.Empty(_ws.GetLogins(client.Id));

        _ws.SaveLogin(client.Id, new SiteLogin { Title = "Portal" });
        var ex = Assert.Throws<ArgumentException>(() => _ws.SaveLogin(client.Id, new SiteLogin { Title = " PORTAL ", Password = SitePassword }));
        Assert.Contains("already has a login titled", ex.Message);
        Assert.DoesNotContain(SitePassword, ex.Message);
        Assert.Single(_ws.GetLogins(client.Id));
    }

    [Fact]
    public void Editing_replaces_the_login_and_may_keep_or_change_its_own_title()
    {
        var client = _ws.CreateClient("Acme");
        var saved = _ws.SaveLogin(client.Id, Portal());

        var edit = _ws.GetLogins(client.Id).Single();
        edit.Password = "new password";
        _ws.SaveLogin(client.Id, edit); // same title as itself is not a clash
        edit.Title = "Acme VPN";
        _ws.SaveLogin(client.Id, edit);

        var loaded = Assert.Single(_ws.GetLogins(client.Id));
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("Acme VPN", loaded.Title);
        Assert.Equal("new password", loaded.Password);
        Assert.Equal(ApiKey, loaded.ApiKey); // untouched fields stay
    }

    [Fact]
    public void Text_is_trimmed_but_a_password_is_kept_exactly()
    {
        var client = _ws.CreateClient("Acme");
        var saved = _ws.SaveLogin(client.Id, new SiteLogin
        {
            Title = "  Portal ",
            Url = " vpn.acme.test ",
            Username = " pat ",
            Password = " has spaces ",
            ApiKey = "  key-123\n",
            Notes = "  note  ",
        });

        Assert.Equal(("Portal", "vpn.acme.test", "pat"), (saved.Title, saved.Url, saved.Username));
        Assert.Equal(" has spaces ", saved.Password);
        Assert.Equal("key-123", saved.ApiKey);
        Assert.Equal("note", saved.Notes);
    }

    [Fact]
    public void Deleting_a_login_removes_only_that_one()
    {
        var client = _ws.CreateClient("Acme");
        var first = _ws.SaveLogin(client.Id, new SiteLogin { Title = "One", Username = "a" });
        var second = _ws.SaveLogin(client.Id, new SiteLogin { Title = "Two", Username = "b" });

        _ws.DeleteLogin(client.Id, first.Id);

        Assert.Equal(second.Id, Assert.Single(_ws.GetLogins(client.Id)).Id);
        _ws.DeleteLogin(client.Id, first.Id); // deleting again is harmless
        _ws.DeleteLogin(client.Id, "no-such-id");
        Assert.Single(_ws.GetLogins(client.Id));
    }

    [Fact]
    public void Saving_for_a_client_that_does_not_exist_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _ws.SaveLogin("no-such-client", Portal()));
        Assert.False(Directory.Exists(Path.Combine(_dir, "clients", "no-such-client")));
    }

    [Fact]
    public void Deleting_a_client_deletes_its_logins_too()
    {
        var client = _ws.CreateClient("Acme");
        _ws.SaveLogin(client.Id, Portal());

        _ws.DeleteClient(client.Id);

        Assert.Empty(Directory.GetFiles(_dir, "logins.pfr", SearchOption.AllDirectories));
    }

    [Fact]
    public void Printing_a_login_never_reveals_its_secrets()
    {
        var text = Portal().ToString();
        Assert.Equal("Acme VPN portal", text);
        Assert.DoesNotContain(SitePassword, text);
        Assert.DoesNotContain(ApiKey, text);
    }
}
