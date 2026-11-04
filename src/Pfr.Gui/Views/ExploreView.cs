using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// Explore data: read-only SQL against the grid that is open. The snapshot becomes an in-memory SQLite database the
/// first time it is needed for a grid (shared with report templates through <see cref="SnapshotDatabaseCache"/>);
/// nothing is stored and the stored snapshot can't be changed. A table browser shows what can be queried, results
/// can be searched, sorted, copied and exported, and a query can be saved as a report template, or a template opened
/// here to edit it.
/// <para>
/// Named parameters in the SQL (<c>:network</c>) get an input box each; their values are bound as data. Saving the
/// query as a report turns them into the report's declared parameters.
/// </para>
/// </summary>
public sealed class ExploreView : UserControl
{
    private static readonly int[] Limits = [1_000, 10_000, 100_000];

    private readonly SnapshotDatabaseCache _cache;
    private readonly ReportTemplateStore _templates;

    private readonly TextBox _query;
    private readonly Button _run = Ui.Accent("Run");
    private readonly Button _saveAs = new() { Content = "Save as report…" };
    private readonly Button _export = new() { Content = "Export…", IsEnabled = false };
    private readonly Button _selectAll = new() { Content = "SELECT * from this table", IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox _examples = new() { PlaceholderText = "Examples…", Width = 300 };
    private readonly ComboBox _limit = new() { ItemsSource = Limits, SelectedItem = 10_000, Width = 110 };
    private readonly TextBlock _status = new() { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _error = Ui.Message();
    private readonly TextBlock _context = new() { Opacity = 0.85 };
    private readonly ContentControl _results = new();
    private readonly ListBox _tableList = new() { Background = Brushes.Transparent };
    private readonly ListBox _columnList;
    private readonly TextBlock _columnTitle = Ui.Heading("Columns", 14);

    private readonly ParameterForm _parameters = new();
    private readonly StackPanel _parameterBox;

    private readonly TextBlock _banner = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _bannerRow;
    private readonly Border _templatePanel;
    private readonly TextBox _nameBox = new()
    {
        Watermark = "Name, e.g. Networks with no DHCP range",
        MaxLength = ReportTemplateStore.MaxNameLength,
        Width = 440,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly TextBox _descriptionBox = new()
    {
        Watermark = "What it shows (optional)",
        Width = 440,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly StackPanel _parameterEditors = new() { Spacing = 6 };
    private readonly Button _saveAsNew = new() { Content = "Save as a new report" };
    private readonly TextBlock _templateMessage = Ui.Message();

    private GridInventory? _inventory;
    private bool _busy;

    public ExploreView(SnapshotDatabaseCache cache, ReportTemplateStore templates)
    {
        _cache = cache;
        _templates = templates;

        _query = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Watermark = "SELECT … FROM records WHERE …   (Ctrl+Enter to run; use :name for a value you want to be asked for)",
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"),
            Height = 140,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_query, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _query.AddHandler(InputElement.KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        _query.TextChanged += (_, _) => RefreshParameters();

        _run.Click += async (_, _) => await RunAsync();
        _saveAs.Click += (_, _) => BeginSaveTemplate();
        _export.Click += async (_, _) => await ExportAsync();

        _examples.ItemsSource = ExampleQueries.All.Select(e => e.Title).ToList();
        _examples.SelectionChanged += (_, _) =>
        {
            if (_examples.SelectedIndex < 0) return;
            LoadExample(_examples.SelectedIndex);
            _examples.SelectedIndex = -1; // back to the "Examples…" prompt, so the same one can be picked again
        };

        _columnList = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<ColumnInfo>((c, _) => ColumnRow(c), supportsRecycling: false),
        };
        _tableList.SelectionChanged += (_, _) => ShowColumns();
        _tableList.DoubleTapped += (_, _) => InsertSelectAll();
        _columnList.DoubleTapped += (_, _) => AddSelectedColumn();

        _selectAll.Click += (_, _) => InsertSelectAll();

        var tables = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,*"),
            Margin = new Thickness(0, 0, 12, 0),
        };
        void Place(Control control, int row)
        {
            Grid.SetRow(control, row);
            tables.Children.Add(control);
        }
        Place(Ui.Heading("Tables", 14), 0);
        Place(Ui.Recessed(_tableList), 1);
        Place(_selectAll, 2);
        _columnTitle.Margin = new Thickness(0, 12, 0, 2);
        var columnHint = new TextBlock
        {
            Text = "Double-click a column to add it to the query.",
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Place(new StackPanel { Children = { _columnTitle, columnHint } }, 3);
        Place(Ui.Recessed(_columnList), 4);

        _parameterBox = new StackPanel
        {
            Spacing = 4,
            IsVisible = false,
            Children =
            {
                Ui.Muted("This query has parameters. Fill them in, then Run. Their values are passed to the query as data."),
                _parameters,
            },
        };

        // The report-template controls: a banner while a template is being edited, and the save panel.
        var stopEditing = new Button { Content = "Stop editing" };
        stopEditing.Click += (_, _) => StopEditing();
        _bannerRow = Ui.Row(_banner, stopEditing);
        _bannerRow.IsVisible = false;

        var saveReport = Ui.Accent("Save report");
        saveReport.Click += (_, _) => SaveTemplate(_nameBox.Text ?? "", _descriptionBox.Text ?? "", asNew: false, EditedParameters());
        _saveAsNew.Click += (_, _) => SaveTemplate(_nameBox.Text ?? "", _descriptionBox.Text ?? "", asNew: true, EditedParameters());
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => _templatePanel.IsVisible = false;

        _templatePanel = new Border
        {
            IsVisible = false,
            Background = Ui.Surface,
            BorderBrush = Ui.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    Ui.Label("Report name"), _nameBox,
                    Ui.Label("Description"), _descriptionBox,
                    _parameterEditors,
                    new Border { Height = 8 },
                    Ui.Row(saveReport, _saveAsNew, cancel),
                    _templateMessage,
                    Ui.Muted($"Saved as a plain file in {_templates.Folder}, so it can be shared with a colleague. " +
                             "It appears under My reports and runs on whichever grid is open."),
                },
            },
        };

        var toolbar = Ui.Row(
            _run,
            new TextBlock { Text = "Rows", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0) },
            _limit,
            _examples,
            _saveAs,
            _export,
            _status);

        var header = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                Ui.Heading("Explore data"),
                Ui.Muted("Query the open grid's snapshot with SQL. Read-only: one SELECT at a time, on a temporary copy. " +
                         "Handy functions: ip_key(x) to order addresses, cidr_contains(prefix, x), ip_in_range(x, start, end)."),
                _context,
                _bannerRow,
                _query,
                _parameterBox,
                toolbar,
                _templatePanel,
                _error,
            },
        };
        DockPanel.SetDock(header, Dock.Top);

