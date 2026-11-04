using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;

namespace Pfr.Core.Query;

/// <summary>A cell that jumps to another sheet when clicked.</summary>
/// <param name="Row">The row of the sheet's data (0 is the first row below the header).</param>
/// <param name="Column">The column (0 is A).</param>
/// <param name="Sheet">The name of the sheet it opens, at A1.</param>
public sealed record XlsxLink(int Row, int Column, string Sheet);

/// <summary>One sheet of a workbook: its name, its header and rows, and the cells that link to other sheets.</summary>
public sealed record XlsxSheet(string Name, QueryResult Data, IReadOnlyList<XlsxLink>? Links = null);

/// <summary>
/// Writes query results as an Excel workbook (.xlsx), so it can be sent to a client as it is. Nothing is installed or
/// referenced for this: an .xlsx file is a zip of a few XML parts, and writing them directly keeps the application free of
/// a spreadsheet library.
/// <list type="bullet">
/// <item>Each sheet has a bold, shaded header row that stays in view while scrolling, a filter on every column, and column
/// widths that fit the content. Numbers are numbers; everything else is text.</item>
/// <item>Text is always stored as text, never as a formula, so a value such as <c>=cmd|...</c> that came from a DNS record
/// or a comment cannot run when the file is opened.</item>
/// <item>Anything Excel cannot hold is handled openly, not silently: rows past its limit, cells over 32,767 characters,
/// control characters, and whole numbers of more than 15 digits (which Excel would round) are dealt with and listed on an
/// "About" sheet, which is added only when there is something to say.</item>
/// <item>A workbook of several sheets can have cells that link to other sheets (a contents page, say).</item>
/// </list>
/// Each sheet is streamed to the zip one row at a time, so a result of a million rows does not need a million cells in
/// memory beyond the result itself.
/// </summary>
public static class XlsxWriter
{
    /// <summary>Rows in an Excel sheet, header included.</summary>
    public const int MaxRows = 1_048_576;

    public const int MaxColumns = 16_384;

    /// <summary>Characters in one cell.</summary>
    public const int MaxCellCharacters = 32_767;

    public const int MaxSheetNameLength = 31;

    /// <summary>Excel keeps 15 significant digits, so a whole number longer than that would be rounded.</summary>
    private const long LargestExactInteger = 999_999_999_999_999;

    /// <summary>How many rows are measured to choose column widths.</summary>
    private const int WidthSampleRows = 1_000;

    private const int StyleHeader = 1;
    private const int StyleWrap = 2;
    private const int StyleBold = 3;
    private const int StyleLink = 4;

    private const string AboutSheetName = "About";
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    /// <summary>Writes one result as a workbook with a single data sheet. <paramref name="output"/> is left open.</summary>
    /// <param name="output">Where to write (a file or memory).</param>
    /// <param name="result">What to write.</param>
    /// <param name="title">The name of the data sheet (made safe for Excel: 31 characters, none of []:*?/\).</param>
    /// <param name="exportedAt">The time shown on the About sheet (now by default).</param>
    /// <returns>What had to be left out or changed, in words (empty when nothing was). The same is on the About sheet.</returns>
    /// <exception cref="ArgumentException">The result has more columns than a worksheet can hold.</exception>
    public static IReadOnlyList<string> Write(Stream output, QueryResult result, string? title = null, DateTimeOffset? exportedAt = null) =>
        WriteSheets(output, [new XlsxSheet(SheetName(title), result)], title, exportedAt, single: true);

