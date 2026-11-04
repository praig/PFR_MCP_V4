using System.IO.Compression;
using System.Xml.Linq;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Reports;

namespace Pfr.Core.Tests.Query;

public class RowCountReportTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 5, 0, TimeSpan.Zero);

    // ---- A grid to count ----

    private static DnsZone Zone(string view, string fqdn, string type = "Authoritative", bool reverse = false) => new()
    {
        Key = $"{view}.{fqdn}", ViewKey = view, Fqdn = fqdn, ZoneType = type, IsReverse = reverse,
    };

    private static DnsRecord Record(DnsZone zone, DnsRecordType type, string name, string value, int? ttl = 300,
        bool disabled = false, string? host = null) => new()
    {
        Type = type, ZoneKey = zone.Key, ViewKey = zone.ViewKey, Name = name,
        Fqdn = name.Length == 0 ? zone.Fqdn : $"{name}.{zone.Fqdn}", Value = value,
        Ttl = new Ttl(ttl, ttl is null ? TtlSource.Grid : TtlSource.Record), Disabled = disabled, HostName = host,
    };

    /// <summary>
    /// View "default": lab.test (3 records), 10.in-addr.arpa (1), empty.test (0), fwd.test (a forward zone, 0).
    /// View "internal": lab.test again (2 records).
    /// </summary>
    private static GridInventory Grid()
    {
        var inventory = new GridInventory { GridName = "Lab" };
        inventory.DnsViews.Add(new DnsView { Key = "._default", Name = "default" });
        inventory.DnsViews.Add(new DnsView { Key = ".internal", Name = "internal" });

        var lab = Zone("._default", "lab.test");
        var arpa = Zone("._default", "10.in-addr.arpa", reverse: true);
        var empty = Zone("._default", "empty.test");
        var forward = Zone("._default", "fwd.test", type: "Forward");
        var internalLab = Zone(".internal", "lab.test");
        inventory.Zones.AddRange([lab, arpa, empty, forward, internalLab]);

        inventory.Records.AddRange(
        [
            Record(lab, DnsRecordType.A, "web1", "10.0.0.20", host: "web1.lab.test"),
            Record(lab, DnsRecordType.A, "app", "10.0.0.21", ttl: null),
            Record(lab, DnsRecordType.CNAME, "www", "web1.lab.test", disabled: true),
            Record(arpa, DnsRecordType.PTR, "20.0.0", "web1.lab.test"),
            Record(internalLab, DnsRecordType.A, "web1", "192.168.0.20"),
            Record(internalLab, DnsRecordType.TXT, "", "\"v=spf1 -all\""),
        ]);
        return inventory;
    }

    [Fact]
    public void Every_zone_is_counted_by_view_and_then_name_and_a_zone_of_the_same_name_in_another_view_is_separate()
    {
        var result = RowCountReport.Run(Grid());

        Assert.Equal(
            ["default|10.in-addr.arpa|1", "default|empty.test|0", "default|fwd.test|0", "default|lab.test|3", "internal|lab.test|2"],
            result.Zones.Select(z => $"{z.View}|{z.Zone}|{z.Count}").ToArray());
        Assert.Equal(6, result.TotalRecords);
        Assert.Equal(2, result.EmptyZones);
    }

    [Fact]
    public void Reverse_zones_and_empty_zones_can_be_left_out()
    {
        var noReverse = RowCountReport.Run(Grid(), new RowCountOptions { IncludeReverseZones = false });
        Assert.DoesNotContain(noReverse.Zones, z => z.IsReverse);
        Assert.Equal(4, noReverse.Zones.Count);

        var noEmpty = RowCountReport.Run(Grid(), new RowCountOptions { IncludeEmptyZones = false });
        Assert.Equal(["10.in-addr.arpa", "lab.test", "lab.test"], noEmpty.Zones.Select(z => z.Zone).ToArray());

        var neither = RowCountReport.Run(Grid(), new RowCountOptions { IncludeReverseZones = false, IncludeEmptyZones = false });
        Assert.Equal(["default|lab.test|3", "internal|lab.test|2"], neither.Zones.Select(z => $"{z.View}|{z.Zone}|{z.Count}").ToArray());
    }

    [Fact]
    public void A_zone_in_a_view_that_is_not_listed_is_shown_under_the_views_key()
    {
        var inventory = new GridInventory();
        inventory.Zones.Add(Zone(".lost", "x.test"));

        var zone = Assert.Single(RowCountReport.Run(inventory).Zones);

        Assert.Equal(".lost", zone.View);
    }

    [Fact]
    public void A_grid_with_no_zones_gives_an_empty_report()
    {
        var result = RowCountReport.Run(new GridInventory());

        Assert.Empty(result.Zones);
        Assert.Equal(0, result.TotalRecords);
    }

    // ---- Sheet names ----

    [Fact]
    public void Each_sheet_is_named_view_dot_zone()
    {
        var result = RowCountReport.Run(Grid());

        Assert.Equal(
            ["default.10.in-addr.arpa", "default.empty.test", "default.fwd.test", "default.lab.test", "internal.lab.test"],
            result.Zones.Select(z => z.SheetName).ToArray());
    }

    [Fact]
    public void Long_names_are_cut_to_excels_limit_and_made_different_from_each_other()
    {
        var inventory = new GridInventory();
        inventory.DnsViews.Add(new DnsView { Key = "._default", Name = "default" });
        inventory.Zones.Add(Zone("._default", "a-very-long-zone-name.same-one.example.com"));
        inventory.Zones.Add(Zone("._default", "a-very-long-zone-name.same-two.example.com"));
        inventory.Zones.Add(Zone("._default", "a-very-long-zone-name.same-three.example.com"));

        var names = RowCountReport.Run(inventory).Zones.Select(z => z.SheetName).ToList();

        Assert.All(names, n => Assert.True(n.Length <= 31, n));
        Assert.Equal(3, names.Select(n => n.ToLowerInvariant()).Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("default.a-very-long-zone-name", n));
        Assert.Contains(names, n => n.EndsWith("~2"));
        Assert.Contains(names, n => n.EndsWith("~3"));
    }

    [Fact]
    public void Characters_excel_does_not_allow_in_a_sheet_name_are_replaced()
    {
        var inventory = new GridInventory();
        inventory.DnsViews.Add(new DnsView { Key = "._default", Name = "default" });
        inventory.Zones.Add(Zone("._default", "0/26.1.0.10.in-addr.arpa", reverse: true)); // a classless reverse zone

        var zone = Assert.Single(RowCountReport.Run(inventory).Zones);

        Assert.DoesNotContain('/', zone.SheetName);
        Assert.StartsWith("default.0 26.1.0.10", zone.SheetName);
        Assert.Equal("0/26.1.0.10.in-addr.arpa", zone.Zone); // the Home sheet still shows the real name
    }

    [Fact]
    public void Sheet_names_that_differ_only_in_case_are_made_different_because_excel_ignores_case()
    {
        var names = XlsxWriter.UniqueSheetNames(["default.Lab.test", "default.lab.test", "DEFAULT.LAB.TEST"]);

        Assert.Equal(["default.Lab.test", "default.lab.test~2", "DEFAULT.LAB.TEST~3"], names);
    }

    [Fact]
    public void A_name_that_would_be_the_home_or_about_sheet_is_changed()
    {
        var names = XlsxWriter.UniqueSheetNames(["Home", "home", "About", "History"], reserved: ["Home"]);

        Assert.Equal(["Home~2", "home~3", "About (data)", "Results"], names);
    }

    [Fact]
    public void A_suffix_leaves_the_name_inside_31_characters()
    {
        var wanted = new string('x', 40);

        var names = XlsxWriter.UniqueSheetNames([wanted, wanted, wanted]);

        Assert.Equal([new string('x', 31), new string('x', 29) + "~2", new string('x', 29) + "~3"], names);
    }

    // ---- The workbook ----

    private static byte[] Build(GridInventory inventory, RowCountOptions? options = null)
    {
        using var stream = new MemoryStream();
        var notes = RowCountReport.WriteWorkbook(stream, RowCountReport.Run(inventory, options), inventory.GridName, At);
        Assert.Empty(notes);
        return stream.ToArray();
    }

    private static XDocument Part(byte[] file, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    private static List<string> SheetNames(byte[] file) =>
        Part(file, "xl/workbook.xml").Descendants(Main + "sheet").Select(s => s.Attribute("name")!.Value).ToList();

    private sealed record Cell(string? Type, string? Style, string Value);

    /// <summary>The cells of the sheet with this name, by reference.</summary>
    private static Dictionary<string, Cell> Cells(byte[] file, string sheetName)
    {
        var number = SheetNames(file).IndexOf(sheetName) + 1;
        Assert.True(number > 0, $"No sheet {sheetName}");
        return Part(file, $"xl/worksheets/sheet{number}.xml").Descendants(Main + "c").ToDictionary(
            c => c.Attribute("r")!.Value,
            c => new Cell(c.Attribute("t")?.Value, c.Attribute("s")?.Value,
                c.Attribute("t")?.Value == "inlineStr" ? c.Element(Main + "is")!.Element(Main + "t")!.Value : c.Element(Main + "v")?.Value ?? ""));
    }

    private static int Rows(Dictionary<string, Cell> cells, char column) => cells.Keys.Count(k => k[0] == column);

    [Fact]
    public void The_first_sheet_is_home_and_then_there_is_a_sheet_for_each_zone_in_the_same_order()
    {
        var file = Build(Grid());

        Assert.Equal(
            ["Home", "default.10.in-addr.arpa", "default.empty.test", "default.fwd.test", "default.lab.test", "internal.lab.test"],
            SheetNames(file));
        Assert.Equal("1", Part(file, "xl/worksheets/sheet1.xml").Descendants(Main + "sheetView").First().Attribute("tabSelected")!.Value);
    }

    [Fact]
    public void Home_has_a_header_and_a_row_per_zone_with_the_view_the_zone_and_the_count()
    {
        var cells = Cells(Build(Grid()), "Home");

        Assert.Equal(["View", "Zone", "Records"], new[] { cells["A1"].Value, cells["B1"].Value, cells["C1"].Value });
        Assert.All(new[] { "A1", "B1", "C1" }, r => Assert.Equal("1", cells[r].Style)); // the header is styled

        Assert.Equal(["default", "10.in-addr.arpa", "1"], new[] { cells["A2"].Value, cells["B2"].Value, cells["C2"].Value });
        Assert.Equal(["default", "lab.test", "3"], new[] { cells["A5"].Value, cells["B5"].Value, cells["C5"].Value });
        Assert.Equal(["internal", "lab.test", "2"], new[] { cells["A6"].Value, cells["B6"].Value, cells["C6"].Value });
        Assert.Null(cells["C5"].Type); // the count is a number, so it sorts and adds up in Excel
        Assert.Equal(5, Rows(cells, 'A') - 1);
    }

    [Fact]
    public void Each_zone_on_home_links_to_its_own_sheet()
    {
        var file = Build(Grid());
        var sheets = SheetNames(file);
        var cells = Cells(file, "Home");

        var links = Part(file, "xl/worksheets/sheet1.xml").Descendants(Main + "hyperlink")
            .ToDictionary(h => h.Attribute("ref")!.Value, h => h.Attribute("location")!.Value);

        Assert.Equal(["B2", "B3", "B4", "B5", "B6"], links.Keys.Order(StringComparer.Ordinal).ToArray()); // one per zone, in the Zone column
        Assert.Equal(5, links.Count);
        Assert.Equal("'default.10.in-addr.arpa'!A1", links["B2"]);
        Assert.Equal("'default.lab.test'!A1", links["B5"]);
        Assert.Equal("'internal.lab.test'!A1", links["B6"]);
        Assert.All(links.Values, l => Assert.Contains(l.Split('!')[0].Trim('\''), sheets));  // every link lands on a real sheet
        Assert.All(links.Keys, r => Assert.Equal("4", cells[r].Style));                       // and looks like a link
    }

    [Fact]
    public void A_zones_sheet_has_the_inventory_panels_columns_and_every_record_of_the_zone_in_its_order()
    {
        var cells = Cells(Build(Grid()), "default.lab.test");

        Assert.Equal(["Name", "Type", "Value", "TTL", "TTL from", "Host", "FQDN", "Disabled"],
            "ABCDEFGH".Select(c => cells[c + "1"].Value).ToArray());
        Assert.All("ABCDEFGH".Select(c => c + "1"), r => Assert.Equal("1", cells[r].Style));
        Assert.Equal(3, Rows(cells, 'A') - 1);

        // app, web1 and www: names relative to the zone, by name
        Assert.Equal(["app", "web1", "www"], new[] { cells["A2"].Value, cells["A3"].Value, cells["A4"].Value });
        Assert.Equal(["A", "A", "CNAME"], new[] { cells["B2"].Value, cells["B3"].Value, cells["B4"].Value });
        Assert.Equal("web1.lab.test", cells["C4"].Value);
        Assert.Equal(["app.lab.test", "web1.lab.test", "www.lab.test"], new[] { cells["G2"].Value, cells["G3"].Value, cells["G4"].Value });
    }

    [Fact]
    public void Ttl_where_it_comes_from_host_objects_and_disabled_records_are_worded_as_in_the_inventory_panel()
    {
        var cells = Cells(Build(Grid()), "default.lab.test");

        Assert.Equal("300 (5m)", cells["D3"].Value);             // web1: the TTL as the panel shows it
        Assert.Equal("Record", cells["E3"].Value);              // set on the record
        Assert.Equal("Yes", cells["F3"].Value);                 // generated by a host object
        Assert.Equal("web1.lab.test", cells["G3"].Value);
        Assert.False(cells.ContainsKey("H3"));                  // not disabled

        Assert.False(cells.ContainsKey("D2"));                  // app: the backup records no TTL, so nothing is shown
        Assert.False(cells.ContainsKey("E2"));
        Assert.False(cells.ContainsKey("F2"));

        Assert.Equal("Yes", cells["H4"].Value);                 // www is disabled
        Assert.False(cells.ContainsKey("F4"));
    }

    [Fact]
    public void A_zone_with_no_records_has_a_sheet_with_only_a_header()
    {
        var cells = Cells(Build(Grid()), "default.empty.test");

        Assert.Equal(8, cells.Count);
        Assert.Equal("Name", cells["A1"].Value);
    }

    [Fact]
    public void The_apex_record_is_named_after_the_zone()
    {
        var cells = Cells(Build(Grid()), "internal.lab.test");

        Assert.Equal("@", cells["A2"].Value); // the TXT record at the zone's own name ("" relative to the zone), as the panel shows it
        Assert.Equal("TXT", cells["B2"].Value);
        Assert.Equal("\"v=spf1 -all\"", cells["C2"].Value);
        Assert.Equal("lab.test", cells["G2"].Value);
    }

    [Fact]
    public void Zones_left_out_have_no_row_or_sheet()
    {
        var file = Build(Grid(), new RowCountOptions { IncludeReverseZones = false, IncludeEmptyZones = false });

        Assert.Equal(["Home", "default.lab.test", "internal.lab.test"], SheetNames(file));
        Assert.Equal(2, Rows(Cells(file, "Home"), 'A') - 1);
    }

    [Fact]
    public void A_grid_with_no_zones_still_makes_a_valid_workbook_with_just_home()
    {
        var file = Build(new GridInventory());

        Assert.Equal(["Home"], SheetNames(file));
        Assert.Equal(3, Cells(file, "Home").Count); // the header
    }

    [Fact]
    public void The_count_on_home_matches_the_rows_on_each_zones_sheet()
    {
        var file = Build(Grid());
        var home = Cells(file, "Home");

        for (var row = 2; row <= 6; row++)
        {
            var link = Part(file, "xl/worksheets/sheet1.xml").Descendants(Main + "hyperlink")
                .Single(h => h.Attribute("ref")!.Value == $"B{row}").Attribute("location")!.Value.Split('!')[0].Trim('\'');
            var sheet = Cells(file, link);
            var records = sheet.Keys.Count(k => k[0] == 'A') - 1;
            Assert.Equal(int.Parse(home[$"C{row}"].Value), records);
        }
    }

    [Fact]
    public void A_big_grid_makes_a_valid_workbook_with_a_sheet_for_every_zone()
    {
        var inventory = new GridInventory { GridName = "Big" };
        inventory.DnsViews.Add(new DnsView { Key = "._default", Name = "default" });
        for (var i = 0; i < 300; i++)
        {
            var zone = Zone("._default", $"zone{i:D3}.example.com");
            inventory.Zones.Add(zone);
            for (var r = 0; r < 5; r++) inventory.Records.Add(Record(zone, DnsRecordType.A, $"h{r}", $"10.0.{i / 250}.{r}"));
        }

        var file = Build(inventory);

        var sheets = SheetNames(file);
        Assert.Equal(301, sheets.Count);
        Assert.Equal(sheets.Count, sheets.Select(s => s.ToLowerInvariant()).Distinct().Count());
        Assert.Equal(300, Part(file, "xl/worksheets/sheet1.xml").Descendants(Main + "hyperlink").Count());
        Assert.Equal(1500, Cells(file, "Home").Where(c => c.Key[0] == 'C' && c.Key != "C1").Sum(c => int.Parse(c.Value.Value)));
    }

    [Fact]
    public void Every_part_of_the_workbook_is_well_formed_and_declared()
    {
        var file = Build(Grid());
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            Assert.NotNull(XDocument.Load(stream).Root);
        }

        var declared = Part(file, "[Content_Types].xml").Descendants().Where(e => e.Name.LocalName == "Override")
            .Select(e => e.Attribute("PartName")!.Value.TrimStart('/')).ToHashSet();
        Assert.All(Enumerable.Range(1, 6), n => Assert.Contains($"xl/worksheets/sheet{n}.xml", declared));
        Assert.Equal(6, Part(file, "xl/_rels/workbook.xml.rels").Descendants().Count(e => e.Attribute("Target")?.Value.StartsWith("worksheets/") == true));
    }

    [Fact]
    public void A_file_is_written_in_one_piece_and_leaves_no_temporary_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pfr-rowcount-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "sub", "rows.xlsx");
        try
        {
            var notes = RowCountReport.WriteFile(path, RowCountReport.Run(Grid()), "Lab");

            Assert.Empty(notes);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 2));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Anything_excel_had_to_change_is_listed_on_an_about_sheet_at_the_end_naming_the_sheet()
    {
        var inventory = new GridInventory { GridName = "Lab" };
        inventory.DnsViews.Add(new DnsView { Key = "._default", Name = "default" });
        var zone = Zone("._default", "lab.test");
        inventory.Zones.Add(zone);
        inventory.Records.Add(Record(zone, DnsRecordType.TXT, "big", new string('x', 40_000)));

        using var stream = new MemoryStream();
        var notes = RowCountReport.WriteWorkbook(stream, RowCountReport.Run(inventory), "Lab", At);

        Assert.Equal("default.lab.test: 1 cell longer than Excel's limit of 32,767 characters was shortened.", Assert.Single(notes));
        var file = stream.ToArray();
        Assert.Equal(["Home", "default.lab.test", "About"], SheetNames(file));
        var about = Cells(file, "About");
        Assert.Equal("Row count: Lab", about["B1"].Value);
        Assert.Equal(notes[0], about["B5"].Value);
    }

    // ---- The multi-sheet writer's own rules ----

    private static QueryResult Table(params string[] columns) => new(columns, new List<object?[]>(), false, TimeSpan.Zero);

    [Fact]
    public void A_workbook_refuses_names_excel_would_not_accept_or_that_repeat()
    {
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentException>(() => XlsxWriter.WriteWorkbook(stream, []));
        Assert.Throws<ArgumentException>(() => XlsxWriter.WriteWorkbook(stream, [new XlsxSheet("a/b", Table("x"))]));
        Assert.Throws<ArgumentException>(() => XlsxWriter.WriteWorkbook(stream, [new XlsxSheet(new string('a', 32), Table("x"))]));
        Assert.Throws<ArgumentException>(() => XlsxWriter.WriteWorkbook(stream, [new XlsxSheet("About", Table("x"))]));
        Assert.Throws<ArgumentException>(() => XlsxWriter.WriteWorkbook(stream, [new XlsxSheet("Home", Table("x")), new XlsxSheet("HOME", Table("x"))]));
    }

    [Fact]
    public void A_link_to_a_sheet_that_is_not_there_is_refused()
    {
        using var stream = new MemoryStream();
        var data = new QueryResult(["x"], new List<object?[]> { new object?[] { "a" } }, false, TimeSpan.Zero);

        var ex = Assert.Throws<ArgumentException>(() =>
            XlsxWriter.WriteWorkbook(stream, [new XlsxSheet("Home", data, [new XlsxLink(0, 0, "Nowhere")])]));

        Assert.Contains("Nowhere", ex.Message);
    }

    [Fact]
    public void A_link_to_a_sheet_with_an_apostrophe_in_its_name_is_quoted_correctly()
    {
        using var stream = new MemoryStream();
        var home = new QueryResult(["x"], new List<object?[]> { new object?[] { "go" } }, false, TimeSpan.Zero);

        XlsxWriter.WriteWorkbook(stream, [new XlsxSheet("Home", home, [new XlsxLink(0, 0, "O'Brien")]), new XlsxSheet("O'Brien", Table("y"))]);

        var link = Part(stream.ToArray(), "xl/worksheets/sheet1.xml").Descendants(Main + "hyperlink").Single();
        Assert.Equal("A2", link.Attribute("ref")!.Value);
        Assert.Equal("'O''Brien'!A1", link.Attribute("location")!.Value);
    }

    [Fact]
    public void Links_to_rows_or_columns_that_are_not_there_are_ignored_not_written()
    {
        using var stream = new MemoryStream();
        var home = new QueryResult(["x"], new List<object?[]> { new object?[] { "go" } }, false, TimeSpan.Zero);

        XlsxWriter.WriteWorkbook(stream, [new XlsxSheet("Home", home, [new XlsxLink(5, 0, "Other"), new XlsxLink(0, 3, "Other")]), new XlsxSheet("Other", Table("y"))]);

        Assert.Empty(Part(stream.ToArray(), "xl/worksheets/sheet1.xml").Descendants(Main + "hyperlinks")); // no empty element
    }

    [Fact]
    public void The_single_sheet_export_is_unchanged_by_the_workbook_writer()
    {
        using var stream = new MemoryStream();
        var data = new QueryResult(["a", "b"], new List<object?[]> { new object?[] { "x", 1L } }, false, TimeSpan.Zero);

        var notes = XlsxWriter.Write(stream, data, "Servers", At);

        Assert.Empty(notes);
        Assert.Equal(["Servers"], SheetNames(stream.ToArray()));
        Assert.Empty(Part(stream.ToArray(), "xl/worksheets/sheet1.xml").Descendants(Main + "hyperlinks"));
    }
}
