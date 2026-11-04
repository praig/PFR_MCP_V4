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

public class DhcpRangePageTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string WriteBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pfr-dhcp-{Guid.NewGuid():N}.bak");
        using var file = File.Create(path);
        OneDbFixture.GzipTarBackup().CopyTo(file);
        return path;
    }

    private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), $"pfr-dhcp-{Guid.NewGuid():N}{extension}");

    private static async Task<MainWindow> Open(GridInventory inventory)
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        window.Reports.ShowInventory(inventory, "test grid");
        window.Reports.SelectReport(ReportsView.DhcpRanges);
        await window.Reports.ReportTask;
        return window;
    }

    /// <summary>Two ranges in the default view, one with two options and one with none, and an IPv6 range with one.</summary>
    private static GridInventory Grid(bool optionsRead = true)
    {
        var inventory = new GridInventory { GridName = "Lab", DhcpOptionsRead = optionsRead };
        inventory.NetworkViews.Add(new NetworkView { Id = 0, Name = "default" });

        var withOptions = new DhcpRange
        {
            NetworkKey = "10.9.16.0/24/0", StartAddress = "10.9.16.100", EndAddress = "10.9.16.199", LeaseTimeSeconds = 3600, LeaseTimeSource = "Range",
        };
        withOptions.Options.Add(new DhcpOption { Code = 66, Value = "tftp.lab.test", Space = "DHCP..false" });
        withOptions.Options.Add(new DhcpOption { Code = 3, Value = "10.9.16.1", Space = "DHCP..false" });

        var without = new DhcpRange
        {
            NetworkKey = "10.9.17.0/24/0", StartAddress = "10.9.17.10", EndAddress = "10.9.17.20", LeaseTimeSeconds = 43200, LeaseTimeSource = "Grid",
        };

        var v6 = new DhcpRange
        {
            NetworkKey = "2001:db8::/64/0", StartAddress = "2001:db8::10", EndAddress = "2001:db8::20", IsIpv6 = true, LeaseTimeSeconds = 43200, LeaseTimeSource = "Grid",
        };
        v6.Options.Add(new DhcpOption { Code = 23, Value = "2001:db8::53", Space = "DHCPv6..false", IsIpv4 = false });

        inventory.DhcpRanges.AddRange([withOptions, without, v6]);
        return inventory;
    }

    private static List<string> TextOnPage(MainWindow window) => window.Reports.PageText.ToList();

    [AvaloniaFact]
    public async Task The_page_lists_a_line_per_option_and_a_line_for_a_range_with_none_in_the_order_given()
    {
        var window = await Open(Grid());
        var result = window.Reports.DhcpResult!;

        Assert.Equal(["10.9.16.0/24/default|3", "10.9.16.0/24/default|66", "10.9.17.0/24/default|", "2001:db8::/64/default|23"],
            result.Rows.Select(r => $"{r.Network}|{r.OptionCode}"));
        Assert.Equal(result.Rows.Count, window.Reports.CurrentTable!.VisibleRowCount);
        Assert.True(window.Reports.CanExport);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("3 ranges, 3 options on 2; 1 with none."));
        Assert.DoesNotContain(TextOnPage(window), t => t.Contains("saved before DHCP"));
    }

    [AvaloniaFact]
    public async Task Inherited_options_are_listed_with_where_they_are_set_and_can_be_turned_off()
    {
        var inventory = Grid();
        var network = new IpNetwork { Address = "10.9.17.0", Cidr = 24, NetworkViewId = 0 };   // the network of the range that has no options of its own
        network.Options.Add(new DhcpOption { Code = 15, Value = "lab.test", Space = "DHCP..false" });
        inventory.Networks.Add(network);

        var window = await Open(inventory);

        Assert.Contains(window.Reports.DhcpResult!.Rows, r => r.Start == "10.9.17.10" && r.OptionCode == 15 && r.SetOn == "Network 10.9.17.0/24");
        Assert.Equal(1, window.Reports.DhcpResult.InheritedOptionCount);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("3 ranges, 4 options on 3 (1 inherited); 0 with none."));

        window.Reports.SetDhcpRangeOptions(includeRangesWithoutOptions: true, includeIpv6: true, includeInherited: false);
        await window.Reports.ReportTask;

        Assert.Equal(0, window.Reports.DhcpResult!.InheritedOptionCount);
        var line = Assert.Single(window.Reports.DhcpResult.Rows, r => r.Start == "10.9.17.10");
        Assert.Null(line.OptionCode);                                   // it has nothing of its own: one line, no option
    }

    [AvaloniaFact]
    public async Task Leaving_out_ranges_with_no_options_or_ipv6_ranges_changes_the_lines()
    {
        var window = await Open(Grid());

        window.Reports.SetDhcpRangeOptions(includeRangesWithoutOptions: false, includeIpv6: true);
        await window.Reports.ReportTask;
        Assert.All(window.Reports.DhcpResult!.Rows, r => Assert.NotNull(r.OptionCode));
        Assert.Equal(3, window.Reports.DhcpResult.Rows.Count);

        window.Reports.SetDhcpRangeOptions(includeRangesWithoutOptions: true, includeIpv6: false);
        await window.Reports.ReportTask;
        Assert.Equal(2, window.Reports.DhcpResult!.RangeCount);
        Assert.DoesNotContain(window.Reports.DhcpResult.Rows, r => r.Network.Contains(':'));
    }

    [AvaloniaFact]
    public async Task A_snapshot_saved_before_options_were_read_says_to_import_the_backup_again()
    {
        var window = await Open(Grid(optionsRead: false));

        Assert.False(window.Reports.DhcpResult!.OptionsRead);
        Assert.Contains(TextOnPage(window), t => t.Contains("saved before DHCP lease times and options were read"));
    }

    [AvaloniaFact]
    public async Task Options_that_could_not_be_placed_are_mentioned()
    {
        var inventory = Grid();
        inventory.UnmatchedRangeOptions = 2;

        var window = await Open(inventory);

        Assert.Contains(TextOnPage(window), t => t.StartsWith("2 options in the backup belong to a range that could not be identified"));
    }

    [AvaloniaFact]
    public async Task The_export_writes_the_six_columns_with_numbers_for_the_lease_and_the_option_number()
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
                c => (Type: c.Attribute("t")?.Value, Value: c.Element(Sheet + "is")?.Element(Sheet + "t")?.Value ?? c.Element(Sheet + "v")?.Value ?? ""));

            Assert.Equal(DhcpRangeResult.Columns, "ABCDEFG".Select(column => cells[column + "1"].Value).ToArray());
            Assert.Equal(["10.9.16.0/24/default", "10.9.16.100", "10.9.16.199", "3600", "3", "10.9.16.1", "Range"],
                "ABCDEFG".Select(column => cells[column + "2"].Value).ToArray());
            Assert.Null(cells["D2"].Type);
            Assert.Null(cells["E2"].Type);
            Assert.False(cells.ContainsKey("E4"));   // the range with no options: no option cells
            Assert.Equal("3600", cells["D3"].Value);
            Assert.Contains("as an Excel workbook", window.Reports.ExportStatusText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task The_report_runs_on_a_real_backup_and_lists_each_of_its_ranges()
    {
        var backup = WriteBackup();
        try
        {
            var window = TestWindows.New();
            window.Show();
            await window.LoadBackupAsync(backup);
            window.ShowModule(AppModule.Reports);
            window.Reports.SelectReport(ReportsView.DhcpRanges);
            await window.Reports.ReportTask;

            var inventory = window.LoadedInventory!;
            Assert.Equal(inventory.DhcpRanges.Count, window.Reports.DhcpResult!.RangeCount);
            Assert.True(window.Reports.DhcpResult.OptionsRead);
            Assert.True(window.Reports.CanExport);
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [AvaloniaFact]
    public async Task With_no_grid_open_the_page_says_to_open_one_and_there_is_nothing_to_export()
    {
        var window = TestWindows.New();
        window.Show();
        window.ShowModule(AppModule.Reports);
        window.Reports.SelectReport(ReportsView.DhcpRanges);
        await window.Reports.ReportTask;

        Assert.Null(window.Reports.DhcpResult);
        Assert.False(window.Reports.CanExport);
        Assert.Contains(TextOnPage(window), t => t.StartsWith("Open a grid first"));
    }

    [AvaloniaFact]
    public async Task A_report_replaced_before_it_finishes_never_shows_up_afterwards()
    {
        var window = await Open(Grid());
        window.Reports.SetDhcpRangeOptions(includeRangesWithoutOptions: false, includeIpv6: false);
        var listing = window.Reports.ReportTask;
        window.Reports.SelectReport(ReportsView.LicenseExpiry);
        await listing;

        Assert.Null(window.Reports.DhcpResult);
        Assert.NotNull(window.Reports.LicenseResult);
    }
}
