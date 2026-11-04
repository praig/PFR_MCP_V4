using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Clients;
using Pfr.Core.Ipam;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// IPAM module (spec §13): a client's address plan across all of its grids. Pick a client and the plan is built from
/// each of its grids' latest stored snapshots: every network once by prefix, which grids it is present in, nested by
/// containment under its network view, with the DHCP ranges inside. Nothing here is written anywhere.
/// <para>
/// The plan is built when the tab is opened and again after clients, grids or snapshots change, off the UI thread
/// (snapshots are decrypted and unpacked). Filters change what is shown without rebuilding it.
/// </para>
/// </summary>
public sealed class IpamView : UserControl
{
    private const string AllGrids = "All grids";

    /// <summary>
    /// More A / AAAA / PTR records than this and they are not listed until asked for: every row is built and kept, so a
    /// grid with hundreds of thousands of records would be slow and heavy. Hosts and DHCP ranges are far fewer.
    /// </summary>
    public const int RecordRowLimit = 20_000;

    private readonly ComboBox _clients = new() { MinWidth = 240 };
    private readonly ComboBox _grids = new() { MinWidth = 180 };
    private readonly CheckBox _shared = new() { Content = "Present in more than one grid", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _ranges = new() { Content = "DHCP ranges", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hosts = new() { Content = "Hosts", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _records = new() { Content = "DNS records", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showFindings = new() { Content = "Findings", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.9 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, IsVisible = false };
    private readonly ContentControl _body = new();
    private readonly Border _details = new()
    {
        IsVisible = false,
        BorderThickness = new Thickness(0, 1, 0, 0),
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
        Padding = new Thickness(18, 10),
        MaxHeight = 240,
    };

    private ClientWorkspace? _workspace;
    private IpamPlan? _plan;
    private List<string> _notes = [];
    private string? _preferredClientId;
    private bool _stale = true;
    private bool _updating;
    private bool _recordsHeldBack;
    private IReadOnlyList<IpamFinding> _findingList = [];
    private int _planVersion;
    private int _tableVersion;

    public IpamView()
    {
        _clients.SelectionChanged += async (_, _) =>
        {
            if (!_updating) await RebuildAsync();
        };
        _grids.SelectionChanged += async (_, _) =>
        {
            if (!_updating) await ApplyFilterAsync();
        };
        _shared.IsCheckedChanged += async (_, _) =>
        {
            if (!_updating) await ApplyFilterAsync();
        };
        _ranges.IsCheckedChanged += async (_, _) =>
        {
            if (!_updating) await ApplyFilterAsync();
        };
        _hosts.IsCheckedChanged += async (_, _) =>
        {
            if (!_updating) await ApplyFilterAsync();
        };
        _records.IsCheckedChanged += async (_, _) =>
        {
            if (_updating) return;
            _recordsHeldBack = false; // the user chose: the automatic safeguard no longer applies
            await ApplyFilterAsync();
        };
        _showFindings.IsCheckedChanged += async (_, _) =>
        {
            if (!_updating) await ApplyFilterAsync();
        };

        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(string? label, Control control)
        {
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 18, 8),
            };
            if (label is not null) item.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            item.Children.Add(control);
            bar.Children.Add(item);
        }
        Add("Client", _clients);
        Add("Show", _grids);
        Add(null, _shared);
        Add(null, _ranges);
        Add(null, _hosts);
        Add(null, _records);
        Add(null, _showFindings);

        var header = new StackPanel
        {
            Margin = new Thickness(18, 14, 18, 8),
            Children = { bar, _summary, _notice },
        };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_details, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(_details);
        layout.Children.Add(_body);
        Content = layout;

        ShowMessage("IPAM", "Open a client's address plan here. Unlock Clients and save grids to a client first.");
    }

    /// <summary>The plan being shown, or null while there is none.</summary>
    public IpamPlan? Plan => _plan;

    /// <summary>The address-plan tree, or null while a message or the findings are showing instead.</summary>
    public TreeTable? Table { get; private set; }

    /// <summary>The findings table while the Findings switch is on (and a plan is showing), otherwise null.</summary>
    public FilterableTable? FindingsTable { get; private set; }

    /// <summary>Everything the analysis found in the plan being shown (spec §13.7), warnings first.</summary>
    public IReadOnlyList<IpamFinding> Findings => _findingList;

    /// <summary>The label of the Findings switch, which carries the number of findings: "Findings (7)".</summary>
    public string FindingsLabel => _showFindings.Content?.ToString() ?? "";

    /// <summary>What the page says instead of a plan (locked, no clients, no snapshots...), or "" when it shows one.</summary>
    public string MessageText { get; private set; } = "";

    /// <summary>
    /// What the panel under the tree shows for the selected row: what each grid says about a network, a DHCP range's
    /// details, or a view's counts per grid. Null when nothing that has details is selected.
    /// </summary>
    public IpamDetail? Details { get; private set; }

    /// <summary>Grids, totals and snapshot dates, above the tree.</summary>
    public string SummaryText => _summary.Text ?? "";

    /// <summary>Grids left out and prefixes that couldn't be read.</summary>
    public string NoticeText => _notice.IsVisible ? _notice.Text ?? "" : "";

    public IReadOnlyList<string> ClientNames => Choices(_clients).Select(c => c.Name).ToList();

    public string? SelectedClientName => (_clients.SelectedItem as ClientChoice)?.Name;

    /// <summary>The grid filter's choices: "All grids", then each grid in the plan.</summary>
    public IReadOnlyList<string> GridChoices => (_grids.ItemsSource as IEnumerable<string>)?.ToList() ?? [];

    /// <summary>Marks the plan out of date (clients, grids or snapshots changed); it is rebuilt the next time it is shown.</summary>
    public void Invalidate() => _stale = true;

    /// <summary>Makes the next <see cref="ShowAsync"/> open this client (e.g. the one whose grid was just opened).</summary>
    public void PreferClient(string clientId) => _preferredClientId = clientId;

    /// <summary>
    /// Shows the address plan for a client, building it if the data changed since it was last built.
    /// </summary>
    /// <param name="workspace">The unlocked client data, or null while it is locked.</param>
    /// <param name="fallbackClientId">The client to open if none has been chosen yet.</param>
    public async Task ShowAsync(ClientWorkspace? workspace, string? fallbackClientId = null)
    {
        if (workspace is null)
        {
            _workspace = null;
            _plan = null;
            _stale = true;
            ShowMessage("Client data is locked", "Unlock Clients to see a client's address plan.");
            return;
        }

        var workspaceChanged = !ReferenceEquals(workspace, _workspace);
        _workspace = workspace;

        if (workspaceChanged || _stale)
        {
            FillClients(fallbackClientId);
            await RebuildAsync();
        }
        else if (_preferredClientId is { } preferred && SelectedClient?.Id != preferred
                 && Choices(_clients).FirstOrDefault(c => c.Id == preferred) is { } choice)
        {
            SelectClientQuietly(choice);
            _preferredClientId = null;
            await RebuildAsync();
        }
    }

    public async Task SelectClientAsync(string clientName)
    {
        if (Choices(_clients).FirstOrDefault(c => string.Equals(c.Name, clientName, StringComparison.OrdinalIgnoreCase)) is not { } choice)
            throw new ArgumentException($"No client named \"{clientName}\".");
        SelectClientQuietly(choice);
        await RebuildAsync();
    }

    /// <summary>Changes the filters and redraws the tree (the plan is not rebuilt).</summary>
    /// <param name="grid">Only networks present in this grid, and its ranges; null for every grid.</param>
    /// <param name="sharedOnly">Only networks present in more than one grid.</param>
    /// <param name="showRanges">Whether DHCP ranges are listed.</param>
    /// <param name="showHosts">Whether hosts are listed.</param>
    /// <param name="showRecords">Whether A / AAAA / PTR records are listed (including the ones under a host).</param>
    public async Task SetFilterAsync(string? grid, bool sharedOnly, bool showRanges, bool showHosts = true, bool showRecords = true)
    {
        _updating = true;
        try
        {
            _grids.SelectedItem = grid is null ? AllGrids : GridChoices.FirstOrDefault(g => string.Equals(g, grid, StringComparison.OrdinalIgnoreCase));
            _shared.IsChecked = sharedOnly;
            _ranges.IsChecked = showRanges;
            _hosts.IsChecked = showHosts;
            _records.IsChecked = showRecords;
        }
        finally
        {
            _updating = false;
        }
        await ApplyFilterAsync();
    }

    /// <summary>Shows the findings (true) or the address-plan tree (false). The plan is not rebuilt.</summary>
    public async Task SetShowFindingsAsync(bool show)
    {
        _updating = true;
        try
        {
            _showFindings.IsChecked = show;
        }
        finally
        {
            _updating = false;
        }
        await ApplyFilterAsync();
    }

    // ---- Building ----------------------------------------------------------------

    private ClientChoice? SelectedClient => _clients.SelectedItem as ClientChoice;

    private static List<ClientChoice> Choices(ComboBox box) =>
        (box.ItemsSource as IEnumerable<ClientChoice>)?.ToList() ?? [];

    /// <summary>Lists the clients and picks one: the one asked for, else the one already chosen, else the fallback, else the first.</summary>
    private void FillClients(string? fallbackClientId)
    {
        var previous = SelectedClient?.Id;
        var choices = _workspace!.ListClients().Select(c => new ClientChoice(c.Id, c.Name)).ToList();
        _updating = true;
        try
        {
            _clients.ItemsSource = choices;
            _clients.SelectedItem = choices.FirstOrDefault(c => c.Id == _preferredClientId)
                                    ?? choices.FirstOrDefault(c => c.Id == previous)
                                    ?? choices.FirstOrDefault(c => c.Id == fallbackClientId)
                                    ?? choices.FirstOrDefault();
        }
        finally
        {
            _updating = false;
        }
        _preferredClientId = null;
    }

    private void SelectClientQuietly(ClientChoice choice)
    {
        _updating = true;
        try
        {
            _clients.SelectedItem = Choices(_clients).FirstOrDefault(c => c.Id == choice.Id);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Builds the selected client's plan from its grids' latest snapshots, then shows it.</summary>
    public async Task RebuildAsync()
    {
        var version = ++_planVersion;
        var workspace = _workspace;
        if (workspace is null) return;
        if (SelectedClient is not { } choice)
        {
            _plan = null;
            ShowMessage("No clients yet", "Add a client in Clients, then import or save grid backups into it.");
            return;
        }

        _summary.Text = $"Building {choice.Name}'s address plan…";
        try
        {
            var (plan, notes, findings) = await Task.Run(() =>
            {
                var client = workspace.GetClient(choice.Id);
                var gridNotes = new List<string>();
                var inputs = IpamSources.LoadGrids(workspace, client, gridNotes);
                var built = IpamBuilder.Build(inputs);
                return (built, gridNotes, IpamFindings.Analyze(built));
            });

            if (version != _planVersion) return; // a newer request replaced this one
            _plan = plan;
            _notes = notes;
            _findingList = findings;
            _showFindings.Content = findings.Count == 0 ? "Findings" : $"Findings ({findings.Count:N0})";
            _stale = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException
                                       or ArgumentException)
        {
            if (version != _planVersion) return;
            _plan = null;
            ShowMessage("Could not build the address plan", ex.Message);
            return;
        }

        if (_plan!.Sources.Count == 0)
        {
            ShowMessage($"{choice.Name} has no grid snapshots yet",
                "Import a backup into this client in Clients; its networks and DHCP ranges then appear here.");
            _notice.Text = string.Join("\n", _notes);
            _notice.IsVisible = _notes.Count > 0;
            return;
        }

        FillGrids();

        // Listing every record of a very large grid would be slow, so they start out unticked, and say so. A choice
        // the user made is left alone; only what this safeguard unticked is ticked again for a smaller plan.
        _updating = true;
        try
        {
            if (_plan!.RecordCount > RecordRowLimit)
            {
                if (_records.IsChecked == true) _recordsHeldBack = true;
                _records.IsChecked = false;
            }
            else if (_recordsHeldBack)
            {
                _records.IsChecked = true;
                _recordsHeldBack = false;
            }
        }
        finally
        {
            _updating = false;
        }
        await ApplyFilterAsync();
    }

    private void FillGrids()
    {
        var previous = _grids.SelectedItem as string;
        var items = new List<string> { AllGrids };
        items.AddRange(_plan!.Sources.Select(s => s.Name));

        _updating = true;
        try
        {
            _grids.ItemsSource = items;
            _grids.SelectedItem = items.FirstOrDefault(i => i == previous) ?? AllGrids;
        }
        finally
        {
            _updating = false;
        }
    }

    private IpamFilter CurrentFilter()
    {
        var kinds = IpamLeafKinds.None;
        if (_ranges.IsChecked == true) kinds |= IpamLeafKinds.Ranges;
        if (_hosts.IsChecked == true) kinds |= IpamLeafKinds.Hosts;
        if (_records.IsChecked == true) kinds |= IpamLeafKinds.Records;

        return new IpamFilter(
            _grids.SelectedItem is string grid && grid != AllGrids ? grid : null,
            kinds,
            sharedOnly: _shared.IsChecked == true);
    }

    private async Task ApplyFilterAsync()
    {
        if (_plan is not { } plan) return;
        var version = ++_tableVersion;
        IReadOnlyList<IpamFinding>? shownFindings = null;

        if (_showFindings.IsChecked == true)
        {
            shownFindings = ShowFindingsTable(plan);
        }
        else
        {
            var filter = CurrentFilter();
            var definition = await Task.Run(() => IpamTables.Build(plan, filter));
            if (version != _tableVersion || !ReferenceEquals(plan, _plan)) return;

            FindingsTable = null;
            Table = new TreeTable(definition);
            Table.SelectionChanged += (_, _) => ShowDetails();
            _body.Content = Table;
            _details.IsVisible = true;
            ShowDetails();
        }

        MessageText = "";
        _summary.Text = SummaryOf(plan) + (shownFindings is null ? "" : "\n" + FindingsSummary(shownFindings, _findingList.Count));
        var notices = _notes.Concat(plan.Problems).ToList();
        if (plan.RecordCount > RecordRowLimit && _records.IsChecked != true)
        {
            notices.Add($"{plan.RecordCount:N0} DNS records are not listed, because that is a lot of rows to build. " +
                        "Tick \"DNS records\" to list them.");
        }
        _notice.Text = string.Join("\n", notices);
        _notice.IsVisible = _notice.Text.Length > 0;
    }

    private static string SummaryOf(IpamPlan plan)
    {
        var grids = string.Join(", ", plan.Sources.Select(s =>
            s.AsOf is { } at ? $"{s.Name} (snapshot {at.ToLocalTime():yyyy-MM-dd})" : s.Name));
        return $"{Count(plan.NetworkCount, "network")}, {Count(plan.ContainerCount, "container")}, " +
               $"{Count(plan.RangeCount, "DHCP range")}, {Count(plan.HostCount, "host")}, {Count(plan.RecordCount, "DNS record")} " +
               $"in {Count(plan.Views.Count, "network view")}.   Grids: {grids}";
    }

    /// <summary>Shows what the selected row is about in the panel under the tree.</summary>
    private void ShowDetails()
    {
        Details = IpamDetails.For(Table?.SelectedRow?.Tag);
        _details.Child = RenderDetails(Details);
    }

    /// <summary>Replaces the tree with the findings (those involving the chosen grid, when one is) and returns the ones shown.</summary>
    private IReadOnlyList<IpamFinding> ShowFindingsTable(IpamPlan plan)
    {
        var grid = _grids.SelectedItem is string chosen && chosen != AllGrids ? chosen : null;
        var shown = IpamFindings.Filter(_findingList, grid: grid);

        var rows = shown.Select(f => new TableRow(
        [
            f.Severity == IpamSeverity.Warning ? "Warning" : "Information", f.Title, f.Subject, f.Count.ToString("N0"),
            string.Join(", ", f.Sources), f.Summary,
        ]) { Tag = f }).ToList();

        Table = null;
        FindingsTable = new FilterableTable(new TableDefinition("findings",
        [
            new TableColumn("Severity", ColumnKind.Text, 100),
            new TableColumn("Finding", ColumnKind.Text, 300),
            new TableColumn("About", ColumnKind.Text, 200),
            new TableColumn("Objects", ColumnKind.Number, 80),
            new TableColumn("Grids", ColumnKind.Text, 160),
            new TableColumn("What it means", ColumnKind.Text, 700),
        ], rows));
        FindingsTable.SelectionChanged += (_, _) => ShowFindingDetails();
        _body.Content = FindingsTable;
        _details.IsVisible = true;
        ShowFindingDetails();
        return shown;
    }

    /// <summary>Shows the objects behind the selected finding in the panel under the table.</summary>
    private void ShowFindingDetails()
    {
        var finding = FindingsTable?.SelectedRow?.Tag as IpamFinding;
        Details = finding is null || _plan is null ? null : IpamDetails.ForFinding(_plan, finding);
        _details.Child = RenderDetails(Details, "Select a finding to see the networks, hosts or records it covers.");
    }

    private static string FindingsSummary(IReadOnlyList<IpamFinding> shown, int total)
    {
        if (total == 0) return "No findings in this plan.";

        var warnings = shown.Count(f => f.Severity == IpamSeverity.Warning);
        var of = shown.Count == total ? "" : $" of {total:N0}";
        return $"{shown.Count:N0}{of} finding{(shown.Count == 1 ? "" : "s")}: {warnings:N0} warning{(warnings == 1 ? "" : "s")}, " +
               $"{shown.Count - warnings:N0} for information.";
    }

    private static Control RenderDetails(IpamDetail? detail, string? whenNothing = null)
    {
        if (detail is null)
        {
            return new TextBlock
            {
                Text = whenNothing ?? "Select a network, a DHCP range or a network view to see what each grid says about it.",
                Opacity = 0.6,
            };
        }

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        for (var c = 0; c < detail.Headers.Count; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r <= detail.Rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        void Cell(string text, int row, int column, bool header)
        {
            var cell = new SelectableTextBlock
            {
                Text = text,
                Margin = new Thickness(0, 1, 24, 1),
                Opacity = header ? 0.6 : 1,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        for (var c = 0; c < detail.Headers.Count; c++) Cell(detail.Headers[c], 0, c, header: true);
        for (var r = 0; r < detail.Rows.Count; r++)
        {
            for (var c = 0; c < detail.Rows[r].Count; c++) Cell(detail.Rows[r][c], r + 1, c, header: false);
        }

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new SelectableTextBlock { Text = detail.Title, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(grid);
        if (detail.Note.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = detail.Note, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0),
            });
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = stack,
        };
    }

    private void ShowMessage(string title, string detail)
    {
        Table = null;
        FindingsTable = null;
        Details = null;
        _details.IsVisible = false;
        MessageText = $"{title}. {detail}";
        _summary.Text = "";
        _notice.IsVisible = false;
        _body.Content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 480,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Opacity = 0.75 },
            },
        };
    }

    private static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    private sealed record ClientChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
