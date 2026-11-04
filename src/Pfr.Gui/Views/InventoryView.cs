using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Core.Nios.Model;
using Pfr.Gui.Controls;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Views;

/// <summary>
/// Inventory module: an empty state until a backup is loaded, then a section
/// list (Overview, Members, Zones…) beside the selected table.
/// </summary>
public sealed class InventoryView : UserControl
{
    private readonly ListBox _sections;
    private readonly ContentControl _body;

    /// <summary>The controls made so far: a section's grid is only created the first time it is opened.</summary>
    private readonly Dictionary<string, Control> _pages = new();

    private IReadOnlyDictionary<string, object> _definitions = new Dictionary<string, object>();
    private IReadOnlyList<SectionItem> _items = [];
    private GridInventory? _shown;
    private int _showVersion;

    public InventoryView()
    {
        _sections = new ListBox
        {
            Width = 210,
            Margin = new Thickness(0, 0, 0, 0),
            Background = Brushes.Transparent,
        };
        _sections.SelectionChanged += (_, _) =>
        {
            if (_sections.SelectedItem is SectionItem item && PageFor(item.Name) is { } page)
            {
                _body!.Content = page;
            }
        };

        _body = new ContentControl();

        // Built once and reused for every backup: Avalonia controls can only have
        // one parent, so re-wrapping _sections/_body on each load would throw.
        var nav = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Padding = new Thickness(6, 10),
            Child = _sections,
        };
        DockPanel.SetDock(nav, Dock.Left);
        _loadedLayout = new DockPanel { Children = { nav, _body } };

