using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core;
using Pfr.Core.Clients;
using Pfr.Core.Nios.Model;
using Pfr.Core.Storage;
using Pfr.Core.Tests.Ipam;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Controls;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

/// <summary>
/// The window must stay free while slow work runs (spec §8). These tests hold the work open on a background thread and
/// check that the UI thread is not stuck behind it: the call comes back at once, jobs posted to the window still run,
/// the busy bar shows, and a result that is no longer wanted is dropped.
/// </summary>
public class ResponsivenessTests
{
    private const string Password = "correct horse battery";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static string WriteBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-busy-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    private static GridInventory GridWithMembers(string name, int members)
    {
        var inventory = IpamFixtures.Grid(name).AddNet("10.0.0.0", 24);
        for (var i = 0; i < members; i++) inventory.Members.Add(new GridMember { Oid = i, HostName = $"m{i}.{name}.test" });
        return inventory;
    }

    // ---- Building a grid's tables ----

    [AvaloniaFact]
    public async Task The_tables_of_a_grid_are_built_in_the_background_while_the_window_keeps_working()
    {
        var window = TestWindows.New();
        window.Show();
        var view = window.Inventory;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var task = view.ShowInventoryAsync(GridWithMembers("Lab", 2), inventory =>
        {
            started.Set();
            release.Wait(Patience); // the build is stuck here until the test lets it go
            return InventoryView.Prepare(inventory);
        });

        Assert.True(started.Wait(Patience));          // it is running on another thread...
        Assert.False(task.IsCompleted);               // ...and the call has already come back
        Assert.True(view.IsPreparing);
        Assert.Empty(view.SectionNames);

        var ran = false;
        Dispatcher.UIThread.Post(() => ran = true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ran);                             // the window still processes its own work meanwhile

        release.Set();
        await task;

        Assert.False(view.IsPreparing);
        Assert.Equal(9, view.SectionNames.Count);     // Overview and the eight tables
        Assert.Equal("Overview", view.SectionNames[0]);
    }

    [AvaloniaFact]
    public async Task A_grid_opened_later_replaces_one_still_being_prepared_and_the_old_one_never_reappears()
    {
        var window = TestWindows.New();
        window.Show();
        var view = window.Inventory;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var slow = view.ShowInventoryAsync(GridWithMembers("Old", 1), inventory =>
        {
            started.Set();
            release.Wait(Patience);
            return InventoryView.Prepare(inventory);
        });
        Assert.True(started.Wait(Patience));

        await view.ShowInventoryAsync(GridWithMembers("New", 3)); // quick, and opened after
        release.Set();
        await slow;                                               // the old one finishes last

        view.SelectSection("Members");
        Assert.Equal(3, Assert.IsType<FilterableTable>(view.CurrentPage).VisibleRowCount);
        Assert.False(view.IsPreparing);
    }

    [AvaloniaFact]
    public void A_section_gets_its_grid_control_the_first_time_it_is_opened_and_keeps_it()
    {
        var window = TestWindows.New();
        window.Show();
        var view = window.Inventory;

        view.ShowInventory(GridWithMembers("Lab", 2));
        Assert.IsNotType<FilterableTable>(view.CurrentPage); // the overview, not a table

        view.SelectSection("Members");
        var members = Assert.IsType<FilterableTable>(view.CurrentPage);
        view.SelectSection("Overview");
        view.SelectSection("Members");

        Assert.Same(members, view.CurrentPage);
        Assert.Equal(2, members.VisibleRowCount);
    }

    [AvaloniaFact]
    public async Task Emptying_the_view_while_a_grid_is_being_prepared_drops_that_grid()
    {
        var window = TestWindows.New();
        window.Show();
        var view = window.Inventory;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var task = view.ShowInventoryAsync(GridWithMembers("Lab", 2), inventory =>
        {
            started.Set();
            release.Wait(Patience);
            return InventoryView.Prepare(inventory);
        });
        Assert.True(started.Wait(Patience));

        view.ShowEmpty("Client data is locked.");
        Assert.False(view.IsPreparing);
        release.Set();
        await task; // it finishes, but its tables are not shown

        Assert.Empty(view.SectionNames);
        Assert.False(view.IsPreparing);
    }

    // ---- Opening, saving and loading in the window ----

