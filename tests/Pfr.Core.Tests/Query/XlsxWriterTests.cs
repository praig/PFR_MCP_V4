using System.Collections;
using System.IO.Compression;
using System.Xml.Linq;
using Pfr.Core.Query;

namespace Pfr.Core.Tests.Query;

public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 5, 0, TimeSpan.Zero);

    private static QueryResult Result(string[] columns, params object?[][] rows) =>
        new(columns, rows, truncated: false, TimeSpan.FromMilliseconds(3));

    /// <summary>The workbook as bytes, with the notes the writer reported.</summary>
    private static (byte[] File, IReadOnlyList<string> Notes) Build(QueryResult result, string? title = null)
    {
        using var stream = new MemoryStream();
        var notes = XlsxWriter.Write(stream, result, title, At);
        return (stream.ToArray(), notes);
    }

    private static ZipArchive Open(byte[] file) => new(new MemoryStream(file), ZipArchiveMode.Read);

    private static XDocument Part(byte[] file, string name)
    {
        using var zip = Open(file);
        using var stream = (zip.GetEntry(name) ?? throw new InvalidOperationException($"No part {name}")).Open();
        return XDocument.Load(stream);
    }

    private static List<string> PartNames(byte[] file)
    {
        using var zip = Open(file);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>What a reader would see in a cell: how it is stored, its style, and its value.</summary>
    private sealed record Cell(string? Type, string? Style, string Value);

    private static Dictionary<string, Cell> Cells(byte[] file, string sheet = "xl/worksheets/sheet1.xml") =>
        Part(file, sheet).Descendants(Main + "c").ToDictionary(
            c => c.Attribute("r")!.Value,
            c => new Cell(
                c.Attribute("t")?.Value,
                c.Attribute("s")?.Value,
                c.Attribute("t")?.Value == "inlineStr" ? c.Element(Main + "is")!.Element(Main + "t")!.Value : c.Element(Main + "v")?.Value ?? ""));

    // ---- The file as a whole ----

    [Fact]
    public void The_file_is_a_zip_of_well_formed_parts_with_the_content_types_first()
    {
        var (file, notes) = Build(Result(["Name", "Count"], ["a", 1L]));

        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(file, 0, 2));
        var names = PartNames(file);
        Assert.Equal("[Content_Types].xml", names[0]);
        Assert.Equal(
            ["[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml"],
            names.Order(StringComparer.Ordinal).ToArray());
        foreach (var name in names) Assert.NotNull(Part(file, name).Root); // each one parses as XML
        Assert.Empty(notes);
    }

    [Fact]
    public void Every_part_is_declared_and_every_relationship_points_at_a_part_that_exists()
    {
        var (file, _) = Build(Result(["a"], ["x"]), title: "T");
        var names = PartNames(file);

        var overrides = Part(file, "[Content_Types].xml").Descendants().Where(e => e.Name.LocalName == "Override")
            .Select(e => e.Attribute("PartName")!.Value.TrimStart('/')).ToList();
        Assert.Equal(["xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml"], overrides.Order(StringComparer.Ordinal).ToArray());

        var targets = Part(file, "xl/_rels/workbook.xml.rels").Descendants().Where(e => e.Name.LocalName == "Relationship")
            .Select(e => "xl/" + e.Attribute("Target")!.Value).ToList();
        Assert.All(targets, t => Assert.Contains(t, names));
        Assert.Equal("xl/workbook.xml", Part(file, "_rels/.rels").Descendants().Single(e => e.Name.LocalName == "Relationship").Attribute("Target")!.Value);

        var sheet = Part(file, "xl/workbook.xml").Descendants(Main + "sheet").Single();
        Assert.Equal("T", sheet.Attribute("name")!.Value);
        Assert.Equal("rId1", sheet.Attributes().Single(a => a.Name.LocalName == "id").Value);
    }

    // ---- The data sheet ----

    [Fact]
    public void The_header_is_styled_frozen_and_filtered_and_the_columns_are_sized()
    {
        var (file, _) = Build(Result(["Name", "Count", "A much longer column heading"], ["a", 1L, "x"], ["b", 2L, "y"]));
        var sheet = Part(file, "xl/worksheets/sheet1.xml");

        Assert.Equal("A1:C3", sheet.Descendants(Main + "dimension").Single().Attribute("ref")!.Value);
        var pane = sheet.Descendants(Main + "pane").Single();
        Assert.Equal(("1", "A2", "frozen"), (pane.Attribute("ySplit")!.Value, pane.Attribute("topLeftCell")!.Value, pane.Attribute("state")!.Value));
        Assert.Equal("A1:C3", sheet.Descendants(Main + "autoFilter").Single().Attribute("ref")!.Value);

        var cells = Cells(file);
        Assert.All(new[] { "A1", "B1", "C1" }, r => Assert.Equal(("inlineStr", "1"), (cells[r].Type, cells[r].Style)));
        Assert.Equal(["Name", "Count", "A much longer column heading"], new[] { cells["A1"].Value, cells["B1"].Value, cells["C1"].Value });

        var widths = sheet.Descendants(Main + "col").Select(c => double.Parse(c.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(3, widths.Length);
        Assert.Equal(8, widths[0]);                      // short content: the minimum
        Assert.Equal(8, widths[1]);
        Assert.Equal("A much longer column heading".Length + 2, widths[2]);

        var styles = Part(file, "xl/styles.xml");
        Assert.Equal(4, styles.Descendants(Main + "cellXfs").Single().Elements().Count());
    }

    [Fact]
    public void The_filter_range_is_also_recorded_as_excel_does_with_the_sheet_name_quoted()
    {
        var (file, _) = Build(Result(["a", "b"], ["x", "y"], ["z", "w"]), title: "O'Brien report");

        var workbook = Part(file, "xl/workbook.xml");
        Assert.Equal("O'Brien report", workbook.Descendants(Main + "sheet").Single().Attribute("name")!.Value);
        var name = workbook.Descendants(Main + "definedName").Single();
        Assert.Equal("_xlnm._FilterDatabase", name.Attribute("name")!.Value);
        Assert.Equal("'O''Brien report'!$A$1:$B$3", name.Value);
    }

    [Fact]
    public void Numbers_are_numbers_and_everything_else_is_text_and_null_is_an_empty_cell()
    {
        var (file, notes) = Build(Result(["id", "ratio", "name", "blob", "nothing"],
            [42L, 0.5, "web1", new byte[] { 1, 2, 3 }, null],
            [-7L, 1e-5, "", null, null],
            [0L, 1234567.891, "x", null, null]));
        var cells = Cells(file);

        Assert.Equal((null, "42"), (cells["A2"].Type, cells["A2"].Value));
        Assert.Equal((null, "0.5"), (cells["B2"].Type, cells["B2"].Value));
        Assert.Equal(("inlineStr", "web1"), (cells["C2"].Type, cells["C2"].Value));
        Assert.Equal("(blob, 3 bytes)", cells["D2"].Value);
        Assert.False(cells.ContainsKey("E2"));          // NULL: no cell
        Assert.Equal("-7", cells["A3"].Value);
        Assert.Equal("1E-05", cells["B3"].Value);       // read back as 0.00001 by Excel
        Assert.False(cells.ContainsKey("C3"));          // an empty string is no cell either
        Assert.Equal("1234567.891", cells["B4"].Value);
        Assert.Empty(notes);
    }

    [Fact]
    public void Text_that_looks_like_a_formula_is_stored_as_text_and_never_calculated()
    {
        var (file, _) = Build(Result(["v"], ["=1+1"], ["=cmd|' /C calc'!A0"], ["+SUM(A1)"], ["@x"], ["-5"]));

        var sheet = Part(file, "xl/worksheets/sheet1.xml");
        Assert.Empty(sheet.Descendants(Main + "f")); // no formula anywhere
        var cells = Cells(file);
        Assert.All(new[] { "A2", "A3", "A4", "A5", "A6" }, r => Assert.Equal("inlineStr", cells[r].Type));
        Assert.Equal(["=1+1", "=cmd|' /C calc'!A0", "+SUM(A1)", "@x", "-5"], new[] { "A2", "A3", "A4", "A5", "A6" }.Select(r => cells[r].Value).ToArray());
    }

    [Fact]
    public void Text_comes_back_exactly_spaces_unicode_markup_and_line_breaks_included()
    {
        var (file, notes) = Build(Result(["t"],
            ["  padded  "], ["caf\u00e9 \u2013 \U0001F600"], ["a & b < c > d \"q\""], ["line 1\nline 2"], ["dos\r\nline"], ["old\rmac"], ["tab\there"]));
        var cells = Cells(file);

        Assert.Equal("  padded  ", cells["A2"].Value);
        Assert.Equal("caf\u00e9 \u2013 \U0001F600", cells["A3"].Value);
        Assert.Equal("a & b < c > d \"q\"", cells["A4"].Value);
        Assert.Equal("line 1\nline 2", cells["A5"].Value);
        Assert.Equal("2", cells["A5"].Style);            // a cell with a line break wraps
        Assert.Equal("dos\nline", cells["A6"].Value);    // line breaks are plain line feeds
        Assert.Equal("old\nmac", cells["A7"].Value);
        Assert.Equal("tab\there", cells["A8"].Value);
        Assert.Null(cells["A2"].Style);
        Assert.Empty(notes);

        var raw = Part(file, "xl/worksheets/sheet1.xml").Descendants(Main + "t").First(t => t.Value == "  padded  ");
        Assert.Equal("preserve", raw.Attribute(XNamespace.Xml + "space")!.Value);
    }

    [Fact]
    public void A_gap_in_a_row_leaves_the_cells_after_it_in_their_own_columns()
    {
        var (file, _) = Build(Result(["a", "b", "c"], ["x", null, "z"]));
        var cells = Cells(file);

        Assert.Equal(["A1", "A2", "B1", "C1", "C2"], cells.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.False(cells.ContainsKey("B2"));
        Assert.Equal("z", cells["C2"].Value);
    }

    [Fact]
    public void A_result_with_no_rows_still_has_a_header_and_a_filter_over_it()
    {
        var (file, notes) = Build(Result(["a", "b"]));

        var sheet = Part(file, "xl/worksheets/sheet1.xml");
        Assert.Equal("A1:B1", sheet.Descendants(Main + "dimension").Single().Attribute("ref")!.Value);
        Assert.Equal("A1:B1", sheet.Descendants(Main + "autoFilter").Single().Attribute("ref")!.Value);
        Assert.Equal(["A1", "B1"], Cells(file).Keys.ToArray());
        Assert.Empty(notes);
    }

    [Fact]
    public void A_result_with_no_columns_is_an_empty_but_valid_sheet()
    {
        var (file, _) = Build(Result([]));

        var sheet = Part(file, "xl/worksheets/sheet1.xml");
        Assert.Equal("A1", sheet.Descendants(Main + "dimension").Single().Attribute("ref")!.Value);
        Assert.Empty(sheet.Descendants(Main + "autoFilter"));
        Assert.Empty(sheet.Descendants(Main + "pane"));
        Assert.Empty(Part(file, "xl/workbook.xml").Descendants(Main + "definedName"));
    }

    [Fact]
    public void Column_widths_follow_the_content_up_to_a_limit()
    {
        var (file, _) = Build(Result(["short", "long"], ["abcdefghijkl", new string('x', 200)], ["a", "line one\nthe second line is the longest one"]));

        var widths = Part(file, "xl/worksheets/sheet1.xml").Descendants(Main + "col")
            .Select(c => double.Parse(c.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(14, widths[0]);    // 12 characters and a margin
        Assert.Equal(60, widths[1]);    // never wider than 60
    }

    // ---- The limits of Excel, handled and said ----

    [Fact]
    public void Characters_xml_cannot_hold_are_replaced_and_the_file_says_so()
    {
        var (file, notes) = Build(Result(["t"], ["a\u0001b"], ["lone\ud800high"], ["fine\u00e9"], ["bell\u0007"]));
        var cells = Cells(file);

        Assert.Equal("a\ufffdb", cells["A2"].Value);
        Assert.Equal("lone\ufffdhigh", cells["A3"].Value);
        Assert.Equal("fine\u00e9", cells["A4"].Value);
        Assert.Equal("bell\ufffd", cells["A5"].Value);
        Assert.Equal("3 cells contained control characters Excel cannot store; they were replaced with \ufffd.", Assert.Single(notes));
        Assert.Contains("xl/worksheets/sheet2.xml", PartNames(file));
    }

    [Fact]
    public void A_cell_over_excels_limit_is_shortened_and_the_file_says_so()
    {
        var (file, notes) = Build(Result(["t"], [new string('x', 40_000)], ["short"]));

        Assert.Equal(XlsxWriter.MaxCellCharacters, Cells(file)["A2"].Value.Length);
        Assert.Equal("1 cell longer than Excel's limit of 32,767 characters was shortened.", Assert.Single(notes));
    }

    [Fact]
    public void A_whole_number_excel_would_round_is_written_as_text_and_the_file_says_so()
    {
        var (file, notes) = Build(Result(["id"], [999_999_999_999_999L], [1_000_000_000_000_000L], [long.MinValue], [-999_999_999_999_999L]));
        var cells = Cells(file);

        Assert.Equal((null, "999999999999999"), (cells["A2"].Type, cells["A2"].Value));         // exactly 15 digits: still a number
        Assert.Equal(("inlineStr", "1000000000000000"), (cells["A3"].Type, cells["A3"].Value)); // 16 digits: text, so nothing is rounded
        Assert.Equal(("inlineStr", "-9223372036854775808"), (cells["A4"].Type, cells["A4"].Value));
        Assert.Equal((null, "-999999999999999"), (cells["A5"].Type, cells["A5"].Value));
        Assert.Equal("2 whole numbers of more than 15 digits were written as text, because Excel would round them.", Assert.Single(notes));
    }

    [Fact]
    public void Not_a_number_and_infinity_are_written_as_text()
    {
        var (file, _) = Build(Result(["d"], [double.NaN], [double.PositiveInfinity], [double.NegativeInfinity]));
        var cells = Cells(file);

        Assert.All(new[] { "A2", "A3", "A4" }, r => Assert.Equal("inlineStr", cells[r].Type));
    }

    [Fact]
    public void A_result_cut_by_the_query_row_limit_says_so_on_an_about_sheet()
    {
        var result = new QueryResult(["a"], new object?[][] { ["x"], ["y"] }, truncated: true, TimeSpan.Zero);

        var (file, notes) = Build(result, "Servers");

        Assert.Equal("The query reached its row limit, so only the first 2 rows were returned and exported. There are more rows than this workbook holds.",
            Assert.Single(notes));
        var workbook = Part(file, "xl/workbook.xml");
        Assert.Equal(["Servers", "About"], workbook.Descendants(Main + "sheet").Select(s => s.Attribute("name")!.Value).ToArray());

        var about = Cells(file, "xl/worksheets/sheet2.xml");
        Assert.Equal(["Report", "Data sheet", "Rows exported", "Exported", "Note"], new[] { "A1", "A2", "A3", "A4", "A5" }.Select(r => about[r].Value).ToArray());
        Assert.Equal(["Servers", "Servers", "2", "2026-10-29 09:05 +00:00"], new[] { "B1", "B2", "B3", "B4" }.Select(r => about[r].Value).ToArray());
        Assert.Equal(notes[0], about["B5"].Value);
        Assert.Equal("3", about["A1"].Style);   // labels are bold
        Assert.Equal("2", about["B5"].Style);   // the note wraps

        var overrides = Part(file, "[Content_Types].xml").Descendants().Where(e => e.Name.LocalName == "Override").Select(e => e.Attribute("PartName")!.Value);
        Assert.Contains("/xl/worksheets/sheet2.xml", overrides);
        var rels = Part(file, "xl/_rels/workbook.xml.rels").Descendants().Where(e => e.Name.LocalName == "Relationship").Select(e => e.Attribute("Target")!.Value);
        Assert.Contains("worksheets/sheet2.xml", rels);
    }

    private sealed class CountedRows(int count) : IReadOnlyList<object?[]>
    {
        public int Count => count;

        public object?[] this[int index] => [(long)index];

        public IEnumerator<object?[]> GetEnumerator()
        {
            for (var i = 0; i < count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void More_rows_than_a_sheet_holds_are_cut_and_the_file_says_how_many()
    {
        var result = new QueryResult(["n"], new CountedRows(XlsxWriter.MaxRows + 4), truncated: false, TimeSpan.Zero);

        var notes = XlsxWriter.Write(Stream.Null, result);

        Assert.Equal("Excel holds at most 1,048,575 data rows on a sheet, so the last 5 rows are not in this workbook.", Assert.Single(notes));
    }

    [Fact]
    public void More_columns_than_a_sheet_holds_are_refused()
    {
        var columns = Enumerable.Range(0, XlsxWriter.MaxColumns + 1).Select(i => $"c{i}").ToArray();

        var ex = Assert.Throws<ArgumentException>(() => XlsxWriter.Write(Stream.Null, Result(columns)));

        Assert.Contains("at most 16,384 columns", ex.Message);
    }

    [Fact]
    public void The_widest_allowed_sheet_has_a_last_column_named_xfd()
    {
        var columns = Enumerable.Range(0, XlsxWriter.MaxColumns).Select(i => $"c{i}").ToArray();
        using var stream = new MemoryStream();

        XlsxWriter.Write(stream, Result(columns, columns.Select(c => (object?)c).ToArray()));

        Assert.Equal("A1:XFD2", Part(stream.ToArray(), "xl/worksheets/sheet1.xml").Descendants(Main + "dimension").Single().Attribute("ref")!.Value);
    }

    // ---- Names ----

    [Theory]
    [InlineData("Query results", "Query results")]
    [InlineData("O'Brien", "O'Brien")]
    [InlineData("a/b:c*d?e[f]g\\h", "a b c d e f g h")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("history", "Results")]
    [InlineData("About", "About (data)")]
    [InlineData("about", "About (data)")]
    [InlineData("", "Results")]
    [InlineData(null, "Results")]
    [InlineData("///", "Results")]
    public void A_sheet_name_is_made_safe(string? title, string expected)
    {
        Assert.Equal(expected, XlsxWriter.SheetName(title));
    }

    [Fact]
    public void A_long_sheet_name_is_cut_to_31_characters_without_ending_in_a_space()
    {
        var name = XlsxWriter.SheetName("Servers with no PTR record in the Production view");

        Assert.Equal(31, name.Length);
        Assert.Equal(name.TrimEnd(), name);

        var spaced = XlsxWriter.SheetName(new string('a', 30) + " tail");
        Assert.Equal(new string('a', 30), spaced);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    [InlineData(16_383, "XFD")]
    public void Columns_are_named_the_way_excel_names_them(int index, string expected)
    {
        Assert.Equal(expected, XlsxWriter.ColumnName(index));
    }

    // ---- A bigger round trip ----

    [Fact]
    public void A_mixed_result_comes_back_cell_for_cell()
    {
        var rows = new List<object?[]>();
        for (var i = 0; i < 2_000; i++)
        {
            rows.Add([(long)i, i / 7.0, $"host{i}.lab.test", i % 5 == 0 ? null : $"10.{i / 250}.{i % 250}.1", i % 11 == 0 ? $"line {i}\nsecond" : "plain"]);
        }
        var result = new QueryResult(["id", "ratio", "name", "address", "note"], rows, truncated: false, TimeSpan.Zero);

        var (file, notes) = Build(result);
        var cells = Cells(file);

        Assert.Empty(notes);
        for (var i = 0; i < rows.Count; i++)
        {
            var n = i + 2;
            Assert.Equal(i.ToString(), cells[$"A{n}"].Value);
            Assert.Equal(((double)rows[i][1]!).ToString("R", System.Globalization.CultureInfo.InvariantCulture), cells[$"B{n}"].Value);
            Assert.Equal($"host{i}.lab.test", cells[$"C{n}"].Value);
            if (rows[i][3] is null) Assert.False(cells.ContainsKey($"D{n}"));
            else Assert.Equal((string?)rows[i][3], cells[$"D{n}"].Value);
            Assert.Equal((string?)rows[i][4], cells[$"E{n}"].Value);
        }

        // Five header cells, then five cells a row except where the address is NULL (every fifth row).
        Assert.Equal(5 + 2_000 * 5 - 400, cells.Count);
    }
}

public class ResultExportXlsxTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"pfr-xlsx-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private static QueryResult Sample() =>
        new(["name", "count"], new object?[][] { ["web1", 3L], ["db1", 5L] }, truncated: false, TimeSpan.Zero);

    [Theory]
    [InlineData("report.xlsx", ExportFormat.Xlsx)]
    [InlineData("REPORT.XLSX", ExportFormat.Xlsx)]
    [InlineData("report.json", ExportFormat.Json)]
    [InlineData("report.csv", ExportFormat.Csv)]
    [InlineData("report", ExportFormat.Csv)]
    [InlineData("report.xls", ExportFormat.Csv)]
    public void The_extension_chooses_the_format(string name, ExportFormat expected)
    {
        Assert.Equal(expected, ResultExport.FormatFor(name));
    }

    [Fact]
    public void An_xlsx_name_writes_an_excel_workbook_named_after_the_report_and_leaves_no_temporary_file()
    {
        var path = Path.Combine(_folder, "sub", "servers.xlsx");

        var format = ResultExport.Write(Sample(), path, spreadsheetSafe: true, title: "Servers by role");

        Assert.Equal(ExportFormat.Xlsx, format);
        Assert.False(File.Exists(path + ".tmp"));
        using var zip = ZipFile.OpenRead(path);
        using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();
        var name = XDocument.Load(workbook).Descendants(XNamespace.Get("http://schemas.openxmlformats.org/spreadsheetml/2006/main") + "sheet").Single().Attribute("name")!.Value;
        Assert.Equal("Servers by role", name);
    }

    [Fact]
    public void Csv_and_json_are_written_as_before()
    {
        var csv = Path.Combine(_folder, "a.csv");
        var json = Path.Combine(_folder, "a.json");

        Assert.Equal(ExportFormat.Csv, ResultExport.Write(Sample(), csv, spreadsheetSafe: false));
        Assert.Equal(ExportFormat.Json, ResultExport.Write(Sample(), json, spreadsheetSafe: false));

        Assert.Equal("name,count", File.ReadAllLines(csv)[0].TrimStart('\uFEFF'));
        Assert.StartsWith("[", File.ReadAllText(json).TrimStart());
    }

    [Fact]
    public void A_result_that_cannot_be_an_excel_sheet_leaves_no_file_behind()
    {
        var columns = Enumerable.Range(0, XlsxWriter.MaxColumns + 1).Select(i => $"c{i}").ToArray();
        var path = Path.Combine(_folder, "wide.xlsx");

        Assert.Throws<ArgumentException>(() =>
            ResultExport.Write(new QueryResult(columns, new List<object?[]>(), false, TimeSpan.Zero), path, spreadsheetSafe: true));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void The_formats_are_described_in_words()
    {
        Assert.Equal("an Excel workbook", ResultExport.Describe(ExportFormat.Xlsx));
        Assert.Equal("CSV", ResultExport.Describe(ExportFormat.Csv));
        Assert.Equal("JSON", ResultExport.Describe(ExportFormat.Json));
    }
}
