using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Pfr.Core;
using Pfr.Core.Nios.Model;
using Pfr.Core.Reports;
using Pfr.Core.Tests.Nios;
using Pfr.Gui.Views;

namespace Pfr.Gui.Tests;

public class ScavengePageTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), $"pfr-scavenge-{Guid.NewGuid():N}{extension}");

    private static GridInventory Grid(bool originsRead = true)
    {
        var inventory = new GridInventory { GridName = "Lab", RecordOriginsRead = originsRead };

        void Add(DnsRecordType type, string fqdn, string value, string creator, int? unixTime) =>
            inventory.Records.Add(new DnsRecord
            {
                Type = type, Fqdn = fqdn, Name = fqdn, Value = value, ZoneKey = "z", ViewKey = "v", Creator = creator,
                LastQueried = unixTime is null ? null : DateTimeOffset.FromUnixTimeSeconds(unixTime.Value),
            });

        Add(DnsRecordType.A, "laptop-7.lab.test", "10.0.0.150", "DYNAMIC", 1700000000);      // 2023-11-14
        Add(DnsRecordType.A, "laptop-12.lab.test", "10.0.0.9", "DYNAMIC", null);
        Add(DnsRecordType.A, "web1.lab.test", "10.0.0.20", "STATIC", 1600000000);            // 2020-09-13
        Add(DnsRecordType.A, "ns1.lab.test", "10.0.0.2", "SYSTEM", 1553078740);              // 2019-03-20
        Add(DnsRecordType.AAAA, "web1.lab.test", "2001:db8::20", "STATIC", 1600000000);        // not an A record
        Add(DnsRecordType.PTR, "20.0.0.10.in-addr.arpa", "web1.lab.test", "STATIC", 1600000000);
        Add(DnsRecordType.CNAME, "www.lab.test", "web1.lab.test", "STATIC", null);
        return inventory;
    }

    private static async Task<MainWindow> Open(GridInventory inventory)
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        window.Reports.ShowInventory(inventory, "test grid");
        window.Reports.SelectReport(ReportsView.Scavenge);
        await window.Reports.ReportTask;
        return window;
    }

    private static List<string> TextOnPage(MainWindow window) => window.Reports.PageText.ToList();

    [AvaloniaFact]
    public async Task The_page_lists_each_A_CNAME_and_PTR_record_with_creator_name_address_date_and_type()
    {
        var window = await Open(Grid());
        var result = window.Reports.ScavengeData!;

        Assert.Equal(
            ["DYNAMIC|laptop-12.lab.test|10.0.0.9||A", "DYNAMIC|laptop-7.lab.test|10.0.0.150|2023-11-14|A",
             "STATIC|20.0.0.10.in-addr.arpa|10.0.0.20|2020-09-13|PTR", "STATIC|web1.lab.test|10.0.0.20|2020-09-13|A",
             "STATIC|www.lab.test|||CNAME", "SYSTEM|ns1.lab.test|10.0.0.2|2019-03-20|A"],
            result.Rows.Select(r => $"{r.Creator}|{r.Fqdn}|{r.IpAddress}|{r.LastQueriedText}|{r.TypeText}"));
        Assert.Equal(result.Rows.Count, window.Reports.CurrentTable!.VisibleRowCount);
        Assert.True(window.Reports.CanExport);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("6 records: 2 dynamic, 3 static, 1 system; 2 with no last-queried date."));
    }

    [AvaloniaFact]
    public async Task Creators_can_be_chosen()
    {
        var window = await Open(Grid());

        window.Reports.SetScavengeOptions(dynamicRecords: true, staticRecords: false, system: false);
        await window.Reports.ReportTask;
        Assert.All(window.Reports.ScavengeData!.Rows, r => Assert.Equal("DYNAMIC", r.Creator));
        Assert.Equal(2, window.Reports.ScavengeData.Rows.Count);

        window.Reports.SetScavengeOptions(dynamicRecords: false, staticRecords: true, system: true);
        await window.Reports.ReportTask;
        Assert.Equal(["STATIC", "STATIC", "STATIC", "SYSTEM"], window.Reports.ScavengeData!.Rows.Select(r => r.Creator));

        window.Reports.SetScavengeOptions(dynamicRecords: true, staticRecords: true, system: true);
        await window.Reports.ReportTask;
        Assert.Equal(6, window.Reports.ScavengeData!.Rows.Count);   // the A, CNAME and PTR records, whatever else the grid holds
    }

    [AvaloniaFact]
    public async Task Record_types_can_be_chosen()
    {
        var window = await Open(Grid());

        window.Reports.SetScavengeOptions(true, true, true, aRecords: true, cnameRecords: false, ptrRecords: false);
        await window.Reports.ReportTask;
        Assert.All(window.Reports.ScavengeData!.Rows, r => Assert.Equal(DnsRecordType.A, r.Type));
        Assert.Equal(4, window.Reports.ScavengeData.Rows.Count);

        window.Reports.SetScavengeOptions(true, true, true, aRecords: false, cnameRecords: true, ptrRecords: true);
        await window.Reports.ReportTask;
        Assert.Equal(["PTR", "CNAME"], window.Reports.ScavengeData!.Rows.Select(r => r.TypeText));
    }

    [AvaloniaFact]
    public async Task A_snapshot_saved_before_creators_were_read_says_to_import_the_backup_again()
    {
        var window = await Open(Grid(originsRead: false));

        Assert.False(window.Reports.ScavengeData!.RecordOriginsRead);
        Assert.Contains(TextOnPage(window), t => t.Contains("saved before record creators and last-queried dates were read"));
    }

    [AvaloniaFact]
    public async Task The_export_writes_five_text_columns_with_the_date_as_yyyy_mm_dd()
    {
        var window = await Open(Grid());
        var path = TempFile(".xlsx");
        try
        {
            Assert.True(await window.Reports.ExportToFileAsync(path));

            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var cells = XDocument.Load(stream).Descendants(Sheet + "c").ToDictionary(
                c => c.Attribute("r")!.Value,
                c => c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? "");

            Assert.Equal(ScavengeResult.Columns, "ABCDE".Select(column => cells[column + "1"]).ToArray());
            Assert.Equal(["DYNAMIC", "laptop-7.lab.test", "10.0.0.150", "2023-11-14", "A"], "ABCDE".Select(column => cells[column + "3"]).ToArray());
            Assert.Contains("as an Excel workbook", window.Reports.ExportStatusText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task With_no_grid_open_the_page_says_to_open_one()
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.Scavenge);
        await window.Reports.ReportTask;

        Assert.Null(window.Reports.ScavengeData);
        Assert.False(window.Reports.CanExport);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("Open a grid first"));
    }

    [AvaloniaFact]
    public async Task A_report_replaced_before_it_finishes_never_shows_up_afterwards()
    {
        var window = await Open(Grid());
        window.Reports.SetScavengeOptions(dynamicRecords: false, staticRecords: true, system: true);
        var listing = window.Reports.ReportTask;
        window.Reports.SelectReport(ReportsView.LicenseExpiry);
        await listing;

        Assert.Null(window.Reports.ScavengeData);
        Assert.NotNull(window.Reports.LicenseResult);
    }

    [AvaloniaFact]
    public async Task The_report_runs_on_a_real_backup()
    {
        var backup = Path.Combine(Path.GetTempPath(), $"pfr-scavenge-{Guid.NewGuid():N}.bak");
        using (var file = File.Create(backup)) OneDbFixture.GzipTarBackup().CopyTo(file);
        try
        {
            var window = TestWindows.New();
            window.Show();
            await window.LoadBackupAsync(backup);
            window.ShowModule(AppModule.Reports);
            window.Reports.SelectReport(ReportsView.Scavenge);
            await window.Reports.ReportTask;

            var inventory = window.LoadedInventory!;
            Assert.Equal(inventory.Records.Count(r => r.Type is DnsRecordType.A or DnsRecordType.CNAME or DnsRecordType.PTR && !r.IsFromHost), window.Reports.ScavengeData!.Rows.Count);
            Assert.True(window.Reports.ScavengeData.RecordOriginsRead);
        }
        finally
        {
            File.Delete(backup);
        }
    }
}
