using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Nios.Model;
using Pfr.Core.Query;
using Pfr.Core.Reports;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// Reports module: predefined reports that run on the grid currently open (chosen with the header switcher or
/// Open backup…), the data explorer, and the engineer's own report templates (saved queries, under My reports).
/// </summary>
public sealed class ReportsView : UserControl
{
    public const string LicenseExpiry = LicenseExpiryReport.Title;
    public const string DanglingCnames = DanglingCnameReport.Title;
    public const string ExploreData = "Explore data";

    private static readonly string[] BuiltIn = [LicenseExpiry, DanglingCnames, ExploreData];

    private readonly ReportTemplateStore _templates;
    private readonly SnapshotDatabaseCache _cache = new();
    private readonly ListBox _list;
    private readonly ListBox _templateList = new() { Background = Brushes.Transparent };
    private readonly TextBlock _noTemplates = new()
    {
        Text = "None yet. Write a query in Explore data and use Save as report.",
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.6,
        FontSize = 12,
        Margin = new Thickness(10, 2, 4, 0),
    };
    private readonly TextBlock _templateProblems = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 12,
        IsVisible = false,
        Margin = new Thickness(10, 6, 4, 0),
    };
    private readonly ContentControl _body = new();
    private GridInventory? _inventory;
    private string _context = "";
    private ExploreView? _explore;
    private TemplateReportView? _templatePage;
    private bool _syncing;

    private int _windowDays = 90;
    private bool _showResolving;
    private bool _showNotVerified = true;

    public ReportsView(ReportTemplateStore templates)
    {
        _templates = templates;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemsSource = BuiltIn,
        };
        _list.SelectionChanged += (_, _) => OnNavSelectionChanged(_list, _templateList);
        _templateList.SelectionChanged += (_, _) => OnNavSelectionChanged(_templateList, _list);

        var reload = new Button { Content = "Reload", FontSize = 12, Padding = new Thickness(8, 2) };
        reload.Click += (_, _) => RefreshTemplates();
        var myReports = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 14, 0, 6) };
        myReports.Children.Add(new TextBlock
        {
            Text = "My reports",
            Opacity = 0.6,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(reload, 1);
        myReports.Children.Add(reload);

        var nav = new Border
        {
            Width = 230,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = Ui.Divider,
            Padding = new Thickness(6, 10),
            Child = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "Reports", Opacity = 0.6, Margin = new Thickness(10, 0, 0, 6) },
                        Ui.Recessed(_list),
                        myReports,
                        Ui.Recessed(new StackPanel { Children = { _templateList, _noTemplates } }),
                        _templateProblems,
                    },
                },
            },
        };
        DockPanel.SetDock(nav, Dock.Left);
        Content = new DockPanel { Children = { nav, _body } };

        RefreshTemplates();
        _list.SelectedIndex = 0;
    }

    /// <summary>The built-in report, or the report template, that is selected.</summary>
    public string? SelectedReport => _list.SelectedItem as string ?? SelectedTemplate?.Name;

    /// <summary>The report template selected under My reports, if any.</summary>
    public ReportTemplate? SelectedTemplate => _templateList.SelectedItem as ReportTemplate;

    /// <summary>Where report templates are kept.</summary>
    public ReportTemplateStore Templates => _templates;

    /// <summary>The names listed under My reports.</summary>
    public IReadOnlyList<string> TemplateNames =>
        (_templateList.ItemsSource as IEnumerable<ReportTemplate> ?? []).Select(t => t.Name).ToList();

    /// <summary>Template files that couldn't be used (and why), as found by the last reload.</summary>
    public IReadOnlyList<string> TemplateProblems { get; private set; } = [];

    /// <summary>The Explore data page (SQL on the open grid's snapshot); created the first time it is needed.</summary>
    public ExploreView Explore
    {
        get
        {
            if (_explore is null)
            {
                _explore = new ExploreView(_cache, _templates);
                _explore.TemplateSaved += (_, _) => RefreshTemplates();
            }
            return _explore;
        }
    }

    /// <summary>The page that runs a report template; created the first time it is needed.</summary>
    public TemplateReportView TemplatePage
    {
        get
        {
            if (_templatePage is null)
            {
                _templatePage = new TemplateReportView(_cache, _templates);
                _templatePage.EditRequested += (_, template) =>
                {
                    Explore.LoadTemplate(template);
                    SelectReport(ExploreData);
                };
                _templatePage.Deleted += (_, template) =>
                {
                    if (_explore?.EditingTemplate?.Id == template.Id) _explore.StopEditing();
                    RefreshTemplates();
                    SelectReport(ExploreData);
                };
            }
            return _templatePage;
        }
    }

    /// <summary>The results table of the report on screen, if any.</summary>
    public FilterableTable? CurrentTable { get; private set; }

    /// <summary>Last results, for tests and the summary.</summary>
    public LicenseExpiryResult? LicenseResult { get; private set; }

    public DanglingCnameResult? CnameResult { get; private set; }

    /// <summary>Selects a built-in report, or a report template by name.</summary>
    public void SelectReport(string name)
    {
        if (BuiltIn.Contains(name))
        {
            _list.SelectedItem = name;
        }
        else if ((_templateList.ItemsSource as IEnumerable<ReportTemplate>)?.FirstOrDefault(t => t.Name == name) is { } template)
        {
            _templateList.SelectedItem = template;
        }
    }

    /// <summary>Reads the templates folder again (after files were added, changed or removed outside the app).</summary>
    public void RefreshTemplates()
    {
        var selectedId = SelectedTemplate?.Id;
        var problems = new List<string>();
        var templates = _templates.List(problems);
        TemplateProblems = problems;

        _syncing = true;
        try
        {
            _templateList.ItemsSource = templates;
            _templateList.SelectedItem = templates.FirstOrDefault(t => t.Id == selectedId);
        }
        finally
        {
            _syncing = false;
        }

        _noTemplates.IsVisible = templates.Count == 0;
        _templateProblems.IsVisible = problems.Count > 0;
        _templateProblems.Text = problems.Count == 1
            ? $"1 file in the templates folder couldn't be used: {problems[0]}"
            : problems.Count > 1
                ? $"{problems.Count} files in the templates folder couldn't be used, e.g. {problems[0]}"
                : "";

        // A template that is on screen may have changed on disk: show it as it is now.
        if (SelectedTemplate is not null) Render();
    }

    /// <summary>Sets the grid the reports run on (null when nothing is open).</summary>
    public void ShowInventory(GridInventory? inventory, string context)
    {
        // The loaded copy of the old grid's data goes as soon as another grid (or none) is shown.
        if (!ReferenceEquals(inventory, _inventory)) _cache.Release();
        _inventory = inventory;
        _context = context;
        Render();
    }

    public void SetLicenseWindow(int days)
    {
        _windowDays = days;
        Render();
    }

    public void SetCnameFilters(bool showResolving, bool showNotVerified)
    {
        _showResolving = showResolving;
        _showNotVerified = showNotVerified;
        Render();
    }

    private void OnNavSelectionChanged(ListBox changed, ListBox other)
    {
        if (_syncing) return;
        if (changed.SelectedItem is not null)
        {
            // One report is selected at a time, whichever list it is in.
            _syncing = true;
            try
            {
                other.SelectedItem = null;
            }
            finally
            {
                _syncing = false;
            }
        }
        Render();
    }

    private void Render()
    {
        CurrentTable = null;
        LicenseResult = null;
        CnameResult = null;

        if (SelectedTemplate is { } template)
        {
            TemplatePage.Show(template, _inventory, _context);
            _body.Content = TemplatePage;
            return;
        }

        if (SelectedReport == ExploreData)
        {
            Explore.SetInventory(_inventory, _context);
            _body.Content = Explore;
            return;
        }

        if (_inventory is null)
        {
            _body.Content = Page(SelectedReport ?? "", SelectedReport == LicenseExpiry
                    ? LicenseExpiryReport.Description
                    : DanglingCnameReport.Description,
                null, null,
                Ui.Muted("Open a grid first: pick one with the Client › Grid switcher in the header, or use Open backup…"));
            return;
        }

        _body.Content = SelectedReport switch
        {
            LicenseExpiry => LicensePage(_inventory),
            DanglingCnames => CnamePage(_inventory),
            _ => null,
        };
    }

    // ---- License expiry -------------------------------------------------------

    private Control LicensePage(GridInventory inv)
    {
        var result = LicenseResult = LicenseExpiryReport.Run(inv, _windowDays);

        var window = new ComboBox
        {
            ItemsSource = LicenseExpiryReport.Windows.Select(d => $"{d} days").ToList(),
            SelectedItem = $"{_windowDays} days",
            Width = 130,
        };
        window.SelectionChanged += (_, _) =>
        {
            if (window.SelectedItem is string s && int.TryParse(s.Split(' ')[0], out var d) && d != _windowDays)
                SetLicenseWindow(d);
        };

        var options = Ui.Row(new TextBlock { Text = "Flag licenses expiring within", VerticalAlignment = VerticalAlignment.Center }, window);

        var summary = Summary(
            (result.Expired, "expired", TableCells.StatusBrush("Expired")),
            (result.Expiring, $"expiring within {_windowDays} days", TableCells.StatusBrush("Expiring")),
            (result.Later, "later", null),
            (result.Permanent, "permanent", null));
        var next = result.NextExpiry is { } n ? $"Next expiry: {n:yyyy-MM-dd}" : "No license on this grid expires.";

        string State(ExpiryState s) => s switch
        {
            ExpiryState.Expired => "Expired",
            ExpiryState.Expiring => "Expiring",
            ExpiryState.Later => "OK",
            _ => "Permanent",
        };

        var table = CurrentTable = new FilterableTable(new TableDefinition(
            "license expiry",
            [
                new("Status", ColumnKind.Status, 110), new("Scope", Width: 200), new("License", Width: 260),
                new("Code", Width: 110), new("Node", Width: 170), new("Expires", Width: 120),
                new("Days left", ColumnKind.Number, 100), new("Note", Width: 300),
            ],
            result.Rows.Select(r => new TableRow(
            [
                State(r.State), r.Scope, r.License, r.Code, r.Node,
                r.Expires is { } e ? e.ToString("yyyy-MM-dd") : "Permanent",
                r.DaysLeft?.ToString() ?? "", r.Note,
            ])).ToList()));

        return Page(LicenseExpiry, LicenseExpiryReport.Description, options,
            new StackPanel { Spacing = 4, Children = { summary, Ui.Muted(next) } }, table);
    }

    // ---- Dangling CNAMEs --------------------------------------------------------

    private Control CnamePage(GridInventory inv)
    {
        var result = CnameResult = DanglingCnameReport.Run(inv);

        var resolving = new CheckBox { Content = "Show CNAMEs that resolve", IsChecked = _showResolving };
        resolving.IsCheckedChanged += (_, _) => SetCnameFilters(resolving.IsChecked == true, _showNotVerified);
        var external = new CheckBox { Content = "Show targets outside the grid (not verified)", IsChecked = _showNotVerified };
        external.IsCheckedChanged += (_, _) => SetCnameFilters(_showResolving, external.IsChecked == true);
        var options = Ui.Row(resolving, external);

        var summary = Summary(
            (result.Dangling, "dangling", TableCells.StatusBrush("High")),
            (result.TakeoverRisks, "possible takeover risks", TableCells.StatusBrush("Medium")),
            (result.NotVerified, "not verified (outside the grid)", null),
            (result.Resolving, "resolve", null));
        var scope = Ui.Muted($"{result.Total} CNAMEs checked, including host aliases. Chains longer than " +
                             $"{result.MaxChain} are flagged. RPZ rules aren't checked.");

        var rows = result.Rows.Where(r =>
            (r.Outcome != CnameOutcome.Resolves || _showResolving)
            && (r.Outcome is not (CnameOutcome.External or CnameOutcome.Delegated) || _showNotVerified || r.TakeoverProvider is not null));

        var table = CurrentTable = new FilterableTable(new TableDefinition(
            "dangling CNAMEs",
            [
                new("Severity", ColumnKind.Status, 100), new("Result", Width: 230), new("CNAME", Width: 260),
                new("Target", Width: 240), new("View", Width: 130), new("Zone", Width: 180),
                new("Chain", Width: 360), new("Host alias"), new("Provider", Width: 160), new("Detail", Width: 480),
            ],
            rows.Select(r => new TableRow(
            [
                r.Severity == ReportSeverity.None ? "" : r.Severity.ToString(),
                DanglingCnameReport.Describe(r.Outcome), r.Name, r.Target.TrimEnd('.'), r.View, r.Zone, r.Chain,
                r.FromHost ? "Yes" : "", r.TakeoverProvider ?? "", r.Detail,
            ])).ToList()));

        return Page(DanglingCnames, DanglingCnameReport.Description, options,
            new StackPanel { Spacing = 4, Children = { summary, scope } }, table);
    }

    // ---- Layout helpers -----------------------------------------------------------

    private Control Page(string title, string description, Control? options, Control? summary, Control body)
    {
        var header = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(12, 12, 12, 0),
            Children = { Ui.Heading(title), Ui.Muted(description) },
        };
        if (_context.Length > 0 && _inventory is not null)
        {
            header.Children.Add(new TextBlock { Text = $"Grid: {_context}", Opacity = 0.85, Margin = new Thickness(0, 4, 0, 0) });
        }
        if (options is not null) header.Children.Add(options);
        if (summary is not null) header.Children.Add(summary);
        DockPanel.SetDock(header, Dock.Top);

        if (body is FilterableTable) return new DockPanel { Children = { header, body } };
        var wrapped = new Border { Margin = new Thickness(12), Child = body };
        return new DockPanel { Children = { header, wrapped } };
    }

    private static Control Summary(params (int Count, string Label, IBrush? Color)[] parts)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (count, label, color) in parts)
        {
            var number = new TextBlock { Text = count.ToString("N0"), FontSize = 20, FontWeight = FontWeight.SemiBold };
            if (count > 0 && color is not null) number.Foreground = color;
            row.Children.Add(new Border
            {
                Background = Ui.Surface,
                BorderBrush = Ui.Divider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { number, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 } },
                },
            });
        }
        return row;
    }
}
