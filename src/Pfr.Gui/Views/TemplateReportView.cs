using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// A saved report template run on the grid that is open: its name and description, the SQL it runs, any parameters it
/// asks for, and the results as the usual searchable, sortable, copyable table that can also be exported. A report
/// without parameters runs as soon as it is shown (and again when the grid changes); one with parameters runs when
/// every required value has been filled in (their defaults count), and whenever Run is clicked. The values you used
/// are remembered per report while the page is open. Edit opens it in Explore data; Delete removes its file.
/// </summary>
public sealed class TemplateReportView : UserControl
{
    private static readonly int[] Limits = [1_000, 10_000, 100_000];

    private readonly SnapshotDatabaseCache _cache;
    private readonly ReportTemplateStore _templates;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _usedValues = new(StringComparer.Ordinal);

    private readonly TextBlock _title = Ui.Heading("Report");
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly TextBlock _context = new() { Opacity = 0.85 };
    private readonly ParameterForm _parameters = new();
    private readonly TextBox _sql;
    private readonly ComboBox _limit = new() { ItemsSource = Limits, SelectedItem = 10_000, Width = 110 };
    private readonly Button _run = Ui.Accent("Run");
    private readonly Button _export = new() { Content = "Export…", IsEnabled = false };
    private readonly TextBlock _status = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _error = Ui.Message();
    private readonly ContentControl _results = new();

    private GridInventory? _inventory;
    private int _version;

