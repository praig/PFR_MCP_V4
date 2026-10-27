using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Backup;
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

    private static string WriteBackup(string? xml = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-client-{Guid.NewGuid():N}.{(xml is null ? "bak" : "xml")}");
        if (xml is not null)
        {
            File.WriteAllText(path, xml);
            return path;
        }
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    private static string ActiveTab(MainWindow w) =>
        (string)((TabItem)w.FindControl<TabControl>("ModuleTabs")!.SelectedItem!).Header!;

    /// <summary>A store with one client; returns its folder and the client.</summary>
    private static (string Folder, Client Client) StoreWithClient(Action<ClientWorkspace, Client>? seed = null)
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var ws = new ClientWorkspace(store);
        var client = ws.CreateClient("Acme Corp");
        seed?.Invoke(ws, client);
        store.Lock();
        return (folder, client);
    }

    private static void SeedTwoGrids(ClientWorkspace ws, Client client)
    {
        var lab = BackupParser.Parse(OneDbFixture.XmlStream());
        ws.AddSnapshot(client.Id, lab, "lab.bak", 1, "lab", label: "Q3 review");
        var dmz = BackupParser.Parse(OneDbFixture.XmlStream());
        dmz.GridName = "DMZ Grid";
        dmz.Members.RemoveAt(0);
        ws.AddSnapshot(client.Id, dmz, "dmz.bak", 1, "dmz");
    }

    private static MainWindow Open(string folder)
    {
        var window = TestWindows.New(folder);
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void New_data_folder_shows_setup_then_locked_store_shows_unlock()
    {
        var folder = TestWindows.NewDataFolder();
        var window = Open(folder);
        Snapshot(window, "20-clients-setup");
        Assert.Null(window.Clients.Workspace);

        new SecureStore(folder).Initialize(Password, TestWindows.Iterations);
        var reopened = Open(folder);
        Snapshot(reopened, "21-clients-unlock");
        Assert.Null(reopened.Clients.Workspace);

        Assert.False(reopened.Clients.TryUnlock("wrong password"));
        Assert.True(reopened.Clients.TryUnlock(Password));
        Assert.NotNull(reopened.Clients.Workspace);
    }

    [AvaloniaFact]
    public void Lock_hides_client_data_until_unlocked_again()
    {
        var (folder, _) = StoreWithClient();
        var window = Open(folder);
        Assert.True(window.Clients.TryUnlock(Password));
        Assert.Single(window.Clients.Clients);

        window.Clients.Lock();
        Assert.Null(window.Clients.Workspace);
        Assert.Empty(window.Clients.Clients);

        Assert.True(window.Clients.TryUnlock(Password));
        Assert.Equal("Acme Corp", Assert.Single(window.Clients.Clients).Name);
    }

    [AvaloniaFact]
    public async Task Imports_go_to_their_grid_and_open_instantly()
    {
        var (folder, client) = StoreWithClient();
        var window = Open(folder);
        window.Clients.TryUnlock(Password);
        window.Clients.SelectClient(client.Id);
        var detail = Assert.IsType<ClientDetailView>(window.Clients.CurrentDetail);
        Snapshot(window, "22-client-details");

        var backup = WriteBackup();
        try
        {
            detail.SelectTab(1);
            await detail.ImportAsync(backup, "Q4 health check");
            await detail.ImportAsync(backup); // same grid again, a later snapshot
            Snapshot(window, "23-client-grids");

            var grid = Assert.Single(detail.Grids);
            Assert.Equal("Lab Grid", grid.Name);
            Assert.Equal(2, grid.Snapshots.Count);

            await detail.OpenLatestAsync(grid.Id);
            Assert.Equal("Lab Grid", window.LoadedInventory?.GridName);
            Assert.Equal((client.Id, grid.Id), (window.LoadedSnapshot?.Client.Id, window.LoadedSnapshot?.Grid.Id));
            Assert.Equal("Inventory", ActiveTab(window));
            Assert.Contains("Acme Corp › Lab Grid", window.Title);
            Assert.False(window.FindControl<Border>("SaveBar")!.IsVisible);
            Snapshot(window, "24-grid-in-inventory");
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task Header_switcher_moves_between_clients_and_grids_without_backups()
    {
        var (folder, client) = StoreWithClient(SeedTwoGrids);
        // A second client with its own grid.
        var other = new SecureStore(folder);
        other.Unlock(Password);
        var ws = new ClientWorkspace(other);
        var globex = ws.CreateClient("Globex");
        ws.AddSnapshot(globex.Id, BackupParser.Parse(OneDbFixture.XmlStream()), "globex.bak", 1, "g");
        other.Lock();

        var window = Open(folder);
        window.Clients.TryUnlock(Password);
        var picker = window.FindControl<ComboBox>("GridPicker")!;
        var choices = picker.ItemsSource!.Cast<object>().Select(c => c.ToString()).ToList();
        Assert.Equal(["Acme Corp › DMZ Grid", "Acme Corp › Lab Grid", "Globex › Lab Grid"], choices);

        await window.Clients.OpenGridAsync(client.Id, window.Clients.Workspace!.GetClient(client.Id).Grids.Single(g => g.Name == "DMZ Grid").Id);
        Assert.Equal("DMZ Grid", window.LoadedInventory!.GridName);
        Assert.Equal(2, window.LoadedInventory.Members.Count);

        await window.Clients.OpenGridAsync(globex.Id, window.Clients.Workspace!.GetClient(globex.Id).Grids.Single().Id);
        Assert.Equal("Globex", window.LoadedSnapshot!.Value.Client.Name);
        Assert.Equal("Globex › Lab Grid", picker.SelectedItem?.ToString());
        Snapshot(window, "26-switcher");
    }

    [AvaloniaFact]
    public void Client_list_shows_grids_under_each_client()
    {
        var (folder, _) = StoreWithClient(SeedTwoGrids);
        var window = Open(folder);
        window.Clients.TryUnlock(Password);
        Snapshot(window, "27-client-list-with-grids");

        Assert.Equal(["DMZ Grid", "Lab Grid"], window.Clients.Clients.Single().Grids.Select(g => g.Name).Order());
    }

    [AvaloniaFact]
    public async Task Last_viewed_grid_reopens_after_unlock()
    {
        var (folder, client) = StoreWithClient(SeedTwoGrids);
        var first = Open(folder);
        first.Clients.TryUnlock(Password);
        var dmz = first.Clients.Workspace!.GetClient(client.Id).Grids.Single(g => g.Name == "DMZ Grid");
        await first.Clients.OpenGridAsync(client.Id, dmz.Id);
        first.Close();

        var second = Open(folder);
        Assert.Null(second.LoadedInventory);
        second.Clients.TryUnlock(Password);
        for (var i = 0; i < 50 && second.LoadedInventory is null; i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("DMZ Grid", second.LoadedInventory?.GridName);
        Assert.Equal("Clients", ActiveTab(second)); // reopened quietly, without leaving Clients
        Assert.StartsWith("Reopened Acme Corp › DMZ Grid", second.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Locking_clears_a_client_grid_from_inventory()
    {
        var (folder, client) = StoreWithClient(SeedTwoGrids);
        var window = Open(folder);
        window.Clients.TryUnlock(Password);
        var lab = window.Clients.Workspace!.GetClient(client.Id).Grids.Single(g => g.Name == "Lab Grid");
        await window.Clients.OpenGridAsync(client.Id, lab.Id);
        Assert.NotNull(window.LoadedInventory);

        window.Clients.Lock();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(window.LoadedInventory);
        Assert.Empty(window.Inventory.SectionNames);
        Assert.DoesNotContain("Acme", window.Title);
        Assert.False(window.FindControl<ComboBox>("GridPicker")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Opened_backup_can_be_saved_to_a_client_from_the_save_bar()
    {
        var (folder, client) = StoreWithClient();
        var window = Open(folder);
        window.Clients.TryUnlock(Password);
        var backup = WriteBackup();
        try
        {
            await window.LoadBackupAsync(backup);
            var bar = window.FindControl<Border>("SaveBar")!;
            Assert.True(bar.IsVisible);
            Assert.Contains("(not saved)", window.HeaderText);
            Snapshot(window, "28-save-bar");

            window.SaveLoadedToClient(client.Id);

            Assert.False(bar.IsVisible);
            Assert.Equal("Lab Grid", window.LoadedSnapshot?.Grid.Name);
            var info = Assert.Single(window.Clients.Workspace!.GetClient(client.Id).AllSnapshots);
            Assert.Equal(ClientWorkspace.Sha256Of(backup), info.SourceSha256);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task Save_bar_asks_to_unlock_when_clients_are_locked()
    {
        var (folder, _) = StoreWithClient();
        var window = Open(folder);
        var backup = WriteBackup();
        try
        {
            await window.LoadBackupAsync(backup);
            Assert.True(window.FindControl<Border>("SaveBar")!.IsVisible);
            Assert.Contains("Unlock Clients", window.FindControl<TextBlock>("SaveBarText")!.Text);
            Assert.False(window.FindControl<Button>("SaveToClientButton")!.IsVisible);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task Backups_without_a_grid_name_are_refused()
    {
        var (folder, client) = StoreWithClient();
        var window = Open(folder);
        window.Clients.TryUnlock(Password);
        var unnamed = WriteBackup(OneDbFixture.Xml.Replace("VALUE=\"Lab Grid\"", "VALUE=\"\""));
        try
        {
            // Opening it: error, nothing loaded.
            await window.LoadBackupAsync(unnamed);
            Assert.Null(window.LoadedInventory);
            Assert.Contains("has no grid name", window.StatusMessage);
            Snapshot(window, "29-no-grid-name");

            // Importing it into a client: error, nothing stored.
            window.Clients.SelectClient(client.Id);
            var detail = window.Clients.CurrentDetail!;
            await detail.ImportAsync(unnamed);
            Assert.Empty(window.Clients.Workspace!.GetClient(client.Id).Grids);
        }
        finally
        {
            File.Delete(unnamed);
        }
    }

    [AvaloniaFact]
    public void Engagement_log_lists_entries_newest_first_and_links_grid_snapshots()
    {
        var (folder, client) = StoreWithClient(SeedTwoGrids);
        var window = Open(folder);
        window.Clients.TryUnlock(Password);
        var ws = window.Clients.Workspace!;
        var dmzSnapshot = ws.GetClient(client.Id).Grids.Single(g => g.Name == "DMZ Grid").Snapshots.Single();
        ws.SaveLogEntry(client.Id, new LogEntry { Date = DateTimeOffset.Now.AddDays(-3), Title = "Kick-off call", Text = "Agreed scope: DNS and DHCP health check." });
        ws.SaveLogEntry(client.Id, new LogEntry { Title = "Backups collected", Text = "Both grids, NIOS 9.0.8.", SnapshotId = dmzSnapshot.Id });

        window.Clients.RefreshClients(client.Id);
        var detail = window.Clients.CurrentDetail!;
        detail.SelectTab(2);
        Snapshot(window, "25-client-log");

        Assert.Equal(["Backups collected", "Kick-off call"], detail.LogEntries.Select(e => e.Title));
    }
}