    /// <summary>
    /// Writes a workbook of several sheets, in the order given, with the cells that link from one to another. Sheet names
    /// have to be ones Excel accepts and be different from each other (see <see cref="UniqueSheetNames"/>).
    /// </summary>
    /// <param name="output">Where to write. It is left open.</param>
    /// <param name="sheets">The sheets; the first is the one shown when the file is opened.</param>
    /// <param name="title">Named on the About sheet, which is added only if something had to be cut or changed.</param>
    /// <param name="exportedAt">The time shown on the About sheet (now by default).</param>
    /// <returns>What had to be left out or changed, in words, each beginning with the sheet it was on.</returns>
    /// <exception cref="ArgumentException">No sheets, a name Excel would not accept or that is used twice, a link to a sheet
    /// that is not there, or a sheet with more columns than a worksheet can hold.</exception>
    public static IReadOnlyList<string> WriteWorkbook(
        Stream output, IReadOnlyList<XlsxSheet> sheets, string? title = null, DateTimeOffset? exportedAt = null)
    {
        if (sheets.Count == 0) throw new ArgumentException("A workbook needs at least one sheet.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            if (sheet.Name != SheetName(sheet.Name) || sheet.Name.Equals(AboutSheetName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"\"{sheet.Name}\" is not a sheet name Excel accepts (or is reserved).");
            if (!names.Add(sheet.Name)) throw new ArgumentException($"There are two sheets called \"{sheet.Name}\".");
        }
        foreach (var link in sheets.SelectMany(s => s.Links ?? []))
        {
            if (!names.Contains(link.Sheet)) throw new ArgumentException($"A link points at \"{link.Sheet}\", which is not a sheet of this workbook.");
        }

