using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Pfr.Core;
using Pfr.Core.Query;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Tables;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class TableExportTests
{
    [Fact]
    public void A_table_becomes_a_result_with_numbers_as_numbers_and_empty_cells_as_null()
    {
        var table = new TableDefinition("t",
            [new TableColumn("Name"), new TableColumn("Days left", ColumnKind.Number), new TableColumn("Ratio", ColumnKind.Number), new TableColumn("Code")],
            [
                new TableRow(["web1", "42", "0.5", "007"]),
                new TableRow(["db1", "", "n/a", ""]),
                new TableRow(["short"]),                       // fewer cells than columns
            ]);

        var result = TableExport.ToResult(table);

        Assert.Equal(["Name", "Days left", "Ratio", "Code"], result.Columns);
        Assert.Equal(new object?[] { "web1", 42L, 0.5, "007" }, result.Rows[0]);   // "007" is in a text column: its zeros are kept
        Assert.Equal(new object?[] { "db1", null, "n/a", null }, result.Rows[1]);  // a number column's text that is not a number stays text
        Assert.Equal(new object?[] { "short", null, null, null }, result.Rows[2]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Numbers_with_separators_are_left_as_text_rather_than_guessed()
    {
        var table = new TableDefinition("t", [new TableColumn("n", ColumnKind.Number)], [new TableRow(["1,234"]), new TableRow(["1 234"])]);

        var result = TableExport.ToResult(table);

        Assert.Equal("1,234", result.Rows[0][0]);
        Assert.Equal("1 234", result.Rows[1][0]);
    }
}

public class BuiltInReportExportTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string WriteBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-export-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), $"pfr-export-{Guid.NewGuid():N}{extension}");

    private static XDocument Part(string workbook, string name)
    {
        using var zip = ZipFile.OpenRead(workbook);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    /// <summary>The text of each cell of the first sheet, by reference.</summary>
    private static Dictionary<string, string> Cells(string workbook) =>
        Part(workbook, "xl/worksheets/sheet1.xml").Descendants(Sheet + "c").ToDictionary(
            c => c.Attribute("r")!.Value,
            c => c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? "");

    private static string SheetName(string workbook) =>
        Part(workbook, "xl/workbook.xml").Descendants(Sheet + "sheet").First().Attribute("name")!.Value;

    private static async Task<MainWindow> WithGrid(string backup)
    {
        var window = TestWindows.New();
        window.Show();
        await window.LoadBackupAsync(backup);
        window.ShowModule(AppModule.Reports);
        return window;
    }

    [AvaloniaFact]
    public async Task Nothing_can_be_exported_before_a_grid_is_open()
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        var path = TempFile(".xlsx");

        Assert.False(window.Reports.CanExport);
        Assert.False(await window.Reports.ExportToFileAsync(path));
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task The_license_report_exports_what_it_shows_to_excel()
    {
        var backup = WriteBackup();
        var path = TempFile(".xlsx");
        try
        {
            var window = await WithGrid(backup);
            Assert.Equal(ReportsView.LicenseExpiry, window.Reports.SelectedReport);
            Assert.True(window.Reports.CanExport);

            Assert.True(await window.Reports.ExportToFileAsync(path));

            var cells = Cells(path);
            Assert.Equal(["Status", "Scope", "License", "Code", "Node", "Expires", "Days left", "Note"],
                "ABCDEFGH".Select(column => cells[column + "1"]).ToArray());
            var rows = window.Reports.LicenseResult!.Rows.Count;
            Assert.Equal(rows + 1, cells.Keys.Count(k => k.StartsWith('A')));   // a header, and a row for each (Status is never empty)
            Assert.Equal("License expiry", SheetName(path));
            Assert.Contains("as an Excel workbook", window.Reports.ExportStatusText);
            Assert.Contains($"Exported {rows:N0} row", window.Reports.ExportStatusText);
        }
        finally
        {
            File.Delete(backup);
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task The_cname_report_exports_only_the_rows_its_filters_show()
    {
        var backup = WriteBackup();
        var byDefault = TempFile(".xlsx");
        var everything = TempFile(".xlsx");
        try
        {
            var window = await WithGrid(backup);
            window.Reports.SelectReport(ReportsView.DanglingCnames);
            await window.Reports.ReportTask;
            Assert.True(window.Reports.CanExport);

            // The default hides the CNAMEs that resolve. Column C (the CNAME's name) is never empty, so it counts the rows.
            var shown = window.Reports.CurrentTable!.VisibleRowCount;
            Assert.True(await window.Reports.ExportToFileAsync(byDefault));
            Assert.Equal(shown + 1, Cells(byDefault).Keys.Count(k => k.StartsWith('C')));

            window.Reports.SetCnameFilters(showResolving: true, showNotVerified: true);
            await window.Reports.ReportTask;
            var all = window.Reports.CurrentTable!.VisibleRowCount;
            Assert.True(await window.Reports.ExportToFileAsync(everything));

            var cells = Cells(everything);
            Assert.Equal(["Severity", "Result", "CNAME", "Target"], new[] { cells["A1"], cells["B1"], cells["C1"], cells["D1"] });
            Assert.Equal(all + 1, cells.Keys.Count(k => k.StartsWith('C')));
            Assert.True(all >= shown);
            Assert.Equal("Dangling CNAMEs", SheetName(everything));
        }
        finally
        {
            File.Delete(backup);
            File.Delete(byDefault);
            File.Delete(everything);
        }
    }

    [AvaloniaFact]
    public async Task An_export_button_is_on_the_page_and_a_failure_is_said_not_thrown()
    {
        var backup = WriteBackup();
        var blocker = TempFile(".txt");
        try
        {
            var window = await WithGrid(backup);
            File.WriteAllText(blocker, "a file where a folder is needed");

            Assert.False(await window.Reports.ExportToFileAsync(Path.Combine(blocker, "out.xlsx")));

            Assert.Contains("Could not write", window.Reports.ExportStatusText);
            var buttons = window.Reports.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => b.Content as string == "Export\u2026");
        }
        finally
        {
            File.Delete(backup);
            File.Delete(blocker);
        }
    }
}