    public TemplateReportView(SnapshotDatabaseCache cache, ReportTemplateStore templates)
    {
        _cache = cache;
        _templates = templates;

        _sql = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            Height = 110,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_sql, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        _run.Click += async (_, _) => await RunAsync();
        _export.Click += async (_, _) => await ExportAsync();

        var edit = new Button { Content = "Edit" };
        edit.Click += (_, _) => RequestEdit();

        var delete = Ui.Confirming("Delete report", Delete, "Click again to delete this report");

        var header = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(12, 12, 12, 8),
            Children =
            {
                _title,
                _description,
                _context,
                _parameters,
                _sql,
                Ui.Row(
                    _run,
                    new TextBlock { Text = "Rows", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0) },
                    _limit,
                    _export,
                    edit,
                    delete,
                    _status),
                _error,
            },
        };
        DockPanel.SetDock(header, Dock.Top);
        Content = new DockPanel { Children = { header, _results } };
    }

    /// <summary>The template being shown.</summary>
    public ReportTemplate? Template { get; private set; }

    /// <summary>The input boxes for the report's parameters (empty for a report without any).</summary>
    public ParameterForm Parameters => _parameters;

    /// <summary>The run started by <see cref="Show"/> (completed if there was nothing to run), for tests to wait on.</summary>
    public Task RunTask { get; private set; } = Task.CompletedTask;

    public QueryResult? LastResult { get; private set; }

    public FilterableTable? ResultTable { get; private set; }

    public string StatusText => _status.Text ?? "";

    public string ErrorText => _error.IsVisible ? _error.Text ?? "" : "";

    /// <summary>The SQL shown (read-only here; Edit opens it in Explore data).</summary>
    public string SqlText => _sql.Text ?? "";

    /// <summary>Raised when Edit is clicked.</summary>
    public event EventHandler<ReportTemplate>? EditRequested;

    /// <summary>Raised after the template's file has been deleted.</summary>
    public event EventHandler<ReportTemplate>? Deleted;

    /// <summary>
    /// Shows <paramref name="template"/> and, if a grid is open and every required parameter has a value, runs it.
    /// Values used earlier for the same report are put back; otherwise parameters start from their defaults.
    /// </summary>
    public void Show(ReportTemplate template, GridInventory? inventory, string context)
    {
        Template = template;
        _inventory = inventory;
        _title.Text = template.Name;
        _description.Text = template.Description;
        _description.IsVisible = template.Description.Length > 0;
        _sql.Text = template.Sql;
        _context.Text = inventory is null ? "" : $"Grid: {context}";
        LastResult = null;
        ResultTable = null;
        _export.IsEnabled = false;
        _error.IsVisible = false;
        _status.Text = "";
        _run.IsEnabled = inventory is not null;
        _version++; // anything still running for the previous report or grid is out of date now

        _parameters.SetParameters(template.Parameters);
        _parameters.SetValues(_usedValues.TryGetValue(template.Id, out var used)
            ? used
            : template.Parameters.ToDictionary(p => p.Name, p => p.Default, StringComparer.Ordinal));

        if (inventory is null)
        {
            _results.Content = Ui.Muted("Open a grid first: pick one with the Client › Grid switcher in the header, or use Open backup…");
            RunTask = Task.CompletedTask;
            return;
        }

        if (!CanRunNow())
        {
            _results.Content = Ui.Muted("Fill in the parameters above, then click Run.");
            RunTask = Task.CompletedTask;
            return;
        }

        _results.Content = Ui.Muted("Running…");
        RunTask = RunAsync();
    }

    /// <summary>Asks for the report to be opened in Explore data for editing (what the Edit button does).</summary>
    public void RequestEdit()
    {
        if (Template is { } template) EditRequested?.Invoke(this, template);
    }

    /// <summary>Deletes the report's file and tells the page (what the Delete button does once confirmed).</summary>
    public void Delete()
    {
        if (Template is not { } template) return;
        try
        {
            _templates.Delete(template.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Ui.ShowError(_error, $"Could not delete the report: {ex.Message}");
            return;
        }
        _usedValues.Remove(template.Id);
        Deleted?.Invoke(this, template);
    }

    /// <summary>
    /// Runs the report on the open grid with the parameter values in the boxes. Returns false (and shows why) if it
    /// can't: no grid, a missing or invalid value, or a problem with the SQL or the data. A result that arrives
    /// after another report or grid was shown is dropped.
    /// </summary>
    public async Task<bool> RunAsync()
    {
        if (Template is not { } template || _inventory is null)
        {
            Ui.ShowError(_error, "Open a grid first.");
            return false;
        }

        IReadOnlyDictionary<string, object?> bound;
        try
        {
            bound = QueryParameters.Bind(template.Parameters, _parameters.Values);
        }
        catch (QueryException ex)
        {
            Ui.ShowError(_error, ex.Message);
            return false;
        }
        _usedValues[template.Id] = _parameters.Values;

        var inventory = _inventory;
        var version = ++_version;
        var rows = _limit.SelectedItem is int n ? n : 10_000;

        _run.IsEnabled = false;
        _error.IsVisible = false;
        _status.Text = "Running…";
        try
        {
            var database = await _cache.GetAsync(inventory);

            // Running the query and turning its rows (up to 100,000) into table rows both happen off the UI thread.
            var (result, table) = await Task.Run(() =>
            {
                var executed = database.Execute(template.Sql, rows, parameters: bound);
                return (executed, ResultTables.Build(executed));
            });
            if (version != _version) return false;

            LastResult = result;
            ResultTable = new FilterableTable(table);
            _results.Content = ResultTable;
            _status.Text = result.Summary();
            _export.IsEnabled = true;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // QueryException for mistakes in the SQL; anything else is reported instead of ending the app.
            if (version == _version)
            {
                Ui.ShowError(_error, ex.Message);
                _status.Text = "";
                _results.Content = null;
            }
            return false;
        }
        finally
        {
            if (version == _version) _run.IsEnabled = _inventory is not null;
        }
    }

    /// <summary>Asks where to save the last result and writes it (an Excel workbook for a .xlsx name, JSON for .json, otherwise CSV).</summary>
    public async Task<bool> ExportAsync()
    {
        if (LastResult is null)
        {
            Ui.ShowError(_error, "Run the report first, then export its results.");
            return false;
        }

        var name = Template is { } template ? ReportTemplateStore.Slug(template.Name) : "report";
        var path = await ResultExportDialog.AskAsync(this, name);
        return path is not null && await ExportToFileAsync(path);
    }

    /// <summary>Writes the last result to this file: Excel for a .xlsx name, JSON for .json, otherwise CSV (spreadsheet formulas neutralised). An Excel sheet is named after the report.</summary>
    public async Task<bool> ExportToFileAsync(string path)
    {
        if (LastResult is not { } result)
        {
            Ui.ShowError(_error, "Run the report first, then export its results.");
            return false;
        }

        try
        {
            var format = await Task.Run(() => ResultExport.Write(result, path, spreadsheetSafe: true, title: Template?.Name ?? "Report"));
            _error.IsVisible = false;
            _status.Text = $"Exported {result.Rows.Count:N0} row{(result.Rows.Count == 1 ? "" : "s")} to {Path.GetFullPath(path)} as {ResultExport.Describe(format)}" +
                           (result.Truncated ? ". The result was cut off by the row limit; raise Rows to export more." : ".");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Ui.ShowError(_error, $"Could not write {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>True if the values in the boxes are enough to run: nothing required is missing or invalid.</summary>
    private bool CanRunNow()
    {
        if (Template is not { } template) return false;
        try
        {
            QueryParameters.Bind(template.Parameters, _parameters.Values);
            return true;
        }
        catch (QueryException)
        {
            return false;
        }
    }
}