    [AvaloniaFact]
    public async Task Loading_a_backup_shows_the_busy_bar_until_the_grid_is_on_screen()
    {
        var window = TestWindows.New();
        window.Show();
        var bar = window.FindControl<ProgressBar>("BusyBar")!;
        var path = WriteBackup();
        try
        {
            Assert.False(window.IsBusy);
            Assert.False(bar.IsVisible);

            var load = window.LoadBackupAsync(path);

            Assert.True(window.IsBusy);                // the busy bar went on before anything was awaited
            Assert.True(bar.IsVisible);
            Assert.Contains("Reading", window.StatusMessage);

            await load;

            Assert.False(window.IsBusy);
            Assert.False(bar.IsVisible);
            Assert.Equal(9, window.Inventory.SectionNames.Count);
            Assert.Contains("Loaded", window.StatusMessage);
            Assert.True(window.FindControl<Button>("OpenButton")!.IsEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Overlapping_work_keeps_the_busy_bar_on_until_the_last_piece_ends()
    {
        var window = TestWindows.New();
        window.Show();
        var bar = window.FindControl<ProgressBar>("BusyBar")!;

        var first = window.BeginBusy("one");
        var second = window.BeginBusy("two");
        Assert.True(bar.IsVisible);

        first.Dispose();
        first.Dispose(); // ending it twice counts once
        Assert.True(window.IsBusy);
        Assert.True(bar.IsVisible);

        second.Dispose();
        Assert.False(window.IsBusy);
        Assert.False(bar.IsVisible);
        await Task.CompletedTask;
    }

    private static (MainWindow Window, Client Client) UnlockedWithClient(Action<ClientWorkspace, Client>? seed = null)
    {
        var folder = TestWindows.NewDataFolder();
        var store = new SecureStore(folder);
        store.Initialize(Password, TestWindows.Iterations);
        var workspace = new ClientWorkspace(store);
        var client = workspace.CreateClient("Acme Corp");
        seed?.Invoke(workspace, client);
        store.Lock();

        var window = TestWindows.New(folder);
        window.Show();
        Assert.True(window.Clients.TryUnlock(Password));
        return (window, client);
    }

    [AvaloniaFact]
    public async Task Saving_a_grid_to_a_client_happens_in_the_background_and_does_not_rebuild_the_tables()
    {
        var (window, client) = UnlockedWithClient();
        var path = WriteBackup();
        try
        {
            await window.LoadBackupAsync(path);
            var button = window.FindControl<Button>("SaveToClientButton")!;
            var tablesBefore = window.Inventory.CurrentPage;

            var save = window.SaveLoadedToClientAsync(client.Id);

            Assert.True(window.IsBusy);
            Assert.False(button.IsEnabled);            // it cannot be started twice
            Assert.Contains("Saving", window.StatusMessage);

            await save;

            Assert.False(window.IsBusy);
            Assert.True(button.IsEnabled);
            Assert.Equal("Lab Grid", window.LoadedSnapshot?.Grid.Name);
            Assert.Single(window.Clients.Workspace!.GetClient(client.Id).AllSnapshots);
            Assert.Contains("Saved", window.StatusMessage);
            Assert.Same(tablesBefore, window.Inventory.CurrentPage); // the same grid: its tables were kept, not rebuilt
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Saving_with_no_client_chosen_says_so_and_starts_nothing()
    {
        var (window, _) = UnlockedWithClient();
        var path = WriteBackup();
        try
        {
            await window.LoadBackupAsync(path);
            window.FindControl<ComboBox>("SaveClientPicker")!.SelectedItem = null;

            await window.SaveLoadedToClientAsync();

            Assert.False(window.IsBusy);
            Assert.Contains("Choose a client", window.StatusMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task Opening_a_saved_grid_shows_the_busy_bar_and_then_the_grid()
    {
        string gridId = "";
        var (window, client) = UnlockedWithClient((workspace, c) =>
        {
            workspace.AddSnapshot(c.Id, IpamFixtures.Grid("Lab").AddNet("10.1.0.0", 24), "lab.bak", 1, "lab");
            gridId = workspace.GetClient(c.Id).Grids.Single().Id;
        });

        var open = window.Clients.OpenGridAsync(client.Id, gridId);

        Assert.True(window.IsBusy);
        await open;

        Assert.False(window.IsBusy);
        Assert.Equal("Lab", window.LoadedSnapshot?.Grid.Name);
        Assert.Equal(9, window.Inventory.SectionNames.Count);
    }

    // ---- Reports ----

    [AvaloniaFact]
    public async Task A_report_chosen_and_then_replaced_before_it_finishes_never_shows_up_afterwards()
    {
        var window = TestWindows.New();
        window.Show();
        var path = WriteBackup();
        try
        {
            await window.LoadBackupAsync(path);
            window.ShowModule(AppModule.Reports);

            window.Reports.SelectReport(ReportsView.DanglingCnames);
            var cnames = window.Reports.ReportTask;            // still being worked out (or only just finished)
            window.Reports.SelectReport(ReportsView.LicenseExpiry);
            await cnames;                                      // the CNAME work ends after its page was replaced

            Assert.Null(window.Reports.CnameResult);           // it must not have come back
            Assert.NotNull(window.Reports.LicenseResult);
            Assert.NotNull(window.Reports.CurrentTable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task The_cname_report_is_worked_out_once_per_grid_however_often_its_filters_change()
    {
        var window = TestWindows.New();
        window.Show();
        var path = WriteBackup();
        try
        {
            await window.LoadBackupAsync(path);
            window.ShowModule(AppModule.Reports);
            window.Reports.SelectReport(ReportsView.DanglingCnames);
            await window.Reports.ReportTask;
            var first = window.Reports.CnameResult;
            Assert.NotNull(first);

            window.Reports.SetCnameFilters(showResolving: true, showNotVerified: true);
            await window.Reports.ReportTask;

            Assert.Same(first, window.Reports.CnameResult);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
