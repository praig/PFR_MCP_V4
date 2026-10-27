using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Controls;

/// <summary>
/// A searchable, sortable grid whose rows form a tree. The first column shows
/// indentation and a twisty; search keeps matching rows and their ancestors and
/// opens those branches; sorting reorders siblings without breaking the tree.
/// </summary>
public sealed class TreeTable : UserControl
{
    private const double IndentWidth = 18;

    private readonly TreeTableDefinition _tree;
    private readonly DataGrid _grid;
    private readonly TextBlock _count;
    private readonly ObservableCollection<TreeRow> _visible = [];
    private readonly List<TreeRow> _roots;

    private HashSet<TreeRow>? _filterKeep;   // rows kept by the current search (null = no search)
    private int? _sortColumn;
    private bool _sortDescending;

    public TreeTable(TreeTableDefinition tree)
    {
        _tree = tree;
        _roots = [.. tree.Roots];

        var search = new TextBox
        {
            Watermark = $"Search {tree.Title}…",
            Width = 340,
        };
        search.TextChanged += (_, _) => ApplyFilter(search.Text);

        var expand = new Button { Content = "Expand all", Margin = new Thickness(10, 0, 0, 0) };
        expand.Click += (_, _) => SetAllExpanded(true);
        var collapse = new Button { Content = "Collapse all", Margin = new Thickness(6, 0, 0, 0) };
        collapse.Click += (_, _) => SetAllExpanded(false);

        _count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Opacity = 0.6,
        };

