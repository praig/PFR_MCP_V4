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
    public const string RowCount = RowCountReport.Title;
    public const string DhcpRanges = DhcpRangeReport.Title;
    public const string Scavenge = ScavengeReport.Title;
    public const string RecordCreation = RecordCreationReport.Title;
    public const string ExploreData = "Explore data";

    private static readonly string[] BuiltIn = [LicenseExpiry, DanglingCnames, RowCount, DhcpRanges, Scavenge, RecordCreation, ExploreData];

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

    // The CNAME report is worked out in the background; a newer choice makes an older result be dropped.
    private int _renderVersion;
    private (GridInventory Inventory, DanglingCnameResult Result)? _cnameCache;

    // The Row count report: which zones it covers, and where it says how creating the workbook went.
    private bool _rowCountReverse = true;
    private bool _rowCountEmpty = true;
    private TextBlock? _workbookStatus;

    // The DHCP range report: which ranges it lists.
    private bool _dhcpWithoutOptions = true;
    private bool _dhcpIpv6 = true;
    private bool _dhcpInherited = true;

    // The Scavenge report: which creators and record types it lists.
    private bool _scavengeDynamic = true;
    private bool _scavengeStatic = true;
    private bool _scavengeSystem = true;
    private bool _scavengeA = true;
    private bool _scavengeCname = true;
    private bool _scavengePtr = true;

    // The Record creation report: which extensible attribute it shows, and which objects it lists.
    private string _creationAttribute = RecordCreationReport.DefaultAttribute;
    private bool _creationA = true;
    private bool _creationHosts = true;
    private bool _creationIpamOnly;

    // What the Export button of the built-in report on screen would write, and where it says how that went.
    private TableDefinition? _exportable;
    private string _exportTitle = "";
    private TextBlock? _exportStatus;

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

    /// <summary>The Row count report on screen: every zone with its record count (null until it has been worked out).</summary>
    public RowCountResult? RowCounts { get; private set; }

    /// <summary>The DHCP range report on screen: every range with one line per option (null until it has been worked out).</summary>
    public DhcpRangeResult? DhcpResult { get; private set; }

    /// <summary>The Scavenge report on screen: each A, CNAME and PTR record with its creator and last-queried date (null until worked out).</summary>
    public ScavengeResult? ScavengeData { get; private set; }

    /// <summary>The Record creation report on screen: each A record and host with its creation date and attribute (null until worked out).</summary>
    public RecordCreationResult? CreationData { get; private set; }

    /// <summary>
    /// Completes when the built-in report on screen has been worked out and shown. The CNAME report follows every CNAME in
    /// the grid, which takes a while on a big one, so it runs in the background; the other pages are ready at once.
    /// </summary>
    public Task ReportTask { get; private set; } = Task.CompletedTask;

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
        if (!ReferenceEquals(inventory, _inventory))
        {
            _cache.Release();
            _cnameCache = null;
        }
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
        _renderVersion++; // a report still being worked out for something else is no longer wanted
        ReportTask = Task.CompletedTask;
        _exportable = null;
        _exportStatus = null;
        _workbookStatus = null;
        CurrentTable = null;
        LicenseResult = null;
        CnameResult = null;
        RowCounts = null;
        DhcpResult = null;
        ScavengeData = null;
        CreationData = null;

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
            _body.Content = Page(SelectedReport ?? "", SelectedReport switch
                {
                    LicenseExpiry => LicenseExpiryReport.Description,
                    RowCount => RowCountReport.Description,
                    DhcpRanges => DhcpRangeReport.Description,
                    Scavenge => ScavengeReport.Description,
                    RecordCreation => RecordCreationReport.Description,
                    _ => DanglingCnameReport.Description,
                },
                null, null,
                Ui.Muted("Open a grid first: pick one with the Client › Grid switcher in the header, or use Open backup…"));
            return;
        }

        if (SelectedReport == RecordCreation)
        {
            _body.Content = Working(RecordCreation, RecordCreationReport.Description, "Listing every A record and host\u2026");
            ReportTask = ShowRecordCreationAsync(_inventory, _renderVersion);
            return;
        }

        if (SelectedReport == Scavenge)
        {
            _body.Content = Working(Scavenge, ScavengeReport.Description, "Listing every A, CNAME and PTR record\u2026");
            ReportTask = ShowScavengeAsync(_inventory, _renderVersion);
            return;
        }

        if (SelectedReport == DhcpRanges)
        {
            _body.Content = Working(DhcpRanges, DhcpRangeReport.Description, "Listing every DHCP range and its options…");
            ReportTask = ShowDhcpRangesAsync(_inventory, _renderVersion);
            return;
        }

        if (SelectedReport == RowCount)
        {
            _body.Content = Working(RowCount, RowCountReport.Description, "Counting the records of every zone…");
            ReportTask = ShowRowCountAsync(_inventory, _renderVersion);
            return;
        }

        if (SelectedReport == DanglingCnames)
        {
            _body.Content = Working(DanglingCnames, DanglingCnameReport.Description, "Checking every CNAME\u2026");
            ReportTask = ShowCnamesAsync(_inventory, _renderVersion);
            return;
        }

        _body.Content = SelectedReport switch
        {
            LicenseExpiry => LicensePage(_inventory),
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

        var definition = new TableDefinition(
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
            ])).ToList());
        var table = CurrentTable = new FilterableTable(definition);

        return Page(LicenseExpiry, LicenseExpiryReport.Description, WithExport(options, definition, LicenseExpiry),
            new StackPanel { Spacing = 4, Children = { summary, Ui.Muted(next) } }, table);
    }

    // ---- Dangling CNAMEs --------------------------------------------------------

    /// <summary>
    /// Follows every CNAME and builds the table's rows on a background thread, then shows the page. The result is kept for
    /// as long as the same grid is open, so changing a filter only rebuilds the rows.
    /// </summary>
    private async Task ShowCnamesAsync(GridInventory inv, int version)
    {
        var showResolving = _showResolving;
        var showNotVerified = _showNotVerified;
        var known = _cnameCache is { } cached && ReferenceEquals(cached.Inventory, inv) ? cached.Result : null;

        try
        {
            var (result, definition) = await Task.Run(() =>
            {
                var found = known ?? DanglingCnameReport.Run(inv);
                return (found, CnameTable(found, showResolving, showNotVerified));
            });
            if (version != _renderVersion) return; // another report or grid was chosen while this ran

            _cnameCache = (inv, result);
            CnameResult = result;
            _body.Content = CnamePage(result, definition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _renderVersion)
                _body.Content = Page(DanglingCnames, DanglingCnameReport.Description, null, null, Ui.Muted($"Could not run the report: {ex.Message}"));
        }
    }

    /// <summary>The rows to show for the chosen filters. Makes no controls, so it can run on any thread.</summary>
    private static TableDefinition CnameTable(DanglingCnameResult result, bool showResolving, bool showNotVerified)
    {
        var rows = result.Rows.Where(r =>
            (r.Outcome != CnameOutcome.Resolves || showResolving)
            && (r.Outcome is not (CnameOutcome.External or CnameOutcome.Delegated) || showNotVerified || r.TakeoverProvider is not null));

        return new TableDefinition(
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
            ])).ToList());
    }

    private Control CnamePage(DanglingCnameResult result, TableDefinition definition)
    {
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

        var table = CurrentTable = new FilterableTable(definition);

        return Page(DanglingCnames, DanglingCnameReport.Description, WithExport(options, definition, DanglingCnames),
            new StackPanel { Spacing = 4, Children = { summary, scope } }, table);
    }

    // ---- Record creation report ----------------------------------------------------

    /// <summary>Changes which extensible attribute the Record creation report shows, and which objects it lists.</summary>
    public void SetRecordCreationOptions(string attribute, bool aRecords = true, bool hosts = true, bool ipamOnlyHosts = false)
    {
        _creationAttribute = string.IsNullOrWhiteSpace(attribute) ? RecordCreationReport.DefaultAttribute : attribute.Trim();
        _creationA = aRecords;
        _creationHosts = hosts;
        _creationIpamOnly = ipamOnlyHosts;
        Render();
    }

    /// <summary>Lists the records in the background, then shows the page.</summary>
    private async Task ShowRecordCreationAsync(GridInventory inv, int version)
    {
        var options = new RecordCreationOptions
        {
            Attribute = _creationAttribute, IncludeARecords = _creationA, IncludeHosts = _creationHosts, IncludeIpamOnlyHosts = _creationIpamOnly,
        };
        try
        {
            var (result, definition) = await Task.Run(() =>
            {
                var found = RecordCreationReport.Run(inv, options);
                return (found, RecordCreationTable(found));
            });
            if (version != _renderVersion) return; // another report or grid was chosen while this ran

            CreationData = result;
            _body.Content = RecordCreationPage(result, definition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _renderVersion)
                _body.Content = Page(RecordCreation, RecordCreationReport.Description, null, null, Ui.Muted($"Could not run the report: {ex.Message}"));
        }
    }

    /// <summary>The report's six columns as a table. Makes no controls, so it can run on any thread.</summary>
    private static TableDefinition RecordCreationTable(RecordCreationResult result) => new(
        "record creation report",
        [
            new(result.Columns[0], Width: 110), new(result.Columns[1], Width: 380), new(result.Columns[2], ColumnKind.Ip, 170),
            new(result.Columns[3], Width: 300), new(result.Columns[4], Width: 130), new(result.Columns[5], Width: 220),
        ],
        result.Rows.Select(r => new TableRow([r.RecordType, r.Fqdn, r.IpAddress, r.Comment, r.CreatedText, r.AttributeValue])).ToList());

    private Control RecordCreationPage(RecordCreationResult result, TableDefinition definition)
    {
        CheckBox Box(string text, bool value, Action<bool> set)
        {
            var box = new CheckBox { Content = text, IsChecked = value };
            box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
            return box;
        }

        var attribute = new TextBox { Text = _creationAttribute, Width = 200, Watermark = "Attribute name" };
        void ApplyAttribute()
        {
            var text = attribute.Text?.Trim() ?? "";
            if (text.Length > 0 && text != _creationAttribute) SetRecordCreationOptions(text, _creationA, _creationHosts, _creationIpamOnly);
        }
        attribute.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyAttribute();
        };
        attribute.LostFocus += (_, _) => ApplyAttribute();

        var options = Ui.Row(
            ToolText("Extensible attribute"), attribute,
            Box("A records", _creationA, v => SetRecordCreationOptions(_creationAttribute, v, _creationHosts, _creationIpamOnly)),
            Box("Hosts", _creationHosts, v => SetRecordCreationOptions(_creationAttribute, _creationA, v, _creationIpamOnly)),
            Box("Include hosts not in DNS", _creationIpamOnly, v => SetRecordCreationOptions(_creationAttribute, _creationA, _creationHosts, v)));

        var notes = new StackPanel { Spacing = 4 };
        notes.Children.Add(new TextBlock
        {
            Text = $"{result.Rows.Count:N0} line{(result.Rows.Count == 1 ? "" : "s")}: {result.Count("A"):N0} A record{(result.Count("A") == 1 ? "" : "s")}, " +
                   $"{result.Count("Host"):N0} host line{(result.Count("Host") == 1 ? "" : "s")}; {result.WithAttribute:N0} with a {result.Attribute} value; " +
                   $"{result.WithoutCreationDate:N0} with no creation date.",
            FontSize = 16,
        });
        notes.Children.Add(Ui.Muted("A host with several addresses has a line for each. The A records a host object makes are not listed again (the host is). " +
                                    "An attribute inherited from a network is shown with the value that applies. Dates are UTC."));
        if (!result.AttributeFound)
        {
            notes.Children.Add(new TextBlock
            {
                Text = $"No A record or host listed has an extensible attribute called \"{result.Attribute}\" set. " +
                       (result.AttributesSeen.Count > 0
                           ? $"Attributes that are set on them: {string.Join(", ", result.AttributesSeen)}. Type one of these above to show it."
                           : "None of them has any extensible attribute set."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.85,
            });
        }
        if (!result.RecordDetailsRead)
        {
            notes.Children.Add(new TextBlock
            {
                Text = "This snapshot was saved before comments, creation dates and extensible attributes were read, so those columns are empty. " +
                       "Import the backup again to fill them in.",
                Foreground = Ui.Danger,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var table = CurrentTable = new FilterableTable(definition);
        return Page(RecordCreation, RecordCreationReport.Description, WithExport(options, definition, RecordCreation), notes, table);
    }

    // ---- Scavenge report -----------------------------------------------------------

    /// <summary>Changes which creators and record types the Scavenge report lists.</summary>
    public void SetScavengeOptions(bool dynamicRecords, bool staticRecords, bool system,
        bool aRecords = true, bool cnameRecords = true, bool ptrRecords = true)
    {
        _scavengeDynamic = dynamicRecords;
        _scavengeStatic = staticRecords;
        _scavengeSystem = system;
        _scavengeA = aRecords;
        _scavengeCname = cnameRecords;
        _scavengePtr = ptrRecords;
        Render();
    }

    /// <summary>Lists the records in the background, then shows the page.</summary>
    private async Task ShowScavengeAsync(GridInventory inv, int version)
    {
        // All three ticked means "everyone", which also covers records whose creator is not known.
        var chosen = new List<string>();
        if (_scavengeDynamic) chosen.Add("DYNAMIC");
        if (_scavengeStatic) chosen.Add("STATIC");
        if (_scavengeSystem) chosen.Add("SYSTEM");
        var types = new List<DnsRecordType>();
        if (_scavengeA) types.Add(DnsRecordType.A);
        if (_scavengeCname) types.Add(DnsRecordType.CNAME);
        if (_scavengePtr) types.Add(DnsRecordType.PTR);
        var options = new ScavengeReportOptions
        {
            Creators = chosen.Count == ScavengeReport.CreatorNames.Count ? null : chosen.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Types = types.Count == ScavengeReport.RecordTypes.Count ? null : types.ToHashSet(),
        };

        try
        {
            var (result, definition) = await Task.Run(() =>
            {
                var found = ScavengeReport.Run(inv, options);
                return (found, ScavengeTable(found));
            });
            if (version != _renderVersion) return; // another report or grid was chosen while this ran

            ScavengeData = result;
            _body.Content = ScavengePage(result, definition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _renderVersion)
                _body.Content = Page(Scavenge, ScavengeReport.Description, null, null, Ui.Muted($"Could not run the report: {ex.Message}"));
        }
    }

    /// <summary>The report's five columns as a table. Makes no controls, so it can run on any thread.</summary>
    private static TableDefinition ScavengeTable(ScavengeResult result) => new(
        "scavenge report",
        [
            new(ScavengeResult.Columns[0], Width: 120), new(ScavengeResult.Columns[1], Width: 420),
            new(ScavengeResult.Columns[2], ColumnKind.Ip, 180), new(ScavengeResult.Columns[3], Width: 140),
            new(ScavengeResult.Columns[4], Width: 120),
        ],
        result.Rows.Select(r => new TableRow([r.Creator, r.Fqdn, r.IpAddress, r.LastQueriedText, r.TypeText])).ToList());

    private Control ScavengePage(ScavengeResult result, TableDefinition definition)
    {
        CheckBox Box(string text, bool value, Action<bool> set)
        {
            var box = new CheckBox { Content = text, IsChecked = value };
            box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
            return box;
        }

        // Each box changes its own setting and keeps the others as they are.
        void Change(bool? dynamicRecords = null, bool? staticRecords = null, bool? system = null,
            bool? aRecords = null, bool? cnameRecords = null, bool? ptrRecords = null) =>
            SetScavengeOptions(dynamicRecords ?? _scavengeDynamic, staticRecords ?? _scavengeStatic, system ?? _scavengeSystem,
                aRecords ?? _scavengeA, cnameRecords ?? _scavengeCname, ptrRecords ?? _scavengePtr);

        var options = Ui.Row(
            ToolText("Created by"),
            Box("Dynamic", _scavengeDynamic, v => Change(dynamicRecords: v)),
            Box("Static", _scavengeStatic, v => Change(staticRecords: v)),
            Box("System", _scavengeSystem, v => Change(system: v)),
            ToolText("  Records"),
            Box("A", _scavengeA, v => Change(aRecords: v)),
            Box("CNAME", _scavengeCname, v => Change(cnameRecords: v)),
            Box("PTR", _scavengePtr, v => Change(ptrRecords: v)));

        var notes = new StackPanel { Spacing = 4 };
        notes.Children.Add(new TextBlock
        {
            Text = $"{result.Rows.Count:N0} record{(result.Rows.Count == 1 ? "" : "s")}: {result.Count("DYNAMIC"):N0} dynamic, " +
                   $"{result.Count("STATIC"):N0} static, {result.Count("SYSTEM"):N0} system; {result.NeverQueried:N0} with no last-queried date.",
            FontSize = 16,
        });
        notes.Children.Add(Ui.Muted("Last queried is blank when NIOS has no time for the record: it has not been queried since NIOS began keeping " +
                                    "the time, or the grid does not keep it. Blank does not prove a record is unused. Dates are UTC."));
        if (!result.RecordOriginsRead)
        {
            notes.Children.Add(new TextBlock
            {
                Text = "This snapshot was saved before record creators and last-queried dates were read, so those columns are empty. " +
                       "Import the backup again to fill them in.",
                Foreground = Ui.Danger,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var table = CurrentTable = new FilterableTable(definition);
        return Page(Scavenge, ScavengeReport.Description, WithExport(options, definition, Scavenge), notes, table);
    }

    private static TextBlock ToolText(string text) => new() { Text = text, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };

    // ---- DHCP range report ---------------------------------------------------------

    /// <summary>Changes which ranges the DHCP range report lists.</summary>
    public void SetDhcpRangeOptions(bool includeRangesWithoutOptions, bool includeIpv6, bool includeInherited = true)
    {
        _dhcpWithoutOptions = includeRangesWithoutOptions;
        _dhcpIpv6 = includeIpv6;
        _dhcpInherited = includeInherited;
        Render();
    }

    /// <summary>Works out the report in the background, then shows the page.</summary>
    private async Task ShowDhcpRangesAsync(GridInventory inv, int version)
    {
        var options = new DhcpRangeReportOptions
        {
            IncludeRangesWithoutOptions = _dhcpWithoutOptions, IncludeIpv6 = _dhcpIpv6, IncludeInheritedOptions = _dhcpInherited,
        };
        try
        {
            var (result, definition) = await Task.Run(() =>
            {
                var found = DhcpRangeReport.Run(inv, options);
                return (found, DhcpRangeTable(found));
            });
            if (version != _renderVersion) return; // another report or grid was chosen while this ran

            DhcpResult = result;
            _body.Content = DhcpRangePage(result, definition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _renderVersion)
                _body.Content = Page(DhcpRanges, DhcpRangeReport.Description, null, null, Ui.Muted($"Could not run the report: {ex.Message}"));
        }
    }

    /// <summary>The report's seven columns as a table. Makes no controls, so it can run on any thread.</summary>
    private static TableDefinition DhcpRangeTable(DhcpRangeResult result) => new(
        "dhcp range report",
        [
            new(DhcpRangeResult.Columns[0], Width: 240), new(DhcpRangeResult.Columns[1], ColumnKind.Ip, 160),
            new(DhcpRangeResult.Columns[2], ColumnKind.Ip, 160), new(DhcpRangeResult.Columns[3], ColumnKind.Number, 120),
            new(DhcpRangeResult.Columns[4], ColumnKind.Number, 120), new(DhcpRangeResult.Columns[5], Width: 380),
            new(DhcpRangeResult.Columns[6], Width: 240),
        ],
        result.Rows.Select(r => new TableRow(
        [
            r.Network, r.Start, r.End, r.LeaseSeconds?.ToString() ?? "", r.OptionCode?.ToString() ?? "", r.OptionValue ?? "", r.SetOn,
        ])).ToList());

    private Control DhcpRangePage(DhcpRangeResult result, TableDefinition definition)
    {
        var withoutOptions = new CheckBox { Content = "Include ranges with no options", IsChecked = _dhcpWithoutOptions };
        withoutOptions.IsCheckedChanged += (_, _) => SetDhcpRangeOptions(withoutOptions.IsChecked == true, _dhcpIpv6, _dhcpInherited);
        var ipv6 = new CheckBox { Content = "Include IPv6 ranges", IsChecked = _dhcpIpv6 };
        ipv6.IsCheckedChanged += (_, _) => SetDhcpRangeOptions(_dhcpWithoutOptions, ipv6.IsChecked == true, _dhcpInherited);
        var inherited = new CheckBox { Content = "Include inherited options", IsChecked = _dhcpInherited };
        inherited.IsCheckedChanged += (_, _) => SetDhcpRangeOptions(_dhcpWithoutOptions, _dhcpIpv6, inherited.IsChecked == true);
        var options = Ui.Row(inherited, withoutOptions, ipv6);

        var notes = new StackPanel { Spacing = 4 };
        notes.Children.Add(new TextBlock
        {
            Text = $"{result.RangeCount:N0} range{(result.RangeCount == 1 ? "" : "s")}, {result.OptionCount:N0} option{(result.OptionCount == 1 ? "" : "s")} on " +
                   $"{result.RangesWithOptions:N0}{(result.InheritedOptionCount > 0 ? $" ({result.InheritedOptionCount:N0} inherited)" : "")}; " +
                   $"{result.RangesWithoutOptions:N0} with none.",
            FontSize = 16,
        });
        notes.Children.Add(Ui.Muted("Default lease is in seconds: the range's own, or the one it inherits from its network, a container or the grid. " +
                                    "Options are those set on the range and, with Include inherited options ticked, those it inherits from its network, " +
                                    "a container above it and the grid; where an option is set at more than one level only the nearest is listed. " +
                                    "Options set at the member level are not read."));
        if (!result.OptionsRead)
        {
            notes.Children.Add(new TextBlock
            {
                Text = "This snapshot was saved before DHCP lease times and options were read, so those columns are empty. " +
                       "Import the backup again to fill them in.",
                Foreground = Ui.Danger,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        if (result.UnmatchedOptions > 0)
        {
            notes.Children.Add(Ui.Muted($"{result.UnmatchedOptions:N0} option{(result.UnmatchedOptions == 1 ? "" : "s")} in the backup belong to a range " +
                                        "that could not be identified, so they are not listed."));
        }

        var table = CurrentTable = new FilterableTable(definition);
        return Page(DhcpRanges, DhcpRangeReport.Description, WithExport(options, definition, DhcpRanges), notes, table);
    }

    // ---- Row count --------------------------------------------------------------

    /// <summary>Changes which zones the Row count report covers.</summary>
    public void SetRowCountOptions(bool includeReverseZones, bool includeEmptyZones)
    {
        _rowCountReverse = includeReverseZones;
        _rowCountEmpty = includeEmptyZones;
        Render();
    }

    /// <summary>True once the Row count report has been worked out, so a workbook can be created.</summary>
    public bool CanCreateWorkbook => RowCounts is not null;

    /// <summary>What the Row count page last said about creating the workbook (where it went, or why it didn't).</summary>
    public string WorkbookStatusText => _workbookStatus?.Text ?? "";

    /// <summary>Counts every zone's records in the background, then shows the page.</summary>
    private async Task ShowRowCountAsync(GridInventory inv, int version)
    {
        var options = new RowCountOptions { IncludeReverseZones = _rowCountReverse, IncludeEmptyZones = _rowCountEmpty };
        try
        {
            var (result, definition) = await Task.Run(() =>
            {
                var counted = RowCountReport.Run(inv, options);
                return (counted, RowCountTable(counted));
            });
            if (version != _renderVersion) return; // another report or grid was chosen while this ran

            RowCounts = result;
            _body.Content = RowCountPage(result, definition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _renderVersion)
                _body.Content = Page(RowCount, RowCountReport.Description, null, null, Ui.Muted($"Could not run the report: {ex.Message}"));
        }
    }

    /// <summary>What the workbook's Home sheet will list. Makes no controls, so it can run on any thread.</summary>
    private static TableDefinition RowCountTable(RowCountResult result) => new(
        "row count",
        [new("View", Width: 160), new("Zone", Width: 380), new("Records", ColumnKind.Number, 110), new("Type", Width: 150)],
        result.Zones.Select(z => new TableRow([z.View, z.Zone, z.Count.ToString(), z.ZoneType])).ToList());

    private Control RowCountPage(RowCountResult result, TableDefinition definition)
    {
        var reverse = new CheckBox { Content = "Include reverse zones", IsChecked = _rowCountReverse };
        reverse.IsCheckedChanged += (_, _) => SetRowCountOptions(reverse.IsChecked == true, _rowCountEmpty);
        var empty = new CheckBox { Content = "Include zones with no records", IsChecked = _rowCountEmpty };
        empty.IsCheckedChanged += (_, _) => SetRowCountOptions(_rowCountReverse, empty.IsChecked == true);

        var create = Ui.Accent("Create Excel workbook\u2026");
        var status = _workbookStatus = new TextBlock { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        create.Click += async (_, _) => await CreateWorkbookAsync();

        var options = new StackPanel { Spacing = 6, Children = { Ui.Row(reverse, empty), Ui.Row(create, status) } };

        var notes = new StackPanel { Spacing = 4 };
        notes.Children.Add(new TextBlock
        {
            Text = $"{result.Zones.Count:N0} zone{(result.Zones.Count == 1 ? "" : "s")}, {result.TotalRecords:N0} record{(result.TotalRecords == 1 ? "" : "s")}" +
                   (result.EmptyZones > 0 ? $", {result.EmptyZones:N0} with no records" : "") + ".",
            FontSize = 16,
        });
        notes.Children.Add(Ui.Muted("The workbook's first sheet, Home, lists these; each zone links to a sheet named view.zone that lists its records. " +
                                    "Sheet names are cut to Excel's 31 characters. The zone's SOA record is not counted."));
        if (result.Zones.Count > 500)
        {
            notes.Children.Add(Ui.Muted($"This makes {result.Zones.Count + 1:N0} sheets. Excel can be slow to open a workbook with this many; " +
                                        "leaving out reverse zones or empty zones makes it smaller."));
        }

        var table = CurrentTable = new FilterableTable(definition);
        return Page(RowCount, RowCountReport.Description, options, notes, table);
    }

    /// <summary>Asks where to save the workbook and creates it.</summary>
    public async Task<bool> CreateWorkbookAsync()
    {
        if (RowCounts is null) return false;
        var path = await ResultExportDialog.AskWorkbookAsync(this, ReportTemplateStore.Slug($"{_inventory?.GridName ?? "grid"} row count"));
        return path is not null && await CreateWorkbookToFileAsync(path);
    }

    /// <summary>
    /// Creates the Row count workbook at this path (.xlsx is added if the name lacks it). It is written in the background.
    /// </summary>
    public async Task<bool> CreateWorkbookToFileAsync(string path)
    {
        if (RowCounts is not { } result) return false;
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";
        var gridName = _inventory?.GridName;
        var status = _workbookStatus;
        var target = path;

        try
        {
            var notes = await Task.Run(() => RowCountReport.WriteFile(target, result, gridName));
            if (status is not null)
            {
                status.Text = $"Created {Path.GetFullPath(target)}: {result.Zones.Count + 1:N0} sheets, {result.TotalRecords:N0} records." +
                              (notes.Count > 0 ? $" {notes.Count} thing{(notes.Count == 1 ? "" : "s")} Excel's limits changed are listed on its About sheet." : "");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (status is not null) status.Text = $"Could not write {target}: {ex.Message}";
            return false;
        }
    }

    // ---- Exporting a built-in report ------------------------------------------------

    /// <summary>True when the built-in report on screen has something to export.</summary>
    public bool CanExport => _exportable is not null;

    /// <summary>What the Export button last said (where the file went, or why it didn't).</summary>
    public string ExportStatusText => _exportStatus?.Text ?? "";

    /// <summary>
    /// All the text on the built-in report page on screen. It is read from the page's own controls, so it is there as soon as the
    /// page is made, before the window has laid it out. Used by tests.
    /// </summary>
    public IReadOnlyList<string> PageText => _body.Content is Control page ? TextIn(page).ToList() : [];

    private static IEnumerable<string> TextIn(Control control)
    {
        if (control is TextBlock { Text: { Length: > 0 } text }) yield return text;
        foreach (var child in Avalonia.LogicalTree.LogicalExtensions.GetLogicalChildren(control).OfType<Control>())
        {
            foreach (var inner in TextIn(child)) yield return inner;
        }
    }

    /// <summary>The options of a report page, with an Export button and its message under them.</summary>
    private Control WithExport(Control options, TableDefinition definition, string title)
    {
        _exportable = definition;
        _exportTitle = title;

        var button = new Button { Content = "Export\u2026" };
        var status = _exportStatus = new TextBlock { Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        button.Click += async (_, _) => await ExportAsync();

        return new StackPanel { Spacing = 6, Children = { options, Ui.Row(button, status) } };
    }

    /// <summary>Asks where to save what the report on screen shows (as Excel, CSV or JSON) and writes it.</summary>
    public async Task<bool> ExportAsync()
    {
        if (_exportable is null) return false;
        var path = await ResultExportDialog.AskAsync(this, ReportTemplateStore.Slug(_exportTitle));
        return path is not null && await ExportToFileAsync(path);
    }

    /// <summary>
    /// Writes what the report on screen shows (with its filters applied) to this file: Excel for a .xlsx name, JSON for
    /// .json, otherwise CSV. The file is written in the background.
    /// </summary>
    public async Task<bool> ExportToFileAsync(string path)
    {
        if (_exportable is not { } definition) return false;
        var title = _exportTitle;
        var status = _exportStatus;

        try
        {
            var (format, rows) = await Task.Run(() =>
            {
                var result = TableExport.ToResult(definition);
                return (ResultExport.Write(result, path, spreadsheetSafe: true, title: title), result.Rows.Count);
            });
            if (status is not null)
                status.Text = $"Exported {rows:N0} row{(rows == 1 ? "" : "s")} to {Path.GetFullPath(path)} as {ResultExport.Describe(format)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (status is not null) status.Text = $"Could not write {path}: {ex.Message}";
            return false;
        }
    }

    /// <summary>A built-in report's page while its result is being worked out.</summary>
    private Control Working(string title, string description, string text) => Page(title, description, null, null,
        new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 30, 0, 0),
            Children =
            {
                new TextBlock { Text = text, FontSize = 16 },
                new ProgressBar { IsIndeterminate = true, Width = 280, HorizontalAlignment = HorizontalAlignment.Left },
                Ui.Muted("The window stays usable while this runs."),
            },
        });

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