        return WriteSheets(output, sheets, title, exportedAt, single: false);
    }

    private static IReadOnlyList<string> WriteSheets(
        Stream output, IReadOnlyList<XlsxSheet> sheets, string? title, DateTimeOffset? exportedAt, bool single)
    {
        foreach (var sheet in sheets)
        {
            if (sheet.Data.Columns.Count > MaxColumns)
                throw new ArgumentException($"An Excel sheet holds at most {MaxColumns:N0} columns; this result has {sheet.Data.Columns.Count:N0}.");
        }

        var rowCounts = sheets.Select(s => Math.Min(s.Data.Rows.Count, MaxRows - 1)).ToArray();
        var scans = sheets.Select((s, i) => Scan(s.Data, rowCounts[i])).ToArray();

        var notes = new List<string>();
        for (var i = 0; i < sheets.Count; i++)
        {
            foreach (var note in NotesFor(sheets[i].Data, rowCounts[i], scans[i]))
                notes.Add(single ? note : $"{sheets[i].Name}: {note}");
        }
        var hasAbout = notes.Count > 0;

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        AddPart(zip, "[Content_Types].xml", ContentTypes(sheets.Count, hasAbout));
        AddPart(zip, "_rels/.rels", RootRelationships);
        AddPart(zip, "xl/workbook.xml", Workbook(sheets, rowCounts, hasAbout));
        AddPart(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count, hasAbout));
        AddPart(zip, "xl/styles.xml", Styles);

        for (var i = 0; i < sheets.Count; i++)
        {
            var index = i;
            var links = new Dictionary<(int Row, int Column), string>();
            foreach (var link in sheets[i].Links ?? []) links[(link.Row, link.Column)] = link.Sheet;
            AddXml(zip, $"xl/worksheets/sheet{i + 1}.xml",
                w => WriteDataSheet(w, sheets[index].Data, rowCounts[index], scans[index].Widths, links, tabSelected: index == 0));
        }

        if (hasAbout)
        {
            var facts = new List<(string Label, string Value)>();
            if (!string.IsNullOrWhiteSpace(title)) facts.Add(("Report", title.Trim()));
            if (single)
            {
                facts.Add(("Data sheet", sheets[0].Name));
            }
            else
            {
                facts.Add(("Sheets", sheets.Count.ToString("N0", CultureInfo.InvariantCulture)));
            }
            facts.Add(("Rows exported", rowCounts.Sum(c => (long)c).ToString("N0", CultureInfo.InvariantCulture)));
            facts.Add(("Exported", (exportedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)));
            AddXml(zip, $"xl/worksheets/sheet{sheets.Count + 1}.xml", w => WriteAboutSheet(w, facts, notes));
        }
        return notes;
    }

    /// <summary>A name Excel accepts for a sheet: at most 31 characters, none of []:*?/\, not starting or ending with an apostrophe.</summary>
    public static string SheetName(string? title)
    {
        var name = new string((title ?? "").Select(c => c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' || char.IsControl(c) ? ' ' : c).ToArray());
        name = name.Trim().Trim('\'').Trim();
        if (name.Length > MaxSheetNameLength) name = name[..MaxSheetNameLength];
        name = name.TrimEnd(' ', '\'');
        name = name.TrimStart(' ', '\'');

        // "History" is reserved by Excel, and "About" is the name of the notes sheet.
        if (name.Length == 0 || name.Equals("History", StringComparison.OrdinalIgnoreCase)) return "Results";
        return name.Equals(AboutSheetName, StringComparison.OrdinalIgnoreCase) ? "About (data)" : name;
    }

    /// <summary>
    /// Sheet names for a list of wanted names, in the same order: each made safe for Excel (see <see cref="SheetName"/>), and
    /// any that would repeat an earlier one (Excel ignores case) or one of the <paramref name="reserved"/> names given a
    /// "~2", "~3"... on the end, with the name cut so it still fits in 31 characters.
    /// </summary>
    public static List<string> UniqueSheetNames(IEnumerable<string> wanted, IEnumerable<string>? reserved = null)
    {
        var used = new HashSet<string>(reserved ?? [], StringComparer.OrdinalIgnoreCase) { AboutSheetName };
        var names = new List<string>();

        foreach (var want in wanted)
        {
            var name = SheetName(want);
            if (!used.Add(name))
            {
                for (var number = 2; ; number++)
                {
                    var suffix = $"~{number}";
                    var candidate = name.Length + suffix.Length > MaxSheetNameLength
                        ? name[..(MaxSheetNameLength - suffix.Length)].TrimEnd(' ', '\'') + suffix
                        : name + suffix;
                    if (!used.Add(candidate)) continue;
                    name = candidate;
                    break;
                }
            }
            names.Add(name);
        }
        return names;
    }

    /// <summary>The letters of a column: 0 is A, 25 is Z, 26 is AA.</summary>
    public static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    // ---- What the data needs, found before anything is written ----

    private sealed class Scan_
    {
        public double[] Widths = [];
        public int LongCells;
        public int UnstorableCells;
        public int LargeIntegers;
    }

    private static Scan_ Scan(QueryResult result, int rowCount)
    {
        var widths = result.Columns.Select(c => (double)LongestLine(c)).ToArray();
        var scan = new Scan_ { Widths = widths };

        for (var r = 0; r < rowCount; r++)
        {
            var row = result.Rows[r];
            for (var c = 0; c < widths.Length && c < row.Length; c++)
            {
                var value = row[c];
                if (value is null) continue;

                if (value is long l && !IsExact(l))
                {
                    scan.LargeIntegers++;
                }
                else if (value is string s)
                {
                    if (s.Length > MaxCellCharacters) scan.LongCells++;
                    else if (HasUnstorable(s)) scan.UnstorableCells++;
                }

                if (r < WidthSampleRows)
                {
                    var length = value is string text ? LongestLine(text) : QueryResult.Format(value).Length;
                    if (length > widths[c]) widths[c] = length;
                }
            }
        }

        for (var c = 0; c < widths.Length; c++) widths[c] = Math.Clamp(widths[c] + 2, 8, 60);
        return scan;
    }

    private static List<string> NotesFor(QueryResult result, int rowCount, Scan_ scan)
    {
        var notes = new List<string>();
        if (result.Truncated)
        {
            notes.Add($"The query reached its row limit, so only the first {result.Rows.Count:N0} rows were returned and exported. " +
                      "There are more rows than this workbook holds.");
        }
        if (rowCount < result.Rows.Count)
        {
            notes.Add($"Excel holds at most {MaxRows - 1:N0} data rows on a sheet, so the last {result.Rows.Count - rowCount:N0} rows are not in this workbook.");
        }
        if (scan.LongCells > 0)
            notes.Add($"{Cells(scan.LongCells)} longer than Excel's limit of {MaxCellCharacters:N0} characters {(scan.LongCells == 1 ? "was" : "were")} shortened.");
        if (scan.UnstorableCells > 0)
            notes.Add($"{Cells(scan.UnstorableCells)} contained control characters Excel cannot store; {(scan.UnstorableCells == 1 ? "it was" : "they were")} replaced with \uFFFD.");
        if (scan.LargeIntegers > 0)
        {
            notes.Add($"{scan.LargeIntegers:N0} whole number{(scan.LargeIntegers == 1 ? "" : "s")} of more than 15 digits " +
                      $"{(scan.LargeIntegers == 1 ? "was" : "were")} written as text, because Excel would round {(scan.LargeIntegers == 1 ? "it" : "them")}.");
        }
        return notes;
    }

    private static string Cells(int n) => $"{n:N0} cell{(n == 1 ? "" : "s")}";

    private static bool IsExact(long value) => value is >= -LargestExactInteger and <= LargestExactInteger;

    private static int LongestLine(string text)
    {
        if (text.IndexOf('\n') < 0) return text.Length;
        return text.Split('\n').Max(line => line.TrimEnd('\r').Length);
    }

    /// <summary>True if the text has a character an XML file cannot hold (control characters, a lone surrogate, U+FFFE / U+FFFF).</summary>
    private static bool HasUnstorable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD)) continue;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }
            return true;
        }
        return false;
    }

    /// <summary>The text as it will be stored: cut to Excel's limit, line breaks as plain line feeds, unstorable characters replaced.</summary>
    private static string Clean(string text)
    {
        if (text.Length > MaxCellCharacters) text = text[..MaxCellCharacters];
        if (!HasUnstorable(text) && text.IndexOf('\r') < 0) return text;

        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                clean.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                clean.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (c is '\t' or '\n' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
            {
                clean.Append(c);
            }
            else
            {
                clean.Append('\uFFFD');
            }
        }
        return clean.ToString();
    }

    // ---- The parts of the file ----

    private static void AddPart(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(Declaration);
        writer.Write(xml);
    }

    private static void AddXml(ZipArchive zip, string name, Action<XmlWriter> body)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            CloseOutput = false,
            NewLineHandling = NewLineHandling.None, // line breaks in cells are plain \n by now, and are kept as they are
        });
        writer.WriteStartDocument(standalone: true);
        body(writer);
        writer.WriteEndDocument();
    }

    private const string RootRelationships =
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string WorksheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

    private static string ContentTypes(int sheetCount, bool hasAbout)
    {
        var text = new StringBuilder(
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        for (var i = 1; i <= sheetCount + (hasAbout ? 1 : 0); i++)
            text.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"{WorksheetContentType}\"/>");
        text.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        return text.ToString();
    }

    private static string Workbook(IReadOnlyList<XlsxSheet> sheets, int[] rowCounts, bool hasAbout)
    {
        var listed = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
            listed.Append($"<sheet name=\"{Esc(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        if (hasAbout) listed.Append($"<sheet name=\"{AboutSheetName}\" sheetId=\"{sheets.Count + 1}\" r:id=\"rId{sheets.Count + 1}\"/>");

        // Excel records each filtered range as a hidden name; it opens the file without them, but this is what it writes itself.
        var names = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
        {
            var columns = sheets[i].Data.Columns.Count;
            if (columns == 0) continue;
            names.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">")
                .Append(Esc($"'{sheets[i].Name.Replace("'", "''")}'!$A$1:${ColumnName(columns - 1)}${rowCounts[i] + 1}"))
                .Append("</definedName>");
        }
        var defined = names.Length == 0 ? "" : $"<definedNames>{names}</definedNames>";

        return $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets>{listed}</sheets>{defined}</workbook>";
    }

    private static string WorkbookRelationships(int sheetCount, bool hasAbout)
    {
        var text = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 1; i <= sheetCount + (hasAbout ? 1 : 0); i++)
            text.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");

        // The styles come after the sheets (after the About sheet's place too, which is reserved whether or not it is there).
        text.Append($"<Relationship Id=\"rId{sheetCount + 2}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        return text.Append("</Relationships>").ToString();
    }

    /// <summary>Five looks: 0 plain, 1 header (bold, pale blue, a line under it), 2 wrapped text, 3 bold, 4 a link (blue, underlined).</summary>
    private static readonly string Styles =
        $"<styleSheet xmlns=\"{Main}\">" +
        "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font>" +
        "<font><u/><sz val=\"11\"/><color rgb=\"FF0563C1\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left/><right/><top/><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"5\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "<dxfs count=\"0\"/><tableStyles count=\"0\" defaultTableStyle=\"TableStyleMedium9\" defaultPivotStyle=\"PivotStyleLight16\"/>" +
        "</styleSheet>";

    private static string Esc(string text) => SecurityElement.Escape(text) ?? "";

    // ---- A data sheet ----

    private static void WriteDataSheet(
        XmlWriter w, QueryResult result, int rowCount, double[] widths,
        Dictionary<(int Row, int Column), string> links, bool tabSelected)
    {
        var columns = result.Columns.Count;
        var last = columns == 0 ? "A1" : $"{ColumnName(columns - 1)}{rowCount + 1}";
        var names = Enumerable.Range(0, columns).Select(ColumnName).ToArray();

        w.WriteStartElement("worksheet", Main);
        w.WriteAttributeString("xmlns", "r", null, Relationships);

        Empty(w, "dimension", ("ref", columns == 0 ? "A1" : $"A1:{last}"));

        w.WriteStartElement("sheetViews", Main);
        w.WriteStartElement("sheetView", Main);
        if (tabSelected) w.WriteAttributeString("tabSelected", "1");
        w.WriteAttributeString("workbookViewId", "0");
        if (columns > 0)
        {
            // The header row stays in view while the rows below it scroll.
            Empty(w, "pane", ("ySplit", "1"), ("topLeftCell", "A2"), ("activePane", "bottomLeft"), ("state", "frozen"));
            Empty(w, "selection", ("pane", "bottomLeft"));
        }
        w.WriteEndElement();
        w.WriteEndElement();

        Empty(w, "sheetFormatPr", ("defaultRowHeight", "15"));

        if (columns > 0)
        {
            w.WriteStartElement("cols", Main);
            for (var c = 0; c < columns; c++)
            {
                var number = (c + 1).ToString(CultureInfo.InvariantCulture);
                Empty(w, "col", ("min", number), ("max", number),
                    ("width", widths[c].ToString("0.##", CultureInfo.InvariantCulture)), ("customWidth", "1"));
            }
            w.WriteEndElement();
        }

        w.WriteStartElement("sheetData", Main);
        if (columns > 0)
        {
            w.WriteStartElement("row", Main);
            w.WriteAttributeString("r", "1");
            for (var c = 0; c < columns; c++) WriteText(w, names[c] + "1", Clean(result.Columns[c]), StyleHeader);
            w.WriteEndElement();

            for (var r = 0; r < rowCount; r++)
            {
                var row = result.Rows[r];
                var number = (r + 2).ToString(CultureInfo.InvariantCulture);

                w.WriteStartElement("row", Main);
                w.WriteAttributeString("r", number);
                for (var c = 0; c < columns && c < row.Length; c++)
                {
                    if (links.Count > 0 && links.ContainsKey((r, c)) && row[c] is { } linked)
                    {
                        var text = Clean(QueryResult.Format(linked));
                        if (text.Length > 0) WriteText(w, names[c] + number, text, StyleLink);
                    }
                    else
                    {
                        WriteCell(w, names[c] + number, row[c]);
                    }
                }
                w.WriteEndElement();
            }
        }
        w.WriteEndElement(); // sheetData

        if (columns > 0) Empty(w, "autoFilter", ("ref", $"A1:{last}"));

        // A click on a linked cell goes to the top of the other sheet. (Links inside the workbook need no relationship.)
        var shown = links.Where(l => l.Key.Row >= 0 && l.Key.Row < rowCount && l.Key.Column >= 0 && l.Key.Column < columns)
            .OrderBy(l => l.Key.Row).ThenBy(l => l.Key.Column).ToList();
        if (shown.Count > 0)
        {
            w.WriteStartElement("hyperlinks", Main);
            foreach (var (cell, target) in shown)
            {
                Empty(w, "hyperlink",
                    ("ref", names[cell.Column] + (cell.Row + 2).ToString(CultureInfo.InvariantCulture)),
                    ("location", $"'{target.Replace("'", "''")}'!A1"));
            }
            w.WriteEndElement();
        }

        Empty(w, "pageMargins", ("left", "0.7"), ("right", "0.7"), ("top", "0.75"), ("bottom", "0.75"), ("header", "0.3"), ("footer", "0.3"));
        w.WriteEndElement(); // worksheet
    }

    private static void WriteCell(XmlWriter w, string reference, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case long l when IsExact(l):
                WriteNumber(w, reference, l.ToString(CultureInfo.InvariantCulture));
                return;
            case double d when double.IsFinite(d):
                WriteNumber(w, reference, d.ToString("R", CultureInfo.InvariantCulture));
                return;
        }

        var text = Clean(QueryResult.Format(value));
        if (text.Length > 0) WriteText(w, reference, text, text.Contains('\n') ? StyleWrap : 0);
    }

    private static void WriteNumber(XmlWriter w, string reference, string number)
    {
        w.WriteStartElement("c", Main);
        w.WriteAttributeString("r", reference);
        w.WriteElementString("v", Main, number);
        w.WriteEndElement();
    }

    /// <summary>A text cell. It is stored as a string, so even text that starts with = is shown as it is and never calculated.</summary>
    private static void WriteText(XmlWriter w, string reference, string text, int style)
    {
        w.WriteStartElement("c", Main);
        w.WriteAttributeString("r", reference);
        if (style != 0) w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("t", "inlineStr");
        w.WriteStartElement("is", Main);
        w.WriteStartElement("t", Main);
        w.WriteAttributeString("xml", "space", null, "preserve");
        w.WriteString(text);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Empty(XmlWriter w, string name, params (string Name, string Value)[] attributes)
    {
        w.WriteStartElement(name, Main);
        foreach (var (attribute, value) in attributes) w.WriteAttributeString(attribute, value);
        w.WriteEndElement();
    }

    // ---- The About sheet ----

    private static void WriteAboutSheet(XmlWriter w, IReadOnlyList<(string Label, string Value)> facts, IReadOnlyList<string> notes)
    {
        var rows = new List<(string Label, string Value, int Style)>();
        rows.AddRange(facts.Select(f => (f.Label, f.Value, 0)));
        rows.AddRange(notes.Select(n => ("Note", n, StyleWrap)));

        w.WriteStartElement("worksheet", Main);
        w.WriteAttributeString("xmlns", "r", null, Relationships);
        Empty(w, "dimension", ("ref", $"A1:B{rows.Count}"));
        w.WriteStartElement("sheetViews", Main);
        Empty(w, "sheetView", ("workbookViewId", "0"));
        w.WriteEndElement();
        Empty(w, "sheetFormatPr", ("defaultRowHeight", "15"));
        w.WriteStartElement("cols", Main);
        Empty(w, "col", ("min", "1"), ("max", "1"), ("width", "18"), ("customWidth", "1"));
        Empty(w, "col", ("min", "2"), ("max", "2"), ("width", "90"), ("customWidth", "1"));
        w.WriteEndElement();

        w.WriteStartElement("sheetData", Main);
        for (var i = 0; i < rows.Count; i++)
        {
            var number = (i + 1).ToString(CultureInfo.InvariantCulture);
            w.WriteStartElement("row", Main);
            w.WriteAttributeString("r", number);
            WriteText(w, "A" + number, rows[i].Label, StyleBold);
            WriteText(w, "B" + number, Clean(rows[i].Value), rows[i].Style);
            w.WriteEndElement();
        }
        w.WriteEndElement();

        Empty(w, "pageMargins", ("left", "0.7"), ("right", "0.7"), ("top", "0.75"), ("bottom", "0.75"), ("header", "0.3"), ("footer", "0.3"));
        w.WriteEndElement();
    }
}
