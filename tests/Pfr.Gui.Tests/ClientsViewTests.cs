using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core.Clients;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class ClientsViewTests
{
    private const string Password = "correct horse battery";
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

    private static string FixtureBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-client-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    /// <summary>A window whose store already exists and is unlocked, with one client.</summary>
    private static (MainWindow Window, Client Client) WindowWithClient()
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var client = new ClientWorkspace(store).CreateClient("Acme Corp");
        store.Lock();

        var window = TestWindows.New(folder);
        window.Show();
        Assert.True(window.Clients.TryUnlock(Password));
        return (window, client);
    }

    [AvaloniaFact]
    public void New_data_folder_shows_setup_then_locked_store_shows_unlock()
    {
        var folder = TestWindows.NewDataFolder();
        var window = TestWindows.New(folder);
        window.Show();
        Snapshot(window, "20-clients-setup");
        Assert.Null(window.Clients.Workspace);

        new SecureStore(folder).Initialize(Password, TestWindows.Iterations);
        var reopened = TestWindows.New(folder);
        reopened.Show();
        Snapshot(reopened, "21-clients-unlock");
        Assert.Null(reopened.Clients.Workspace);

        Assert.False(reopened.Clients.TryUnlock("wrong password"));
        Assert.True(reopened.Clients.TryUnlock(Password));
        Assert.NotNull(reopened.Clients.Workspace);
    }

    [AvaloniaFact]
    public void Lock_hides_client_data_until_unlocked_again()
    {
        var (window, _) = WindowWithClient();
        Assert.Single(window.Clients.Clients);

        window.Clients.Lock();
        Assert.Null(window.Clients.Workspace);
        Assert.Empty(window.Clients.Clients);

        Assert.True(window.Clients.TryUnlock(Password));
        Assert.Equal("Acme Corp", Assert.Single(window.Clients.Clients).Name);
    }

    [AvaloniaFact]
    public async Task Import_snapshot_then_open_it_in_inventory()
    {
        var (window, client) = WindowWithClient();
        window.Clients.SelectClient(client.Id);
        var detail = Assert.IsType<ClientDetailView>(window.Clients.CurrentDetail);
        Snapshot(window, "22-client-details");

        var backup = FixtureBackup();
        try
        {
            detail.SelectTab(1);
            await detail.ImportAsync(backup, "Q4 health check");
            Snapshot(window, "23-client-snapshots");

            var info = Assert.Single(detail.Snapshots);
            Assert.Equal("Q4 health check", info.Label);
            Assert.Equal("Lab Grid", info.GridName);

            await detail.OpenSnapshotAsync(info.Id);
            Assert.Equal("Lab Grid", window.LoadedInventory?.GridName);
            Assert.Equal(client.Id, window.LoadedSnapshot?.Client.Id);
            Assert.StartsWith("Acme Corp", window.HeaderText);
            Assert.Equal("Inventory", ((TabItem)window.FindControl<TabControl>("ModuleTabs")!.SelectedItem!).Header);
            Snapshot(window, "24-snapshot-in-inventory");
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task Grid_opened_in_inventory_can_be_saved_to_a_client()
    {
        var (window, client) = WindowWithClient();
        var backup = FixtureBackup();
        try
        {
            await window.LoadBackupAsync(backup);
            Assert.NotNull(window.LoadedSource);

            window.ShowModule(Pfr.Core.AppModule.ClientWorkspace);
            window.Clients.SelectClient(client.Id);
            var detail = window.Clients.CurrentDetail!;
            detail.AddCurrentGrid("From Inventory");

            var info = Assert.Single(detail.Snapshots);
            Assert.Equal(Path.GetFileName(backup), info.SourceFileName);
            Assert.Equal(ClientWorkspace.Sha256Of(backup), info.SourceSha256);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public void Engagement_log_lists_entries_newest_first()
    {
        var (window, client) = WindowWithClient();
        var ws = window.Clients.Workspace!;
        ws.SaveLogEntry(client.Id, new LogEntry { Date = DateTimeOffset.Now.AddDays(-3), Title = "Kick-off call", Text = "Agreed scope: DNS and DHCP health check." });
        ws.SaveLogEntry(client.Id, new LogEntry { Title = "Backup collected", Text = "Grid backup from GM, NIOS 9.0.8." });

        window.Clients.RefreshClients(client.Id);
        var detail = window.Clients.CurrentDetail!;
        detail.SelectTab(2);
        Snapshot(window, "25-client-log");

        Assert.Equal(["Backup collected", "Kick-off call"], detail.LogEntries.Select(e => e.Title));
    }

    [AvaloniaFact]
    public void Client_list_shows_every_client_by_name()
    {
        var (window, _) = WindowWithClient();
        window.Clients.Workspace!.CreateClient("Globex");
        window.Clients.Workspace!.CreateClient("Initech");
        window.Clients.RefreshClients();

        Assert.Equal(["Acme Corp", "Globex", "Initech"], window.Clients.Clients.Select(c => c.Name));
    }
}