        var right = new DockPanel { Children = { header, _results } };
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            Margin = new Thickness(12),
        };
        layout.Children.Add(tables);
        Grid.SetColumn(right, 1);
        layout.Children.Add(right);
        Content = layout;

        SetInventory(null, "");
    }

    /// <summary>The SQL in the editor.</summary>
    public string QueryText
    {
        get => _query.Text ?? "";
        set
        {
            _query.Text = value;
            RefreshParameters(); // don't rely on the text box's change event when code sets the text
        }
    }

    /// <summary>The input boxes for the parameters in the SQL (empty when it has none).</summary>
    public ParameterForm Parameters => _parameters;

    /// <summary>The result of the last successful query, or null.</summary>
    public QueryResult? LastResult { get; private set; }

    /// <summary>The grid of the last successful query, or null.</summary>
    public FilterableTable? ResultTable { get; private set; }

    /// <summary>The summary line ("12 rows in 3 ms") or the loading message.</summary>
    public string StatusText => _status.Text ?? "";

    /// <summary>The message shown when a query can't be run (empty when there is none).</summary>
    public string ErrorText => _error.IsVisible ? _error.Text ?? "" : "";

    /// <summary>The tables listed in the browser.</summary>
    public IReadOnlyList<string> TableNames =>
        (_tableList.ItemsSource as IEnumerable<TableItem> ?? []).Select(t => t.Info.Name).ToList();

    public int ExampleCount => ExampleQueries.All.Count;

    /// <summary>True while a table is selected in the browser (the "SELECT * from this table" button only shows then).</summary>
    public bool SelectAllButtonVisible => _selectAll.IsVisible;

    /// <summary>The report template being edited, or null for a new query.</summary>
    public ReportTemplate? EditingTemplate { get; private set; }

    /// <summary>Raised after a report template was saved (new or changed).</summary>
    public event EventHandler<ReportTemplate>? TemplateSaved;

    /// <summary>True while the "save as report" panel is open.</summary>
    public bool TemplatePanelVisible => _templatePanel.IsVisible;

    /// <summary>The problem shown in the save panel (empty when there is none).</summary>
    public string TemplateMessageText => _templateMessage.IsVisible ? _templateMessage.Text ?? "" : "";

    /// <summary>The "Editing report …" line (empty when no template is being edited).</summary>
    public string BannerText => _bannerRow.IsVisible ? _banner.Text ?? "" : "";

    /// <summary>The name and description currently typed in the save panel.</summary>
    public (string Name, string Description) TemplateFields => (_nameBox.Text ?? "", _descriptionBox.Text ?? "");

    /// <summary>How many parameter descriptions the save panel is showing (one per parameter in the SQL).</summary>
    public int ParameterEditorCount => _parameterEditors.Children.Count;

    /// <summary>Selects a table in the browser, showing its columns.</summary>
    public void SelectTable(string name) =>
        _tableList.SelectedItem = (_tableList.ItemsSource as IEnumerable<TableItem>)?.FirstOrDefault(t => t.Info.Name == name);

    /// <summary>The columns listed for the selected table.</summary>
    public IReadOnlyList<string> ColumnNames =>
        (_columnList.ItemsSource as IEnumerable<ColumnInfo> ?? []).Select(c => c.Name).ToList();

    /// <summary>Puts an example query in the editor.</summary>
    public void LoadExample(int index)
    {
        if (index >= 0 && index < ExampleQueries.All.Count) QueryText = ExampleQueries.All[index].Sql;
    }

    /// <summary>
    /// Opens a report template for editing: its SQL goes in the editor, its parameters start from their defaults,
    /// and saving updates the template (or, with "Save as a new report", makes a copy).
    /// </summary>
    public void LoadTemplate(ReportTemplate template)
    {
        StartEditing(template);
        QueryText = template.Sql;
        RefreshParameters();
        _parameters.SetValues(template.Parameters.ToDictionary(p => p.Name, p => p.Default, StringComparer.Ordinal));
        _templatePanel.IsVisible = false;
        _error.IsVisible = false;
    }

    /// <summary>Goes back to working on a new query; the template that was being edited is left as it was saved.</summary>
    public void StopEditing()
    {
        EditingTemplate = null;
        _bannerRow.IsVisible = false;
        _templatePanel.IsVisible = false;
        _saveAs.Content = "Save as report…";
        RefreshParameters();
    }

    /// <summary>Opens the save panel, filled in from the template being edited if there is one.</summary>
    public void BeginSaveTemplate()
    {
        _nameBox.Text = EditingTemplate?.Name ?? "";
        _descriptionBox.Text = EditingTemplate?.Description ?? "";
        _saveAsNew.IsVisible = EditingTemplate is not null;
        _templateMessage.IsVisible = false;

        _parameterEditors.Children.Clear();
        foreach (var definition in DefinitionsWithCurrentValues())
            _parameterEditors.Children.Add(new ParameterDefinitionEditor(definition));
        _templatePanel.IsVisible = true;
    }

    /// <summary>
    /// Saves the SQL in the editor as a report template. While editing one it updates that template (and renames it
    /// if the name changed), or with <paramref name="asNew"/> saves a separate copy. The parameters are the ones
    /// given, or by default one per <c>:name</c> in the SQL (keeping the description of an existing parameter, and
    /// starting a new one from the value typed in its box). Returns false, with the reason in the save panel, if
    /// it can't be saved: no name, a name already used, a problem with a parameter, or SQL that isn't read-only.
    /// </summary>
    public bool SaveTemplate(string name, string description, bool asNew = false, IReadOnlyList<ReportParameter>? parameters = null)
    {
        try
        {
            var basis = asNew ? null : EditingTemplate;
            var saved = _templates.Save(new ReportTemplate
            {
                Id = basis?.Id ?? "",
                Name = name,
                Description = description,
                Sql = QueryText,
                Parameters = (parameters ?? DefinitionsWithCurrentValues()).Select(p => p.Clone()).ToList(),
                CreatedUtc = basis?.CreatedUtc ?? DateTimeOffset.UtcNow,
            });

            StartEditing(saved);
            _templatePanel.IsVisible = false;
            _status.Text = $"Saved report “{saved.Name}”. It is now under My reports.";
            TemplateSaved?.Invoke(this, saved);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or QueryException or IOException or UnauthorizedAccessException)
        {
            Ui.ShowError(_templateMessage, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Sets the grid to query (null when none is open). Asking again for the same grid keeps the current results;
    /// a different grid (or none) discards them. The loaded data itself belongs to the shared cache.
    /// </summary>
    public void SetInventory(GridInventory? inventory, string context)
    {
        _context.Text = inventory is null ? "" : $"Grid: {context}";
        if (ReferenceEquals(inventory, _inventory) && _inventory is not null) return;

        _inventory = inventory;
        LastResult = null;
        ResultTable = null;
        _export.IsEnabled = false;
        _error.IsVisible = false;
        _run.IsEnabled = inventory is not null;
        ShowTables(SnapshotDatabase.Describe(), counted: false);

        if (inventory is null)
        {
            _status.Text = "";
            _results.Content = Ui.Muted("Open a grid first: pick one with the Client › Grid switcher in the header, or use Open backup…");
            return;
        }

        _status.Text = "Preparing the data…";
        _results.Content = Ui.Muted("Run a query to see results. Pick an example, or double-click a table to start.");
        _ = PrepareAsync(inventory);
    }

    /// <summary>
    /// Runs the SQL in the editor against the open grid. Returns false (and shows why) if it can't be run:
    /// no grid, an empty or non-read-only query, a missing or invalid parameter value, a mistake in the SQL,
    /// or a query that takes too long.
    /// </summary>
    public async Task<bool> RunAsync()
    {
        if (_inventory is null)
        {
            ShowError("Open a grid first.");
            return false;
        }
        if (_busy) return false;

        IReadOnlyDictionary<string, object?> bound;
        try
        {
            bound = QueryParameters.Bind(_parameters.Definitions, _parameters.Values);
        }
        catch (QueryException ex)
        {
            ShowError(ex.Message);
            return false;
        }

        var inventory = _inventory;
        var sql = QueryText;
        var rows = _limit.SelectedItem is int n ? n : 10_000;

        _busy = true;
        _run.IsEnabled = false;
        _error.IsVisible = false;
        _status.Text = "Running…";
        try
        {
            var database = await _cache.GetAsync(inventory);

            // Running the query and turning its rows (up to 100,000) into table rows both happen off the UI thread.
            var (result, table) = await Task.Run(() =>
            {
                var executed = database.Execute(sql, rows, parameters: bound);
                return (executed, ResultTables.Build(executed));
            });
            if (!ReferenceEquals(inventory, _inventory)) return false; // the grid changed while it ran

            LastResult = result;
            ResultTable = new FilterableTable(table);
            _results.Content = ResultTable;
            _status.Text = result.Summary();
            _export.IsEnabled = true;
            return true;
        }
        catch (QueryException ex)
        {
            ShowError(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A UI event: report the problem instead of ending the app.
            ShowError(ex.Message);
            return false;
        }
        finally
        {
            _busy = false;
            _run.IsEnabled = _inventory is not null;
        }
    }

    /// <summary>
    /// Asks where to save the last result and writes it (an Excel workbook for a .xlsx name, JSON for .json, otherwise CSV).
    /// Returns false if there was
    /// nothing to export, the dialog was cancelled, or the file couldn't be written.
    /// </summary>
    public async Task<bool> ExportAsync()
    {
        if (LastResult is null)
        {
            ShowError("Run a query first, then export its results.");
            return false;
        }

        var path = await ResultExportDialog.AskAsync(this, "query-results");
        return path is not null && await ExportToFileAsync(path);
    }

    /// <summary>Writes the last result to this file: Excel for a .xlsx name, JSON for .json, otherwise CSV (spreadsheet formulas neutralised).</summary>
    public async Task<bool> ExportToFileAsync(string path)
    {
        if (LastResult is not { } result)
        {
            ShowError("Run a query first, then export its results.");
            return false;
        }

        try
        {
            var format = await Task.Run(() => ResultExport.Write(result, path, spreadsheetSafe: true, title: "Query results"));
            _error.IsVisible = false;
            _status.Text = $"Exported {result.Rows.Count:N0} row{(result.Rows.Count == 1 ? "" : "s")} to {Path.GetFullPath(path)} as {ResultExport.Describe(format)}" +
                           (result.Truncated ? ". The result was cut off by the row limit; raise Rows to export more." : ".");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError($"Could not write {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shows an input box for each <c>:name</c> in the SQL, described by the template being edited if it has it.</summary>
    private void RefreshParameters()
    {
        IReadOnlyList<string> names;
        try
        {
            names = QueryParameters.FindNames(QueryText);
        }
        catch (QueryException)
        {
            names = []; // a "?" placeholder: running the query explains what to use instead
        }

        var declared = EditingTemplate?.Parameters ?? [];
        _parameters.SetParameters(names.Select(n => declared.FirstOrDefault(p => p.Name == n) ?? QueryParameters.AdHoc(n)).ToList());
        _parameterBox.IsVisible = _parameters.HasParameters;
    }

    /// <summary>
    /// One definition per parameter in the SQL: what the template being edited says about it, or for a new one a
    /// text parameter that starts as whatever is typed in its box (and is then required).
    /// </summary>
    private List<ReportParameter> DefinitionsWithCurrentValues()
    {
        var declared = EditingTemplate?.Parameters ?? [];
        var values = _parameters.Values;
        return _parameters.Definitions.Select(definition =>
        {
            var copy = definition.Clone();
            var known = declared.FirstOrDefault(p => p.Name == definition.Name);
            if (known is null)
            {
                copy.Default = values.TryGetValue(definition.Name, out var typed) ? typed.Trim() : "";
                if (copy.Default.Length > 0) copy.Required = true;
            }
            return copy;
        }).ToList();
    }

    private List<ReportParameter> EditedParameters() =>
        _parameterEditors.Children.OfType<ParameterDefinitionEditor>().Select(e => e.ToDefinition()).ToList();

    private void StartEditing(ReportTemplate template)
    {
        EditingTemplate = template;
        _banner.Text = $"Editing report “{template.Name}”. Change the SQL, then use Save report to update it.";
        _bannerRow.IsVisible = true;
        _saveAs.Content = "Save report…";
        RefreshParameters();
    }

    private async void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        await RunAsync();
    }

    /// <summary>Loads the data in the background and then shows the row counts.</summary>
    private async Task PrepareAsync(GridInventory inventory)
    {
        try
        {
            var database = await _cache.GetAsync(inventory);
            if (!ReferenceEquals(inventory, _inventory)) return;
            ShowTables(database.Tables, counted: true);
            if (_status.Text == "Preparing the data…") _status.Text = "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ReferenceEquals(inventory, _inventory)) ShowError($"Could not prepare the data: {ex.Message}");
        }
    }

    private void ShowTables(IReadOnlyList<TableInfo> tables, bool counted)
    {
        var selected = (_tableList.SelectedItem as TableItem)?.Info.Name;
        var items = tables.Select(t => new TableItem(t, counted)).ToList();
        _tableList.ItemsSource = items;
        _tableList.SelectedItem = items.FirstOrDefault(i => i.Info.Name == selected);
        ShowColumns();
    }

    private void ShowColumns()
    {
        if (_tableList.SelectedItem is TableItem item)
        {
            _columnTitle.Text = $"Columns of {item.Info.Name}";
            _columnList.ItemsSource = item.Info.Columns;
            _selectAll.Content = $"SELECT * from {item.Info.Name}";
            _selectAll.IsVisible = true;
        }
        else
        {
            _columnTitle.Text = "Columns";
            _columnList.ItemsSource = null;
            _selectAll.IsVisible = false;
        }
    }

    /// <summary>Puts <c>SELECT * FROM table LIMIT 100</c> for the selected table in the editor.</summary>
    public void InsertSelectAll()
    {
        if (_tableList.SelectedItem is TableItem item) QueryText = $"SELECT * FROM {item.Info.Name} LIMIT 100";
    }

    /// <summary>Adds the column selected in the browser to the query (what double-clicking a column does).</summary>
    public void AddSelectedColumn()
    {
        if (_tableList.SelectedItem is TableItem table && _columnList.SelectedItem is ColumnInfo column)
            AddColumn(table.Info.Name, column.Name);
    }

    /// <summary>
    /// Adds a column to the query: into the SELECT list unless it is already there, with its table added to FROM if
    /// needed (see <see cref="QueryBuilder"/>). A query too involved to edit gets <c>table.column</c> at the cursor.
    /// </summary>
    public void AddColumn(string table, string column)
    {
        var result = QueryBuilder.AddColumn(QueryText, table, column);
        if (result.Outcome == ColumnAddOutcome.NotEditable)
        {
            var text = QueryText;
            var at = Math.Clamp(_query.CaretIndex, 0, text.Length);
            QueryText = text.Insert(at, result.Fallback);
            _query.CaretIndex = at + result.Fallback.Length;
        }
        else if (result.Sql != QueryText)
        {
            QueryText = result.Sql;
        }

        _status.Text = result.Message;
        _query.Focus();
    }

    private void ShowError(string message) => Ui.ShowError(_error, message);

    private static Control ColumnRow(ColumnInfo? column)
    {
        if (column is null) return new TextBlock();
        return new StackPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = column.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = column.SqlType, Opacity = 0.55 },
                    },
                },
                new TextBlock { Text = column.Description, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            },
        };
    }

    private sealed record TableItem(TableInfo Info, bool Counted)
    {
        public override string ToString() => Counted ? $"{Info.Name}  ({Info.RowCount:N0})" : Info.Name;
    }
}
