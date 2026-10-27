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
    private readonly Dictionary<string, Control> _pages = new();

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
            if (_sections.SelectedItem is SectionItem item) _body!.Content = _pages[item.Name];
        };

        _body = new ContentControl();
        ShowEmpty();
    }

    /// <summary>Raised when the user clicks "Open backup" in the empty state.</summary>
    public event EventHandler? OpenRequested;

    public IReadOnlyList<string> SectionNames => _pages.Keys.ToList();

    public string? SelectedSection => (_sections.SelectedItem as SectionItem)?.Name;

    public Control? CurrentPage => _body.Content as Control;

    public void ShowEmpty(string? message = null)
    {
        _pages.Clear();
        _sections.ItemsSource = null;

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

    public void ShowInventory(GridInventory inventory)
    {
        _pages.Clear();
        var items = new List<SectionItem>();

        _pages["Overview"] = BuildOverview(inventory);
        items.Add(new SectionItem("Overview", null));

        foreach (var section in InventoryTables.Build(inventory))
        {
            switch (section)
            {
                case TableDefinition table:
                    _pages[table.Title] = new FilterableTable(table);
                    items.Add(new SectionItem(table.Title, table.Rows.Count));
                    break;
                case TreeTableDefinition tree:
                    _pages[tree.Title] = new TreeTable(tree);
                    var count = tree.Roots.SelectMany(r => r.SelfAndDescendants()).Count(r => !r.IsGroup);
                    items.Add(new SectionItem(tree.Title, count));
                    break;
            }
        }

        _sections.ItemsSource = items;

        var nav = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Padding = new Thickness(6, 10),
            Child = _sections,
        };
        DockPanel.SetDock(nav, Dock.Left);

        Content = new DockPanel { Children = { nav, _body } };
        _sections.SelectedIndex = 0;
    }

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
            $"{inv.Members.Count(m => m.HaEnabled)} HA pairs");
        Card("DNS views", inv.DnsViews.Count,
            $"{inv.DnsViews.Count(v => v.RecursionEnabled)} with recursion");
        Card("Zones", inv.Zones.Count,
            $"{inv.Zones.Count(z => !z.IsReverse)} forward · {inv.Zones.Count(z => z.IsReverse)} reverse");
        Card("DNS records", inv.Records.Count,
            string.Join(" · ", inv.Records.GroupBy(r => r.Type).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")));
        Card("Networks", nets.Count(n => !n.IsContainer),
            $"{nets.Count(n => n.IsContainer)} containers · {inv.NetworkViews.Count} network views");
        Card("DHCP ranges", inv.DhcpRanges.Count,
            $"{inv.DhcpRanges.Count(r => r.AssociationType is "Failover" or "MsFailover")} in failover");

        var facts = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 20),
        };
        void Fact(int row, string label, string value)
        {
            var l = new TextBlock { Text = label, Opacity = 0.7, Margin = new Thickness(0, 2, 24, 2) };
            var v = new TextBlock { Text = value, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            facts.Children.Add(l);
            facts.Children.Add(v);
        }
        Fact(0, "NIOS version", inv.NiosVersion ?? "unknown");
        Fact(1, "Grid Master", gm is null ? "unknown" : $"{gm.HostName}  ({gm.Ipv4Address})");
        Fact(2, "Master candidates", gmc.Count == 0 ? "none" : string.Join(", ", gmc));

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

    private sealed record SectionItem(string Name, int? Count)
    {
        public override string ToString() => Count is null ? Name : $"{Name}  ({Count:N0})";
    }
}
