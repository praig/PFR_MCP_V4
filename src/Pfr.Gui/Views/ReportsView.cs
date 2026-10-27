using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Nios.Model;
using Pfr.Core.Reports;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// Reports module: predefined reports that run on the grid currently open
/// (chosen with the header switcher or Open backup…), plus the data explorer.
/// </summary>
public sealed class ReportsView : UserControl
{
    public const string LicenseExpiry = LicenseExpiryReport.Title;
    public const string DanglingCnames = DanglingCnameReport.Title;
    public const string ExploreData = "Explore data";

    private readonly ListBox _list;
    private readonly ContentControl _body = new();
    private GridInventory? _inventory;
    private string _context = "";

    private int _windowDays = 90;
    private bool _showResolving;
    private bool _showNotVerified = true;

    public ReportsView()
    {
        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemsSource = new[] { LicenseExpiry, DanglingCnames, ExploreData },
        };
        _list.SelectionChanged += (_, _) => Render();

        var nav = new Border
        {
            Width = 210,
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = Ui.Divider,
            Padding = new Thickness(6, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Reports", Opacity = 0.6, Margin = new Thickness(10, 0, 0, 6) },
                    _list,
                },
            },
        };
        DockPanel.SetDock(nav, Dock.Left);
        Content = new DockPanel { Children = { nav, _body } };
        _list.SelectedIndex = 0;
    }

    public string? SelectedReport => _list.SelectedItem as string;

    /// <summary>The results table of the report on screen, if any.</summary>
    public FilterableTable? CurrentTable { get; private set; }

    /// <summary>Last results, for tests and the summary.</summary>
    public LicenseExpiryResult? LicenseResult { get; private set; }

    public DanglingCnameResult? CnameResult { get; private set; }

    public void SelectReport(string name) => _list.SelectedItem = name;

    /// <summary>Sets the grid the reports run on (null when nothing is open).</summary>
    public void ShowInventory(GridInventory? inventory, string context)
    {
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

    private void Render()
    {
        CurrentTable = null;
        LicenseResult = null;
        CnameResult = null;

        if (SelectedReport == ExploreData)
        {
            _body.Content = ExplorePlaceholder();
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

    // ---- Explore data (placeholder) -----------------------------------------------

    private static Control ExplorePlaceholder() => new ScrollViewer
    {
        Content = new StackPanel
        {
            Margin = new Thickness(24, 18),
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 10,
            Children =
            {
                Ui.Heading("Explore data"),
                new Border
                {
                    Background = Ui.Surface,
                    BorderBrush = Ui.Divider,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 12),
                    Child = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Coming in a later version", FontWeight = FontWeight.SemiBold },
                            Ui.Muted("Browse the tables found in a grid backup (each NIOS object type), see their " +
                                     "fields, and query against them."),
                        },
                    },
                },
            },
        },
    };

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
