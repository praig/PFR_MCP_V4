using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Pfr.Gui.Controls;

/// <summary>The "where do you want to save the results" dialog shared by Explore data and report pages.</summary>
public static class ResultExportDialog
{
    private static readonly FilePickerFileType Excel = new("Excel workbook") { Patterns = ["*.xlsx"] };
    private static readonly FilePickerFileType Csv = new("CSV (opens in Excel)") { Patterns = ["*.csv"] };
    private static readonly FilePickerFileType Json = new("JSON") { Patterns = ["*.json"] };

    /// <summary>
    /// Asks for a file name. The type chosen (or the extension typed) decides the format: Excel, CSV or JSON. Returns null
    /// if the dialog was cancelled or there is no window to show it in.
    /// </summary>
    public static async Task<string?> AskAsync(Control owner, string suggestedName)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null) return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export results",
            SuggestedFileName = suggestedName,
            DefaultExtension = "xlsx",
            FileTypeChoices = [Excel, Csv, Json],
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>For a report that only makes sense as a workbook: asks for an .xlsx file name. Null if cancelled.</summary>
    public static async Task<string?> AskWorkbookAsync(Control owner, string suggestedName)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null) return null;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create Excel workbook",
            SuggestedFileName = suggestedName,
            DefaultExtension = "xlsx",
            FileTypeChoices = [Excel],
        });
        return file?.TryGetLocalPath();
    }
}
