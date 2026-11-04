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

public class RecordCreationPageTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), $"pfr-creation-{Guid.NewGuid():N}{extension}");

    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static GridInventory Grid(bool detailsRead = true)
    {
        var inventory = new GridInventory { GridName = "Lab", RecordDetailsRead = detailsRead };

        inventory.Records.Add(new DnsRecord
        {
            Type = DnsRecordType.A, Fqdn = "www.lab.test", Value = "10.0.0.10", ZoneKey = "z", ViewKey = "v", Comment = "front page",
            Created = Utc("2024-03-02T10:00:00Z"), ExtensibleAttributes = new Dictionary<string, string> { ["Mnemonic"] = "WEB", ["Site"] = "HQ" },
        });
        inventory.Records.Add(new DnsRecord { Type = DnsRecordType.A, Fqdn = "apple.lab.test", Value = "10.0.0.5", ZoneKey = "z", ViewKey = "v" });

        var host = new HostObject
        {
            Name = "server1.lab.test", IsDns = true, ZoneKey = "z", ViewKey = "v", Comment = "B1 Host", Created = Utc("2021-12-01T00:00:00Z"),
            ExtensibleAttributes = new Dictionary<string, string> { ["Mnemonic"] = "SRV" },
        };
        host.Addresses.Add(new HostAddress { Address = "10.0.0.20" });
        host.Addresses.Add(new HostAddress { Address = "2001:db8::20", IsIpv6 = true });
        inventory.Hosts.Add(host);

        var printer = new HostObject { Name = "printer", IsDns = false, ZoneKey = "root", ViewKey = "root", Created = Utc("2020-09-13T12:00:00Z") };
        printer.Addresses.Add(new HostAddress { Address = "10.0.0.30" });
        inventory.Hosts.Add(printer);
        return inventory;
    }

    private static async Task<MainWindow> Open(GridInventory inventory)
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        window.Reports.ShowInventory(inventory, "test grid");
        window.Reports.SelectReport(ReportsView.RecordCreation);
        await window.Reports.ReportTask;
        return window;
    }

    private static List<string> TextOnPage(MainWindow window) => window.Reports.PageText.ToList();

    [AvaloniaFact]
    public async Task The_page_lists_each_A_record_and_host_address_with_comment_date_and_the_mnemonic()
    {
        var window = await Open(Grid());
        var result = window.Reports.CreationData!;

        Assert.Equal(
            ["A|apple.lab.test|10.0.0.5|||", "Host|server1.lab.test|10.0.0.20|B1 Host|2021-12-01|SRV",
             "Host|server1.lab.test|2001:db8::20|B1 Host|2021-12-01|SRV", "A|www.lab.test|10.0.0.10|front page|2024-03-02|WEB"],
            result.Rows.Select(r => $"{r.RecordType}|{r.Fqdn}|{r.IpAddress}|{r.Comment}|{r.CreatedText}|{r.AttributeValue}"));
        Assert.Equal(result.Rows.Count, window.Reports.CurrentTable!.VisibleRowCount);
        Assert.True(window.Reports.CanExport);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("4 lines: 2 A records, 2 host lines; 3 with a Mnemonic value; 1 with no creation date."));
    }

    [AvaloniaFact]
    public async Task Another_attribute_can_be_chosen_and_the_column_is_named_after_it()
    {
        var window = await Open(Grid());

        window.Reports.SetRecordCreationOptions("Site");
        await window.Reports.ReportTask;

        Assert.Equal("Site", window.Reports.CreationData!.Columns[5]);
        Assert.Equal(["HQ"], window.Reports.CreationData.Rows.Where(r => r.AttributeValue.Length > 0).Select(r => r.AttributeValue));
        Assert.Contains(TextOnPage(window), t => t.Contains("1 with a Site value"));
    }

    [AvaloniaFact]
    public async Task An_attribute_that_nothing_has_is_said_to_be_unset_and_the_ones_that_are_set_are_named()
    {
        var window = await Open(Grid());

        window.Reports.SetRecordCreationOptions("Rack");
        await window.Reports.ReportTask;

        Assert.False(window.Reports.CreationData!.AttributeFound);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("No A record or host listed has an extensible attribute called \"Rack\" set.")
                                                 && t.Contains("Mnemonic, Site"));
    }

    [AvaloniaFact]
    public async Task A_records_hosts_and_hosts_not_in_dns_can_be_chosen()
    {
        var window = await Open(Grid());

        window.Reports.SetRecordCreationOptions("Mnemonic", aRecords: true, hosts: false);
        await window.Reports.ReportTask;
        Assert.All(window.Reports.CreationData!.Rows, r => Assert.Equal("A", r.RecordType));

        window.Reports.SetRecordCreationOptions("Mnemonic", aRecords: false, hosts: true, ipamOnlyHosts: true);
        await window.Reports.ReportTask;
        Assert.Equal(["printer", "server1.lab.test", "server1.lab.test"], window.Reports.CreationData!.Rows.Select(r => r.Fqdn));
    }

    [AvaloniaFact]
    public async Task A_snapshot_saved_before_the_details_were_read_says_to_import_the_backup_again()
    {
        var window = await Open(Grid(detailsRead: false));

        Assert.False(window.Reports.CreationData!.RecordDetailsRead);
        Assert.Contains(TextOnPage(window), t => t.Contains("saved before comments, creation dates and extensible attributes were read"));
    }

    [AvaloniaFact]
    public async Task The_export_writes_six_text_columns_the_last_named_after_the_attribute()
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

            Assert.Equal(["Record type", "FQDN", "IP address", "Comment", "Creation date", "Mnemonic"], "ABCDEF".Select(column => cells[column + "1"]).ToArray());
            Assert.Equal(["Host", "server1.lab.test", "10.0.0.20", "B1 Host", "2021-12-01", "SRV"], "ABCDEF".Select(column => cells[column + "3"]).ToArray());
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
        window.Reports.SelectReport(ReportsView.RecordCreation);
        await window.Reports.ReportTask;

        Assert.Null(window.Reports.CreationData);
        Assert.False(window.Reports.CanExport);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("Open a grid first"));
    }

    [AvaloniaFact]
    public async Task A_report_replaced_before_it_finishes_never_shows_up_afterwards()
    {
        var window = await Open(Grid());
        window.Reports.SetRecordCreationOptions("Site", aRecords: false);
        var listing = window.Reports.ReportTask;
        window.Reports.SelectReport(ReportsView.LicenseExpiry);
        await listing;

        Assert.Null(window.Reports.CreationData);
        Assert.NotNull(window.Reports.LicenseResult);
    }

    [AvaloniaFact]
    public async Task The_report_runs_on_a_real_backup()
    {
        var backup = Path.Combine(Path.GetTempPath(), $"pfr-creation-{Guid.NewGuid():N}.bak");
        using (var file = File.Create(backup)) OneDbFixture.GzipTarBackup().CopyTo(file);
        try
        {
            var window = TestWindows.New();
            window.Show();
            await window.LoadBackupAsync(backup);
            window.ShowModule(AppModule.Reports);
            window.Reports.SelectReport(ReportsView.RecordCreation);
            await window.Reports.ReportTask;

            var inventory = window.LoadedInventory!;
            var expected = inventory.Records.Count(r => r.Type == DnsRecordType.A && !r.IsFromHost)
                + inventory.Hosts.Where(h => h.IsDns).Sum(h => Math.Max(1, h.Addresses.Count));
            Assert.Equal(expected, window.Reports.CreationData!.Rows.Count);
            Assert.True(window.Reports.CreationData.RecordDetailsRead);
        }
        finally
        {
            File.Delete(backup);
        }
    }
}
