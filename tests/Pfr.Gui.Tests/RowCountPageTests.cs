using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Pfr.Core;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class RowCountPageTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string WriteBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-rowcount-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    private static async Task<MainWindow> WithGrid(string backup)
    {
        var window = TestWindows.New();
        window.Show();
        await window.LoadBackupAsync(backup);
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.RowCount);
        await window.Reports.ReportTask;
        return window;
    }

    private static List<string> SheetNames(string workbook)
    {
        using var zip = ZipFile.OpenRead(workbook);
        using var stream = zip.GetEntry("xl/workbook.xml")!.Open();
        return XDocument.Load(stream).Descendants(Sheet + "sheet").Select(s => s.Attribute("name")!.Value).ToList();
    }

    private static Dictionary<string, string> FirstSheet(string workbook)
    {
        using var zip = ZipFile.OpenRead(workbook);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream).Descendants(Sheet + "c").ToDictionary(
            c => c.Attribute("r")!.Value,
            c => c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? "");
    }

    [AvaloniaFact]
    public async Task Nothing_can_be_created_before_a_grid_is_open()
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.RowCount);
        var path = Path.Combine(Path.GetTempPath(), $"pfr-rowcount-{Guid.NewGuid():N}.xlsx");

        Assert.Equal(ReportsView.RowCount, window.Reports.SelectedReport);
        Assert.False(window.Reports.CanCreateWorkbook);
        Assert.False(await window.Reports.CreateWorkbookToFileAsync(path));
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task The_page_lists_every_zone_with_its_count_and_offers_to_create_the_workbook()
    {
        var backup = WriteBackup();
        try
        {
            var window = await WithGrid(backup);
            var inventory = window.LoadedInventory!;
            var counted = window.Reports.RowCounts!;

            Assert.True(window.Reports.CanCreateWorkbook);
            Assert.Equal(inventory.Zones.Count, counted.Zones.Count);               // every zone, by default
            Assert.Equal(counted.Zones.Count, window.Reports.CurrentTable!.VisibleRowCount);
            var zoneKeys = inventory.Zones.Select(z => z.Key).ToHashSet();
            Assert.Equal(inventory.Records.Count(r => zoneKeys.Contains(r.ZoneKey)), counted.TotalRecords);

            var buttons = window.Reports.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => b.Content as string == "Create Excel workbook\u2026");
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task The_workbook_has_a_home_sheet_and_a_sheet_for_each_zone()
    {
        var backup = WriteBackup();
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-rowcount-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "rows");   // no extension: .xlsx is added
        try
        {
            var window = await WithGrid(backup);
            var counted = window.Reports.RowCounts!;

            Assert.True(await window.Reports.CreateWorkbookToFileAsync(path));

            var file = path + ".xlsx";
            Assert.True(File.Exists(file));
            var sheets = SheetNames(file);
            Assert.Equal("Home", sheets[0]);
            Assert.Equal(counted.Zones.Count + 1, sheets.Count);
            Assert.Equal(counted.Zones.Select(z => z.SheetName), sheets.Skip(1));

            var home = FirstSheet(file);
            Assert.Equal(["View", "Zone", "Records"], new[] { home["A1"], home["B1"], home["C1"] });
            Assert.Equal(counted.Zones.Count, home.Keys.Count(k => k[0] == 'A') - 1);
            Assert.Contains("Created", window.Reports.WorkbookStatusText);
            Assert.Contains($"{counted.TotalRecords:N0} records", window.Reports.WorkbookStatusText);
        }
        finally
        {
            File.Delete(backup);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public async Task Leaving_out_reverse_zones_or_empty_zones_changes_what_is_counted()
    {
        var backup = WriteBackup();
        try
        {
            var window = await WithGrid(backup);
            var inventory = window.LoadedInventory!;

            window.Reports.SetRowCountOptions(includeReverseZones: false, includeEmptyZones: true);
            await window.Reports.ReportTask;
            Assert.Equal(inventory.Zones.Count(z => !z.IsReverse), window.Reports.RowCounts!.Zones.Count);
            Assert.DoesNotContain(window.Reports.RowCounts.Zones, z => z.IsReverse);

            window.Reports.SetRowCountOptions(includeReverseZones: true, includeEmptyZones: false);
            await window.Reports.ReportTask;
            Assert.All(window.Reports.RowCounts!.Zones, z => Assert.True(z.Count > 0));
            Assert.Equal(window.Reports.RowCounts.Zones.Count, window.Reports.CurrentTable!.VisibleRowCount);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task A_report_replaced_before_it_finishes_never_shows_up_afterwards()
    {
        var backup = WriteBackup();
        try
        {
            var window = await WithGrid(backup);
            window.Reports.SetRowCountOptions(includeReverseZones: false, includeEmptyZones: false);
            var counting = window.Reports.ReportTask;
            window.Reports.SelectReport(ReportsView.LicenseExpiry);
            await counting;

            Assert.Null(window.Reports.RowCounts);
            Assert.False(window.Reports.CanCreateWorkbook);
            Assert.NotNull(window.Reports.LicenseResult);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task A_workbook_that_cannot_be_written_is_said_not_thrown()
    {
        var backup = WriteBackup();
        var blocker = Path.Combine(Path.GetTempPath(), $"pfr-rowcount-{Guid.NewGuid():N}.txt");
        try
        {
            var window = await WithGrid(backup);
            File.WriteAllText(blocker, "a file where a folder is needed");

            Assert.False(await window.Reports.CreateWorkbookToFileAsync(Path.Combine(blocker, "out.xlsx")));

            Assert.Contains("Could not write", window.Reports.WorkbookStatusText);
        }
        finally
        {
            File.Delete(backup);
            File.Delete(blocker);
        }
    }
}
