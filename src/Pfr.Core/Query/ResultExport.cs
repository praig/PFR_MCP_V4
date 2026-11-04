using System.Text;

namespace Pfr.Core.Query;

public enum ExportFormat { Csv, Json, Xlsx }

/// <summary>
/// Writes a query result to a file, in the format its name asks for: .json is JSON, .xlsx is an Excel workbook, anything
/// else is CSV. A CSV file starts with a UTF-8 byte-order mark so Excel reads accented characters correctly. The file is
/// written beside its final name and moved into place, so a failure never leaves a half-written file.
/// </summary>
public static class ResultExport
{
    public static ExportFormat FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => ExportFormat.Json,
        ".xlsx" => ExportFormat.Xlsx,
        _ => ExportFormat.Csv,
    };

    /// <summary>The format in words, for messages: "CSV", "JSON" or "an Excel workbook".</summary>
    public static string Describe(ExportFormat format) => format switch
    {
        ExportFormat.Json => "JSON",
        ExportFormat.Xlsx => "an Excel workbook",
        _ => "CSV",
    };

    /// <summary>Writes the result and returns the format used. Missing folders are created.</summary>
    /// <param name="spreadsheetSafe">For CSV: keep a spreadsheet from running cells that start with = + - @ as formulas
    /// (see <see cref="QueryResult.WriteCsv"/>). An Excel workbook never needs it: its text cells are stored as text.</param>
    /// <param name="title">For Excel: the name of the sheet (a report's name, say). Ignored for CSV and JSON.</param>
    /// <exception cref="ArgumentException">An Excel workbook was asked for and the result has more columns than a sheet holds.</exception>
    public static ExportFormat Write(QueryResult result, string path, bool spreadsheetSafe, string? title = null)
    {
        var full = Path.GetFullPath(path);
        var format = FormatFor(full);
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder) Directory.CreateDirectory(folder);

        var temp = full + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
            {
                switch (format)
                {
                    case ExportFormat.Json:
                        result.WriteJson(stream);
                        break;
                    case ExportFormat.Xlsx:
                        XlsxWriter.Write(stream, result, title);
                        break;
                    default:
                    {
                        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                        result.WriteCsv(writer, spreadsheetSafe);
                        break;
                    }
                }
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
        return format;
    }
}
