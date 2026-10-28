using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Threading;
using Pfr.Core.Clients;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Wapi;
using Pfr.Core.Wapi;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class GridConnectionViewTests
{
    private const string Password = "correct horse battery";
    private const string RoSecret = "Ro-Sup3rSecret!";
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        if (ScreenshotDir is null) return;
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"));
    }

    /// <summary>A store with one client and the given (empty) grids.</summary>
    private static (string Folder, Client Client) StoreWithGrids(params string[] grids)
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);
        var client = ws.CreateClient("Acme Corp");
        foreach (var grid in grids) ws.CreateGrid(client.Id, grid);
        store.Lock();
        return (folder, client);
    }

    private static (MainWindow Window, ClientDetailView Detail) OpenClient(string folder, Client client)
    {
        var window = TestWindows.New(folder);
        window.Show();
        Assert.True(window.Clients.TryUnlock(Password));
        window.Clients.SelectClient(client.Id);
        var detail = Assert.IsType<ClientDetailView>(window.Clients.CurrentDetail);
        detail.SelectTab(1);
        return (window, detail);
    }

    [AvaloniaFact]
    public void Connection_is_saved_per_grid_and_shown_when_that_grid_is_selected()
    {
        var (folder, client) = StoreWithGrids("Production", "Lab");
        var (window, detail) = OpenClient(folder, client);
        var ws = window.Clients.Workspace!;
        var production = detail.Grids.Single(g => g.Name == "Production");
        var lab = detail.Grids.Single(g => g.Name == "Lab");
        var editor = detail.ConnectionEditor;

        detail.SelectGrid(production.Id);
        Assert.Equal("", editor.Address.Text);

        editor.Address.Text = "gm.acme.test";
        editor.Port.Text = "8443";
        editor.WapiVersion.Text = "v2.12";
        editor.ReadOnlyUser.Text = "pfr-ro";
        editor.ReadOnlyPassword.Text = RoSecret;
        editor.ReadWriteUser.Text = "pfr-rw";
        editor.ReadWritePassword.Text = "Rw-Sup3rSecret!";
        Assert.True(editor.Save());
        Assert.Equal("Connection saved.", editor.MessageText);
        Snapshot(window, "30-grid-connection");

        var saved = ws.GetConnection(client.Id, production.Id)!;
        Assert.Equal(("gm.acme.test", 8443, "v2.12"), (saved.Address, saved.Port, saved.WapiVersion));
        Assert.Equal(("pfr-ro", RoSecret), (saved.ReadOnly!.Username, saved.ReadOnly.Password));
        Assert.Equal("pfr-rw", saved.ReadWrite?.Username);
        Assert.False(ws.HasConnection(client.Id, lab.Id));

        // Another grid shows an empty form; coming back shows what was saved.
        detail.SelectGrid(lab.Id);
        Assert.Equal("", editor.Address.Text);
        Assert.Equal("", editor.ReadOnlyUser.Text);
        Assert.Equal("", editor.MessageText);

        detail.SelectGrid(production.Id);
        Assert.Equal("gm.acme.test", editor.Address.Text);
        Assert.Equal("8443", editor.Port.Text);
        Assert.Equal("pfr-ro", editor.ReadOnlyUser.Text);
        Assert.Equal(RoSecret, editor.ReadOnlyPassword.Text);
    }

    [AvaloniaFact]
    public void Passwords_are_masked_until_show_passwords_is_ticked()
    {
        var (folder, client) = StoreWithGrids("Production");
        var (_, detail) = OpenClient(folder, client);
        detail.SelectGrid(detail.Grids.Single().Id);
        var editor = detail.ConnectionEditor;

        Assert.False(editor.PasswordsRevealed);
        Assert.Equal('•', editor.ReadOnlyPassword.PasswordChar);
        Assert.Equal('•', editor.ReadWritePassword.PasswordChar);

        editor.PasswordsRevealed = true;
        Assert.Equal('\0', editor.ReadOnlyPassword.PasswordChar);
        Assert.Equal('\0', editor.ReadWritePassword.PasswordChar);

        editor.PasswordsRevealed = false;
        Assert.Equal('•', editor.ReadOnlyPassword.PasswordChar);
    }

    [AvaloniaFact]
    public void Unusable_input_shows_a_message_and_saves_nothing()
    {
        var (folder, client) = StoreWithGrids("Production");
        var (window, detail) = OpenClient(folder, client);
        var grid = detail.Grids.Single();
        detail.SelectGrid(grid.Id);
        var editor = detail.ConnectionEditor;
        var ws = window.Clients.Workspace!;

        Assert.False(editor.Save());
        Assert.Contains("needs an address", editor.MessageText);

        editor.Address.Text = "gm.acme.test";
        editor.Port.Text = "abc";
        Assert.False(editor.Save());
        Assert.Contains("not a valid port", editor.MessageText);

        editor.Port.Text = "";
        editor.ReadOnlyUser.Text = "pfr-ro"; // no password
        Assert.False(editor.Save());
        Assert.Contains("read-only account needs both", editor.MessageText);
        Assert.DoesNotContain(RoSecret, editor.MessageText);

        Assert.False(ws.HasConnection(client.Id, grid.Id));
        Snapshot(window, "31-grid-connection-error");
    }

    [AvaloniaFact]
    public void A_pasted_url_is_tidied_and_an_empty_port_means_443()
    {
        var (folder, client) = StoreWithGrids("Production");
        var (_, detail) = OpenClient(folder, client);
        detail.SelectGrid(detail.Grids.Single().Id);
        var editor = detail.ConnectionEditor;

        editor.Address.Text = "https://gm.acme.test/";
        editor.Port.Text = "";
        Assert.True(editor.Save());

        Assert.Equal("gm.acme.test", editor.Address.Text);
        Assert.Equal("443", editor.Port.Text);
    }

    [AvaloniaFact]
    public void Removing_the_connection_clears_the_form_and_keeps_the_grid()
    {
        var (folder, client) = StoreWithGrids("Production");
        var (window, detail) = OpenClient(folder, client);
        var grid = detail.Grids.Single();
        detail.SelectGrid(grid.Id);
        var editor = detail.ConnectionEditor;
        var ws = window.Clients.Workspace!;

        editor.Address.Text = "gm.acme.test";
        editor.ReadOnlyUser.Text = "pfr-ro";
        editor.ReadOnlyPassword.Text = RoSecret;
        Assert.True(editor.Save());
        Assert.True(ws.HasConnection(client.Id, grid.Id));

        editor.Remove();

        Assert.False(ws.HasConnection(client.Id, grid.Id));
        Assert.Equal("", editor.Address.Text);
        Assert.Equal("", editor.ReadOnlyPassword.Text);
        Assert.Single(ws.GetClient(client.Id).Grids);
    }

    [AvaloniaFact]
    public void A_client_without_grids_has_nothing_to_save_a_connection_to()
    {
        var (folder, client) = StoreWithGrids();
        var (_, detail) = OpenClient(folder, client);

        detail.ConnectionEditor.Address.Text = "gm.acme.test";
        Assert.False(detail.ConnectionEditor.Save());
        Assert.Contains("Select a grid", detail.ConnectionEditor.MessageText);
    }

    // ---- WAPI: test connection and pull a backup ---------------------------------------

    private static void FillConnection(GridConnectionEditor editor, FakeWapiServer server)
    {
        editor.Address.Text = "127.0.0.1";
        editor.Port.Text = server.Port.ToString();
        editor.ReadOnlyUser.Text = server.User;
        editor.ReadOnlyPassword.Text = server.Password;
    }

    [AvaloniaFact]
    public async Task Test_connection_shows_an_unknown_certificate_and_works_once_it_is_trusted()
    {
        using var server = new FakeWapiServer();
        var (folder, client) = StoreWithGrids("Production");
        var (window, detail) = OpenClient(folder, client);
        var grid = detail.Grids.Single();
        detail.SelectGrid(grid.Id);
        var editor = detail.ConnectionEditor;
        FillConnection(editor, server);

        Assert.False(await editor.TestAsync());
        var pending = Assert.IsType<WapiCertificateException>(editor.PendingCertificate);
        Assert.Equal(CertificateProblem.NotPinned, pending.Problem);
        Assert.Equal(server.Fingerprint, pending.ObservedFingerprint);
        Assert.Empty(server.Requests); // nothing, so no user id or password, reached the grid
        Snapshot(window, "34-trust-certificate");

        Assert.True(editor.TrustPending());
        Assert.Null(editor.PendingCertificate);
        var saved = window.Clients.Workspace!.GetConnection(client.Id, grid.Id)!;
        Assert.Equal(server.Fingerprint, saved.CertificateFingerprint);
        Assert.Equal(CertificatePin.Format(server.Fingerprint), editor.FingerprintBox.Text);

        Assert.True(await editor.TestAsync());
        Assert.Contains("Lab Grid", editor.MessageText);
        Assert.Contains("v2.12", editor.MessageText);
    }

    [AvaloniaFact]
    public async Task A_changed_certificate_is_refused_and_only_replaced_when_confirmed()
    {
        using var server = new FakeWapiServer();
        var (folder, client) = StoreWithGrids("Production");
        var (window, detail) = OpenClient(folder, client);
        var grid = detail.Grids.Single();
        detail.SelectGrid(grid.Id);
        var editor = detail.ConnectionEditor;
        var oldPin = new string('0', 64);
        FillConnection(editor, server);
        editor.FingerprintBox.Text = oldPin;

        Assert.False(await editor.TestAsync());
        var pending = Assert.IsType<WapiCertificateException>(editor.PendingCertificate);
        Assert.Equal(CertificateProblem.Mismatch, pending.Problem);
        Assert.Equal(oldPin, pending.PinnedFingerprint);
        Assert.Empty(server.Requests);
        Snapshot(window, "35-certificate-changed");

        // Refusing leaves the old pin in place; only an explicit decision replaces it.
        var ws = window.Clients.Workspace!;
        Assert.Equal(oldPin, ws.GetConnection(client.Id, grid.Id)!.CertificateFingerprint);
        Assert.True(editor.TrustPending());
        Assert.Equal(server.Fingerprint, ws.GetConnection(client.Id, grid.Id)!.CertificateFingerprint);
    }

    [AvaloniaFact]
    public async Task Pull_backup_saves_a_snapshot_and_refreshes_the_page()
    {
        using var server = new FakeWapiServer();
        var (folder, client) = StoreWithGrids("Production");
        var (window, detail) = OpenClient(folder, client);
        var grid = detail.Grids.Single();
        detail.SelectGrid(grid.Id);
        var editor = detail.ConnectionEditor;
        FillConnection(editor, server);
        editor.FingerprintBox.Text = CertificatePin.Format(server.Fingerprint);

        var added = new List<SnapshotInfo>();
        editor.SnapshotAdded += (_, e) => added.Add(e.Snapshot);

        Assert.True(await editor.PullBackupAsync("Pulled over WAPI"));

        var snapshot = Assert.Single(Assert.Single(window.Clients.Workspace!.GetClient(client.Id).Grids).Snapshots);
        Assert.Equal(("Lab Grid", "Pulled over WAPI"), (snapshot.GridName, snapshot.Label));
        Assert.Equal(server.BackupSha256, snapshot.SourceSha256);
        Assert.Equal(snapshot.Id, Assert.Single(added).Id);
        Assert.Contains("Saved a snapshot", editor.MessageText);
        Assert.Equal(snapshot.Id, Assert.Single(detail.Grids.Single().Snapshots).Id); // the page itself was refreshed
        Snapshot(window, "36-pulled-backup");
    }

    [AvaloniaFact]
    public async Task Connecting_needs_a_read_only_account()
    {
        var (folder, client) = StoreWithGrids("Production");
        var (_, detail) = OpenClient(folder, client);
        detail.SelectGrid(detail.Grids.Single().Id);
        var editor = detail.ConnectionEditor;
        editor.Address.Text = "127.0.0.1";

        Assert.False(await editor.TestAsync());
        Assert.Contains("read-only account", editor.MessageText);
        Assert.False(await editor.PullBackupAsync());
    }
}