        _grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            Margin = new Thickness(0, 10, 0, 0),
            ItemsSource = _visible,
        };
        _grid.Sorting += OnSorting;
        _grid.KeyDown += OnKeyDown;

        for (var i = 0; i < tree.Columns.Count; i++)
        {
            var column = tree.Columns[i];
            var index = i;
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = column.Header,
                CellTemplate = index == 0
                    ? new FuncDataTemplate<TreeRow>((_, _) => new TreeCell(this), supportsRecycling: true)
                    : new FuncDataTemplate<TreeRow>((_, _) => TableCells.Text(index), supportsRecycling: true),
                SortMemberPath = $"Cells[{index}]",
                CanUserSort = true,
                Width = column.Width is { } w ? new DataGridLength(w) : DataGridLength.Auto,
                IsReadOnly = true,
                Tag = index,
            });
        }

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { search, expand, collapse, _count },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        Content = new DockPanel { Margin = new Thickness(12), Children = { toolbar, _grid } };

        Rebuild();
    }

    public string Title => _tree.Title;

    /// <summary>Rows currently in the grid (expanded and matching the search).</summary>
    public IReadOnlyList<TreeRow> VisibleRows => _visible;

    public void Toggle(TreeRow row)
    {
        if (!row.HasChildren) return;
        row.IsExpanded = !row.IsExpanded;

        var index = _visible.IndexOf(row);
        if (index < 0) return;

        if (row.IsExpanded)
        {
            var insertAt = index + 1;
            foreach (var r in VisibleDescendants(row)) _visible.Insert(insertAt++, r);
        }
        else
        {
            while (index + 1 < _visible.Count && _visible[index + 1].Depth > row.Depth)
            {
                _visible.RemoveAt(index + 1);
            }
        }
        UpdateCount();
    }

    public void SetAllExpanded(bool expanded)
    {
        foreach (var r in _roots.SelectMany(r => r.SelfAndDescendants()))
        {
            if (r.HasChildren) r.IsExpanded = expanded;
        }
        Rebuild();
    }

    public void ApplyFilter(string? text)
    {
        var terms = (text ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (terms.Length == 0)
        {
            _filterKeep = null;
        }
        else
        {
            // Keep matches and their ancestors; open every kept branch so matches show.
            _filterKeep = new HashSet<TreeRow>(ReferenceEqualityComparer.Instance);
            foreach (var match in _roots.SelectMany(r => r.SelfAndDescendants()).Where(r => r.Matches(terms)))
            {
                for (var r = match; r is not null && _filterKeep.Add(r); r = r.Parent)
                {
                    if (r != match) r.IsExpanded = true;
                }
            }
        }
        Rebuild();
    }

    private void Rebuild()
    {
        _visible.Clear();
        foreach (var root in _roots.Where(Kept))
        {
            _visible.Add(root);
            foreach (var r in VisibleDescendants(root)) _visible.Add(r);
        }
        UpdateCount();
    }

    private bool Kept(TreeRow row) => _filterKeep is null || _filterKeep.Contains(row);

    private IEnumerable<TreeRow> VisibleDescendants(TreeRow row)
    {
        if (!row.IsExpanded) yield break;
        foreach (var child in row.Children.Where(Kept))
        {
            yield return child;
            foreach (var d in VisibleDescendants(child)) yield return d;
        }
    }

    private void UpdateCount()
    {
        var items = _roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsGroup).ToList();
        var shown = _filterKeep is null ? items.Count : items.Count(_filterKeep.Contains);
        _count.Text = _filterKeep is null
            ? $"{items.Count:N0} {_tree.ItemNoun}"
            : $"{shown:N0} of {items.Count:N0} {_tree.ItemNoun} match";
    }

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        // Sort siblings at every level instead of the flat list, so the tree stays intact.
        e.Handled = true;
        if (e.Column.Tag is int index) SortBy(index);
    }

    /// <summary>
    /// Sorts siblings at every level by a column. Calling it again for the same
    /// column flips the direction, like clicking a header twice.
    /// </summary>
    public void SortBy(int index)
    {
        _sortDescending = _sortColumn == index && !_sortDescending;
        _sortColumn = index;

        var comparer = new ColumnComparer(index, _tree.Columns[index].Kind);
        Comparison<TreeRow> compare = (a, b) =>
        {
            var c = comparer.CompareCells(a.Cells, b.Cells);
            return _sortDescending ? -c : c;
        };

        _roots.Sort(compare);
        foreach (var r in _roots.SelectMany(r => r.SelfAndDescendants())) r.Children.Sort(compare);

        foreach (var col in _grid.Columns)
        {
            var i = (int)col.Tag!;
            var header = _tree.Columns[i].Header;
            col.Header = i == index ? $"{header} {(_sortDescending ? "▼" : "▲")}" : header;
        }
        Rebuild();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Left/Right arrows collapse/expand the selected row, like a file tree.
        if (_grid.SelectedItem is not TreeRow row || !row.HasChildren) return;
        if ((e.Key == Key.Right && !row.IsExpanded) || (e.Key == Key.Left && row.IsExpanded))
        {
            Toggle(row);
            e.Handled = true;
        }
    }

    /// <summary>First-column cell: indent, twisty and label. Rebinds itself when rows are recycled.</summary>
    private sealed class TreeCell : DockPanel
    {
        private static readonly Geometry Chevron = Geometry.Parse("M 0,0 L 5,5 L 0,10");

        private readonly TreeTable _owner;
        private readonly Border _indent = new();
        private readonly Button _twisty;
        private readonly Path _arrow;
        private readonly TextBlock _label = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        private TreeRow? _row;

        public TreeCell(TreeTable owner)
        {
            _owner = owner;
            _arrow = new Path
            {
                Data = Chevron,
                Stroke = Brushes.Gray,
                StrokeThickness = 1.6,
                Width = 6,
                Height = 10,
                Stretch = Stretch.None,
                RenderTransform = new RotateTransform(0),
            };
            _twisty = new Button
            {
                Content = _arrow,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,
                Margin = new Thickness(0, 0, 4, 0),
            };
            _twisty.Click += (_, _) =>
            {
                if (_row is not null) _owner.Toggle(_row);
            };

            Margin = new Thickness(6, 0, 12, 0);
            Children.Add(_indent);
            Children.Add(_twisty);
            Children.Add(_label);
            SetDock(_indent, Dock.Left);
            SetDock(_twisty, Dock.Left);

            DoubleTapped += (_, _) =>
            {
                if (_row is not null) _owner.Toggle(_row);
            };
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_row is not null) _row.PropertyChanged -= OnRowChanged;
            _row = DataContext as TreeRow;
            if (_row is not null) _row.PropertyChanged += OnRowChanged;
            Refresh();
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            var row = _row;
            _indent.Width = (row?.Depth ?? 0) * IndentWidth;
            // Leaves keep an invisible twisty so their labels line up with siblings.
            var expandable = row?.HasChildren == true;
            _twisty.Opacity = expandable ? 1 : 0;
            _twisty.IsHitTestVisible = expandable;
            ((RotateTransform)_arrow.RenderTransform!).Angle = row?.IsExpanded == true ? 90 : 0;
            _label.Text = row?.Cells[0] ?? "";
            _label.FontWeight = row?.IsGroup == true ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }
}

/// <summary>Cell factories shared by flat and tree tables.</summary>
public static class TableCells
{
    /// <summary>
    /// A text cell bound to the row (DataContext), so it stays correct when the
    /// grid recycles row containers while scrolling.
    /// </summary>
    public static TextBlock Text(int index)
    {
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.Bind(TextBlock.TextProperty, new Binding(".")
        {
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<object?, string>(row => row switch
            {
                TableRow t => t.Cells[index],
                TreeRow t => t.Cells[index],
                _ => "",
            }),
        });
        return text;
    }
}