        ShowEmpty();
    }

    private readonly DockPanel _loadedLayout;

    /// <summary>Raised when the user clicks "Open backup" in the empty state.</summary>
    public event EventHandler? OpenRequested;

    public IReadOnlyList<string> SectionNames => _items.Select(i => i.Name).ToList();

    /// <summary>True while the tables of a grid are being built in the background.</summary>
    public bool IsPreparing { get; private set; }

    public string? SelectedSection => (_sections.SelectedItem as SectionItem)?.Name;

    public Control? CurrentPage => _body.Content as Control;

    public void ShowEmpty(string? message = null)
    {
        _showVersion++; // a grid still being prepared is no longer wanted
        IsPreparing = false;
        _pages.Clear();
        _definitions = new Dictionary<string, object>();
        _items = [];
        _shown = null;
        _sections.ItemsSource = null;
        _body.Content = null;

        var open = new Button
        {
            Content = "Open grid backup…",
            HorizontalAlignment = HorizontalAlignment.Center,
            Classes = { "accent" },
            Padding = new Thickness(18, 8),
        };
        open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        Content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 14,
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = "No grid loaded",
                    FontSize = 22, FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = message ?? "Open a NIOS grid backup (.bak, .tar.gz) or onedb.xml to see its members, " +
                                      "DNS, IPAM and DHCP inventory. Nothing leaves this computer.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Opacity = 0.75,
                },
                open,
            },
        };
    }

    /// <summary>An inventory's tables, built and ready to show (see <see cref="Prepare"/>). Holds data only, no controls.</summary>
    public sealed class PreparedInventory
    {
        internal PreparedInventory(GridInventory inventory, IReadOnlyList<SectionItem> items, IReadOnlyDictionary<string, object> definitions)
        {
            Inventory = inventory;
            Items = items;
            Definitions = definitions;
        }

        public GridInventory Inventory { get; }
        internal IReadOnlyList<SectionItem> Items { get; }
        internal IReadOnlyDictionary<string, object> Definitions { get; }
    }

    /// <summary>
    /// Builds every section's rows. This is the slow part of showing a grid (a row for every record), and it makes no
    /// controls, so it can run on any thread; <see cref="ShowInventoryAsync"/> does that.
    /// </summary>
    public static PreparedInventory Prepare(GridInventory inventory)
    {
        var items = new List<SectionItem> { new("Overview", null) };
        var definitions = new Dictionary<string, object>();

        foreach (var section in InventoryTables.Build(inventory))
        {
            switch (section)
            {
                case TableDefinition table:
                    definitions[table.Title] = table;
                    items.Add(new SectionItem(table.Title, table.Rows.Count));
                    break;
                case TreeTableDefinition tree:
                    definitions[tree.Title] = tree;
                    items.Add(new SectionItem(tree.Title, tree.Roots.SelectMany(r => r.SelfAndDescendants()).Count(r => !r.IsGroup)));
                    break;
            }
        }
        return new PreparedInventory(inventory, items, definitions);
    }

    /// <summary>Shows a grid, building its tables on the calling thread (the window uses <see cref="ShowInventoryAsync"/>).</summary>
    public void ShowInventory(GridInventory inventory)
    {
        _showVersion++;
        Show(Prepare(inventory));
    }

    /// <summary>
    /// Shows a grid without holding up the window: a "preparing" message is shown while its tables are built on a
    /// background thread, then the sections appear. If another grid is shown (or the view is emptied) in the meantime,
    /// this one is dropped.
    /// </summary>
    public async Task ShowInventoryAsync(GridInventory inventory)
    {
        var version = ++_showVersion;
        IsPreparing = true;
        Content = Preparing(inventory);
        try
        {
            var prepared = await Task.Run(() => Prepare(inventory));
            if (version != _showVersion) return;
            Show(prepared);
        }
        catch (Exception ex)
        {
            if (version == _showVersion) ShowEmpty($"Could not prepare the tables: {ex.Message}");
            throw;
        }
    }

    private void Show(PreparedInventory prepared)
    {
        IsPreparing = false;
        _pages.Clear();
        _definitions = prepared.Definitions;
        _items = prepared.Items;
        _shown = prepared.Inventory;

        _sections.ItemsSource = _items.ToList();
        Content = _loadedLayout;
        _sections.SelectedIndex = 0;
        _body.Content = PageFor("Overview"); // also when Overview was already selected
    }

    /// <summary>
    /// The control for a section, made the first time it is asked for and kept after that, so opening a grid does not
    /// build a grid control for every table up front.
    /// </summary>
    private Control? PageFor(string name)
    {
        if (_pages.TryGetValue(name, out var page)) return page;

        Control? made = null;
        if (name == "Overview" && _shown is { } inventory)
        {
            made = BuildOverview(inventory);
        }
        else if (_definitions.TryGetValue(name, out var definition))
        {
            if (definition is TableDefinition table) made = new FilterableTable(table);
            else if (definition is TreeTableDefinition tree) made = new TreeTable(tree);
        }

        if (made is not null) _pages[name] = made;
        return made;
    }

    private static Control Preparing(GridInventory inventory) => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = 14,
        Children =
        {
            new TextBlock
            {
                Text = $"Preparing {inventory.GridName ?? "the grid"}\u2026",
                FontSize = 20, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            },
            new TextBlock
            {
                Text = "Building the tables. The window stays usable while this runs.",
                Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center,
            },
            new ProgressBar { IsIndeterminate = true, Width = 280 },
        },
    };

    public void SelectSection(string name)
    {
        var items = _sections.ItemsSource as IEnumerable<SectionItem>;
        _sections.SelectedItem = items?.FirstOrDefault(i => i.Name == name);
    }

    private static Control BuildOverview(GridInventory inv)
    {
        var gm = inv.Members.FirstOrDefault(m => m.IsGridMaster);
        var gmc = inv.Members.Where(m => m.IsGridMasterCandidate).Select(m => m.HostName).ToList();

        var cards = new WrapPanel { Orientation = Orientation.Horizontal };
        void Card(string label, int value, string detail)
        {
            cards.Children.Add(new Border
            {
                Width = 210,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(16, 12),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = label, Opacity = 0.7 },
                        new TextBlock { Text = value.ToString("N0"), FontSize = 28, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = detail, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    },
                },
            });
        }

        var nets = inv.Networks;
        Card("Members", inv.Members.Count,
            Plural(inv.Members.Count(m => m.HaEnabled), "HA pair"));
        Card("DNS views", inv.DnsViews.Count,
            $"{inv.DnsViews.Count(v => v.RecursionEnabled)} with recursion");
        Card("Zones", inv.Zones.Count,
            $"{inv.Zones.Count(z => !z.IsReverse)} forward · {inv.Zones.Count(z => z.IsReverse)} reverse");
        Card("DNS records", inv.Records.Count,
            string.Join(" · ", inv.Records.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Take(4)
                .Select(g => $"{g.Count()} {g.Key}"))
            + $"\n{Plural(inv.HostCount, "host object")}");
        Card("Networks", nets.Count(n => !n.IsContainer),
            $"{Plural(nets.Count(n => n.IsContainer), "container")} · {Plural(inv.NetworkViews.Count, "network view")}");
        Card("DHCP ranges", inv.DhcpRanges.Count,
            $"{inv.DhcpRanges.Count(r => r.AssociationType is "Failover" or "MsFailover")} in failover");

        var facts = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 20),
        };
        void Fact(int row, string label, string value, IBrush? color = null)
        {
            var l = new TextBlock { Text = label, Opacity = 0.7, Margin = new Thickness(0, 2, 24, 2) };
            var v = new TextBlock { Text = value, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap };
            if (color is not null)
            {
                v.Foreground = color;
                v.FontWeight = FontWeight.SemiBold;
            }
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            facts.Children.Add(l);
            facts.Children.Add(v);
        }
        Fact(0, "NIOS version", inv.NiosVersion ?? "unknown");
        Fact(1, "Grid Master", gm is null ? "unknown" : $"{gm.HostName}  ({gm.Ipv4Address})");
        Fact(2, "Master candidates", gmc.Count == 0 ? "none" : string.Join(", ", gmc));
        var nextExpiry = inv.Members.Select(m => m.NextLicenseExpiry)
            .Concat(inv.Licenses.Select(l => l.Expires)).Where(e => e is not null).Min();
        var expiryStatus = GridLicense.ExpiryStatus(nextExpiry, DateTimeOffset.UtcNow);
        Fact(3, "Next license expiry", nextExpiry is { } ne
            ? $"{ne:yyyy-MM-dd}" + (expiryStatus.Length > 0 ? $"  ({expiryStatus})" : "")
            : "none (all permanent)",
            TableCells.StatusBrush(expiryStatus));

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24, 18),
                Children =
                {
                    new TextBlock
                    {
                        Text = inv.GridName ?? "Grid", FontSize = 24, FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(0, 0, 0, 10),
                    },
                    facts,
                    cards,
                },
            },
        };
    }

    private static string Plural(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    internal sealed record SectionItem(string Name, int? Count)
    {
        public override string ToString() => Count is null ? Name : $"{Name}  ({Count:N0})";
    }
}
