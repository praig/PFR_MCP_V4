using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core;
using Pfr.Core.Reports;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class ReportsViewTests
{
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    private static string BackupPath()
    {
        var real = Environment.GetEnvironmentVariable("PFR_SAMPLE_BACKUP");
        if (real is not null && File.Exists(real)) return real;
        var path = Path.Combine(Path.GetTempPath(), $"pfr-rep-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        if (ScreenshotDir is null) return;
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"));
    }

    [AvaloniaFact]
    public void Reports_ask_for_a_grid_when_none_is_open()
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        Snapshot(window, "40-reports-empty");

        Assert.Equal(ReportsView.LicenseExpiry, window.Reports.SelectedReport);
        Assert.Null(window.Reports.CurrentTable);
        Assert.Null(window.Reports.LicenseResult);
    }

    [AvaloniaFact]
    public async Task License_expiry_runs_on_the_open_grid_and_follows_the_window()
    {
        var window = TestWindows.New();
        window.Show();
        await window.LoadBackupAsync(BackupPath());
        window.ShowModule(AppModule.Reports);
        Snapshot(window, "41-reports-license");

        var result = window.Reports.LicenseResult!;
        Assert.Equal(90, result.WindowDays);
        Assert.Equal(result.Rows.Count, window.Reports.CurrentTable!.VisibleRowCount);
        Assert.Equal(window.LoadedInventory!.Licenses.Count, result.Rows.Count(r => r.Code.Length > 0));

        window.Reports.SetLicenseWindow(365);
        Assert.Equal(365, window.Reports.LicenseResult!.WindowDays);
        Assert.True(window.Reports.LicenseResult.Expiring >= result.Expiring);
    }

    [AvaloniaFact]
    public async Task Dangling_cnames_hide_resolving_by_default()
    {
        var window = TestWindows.New();
        window.Show();
        await window.LoadBackupAsync(BackupPath());
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.DanglingCnames);
        Snapshot(window, "42-reports-cnames");

        var result = window.Reports.CnameResult!;
        var shown = window.Reports.CurrentTable!.VisibleRowCount;
        Assert.Equal(result.Rows.Count(r => r.Outcome != CnameOutcome.Resolves), shown);

        window.Reports.SetCnameFilters(showResolving: true, showNotVerified: true);
        Assert.Equal(result.Rows.Count, window.Reports.CurrentTable!.VisibleRowCount);

        window.Reports.SetCnameFilters(showResolving: false, showNotVerified: false);
        Assert.Equal(
            result.Rows.Count(r => r.Outcome is not (CnameOutcome.Resolves or CnameOutcome.External or CnameOutcome.Delegated)
                                   || (r.Outcome != CnameOutcome.Resolves && r.TakeoverProvider is not null)),
            window.Reports.CurrentTable!.VisibleRowCount);
    }

    [AvaloniaFact]
    public async Task Explore_data_asks_for_a_grid_when_none_is_open()
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.ExploreData);
        Snapshot(window, "43-reports-explore");

        Assert.Equal(ReportsView.ExploreData, window.Reports.SelectedReport);
        Assert.Null(window.Reports.CurrentTable);
        Assert.Null(window.Reports.Explore.LastResult);
        Assert.Contains("records", window.Reports.Explore.TableNames); // the tables can be browsed before a grid is open

        Assert.False(await window.Reports.Explore.RunAsync());
        Assert.Equal("Open a grid first.", window.Reports.Explore.ErrorText);
    }
}
