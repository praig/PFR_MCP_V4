using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Controls;

namespace Pfr.Gui.Tests;

public class MainWindowTests
{
    /// <summary>Set PFR_SCREENSHOT_DIR to save PNGs of each screen for visual review.</summary>
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    /// <summary>Optional real backup (PFR_SAMPLE_BACKUP) for screenshots with realistic data.</summary>
    private static string BackupPath()
    {
        var real = Environment.GetEnvironmentVariable("PFR_SAMPLE_BACKUP");
        if (real is not null && File.Exists(real)) return real;

        var path = Path.Combine(Path.GetTempPath(), $"pfr-gui-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Snapshot(Window window, string name)
    {
        Render(window);
        if (ScreenshotDir is null) return;
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"));
    }

    [AvaloniaFact]
    public void Starts_on_inventory_with_empty_state()
    {
        var window = new MainWindow();
        window.Show();
        Snapshot(window, "01-empty");

        Assert.Null(window.LoadedInventory);
        Assert.Empty(window.Inventory.SectionNames);
        Assert.Equal("Inventory", ((TabItem)window.FindControl<TabControl>("ModuleTabs")!.SelectedItem!).Header);
    }

    [AvaloniaFact]
    public async Task Loads_backup_and_shows_every_section()
    {
        var window = new MainWindow();
        window.Show();

        await window.LoadBackupAsync(BackupPath());
        Snapshot(window, "02-overview");

        Assert.NotNull(window.LoadedInventory);
        Assert.StartsWith("Loaded", window.StatusMessage);
        Assert.Equal(
            ["Overview", "Members", "DNS views", "Zones", "Networks", "DHCP ranges", "DHCP failover", "DNS records"],
            window.Inventory.SectionNames);

        var index = 3;
        foreach (var section in window.Inventory.SectionNames.Skip(1))
        {
            window.Inventory.SelectSection(section);
            Assert.Equal(section, window.Inventory.SelectedSection);
            var rows = window.Inventory.CurrentPage switch
            {
                FilterableTable t => t.VisibleRowCount,
                TreeTable t => t.VisibleRows.Count,
                var other => throw new Xunit.Sdk.XunitException($"{section}: unexpected page {other}"),
            };
            Assert.True(rows > 0, $"{section} has rows");
            Snapshot(window, $"{index++:00}-{section.Replace(' ', '-').ToLowerInvariant()}");
            if (window.Inventory.CurrentPage is TreeTable tree)
            {
                tree.SetAllExpanded(true);
                Snapshot(window, $"{index - 1:00}b-{section.Replace(' ', '-').ToLowerInvariant()}-expanded");
            }
        }
    }

    [AvaloniaFact]
    public async Task Search_filters_rows()
    {
        var window = new MainWindow();
        window.Show();
        await window.LoadBackupAsync(BackupPath());

        window.Inventory.SelectSection("Members");
        var table = Assert.IsType<FilterableTable>(window.Inventory.CurrentPage);
        var all = table.VisibleRowCount;

        table.ApplyFilter("grid master");
        Assert.InRange(table.VisibleRowCount, 1, all - 1);

        table.ApplyFilter("no-such-thing-anywhere");
        Assert.Equal(0, table.VisibleRowCount);

        table.ApplyFilter("");
        Assert.Equal(all, table.VisibleRowCount);
    }

    [AvaloniaFact]
    public async Task Bad_file_shows_error_and_keeps_window_usable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-bad-{Guid.NewGuid():N}.bak");
        await File.WriteAllTextAsync(path, "not a backup");

        var window = new MainWindow();
        window.Show();
        await window.LoadBackupAsync(path);
        Snapshot(window, "10-bad-file");

        Assert.Null(window.LoadedInventory);
        Assert.StartsWith("Could not read", window.StatusMessage);
        File.Delete(path);
    }
}
