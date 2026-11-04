using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core;
using Pfr.Core.Query;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class ReportTemplateViewTests
{
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    private static string BackupPath()
    {
        var real = Environment.GetEnvironmentVariable("PFR_SAMPLE_BACKUP");
        if (real is not null && File.Exists(real)) return real;
        var path = Path.Combine(Path.GetTempPath(), $"pfr-tpl-{Guid.NewGuid():N}.bak");
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

    /// <summary>A window with a grid open and the Reports module showing Explore data.</summary>
    private static async Task<MainWindow> OpenReports(bool withGrid = true)
    {
        var window = TestWindows.New();
        window.Show();
        if (withGrid) await window.LoadBackupAsync(BackupPath());
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.ExploreData);
        return window;
    }

    private static ReportTemplate SaveDirectly(MainWindow window, string name, string sql, string description = "")
    {
        var saved = window.Reports.Templates.Save(new ReportTemplate { Name = name, Sql = sql, Description = description });
        window.Reports.RefreshTemplates();
        return saved;
    }

    private static string FilePath(MainWindow window, ReportTemplate template) =>
        Path.Combine(window.Reports.Templates.Folder, template.Id + ".json");

    // ---- Save ------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_query_is_saved_as_a_report_and_appears_under_my_reports()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        Assert.Empty(window.Reports.TemplateNames);

        explore.QueryText = "SELECT type, COUNT(*) AS records FROM records GROUP BY type";
        explore.BeginSaveTemplate();
        Assert.True(explore.TemplatePanelVisible);
        Assert.Equal(("", ""), explore.TemplateFields);
        Snapshot(window, "45-save-as-report");

        Assert.True(explore.SaveTemplate("Records by type", "How many records of each type."));

        Assert.False(explore.TemplatePanelVisible);
        Assert.Equal(["Records by type"], window.Reports.TemplateNames);
        Assert.True(File.Exists(Path.Combine(window.Reports.Templates.Folder, "records-by-type.json")));
        Assert.Contains("Saved report", explore.StatusText);
        Assert.Contains("Records by type", explore.BannerText); // from now on it is the report being edited
        Assert.Equal("Records by type", explore.EditingTemplate!.Name);
    }

    [AvaloniaFact]
    public async Task Problems_with_a_name_or_the_sql_are_explained_and_nothing_is_saved()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        SaveDirectly(window, "Taken", "SELECT 1");

        explore.QueryText = "SELECT 1";
        explore.BeginSaveTemplate();

        Assert.False(explore.SaveTemplate("   ", ""));
        Assert.Contains("needs a name", explore.TemplateMessageText);

        Assert.False(explore.SaveTemplate("explore data", ""));
        Assert.Contains("built-in report", explore.TemplateMessageText);

        Assert.False(explore.SaveTemplate("taken", ""));
        Assert.Contains("already exists", explore.TemplateMessageText);

        explore.QueryText = "DELETE FROM members";
        Assert.False(explore.SaveTemplate("Sneaky", ""));
        Assert.Contains("start with SELECT", explore.TemplateMessageText);

        explore.QueryText = "";
        Assert.False(explore.SaveTemplate("Empty", ""));
        Assert.Contains("Type a query", explore.TemplateMessageText);

        Assert.True(explore.TemplatePanelVisible); // stays open so it can be corrected
        Assert.Equal(["Taken"], window.Reports.TemplateNames);
        Assert.Single(Directory.GetFiles(window.Reports.Templates.Folder));
    }

    // ---- Run ---------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Picking_a_report_runs_it_on_the_open_grid()
    {
        var window = await OpenReports();
        SaveDirectly(window, "Records by type", "SELECT type, COUNT(*) AS n FROM records GROUP BY type ORDER BY n DESC", "Counts per type.");

        window.Reports.SelectReport("Records by type");
        await window.Reports.TemplatePage.RunTask;
        Snapshot(window, "46-report-template");

        var page = window.Reports.TemplatePage;
        Assert.Equal("Records by type", window.Reports.SelectedReport);
        Assert.Equal("Records by type", window.Reports.SelectedTemplate!.Name);
        Assert.Contains("GROUP BY type", page.SqlText);
        Assert.Equal(window.LoadedInventory!.Records.Count, page.LastResult!.Rows.Sum(r => (long)r[1]!));
        Assert.Equal(page.LastResult.Rows.Count, page.ResultTable!.VisibleRowCount);
        Assert.Equal("", page.ErrorText);
    }

    [AvaloniaFact]
    public async Task A_report_runs_again_on_the_next_grid_that_is_opened()
    {
        var window = await OpenReports();
        SaveDirectly(window, "Members", "SELECT host_name FROM members ORDER BY host_name");
        window.Reports.SelectReport("Members");
        await window.Reports.TemplatePage.RunTask;
        var first = window.Reports.TemplatePage.LastResult;
        Assert.NotNull(first);

        await window.LoadBackupAsync(BackupPath()); // another grid replaces it
        await window.Reports.TemplatePage.RunTask;

        Assert.Equal("Members", window.Reports.SelectedTemplate!.Name);
        Assert.NotNull(window.Reports.TemplatePage.LastResult);
        Assert.NotSame(first, window.Reports.TemplatePage.LastResult);
    }

    [AvaloniaFact]
    public async Task Without_a_grid_a_report_asks_for_one()
    {
        var window = await OpenReports(withGrid: false);
        SaveDirectly(window, "Members", "SELECT host_name FROM members");

        window.Reports.SelectReport("Members");
        await window.Reports.TemplatePage.RunTask;

        Assert.Null(window.Reports.TemplatePage.LastResult);
        Assert.False(await window.Reports.TemplatePage.RunAsync());
        Assert.Equal("Open a grid first.", window.Reports.TemplatePage.ErrorText);
    }

    [AvaloniaFact]
    public async Task A_report_with_a_mistake_in_it_says_so()
    {
        var window = await OpenReports();
        // Valid SQL as far as the guard goes, but it names a table that doesn't exist.
        SaveDirectly(window, "Broken", "SELECT * FROM no_such_table");

        window.Reports.SelectReport("Broken");
        await window.Reports.TemplatePage.RunTask;

        Assert.Null(window.Reports.TemplatePage.LastResult);
        Assert.Contains("no such table", window.Reports.TemplatePage.ErrorText);
    }

    // ---- Edit --------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Edit_opens_the_report_in_explore_and_saving_updates_the_same_report()
    {
        var window = await OpenReports();
        var original = SaveDirectly(window, "Members", "SELECT host_name FROM members", "Just the names.");
        window.Reports.SelectReport("Members");
        await window.Reports.TemplatePage.RunTask;

        window.Reports.TemplatePage.RequestEdit();

        var explore = window.Reports.Explore;
        Assert.Equal(ReportsView.ExploreData, window.Reports.SelectedReport);
        Assert.Equal("SELECT host_name FROM members", explore.QueryText);
        Assert.Equal(original.Id, explore.EditingTemplate!.Id);
        Assert.Contains("Editing report “Members”", explore.BannerText);
        Snapshot(window, "47-edit-report");

        explore.QueryText = "SELECT host_name, role FROM members ORDER BY host_name";
        explore.BeginSaveTemplate();
        Assert.Equal(("Members", "Just the names."), explore.TemplateFields); // filled in from the report
        Assert.True(explore.SaveTemplate("Members", "Names and roles."));

        var saved = Assert.Single(window.Reports.Templates.List());
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("Names and roles.", saved.Description);
        Assert.Equal("SELECT host_name, role FROM members ORDER BY host_name", saved.Sql);
        Assert.Equal(["Members"], window.Reports.TemplateNames);
    }

    [AvaloniaFact]
    public async Task A_report_can_be_renamed_and_saved_as_a_copy_when_edited()
    {
        var window = await OpenReports();
        var original = SaveDirectly(window, "Members", "SELECT host_name FROM members");
        window.Reports.SelectReport("Members");
        await window.Reports.TemplatePage.RunTask;
        window.Reports.TemplatePage.RequestEdit();
        var explore = window.Reports.Explore;

        // Rename: the file follows the name.
        Assert.True(explore.SaveTemplate("Members by role", ""));
        Assert.False(File.Exists(FilePath(window, original)));
        Assert.True(File.Exists(Path.Combine(window.Reports.Templates.Folder, "members-by-role.json")));
        Assert.Equal(["Members by role"], window.Reports.TemplateNames);

        // Save as a new report: the first one stays.
        explore.QueryText = "SELECT host_name, role FROM members";
        Assert.True(explore.SaveTemplate("Members with roles", "", asNew: true));
        Assert.Equal(["Members by role", "Members with roles"], window.Reports.TemplateNames);
        Assert.Equal("SELECT host_name FROM members", window.Reports.Templates.Find("Members by role")!.Sql);
        Assert.Equal("Members with roles", explore.EditingTemplate!.Name); // now editing the copy
    }

    [AvaloniaFact]
    public async Task Stopping_editing_goes_back_to_a_new_query()
    {
        var window = await OpenReports();
        SaveDirectly(window, "Members", "SELECT host_name FROM members");
        var explore = window.Reports.Explore;
        explore.LoadTemplate(window.Reports.Templates.Find("Members")!);
        Assert.NotNull(explore.EditingTemplate);

        explore.StopEditing();

        Assert.Null(explore.EditingTemplate);
        Assert.Equal("", explore.BannerText);

        // Saving now makes a new report instead of changing "Members".
        Assert.True(explore.SaveTemplate("Another", ""));
        Assert.Equal(["Another", "Members"], window.Reports.TemplateNames);
    }

    // ---- Delete --------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Deleting_a_report_removes_its_file_and_returns_to_explore()
    {
        var window = await OpenReports();
        var one = SaveDirectly(window, "One", "SELECT 1");
        SaveDirectly(window, "Two", "SELECT 2");
        window.Reports.SelectReport("One");
        await window.Reports.TemplatePage.RunTask;

        window.Reports.TemplatePage.Delete();

        Assert.False(File.Exists(FilePath(window, one)));
        Assert.Equal(["Two"], window.Reports.TemplateNames);
        Assert.Equal(["Two"], window.Reports.Templates.List().Select(t => t.Name));
        Assert.Equal(ReportsView.ExploreData, window.Reports.SelectedReport);
    }

    [AvaloniaFact]
    public async Task Deleting_the_report_being_edited_stops_the_editing()
    {
        var window = await OpenReports();
        SaveDirectly(window, "One", "SELECT 1");
        window.Reports.SelectReport("One");
        await window.Reports.TemplatePage.RunTask;
        window.Reports.TemplatePage.RequestEdit();
        Assert.NotNull(window.Reports.Explore.EditingTemplate);

        window.Reports.SelectReport("One");
        await window.Reports.TemplatePage.RunTask;
        window.Reports.TemplatePage.Delete();

        Assert.Null(window.Reports.Explore.EditingTemplate);
        Assert.Empty(window.Reports.TemplateNames);
    }

    // ---- Files from other people -------------------------------------------------------------

    [AvaloniaFact]
    public async Task Reload_picks_up_files_added_outside_the_app_and_reports_the_bad_ones()
    {
        var window = await OpenReports();
        var folder = window.Reports.Templates.Folder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "from-a-colleague.json"), """
            {
              "name": "From a colleague",
              "description": "Shared as a file.",
              "sql": [ "SELECT host_name", "FROM members" ]
            }
            """);
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ nope");
        File.WriteAllText(Path.Combine(folder, "writes.json"), """{ "name": "Writes", "sql": "DELETE FROM members" }""");

        window.Reports.RefreshTemplates();

        Assert.Equal(["From a colleague"], window.Reports.TemplateNames);
        Assert.Equal(2, window.Reports.TemplateProblems.Count);
        Assert.Contains(window.Reports.TemplateProblems, p => p.StartsWith("broken.json", StringComparison.Ordinal));
        Assert.Contains(window.Reports.TemplateProblems, p => p.StartsWith("writes.json", StringComparison.Ordinal) && p.Contains("read-only"));

        window.Reports.SelectReport("From a colleague");
        await window.Reports.TemplatePage.RunTask;
        Assert.Equal(window.LoadedInventory!.Members.Count, window.Reports.TemplatePage.LastResult!.Rows.Count);
    }

    [AvaloniaFact]
    public void The_built_in_report_names_are_the_reserved_ones()
    {
        Assert.Contains(ReportsView.LicenseExpiry, ReportTemplateStore.ReservedNames);
        Assert.Contains(ReportsView.DanglingCnames, ReportTemplateStore.ReservedNames);
        Assert.Contains(ReportsView.ExploreData, ReportTemplateStore.ReservedNames);
    }
}
