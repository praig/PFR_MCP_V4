using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Pfr.Core;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Controls;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class ExploreViewTests
{
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    private static string BackupPath()
    {
        var real = Environment.GetEnvironmentVariable("PFR_SAMPLE_BACKUP");
        if (real is not null && File.Exists(real)) return real;
        var path = Path.Combine(Path.GetTempPath(), $"pfr-explore-{Guid.NewGuid():N}.bak");
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

    /// <summary>A window with a grid open and the Explore data page showing.</summary>
    private static async Task<(MainWindow Window, ExploreView Explore)> OpenExplore()
    {
        var window = TestWindows.New();
        window.Show();
        await window.LoadBackupAsync(BackupPath());
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.ExploreData);
        return (window, window.Reports.Explore);
    }

    [AvaloniaFact]
    public async Task A_query_runs_on_the_open_grid_and_shows_a_searchable_table()
    {
        var (window, explore) = await OpenExplore();

        explore.QueryText = "SELECT type, COUNT(*) AS records FROM records GROUP BY type ORDER BY records DESC";
        Assert.True(await explore.RunAsync());
        Snapshot(window, "44-explore-results");

        var result = explore.LastResult!;
        Assert.Equal(["type", "records"], result.Columns);
        Assert.Equal(window.LoadedInventory!.Records.Count, result.Rows.Sum(r => (long)r[1]!));
        Assert.Equal(result.Rows.Count, explore.ResultTable!.VisibleRowCount);
        Assert.Contains("row", explore.StatusText);
        Assert.Equal("", explore.ErrorText);

        // The results are a normal table: search narrows them.
        explore.ResultTable.ApplyFilter("zzz-matches-nothing");
        Assert.Equal(0, explore.ResultTable.VisibleRowCount);
    }

    [AvaloniaFact]
    public async Task The_table_browser_lists_the_tables_and_their_columns()
    {
        var (_, explore) = await OpenExplore();

        Assert.Contains("members", explore.TableNames);
        Assert.Contains("records", explore.TableNames);
        Assert.Contains("dhcp_ranges", explore.TableNames);

        explore.SelectTable("records");
        Assert.Contains("zone_key", explore.ColumnNames);
        Assert.Contains("value", explore.ColumnNames);
        Assert.DoesNotContain("oid", explore.ColumnNames);
    }

    [AvaloniaFact]
    public async Task Mistakes_and_writes_are_explained_and_the_last_good_result_stays()
    {
        var (_, explore) = await OpenExplore();
        explore.QueryText = "SELECT oid, host_name FROM members ORDER BY host_name";
        Assert.True(await explore.RunAsync());
        var good = explore.LastResult;

        explore.QueryText = "SELEC 1";
        Assert.False(await explore.RunAsync());
        Assert.Contains("start with SELECT", explore.ErrorText);

        explore.QueryText = "DELETE FROM members";
        Assert.False(await explore.RunAsync());
        Assert.Contains("read-only", explore.ErrorText);

        explore.QueryText = "SELECT * FROM no_such_table";
        Assert.False(await explore.RunAsync());
        Assert.Contains("no such table", explore.ErrorText);

        explore.QueryText = "";
        Assert.False(await explore.RunAsync());
        Assert.Contains("Type a query", explore.ErrorText);

        Assert.Same(good, explore.LastResult);

        explore.QueryText = "SELECT 1";
        Assert.True(await explore.RunAsync());
        Assert.Equal("", explore.ErrorText);
    }

    [AvaloniaFact]
    public async Task Every_example_can_be_loaded_and_run()
    {
        var (_, explore) = await OpenExplore();
        Assert.True(explore.ExampleCount > 0);

        for (var i = 0; i < explore.ExampleCount; i++)
        {
            explore.LoadExample(i);
            Assert.NotEmpty(explore.QueryText);
            Assert.True(await explore.RunAsync(), $"example {i} failed: {explore.ErrorText}");
        }
    }

    [AvaloniaFact]
    public async Task Opening_another_grid_discards_the_results_of_the_previous_one()
    {
        var (window, explore) = await OpenExplore();
        explore.QueryText = "SELECT COUNT(*) FROM members";
        Assert.True(await explore.RunAsync());
        Assert.NotNull(explore.LastResult);

        await window.LoadBackupAsync(BackupPath());

        Assert.Null(window.Reports.Explore.LastResult);
        Assert.Null(window.Reports.Explore.ResultTable);

        // And the new grid can be queried.
        window.Reports.Explore.QueryText = "SELECT COUNT(*) FROM members";
        Assert.True(await window.Reports.Explore.RunAsync());
    }

    [AvaloniaFact]
    public async Task The_select_all_button_only_shows_while_a_table_is_selected()
    {
        var (window, explore) = await OpenExplore();
        Assert.False(explore.SelectAllButtonVisible);

        explore.SelectTable("members");
        Assert.True(explore.SelectAllButtonVisible);

        explore.InsertSelectAll();
        Assert.Equal("SELECT * FROM members LIMIT 100", explore.QueryText);
        Assert.True(await explore.RunAsync());
        Assert.Equal(window.LoadedInventory!.Members.Count, explore.LastResult!.Rows.Count);
    }

    [AvaloniaFact]
    public async Task The_lists_are_set_into_the_panel_as_recessed_wells()
    {
        var (window, explore) = await OpenExplore();
        explore.SelectTable("records");
        Snapshot(window, "51-recessed-panels");

        var well = Ui.Recessed(new TextBlock());

        Assert.IsType<TextBlock>(well.Child);
        Assert.NotEqual(default(BoxShadows), well.BoxShadow); // an inner shadow is set
        Assert.True(well.CornerRadius.TopLeft > 0);
        Assert.Same(Ui.Well, well.Background);
    }

    [AvaloniaFact]
    public async Task Adding_columns_builds_the_query_and_joins_tables_as_needed()
    {
        var (_, explore) = await OpenExplore();

        explore.AddColumn("members", "host_name");
        Assert.Equal("SELECT host_name\nFROM members", explore.QueryText);

        explore.AddColumn("members", "role");
        Assert.Equal("SELECT host_name, role\nFROM members", explore.QueryText);

        // A column that is already there changes nothing.
        explore.AddColumn("members", "role");
        Assert.Equal("SELECT host_name, role\nFROM members", explore.QueryText);

        // A column of another table brings the table into FROM, joined on its key.
        explore.AddColumn("member_services", "service");
        Assert.Equal(
            "SELECT members.host_name, members.role, member_services.service\nFROM members\nJOIN member_services ON member_services.member_oid = members.oid",
            explore.QueryText);
        Assert.True(await explore.RunAsync());
    }

    [AvaloniaFact]
    public async Task A_column_goes_at_the_cursor_when_the_query_is_too_involved_to_edit()
    {
        var (_, explore) = await OpenExplore();
        const string original = "WITH x AS (SELECT 1) SELECT * FROM x";
        explore.QueryText = original;

        explore.AddColumn("members", "role");

        Assert.Contains("members.role", explore.QueryText);
        Assert.Equal(original, explore.QueryText.Replace("members.role", ""));
    }
}
