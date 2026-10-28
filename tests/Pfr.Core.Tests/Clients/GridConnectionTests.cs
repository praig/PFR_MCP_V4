using System.Text;
using Pfr.Core.Clients;
using Pfr.Core.Storage;

namespace Pfr.Core.Tests.Clients;

public sealed class GridConnectionTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string RoSecret = "Ro-Sup3rSecret!";
    private const string RwSecret = "Rw-Sup3rSecret!";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pfr-conn-{Guid.NewGuid():N}");
    private readonly ClientWorkspace _ws;

    public GridConnectionTests()
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

    private (Client Client, ClientGrid Grid) ClientWithGrid(string grid = "Production")
    {
        var client = _ws.CreateClient("Acme");
        return (client, _ws.CreateGrid(client.Id, grid));
    }

    private static GridConnection Sample() => new()
    {
        Address = "gm.acme.test",
        Port = 8443,
        WapiVersion = "v2.12",
        ReadOnly = new Credential { Username = "pfr-ro", Password = RoSecret },
        ReadWrite = new Credential { Username = "pfr-rw", Password = RwSecret },
    };

    [Fact]
    public void A_grid_has_no_connection_until_one_is_saved()
    {
        var (client, grid) = ClientWithGrid();
        Assert.Null(_ws.GetConnection(client.Id, grid.Id));
        Assert.False(_ws.HasConnection(client.Id, grid.Id));
    }

    [Fact]
    public void Connection_with_both_accounts_round_trips()
    {
        var (client, grid) = ClientWithGrid();
        _ws.SaveConnection(client.Id, grid.Id, Sample());

        Assert.True(_ws.HasConnection(client.Id, grid.Id));
        var loaded = _ws.GetConnection(client.Id, grid.Id)!;
        Assert.Equal("gm.acme.test", loaded.Address);
        Assert.Equal(8443, loaded.Port);
        Assert.Equal("v2.12", loaded.WapiVersion);
        Assert.Equal(("pfr-ro", RoSecret), (loaded.ReadOnly!.Username, loaded.ReadOnly.Password));
        Assert.Equal(("pfr-rw", RwSecret), (loaded.ReadWrite!.Username, loaded.ReadWrite.Password));
        Assert.Equal("https://gm.acme.test:8443", loaded.BaseUrl);
    }

    [Fact]
    public void Connection_is_readable_again_after_reopening_with_the_master_password()
    {
        var (client, grid) = ClientWithGrid();
        _ws.SaveConnection(client.Id, grid.Id, Sample());
        _ws.Store.Lock();

        var store = new SecureStore(_dir);
        Assert.True(store.Unlock(Password));
        var loaded = new ClientWorkspace(store).GetConnection(client.Id, grid.Id);
        Assert.Equal(RoSecret, loaded?.ReadOnly?.Password);
    }

    [Fact]
    public void Addresses_user_ids_and_passwords_never_appear_on_disk()
    {
        var (client, grid) = ClientWithGrid();
        _ws.SaveConnection(client.Id, grid.Id, Sample());

        var files = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories);
        Assert.Contains(files, f => f.EndsWith("connection.pfr", StringComparison.Ordinal));
        foreach (var secret in new[] { "gm.acme.test", "pfr-ro", "pfr-rw", RoSecret, RwSecret })
        {
            Assert.All(files, f => Assert.DoesNotContain(secret, f));                                        // paths
            Assert.All(files, f => Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(f)))); // contents
        }
    }

    [Fact]
    public void Each_grid_keeps_its_own_connection_and_the_client_file_holds_none()
    {
        var (client, production) = ClientWithGrid();
        var lab = _ws.CreateGrid(client.Id, "Lab");
        _ws.SaveConnection(client.Id, production.Id, Sample());
        var other = Sample();
        other.Address = "lab-gm.acme.test";
        other.Port = 443;
        _ws.SaveConnection(client.Id, lab.Id, other);

        Assert.Equal("gm.acme.test", _ws.GetConnection(client.Id, production.Id)!.Address);
        Assert.Equal("lab-gm.acme.test", _ws.GetConnection(client.Id, lab.Id)!.Address);

        // The connection lives in its own file, so listing clients never loads a password.
        var clientFile = Path.Combine(_dir, "clients", client.Id, "client.pfr");
        Assert.DoesNotContain(RoSecret, Encoding.UTF8.GetString(_ws.Store.Read($"clients/{client.Id}/client.pfr")));
        Assert.True(File.Exists(clientFile));
    }

    [Fact]
    public void Saving_again_replaces_the_connection()
    {
        var (client, grid) = ClientWithGrid();
        _ws.SaveConnection(client.Id, grid.Id, Sample());

        var changed = _ws.GetConnection(client.Id, grid.Id)!;
        changed.ReadWrite = null;
        changed.Port = 443;
        _ws.SaveConnection(client.Id, grid.Id, changed);

        var loaded = _ws.GetConnection(client.Id, grid.Id)!;
        Assert.Null(loaded.ReadWrite);
        Assert.Equal("https://gm.acme.test", loaded.BaseUrl); // port 443 is left out
        Assert.Equal("pfr-ro", loaded.ReadOnly?.Username);
    }

    [Fact]
    public void Pasted_values_are_tidied()
    {
        var (client, grid) = ClientWithGrid();

        var url = _ws.SaveConnection(client.Id, grid.Id, new GridConnection { Address = "  https://GM.Acme.test/ ", WapiVersion = "auto" });
        Assert.Equal("GM.Acme.test", url.Address);
        Assert.Null(url.WapiVersion);

        var hostAndPort = _ws.SaveConnection(client.Id, grid.Id, new GridConnection { Address = "10.0.0.5:8443" });
        Assert.Equal(("10.0.0.5", 8443), (hostAndPort.Address, hostAndPort.Port));

        var ipv6 = _ws.SaveConnection(client.Id, grid.Id, new GridConnection { Address = "fd00::1" });
        Assert.Equal("fd00::1", ipv6.Address);
        Assert.Equal("https://[fd00::1]", ipv6.BaseUrl);
    }

    [Theory]
    [InlineData("", 443, null, "needs an address")]
    [InlineData("   ", 443, null, "needs an address")]
    [InlineData("gm acme", 443, null, "host name or IP address only")]
    [InlineData("gm.acme.test/wapi/v2.12", 443, null, "host name or IP address only")]
    [InlineData("admin@gm.acme.test", 443, null, "host name or IP address only")]
    [InlineData("gm.acme.test", 0, null, "port must be between")]
    [InlineData("gm.acme.test", 70000, null, "port must be between")]
    [InlineData("gm.acme.test", 443, "two", "WAPI version")]
    public void Unusable_values_are_refused_and_nothing_is_stored(string address, int port, string? version, string expected)
    {
        var (client, grid) = ClientWithGrid();
        var ex = Assert.Throws<ArgumentException>(() =>
            _ws.SaveConnection(client.Id, grid.Id, new GridConnection { Address = address, Port = port, WapiVersion = version }));

        Assert.Contains(expected, ex.Message);
        Assert.False(_ws.HasConnection(client.Id, grid.Id));
    }

    [Fact]
    public void An_account_needs_both_user_id_and_password_and_an_empty_one_is_dropped()
    {
        var (client, grid) = ClientWithGrid();

        var noPassword = new GridConnection { Address = "gm", ReadOnly = new Credential { Username = "pfr-ro" } };
        Assert.Contains("read-only account needs both", Assert.Throws<ArgumentException>(() =>
            _ws.SaveConnection(client.Id, grid.Id, noPassword)).Message);

        var noUser = new GridConnection { Address = "gm", ReadWrite = new Credential { Password = RwSecret } };
        var ex = Assert.Throws<ArgumentException>(() => _ws.SaveConnection(client.Id, grid.Id, noUser));
        Assert.Contains("read-write account needs both", ex.Message);
        Assert.DoesNotContain(RwSecret, ex.Message); // errors never echo a password

        var blankRw = new GridConnection
        {
            Address = "gm",
            ReadOnly = new Credential { Username = " pfr-ro ", Password = " has spaces " },
            ReadWrite = new Credential(),
        };
        var saved = _ws.SaveConnection(client.Id, grid.Id, blankRw);
        Assert.Null(saved.ReadWrite);
        Assert.Equal("pfr-ro", saved.ReadOnly!.Username);          // user id is trimmed
        Assert.Equal(" has spaces ", saved.ReadOnly.Password);     // a password is kept exactly
    }

    [Fact]
    public void An_address_alone_can_be_saved()
    {
        var (client, grid) = ClientWithGrid();
        var saved = _ws.SaveConnection(client.Id, grid.Id, new GridConnection { Address = "gm.acme.test" });
        Assert.Null(saved.ReadOnly);
        Assert.Null(saved.ReadWrite);
        Assert.Equal(GridConnection.DefaultPort, _ws.GetConnection(client.Id, grid.Id)!.Port);
    }

    [Fact]
    public void Saving_for_a_grid_that_does_not_exist_is_refused()
    {
        var client = _ws.CreateClient("Acme");
        Assert.Throws<ArgumentException>(() => _ws.SaveConnection(client.Id, "no-such-grid", Sample()));
        Assert.False(Directory.Exists(Path.Combine(_dir, "clients", client.Id, "grids")));
    }

    [Fact]
    public void Removing_a_connection_leaves_the_grid_and_its_snapshots()
    {
        var (client, grid) = ClientWithGrid();
        _ws.SaveConnection(client.Id, grid.Id, Sample());

        _ws.DeleteConnection(client.Id, grid.Id);

        Assert.False(_ws.HasConnection(client.Id, grid.Id));
        Assert.Single(_ws.GetClient(client.Id).Grids);
        _ws.DeleteConnection(client.Id, grid.Id); // removing again is harmless
    }

    [Fact]
    public void Deleting_a_grid_or_a_client_deletes_its_connection_too()
    {
        var (client, grid) = ClientWithGrid();
        _ws.SaveConnection(client.Id, grid.Id, Sample());

        _ws.DeleteGrid(client.Id, grid.Id);
        Assert.False(_ws.HasConnection(client.Id, grid.Id));
        Assert.False(Directory.Exists(Path.Combine(_dir, "clients", client.Id, "grids", grid.Id)));

        var second = _ws.CreateGrid(client.Id, "Second");
        _ws.SaveConnection(client.Id, second.Id, Sample());
        _ws.DeleteClient(client.Id);
        Assert.Empty(Directory.GetFiles(_dir, "connection.pfr", SearchOption.AllDirectories));
    }

    [Fact]
    public void Printing_a_connection_or_account_never_reveals_a_password()
    {
        var connection = Sample();
        Assert.DoesNotContain(RoSecret, connection.ToString());
        Assert.DoesNotContain(RwSecret, connection.ToString());
        Assert.Equal("pfr-ro", connection.ReadOnly!.ToString());
    }
}
