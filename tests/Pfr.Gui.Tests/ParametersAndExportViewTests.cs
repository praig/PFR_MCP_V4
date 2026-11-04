using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pfr.Core;
using Pfr.Core.Query;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class ParametersAndExportViewTests
{
    private static readonly string? ScreenshotDir = Environment.GetEnvironmentVariable("PFR_SCREENSHOT_DIR");

    private const string NetworkSql =
        "SELECT fqdn, value FROM records WHERE type = :type AND cidr_contains(:network, value) ORDER BY ip_key(value)";

    private static string BackupPath()
    {
        var real = Environment.GetEnvironmentVariable("PFR_SAMPLE_BACKUP");
        if (real is not null && File.Exists(real)) return real;
        var path = Path.Combine(Path.GetTempPath(), $"pfr-params-{Guid.NewGuid():N}.bak");
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

    private static async Task<MainWindow> OpenReports()
    {
        var window = TestWindows.New();
        window.Show();
        await window.LoadBackupAsync(BackupPath());
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.ExploreData);
        return window;
    }

    private static ReportTemplate SaveDirectly(MainWindow window, ReportTemplate template)
    {
        var saved = window.Reports.Templates.Save(template);
        window.Reports.RefreshTemplates();
        return saved;
    }

    private static ReportTemplate NetworkReport() => new()
    {
        Name = "A in net",
        Sql = NetworkSql,
        Parameters =
        [
            new ReportParameter { Name = "type", Label = "Record type", Type = ParameterType.Choice, Options = ["A", "AAAA"], Default = "A" },
            new ReportParameter { Name = "network", Label = "Network", Default = "10.0.0.0/24", Description = "Only records inside it." },
        ],
    };

    private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), $"pfr-export-{Guid.NewGuid():N}{extension}");

    // ---- Parameters in Explore data ---------------------------------------------------

    [AvaloniaFact]
    public async Task Explore_shows_an_input_for_each_parameter_in_the_sql_and_binds_what_is_typed()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        Assert.False(explore.Parameters.HasParameters);

        explore.QueryText = "SELECT host_name FROM members WHERE host_name = :host";
        Assert.True(explore.Parameters.HasParameters);
        Assert.Equal(["host"], explore.Parameters.Definitions.Select(d => d.Name));
        Snapshot(window, "48-explore-parameters");

        explore.Parameters.SetValue("host", "gm.lab.test");
        Assert.True(await explore.RunAsync());
        Assert.Equal("gm.lab.test", Assert.Single(explore.LastResult!.Rows)[0]);

        explore.QueryText = "SELECT 1";
        Assert.False(explore.Parameters.HasParameters);
    }

    [AvaloniaFact]
    public async Task What_was_typed_survives_while_the_sql_around_it_is_edited()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        explore.QueryText = "SELECT * FROM members WHERE host_name = :host";
        explore.Parameters.SetValue("host", "gm.lab.test");

        explore.QueryText = "SELECT oid, host_name FROM members WHERE host_name = :host AND oid > :min";

        Assert.Equal(["host", "min"], explore.Parameters.Definitions.Select(d => d.Name));
        Assert.Equal("gm.lab.test", explore.Parameters.GetValue("host"));
        Assert.Equal("", explore.Parameters.GetValue("min"));
    }

    [AvaloniaFact]
    public async Task A_value_that_looks_like_sql_is_only_data_and_a_blank_one_is_null()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        explore.QueryText = "SELECT host_name FROM members WHERE (:host IS NULL OR host_name = :host)";

        explore.Parameters.SetValue("host", "x'; DELETE FROM members; --");
        Assert.True(await explore.RunAsync());
        Assert.Empty(explore.LastResult!.Rows);

        explore.Parameters.SetValue("host", "");
        Assert.True(await explore.RunAsync());
        Assert.Equal(window.LoadedInventory!.Members.Count, explore.LastResult!.Rows.Count); // blank means NULL: no filter
    }

    [AvaloniaFact]
    public async Task A_question_mark_placeholder_is_explained_when_the_query_runs()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        explore.QueryText = "SELECT * FROM members WHERE oid = ?";

        Assert.False(explore.Parameters.HasParameters);
        Assert.False(await explore.RunAsync());
        Assert.Contains(":name", explore.ErrorText);
    }

    // ---- Saving a query with parameters as a report ------------------------------------------

    [AvaloniaFact]
    public async Task Saving_a_query_with_parameters_declares_them_starting_from_what_was_typed()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        explore.QueryText = NetworkSql;
        explore.Parameters.SetValue("type", "A");
        explore.Parameters.SetValue("network", "10.0.0.0/24");

        explore.BeginSaveTemplate();
        Assert.Equal(2, explore.ParameterEditorCount);
        Snapshot(window, "49-save-report-parameters");
        Assert.True(explore.SaveTemplate("A records in a network", "Records inside a network."));

        var saved = Assert.Single(window.Reports.Templates.List());
        Assert.Equal(["type", "network"], saved.Parameters.Select(p => p.Name));
        Assert.Equal(["A", "10.0.0.0/24"], saved.Parameters.Select(p => p.Default));
        Assert.All(saved.Parameters, p => Assert.True(p.Required));
    }

    [AvaloniaFact]
    public async Task Parameters_can_be_described_when_saving_and_problems_are_explained()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        explore.QueryText = NetworkSql;
        explore.BeginSaveTemplate();

        var described = new List<ReportParameter>
        {
            new() { Name = "type", Label = "Record type", Type = ParameterType.Choice, Options = ["A", "AAAA"], Default = "A" },
            new() { Name = "network", Label = "Network", Description = "Only records inside it.", Default = "10.0.0.0/8" },
        };
        Assert.True(explore.SaveTemplate("Described", "", asNew: false, described));

        var saved = Assert.Single(window.Reports.Templates.List());
        Assert.Equal(ParameterType.Choice, saved.Parameters[0].Type);
        Assert.Equal(["A", "AAAA"], saved.Parameters[0].Options);
        Assert.Equal("Only records inside it.", saved.Parameters[1].Description);

        // A parameter missing from the list is a mistake the save panel explains; nothing is saved.
        explore.StopEditing();
        explore.QueryText = NetworkSql;
        explore.BeginSaveTemplate();
        Assert.False(explore.SaveTemplate("Incomplete", "", asNew: false, [described[0]]));
        Assert.Contains("no parameter named", explore.TemplateMessageText);
        Assert.Single(window.Reports.Templates.List());
    }

    // ---- Running a report with parameters ----------------------------------------------------

    [AvaloniaFact]
    public async Task A_report_with_parameters_starts_from_their_defaults_and_runs_with_what_is_entered()
    {
        var window = await OpenReports();
        SaveDirectly(window, NetworkReport());

        window.Reports.SelectReport("A in net");
        var page = window.Reports.TemplatePage;
        await page.RunTask;
        Snapshot(window, "50-report-parameters");

        Assert.Equal("A", page.Parameters.GetValue("type"));
        Assert.Equal("10.0.0.0/24", page.Parameters.GetValue("network"));
        Assert.NotEmpty(page.LastResult!.Rows); // the defaults ran straight away

        page.Parameters.SetValue("network", "10.99.0.0/16");
        Assert.True(await page.RunAsync());
        Assert.Empty(page.LastResult!.Rows);

        page.Parameters.SetValue("type", "AAAA");
        page.Parameters.SetValue("network", "2001:db8::/32");
        Assert.True(await page.RunAsync());
        Assert.NotEmpty(page.LastResult!.Rows); // the fixture has an AAAA record in 2001:db8::/32
    }

    [AvaloniaFact]
    public async Task The_values_used_are_remembered_for_that_report_while_the_page_is_open()
    {
        var window = await OpenReports();
        SaveDirectly(window, NetworkReport());
        window.Reports.SelectReport("A in net");
        var page = window.Reports.TemplatePage;
        await page.RunTask;
        page.Parameters.SetValue("network", "10.99.0.0/16");
        Assert.True(await page.RunAsync());

        window.Reports.SelectReport(ReportsView.LicenseExpiry);
        window.Reports.SelectReport("A in net");
        await page.RunTask;

        Assert.Equal("10.99.0.0/16", page.Parameters.GetValue("network"));
        Assert.Empty(page.LastResult!.Rows);
    }

    [AvaloniaFact]
    public async Task A_required_value_left_blank_stops_the_run_and_says_which()
    {
        var window = await OpenReports();
        SaveDirectly(window, NetworkReport());
        window.Reports.SelectReport("A in net");
        var page = window.Reports.TemplatePage;
        await page.RunTask;

        page.Parameters.SetValue("network", "  ");

        Assert.False(await page.RunAsync());
        Assert.Contains("Enter a value for “Network”", page.ErrorText);
    }

    [AvaloniaFact]
    public async Task A_report_that_needs_a_value_it_has_no_default_for_waits_to_be_filled_in()
    {
        var window = await OpenReports();
        SaveDirectly(window, new ReportTemplate
        {
            Name = "Needs a zone",
            Sql = "SELECT fqdn FROM zones WHERE fqdn = :zone",
            Parameters = [new ReportParameter { Name = "zone", Label = "Zone" }],
        });

        window.Reports.SelectReport("Needs a zone");
        var page = window.Reports.TemplatePage;
        await page.RunTask;

        Assert.Null(page.LastResult);
        Assert.Equal("", page.ErrorText); // nothing is wrong yet: it is waiting for a value

        page.Parameters.SetValue("zone", "lab.test");
        Assert.True(await page.RunAsync());
        Assert.NotEmpty(page.LastResult!.Rows);
    }

    [AvaloniaFact]
    public async Task An_optional_parameter_left_blank_is_null()
    {
        var window = await OpenReports();
        SaveDirectly(window, new ReportTemplate
        {
            Name = "Maybe a member",
            Sql = "SELECT host_name FROM members WHERE (:host IS NULL OR host_name = :host)",
            Parameters = [new ReportParameter { Name = "host", Label = "Member", Required = false }],
        });

        window.Reports.SelectReport("Maybe a member");
        await window.Reports.TemplatePage.RunTask;

        Assert.Equal(window.LoadedInventory!.Members.Count, window.Reports.TemplatePage.LastResult!.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Editing_a_report_keeps_what_was_said_about_its_parameters()
    {
        var window = await OpenReports();
        SaveDirectly(window, NetworkReport());
        window.Reports.SelectReport("A in net");
        await window.Reports.TemplatePage.RunTask;

        window.Reports.TemplatePage.RequestEdit();

        var explore = window.Reports.Explore;
        var network = explore.Parameters.Definitions.Single(d => d.Name == "network");
        Assert.Equal(("Network", "Only records inside it."), (network.Label, network.Description));
        Assert.Equal("10.0.0.0/24", explore.Parameters.GetValue("network")); // starts from the default

        // Saving without touching the details leaves them (and the choice type) as they were.
        Assert.True(explore.SaveTemplate("A in net", ""));
        var saved = window.Reports.Templates.Find("A in net")!;
        Assert.Equal(ParameterType.Choice, saved.Parameters.Single(p => p.Name == "type").Type);
        Assert.Equal(["A", "AAAA"], saved.Parameters.Single(p => p.Name == "type").Options);
        Assert.Equal("10.0.0.0/24", saved.Parameters.Single(p => p.Name == "network").Default);
    }

    // ---- Exporting ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Explore_results_can_be_exported_as_csv_and_json()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        var csv = TempFile(".csv");
        var json = TempFile(".json");
        try
        {
            Assert.False(await explore.ExportToFileAsync(csv));
            Assert.Contains("Run a query first", explore.ErrorText);
            Assert.False(File.Exists(csv));

            explore.QueryText = "SELECT host_name, '=1+1' AS formula FROM members ORDER BY host_name";
            Assert.True(await explore.RunAsync());
            var members = window.LoadedInventory!.Members.Count;

            Assert.True(await explore.ExportToFileAsync(csv));
            var bytes = File.ReadAllBytes(csv);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            var lines = Encoding.UTF8.GetString(bytes[3..]).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("host_name,formula", lines[0]);
            Assert.Equal(members + 1, lines.Length);
            Assert.EndsWith(",'=1+1", lines[1]); // a spreadsheet can't run it as a formula
            Assert.Contains("Exported", explore.StatusText);
            Assert.Equal("", explore.ErrorText);

            Assert.True(await explore.ExportToFileAsync(json));
            using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
            Assert.Equal(members, doc.RootElement.GetArrayLength());
            Assert.Equal("=1+1", doc.RootElement[0].GetProperty("formula").GetString()); // JSON is data and stays as it is
        }
        finally
        {
            File.Delete(csv);
            File.Delete(json);
        }
    }

    [AvaloniaFact]
    public async Task A_report_can_export_what_it_shows()
    {
        var window = await OpenReports();
        SaveDirectly(window, NetworkReport());
        window.Reports.SelectReport("A in net");
        var page = window.Reports.TemplatePage;
        await page.RunTask;
        var path = TempFile(".csv");
        try
        {
            Assert.True(await page.ExportToFileAsync(path));

            var lines = Encoding.UTF8.GetString(File.ReadAllBytes(path)[3..]).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("fqdn,value", lines[0]);
            Assert.Equal(page.LastResult!.Rows.Count + 1, lines.Length);
            Assert.Contains("Exported", page.StatusText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Exporting to Excel ----

    private static readonly System.Xml.Linq.XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static System.Xml.Linq.XDocument ReadPart(string workbook, string part)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(workbook);
        using var stream = zip.GetEntry(part)!.Open();
        return System.Xml.Linq.XDocument.Load(stream);
    }

    /// <summary>The text of every cell of the first sheet, by reference.</summary>
    private static Dictionary<string, string> SheetText(string workbook) =>
        ReadPart(workbook, "xl/worksheets/sheet1.xml").Descendants(Sheet + "c").ToDictionary(
            c => c.Attribute("r")!.Value,
            c => c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? "");

    [AvaloniaFact]
    public async Task Explore_results_can_be_exported_as_an_excel_workbook()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        var path = TempFile(".xlsx");
        try
        {
            explore.QueryText = "SELECT host_name, '=1+1' AS formula, 42 AS answer FROM members ORDER BY host_name";
            Assert.True(await explore.RunAsync());
            var members = window.LoadedInventory!.Members.Count;

            Assert.True(await explore.ExportToFileAsync(path));

            var cells = SheetText(path);
            Assert.Equal(["host_name", "formula", "answer"], new[] { cells["A1"], cells["B1"], cells["C1"] });
            Assert.Equal("=1+1", cells["B2"]);                            // kept as text, not run as a formula
            Assert.Equal("42", cells["C2"]);
            Assert.Equal(members, cells.Keys.Count(k => k.StartsWith('A')) - 1);   // one row per member, plus the header
            Assert.Empty(ReadPart(path, "xl/worksheets/sheet1.xml").Descendants(Sheet + "f"));
            Assert.Equal("Query results", ReadPart(path, "xl/workbook.xml").Descendants(Sheet + "sheet").First().Attribute("name")!.Value);
            Assert.Contains("as an Excel workbook", explore.StatusText);
            Assert.Equal("", explore.ErrorText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_report_exported_to_excel_names_its_sheet_after_the_report()
    {
        var window = await OpenReports();
        SaveDirectly(window, NetworkReport());
        window.Reports.SelectReport("A in net");
        var page = window.Reports.TemplatePage;
        await page.RunTask;
        var path = TempFile(".xlsx");
        try
        {
            Assert.True(await page.ExportToFileAsync(path));

            var cells = SheetText(path);
            Assert.Equal(["fqdn", "value"], new[] { cells["A1"], cells["B1"] });
            Assert.Equal(page.LastResult!.Rows.Count + 1, cells.Keys.Count(k => k.StartsWith('A')));
            Assert.Equal("A in net", ReadPart(path, "xl/workbook.xml").Descendants(Sheet + "sheet").First().Attribute("name")!.Value);
            Assert.Contains("as an Excel workbook", page.StatusText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task A_file_that_cannot_be_written_is_reported_not_thrown()
    {
        var window = await OpenReports();
        var explore = window.Reports.Explore;
        explore.QueryText = "SELECT 1";
        Assert.True(await explore.RunAsync());
        var blocker = TempFile(".txt");
        File.WriteAllText(blocker, "a file where a folder is needed");
        try
        {
            Assert.False(await explore.ExportToFileAsync(Path.Combine(blocker, "out.csv")));
            Assert.Contains("Could not write", explore.ErrorText);
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
