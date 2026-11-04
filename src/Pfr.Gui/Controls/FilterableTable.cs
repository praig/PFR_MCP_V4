using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Pfr.Gui.Tables;

namespace Pfr.Gui.Controls;

/// <summary>
/// A search box over a sortable, read-only grid. Search matches every word
/// typed against any column (case-insensitive).
/// </summary>
public sealed class FilterableTable : UserControl
{
    private readonly TableDefinition _table;
    private readonly DataGrid _grid;
    private readonly TextBlock _count;

    public FilterableTable(TableDefinition table)
    {
        _table = table;

        var search = new TextBox
        {
            Watermark = $"Search {table.Title}…",
            Width = 340,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        search.TextChanged += (_, _) => ApplyFilter(search.Text);

        _count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = Brushes.Gray,
        };

        _grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            SelectionMode = DataGridSelectionMode.Extended,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            Margin = new Thickness(0, 10, 0, 0),
        };

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var index = i;
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = column.Header,
                // Bound to the row, so recycled rows show the right values while scrolling.
                CellTemplate = new FuncDataTemplate<TableRow>((_, _) => TableCells.Text(index, column.Kind), supportsRecycling: true),
                SortMemberPath = $"{nameof(TableRow.Cells)}[{index}]",
                CustomSortComparer = new ColumnComparer(index, column.Kind),
                CanUserSort = true,
                Width = column.Width is { } w ? new DataGridLength(w) : DataGridLength.Auto,
                IsReadOnly = true,
            });
        }

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Children = { search, _count } };
        DockPanel.SetDock(toolbar, Dock.Top);

        _grid.SelectionChanged += (_, _) => RaiseSelectionChanged();

        Content = new DockPanel { Margin = new Thickness(12), Children = { toolbar, _grid } };
        ApplyFilter(null);
    }

    public string Title => _table.Title;

    /// <summary>Raised when the selected row changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>The selected row (the first, if several are selected), or null.</summary>
    public TableRow? SelectedRow => _grid.SelectedItem as TableRow ?? _lastSelected;

    /// <summary>Selects a row (used by tests and by code that jumps to one).</summary>
    public void Select(TableRow? row)
    {
        _grid.SelectedItem = row;

        // The grid normally reports the change itself; make sure listeners hear of it even if it has not yet been laid out.
        if (!ReferenceEquals(_lastSelected, row))
        {
            _lastSelected = row;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private TableRow? _lastSelected;

    private void RaiseSelectionChanged()
    {
        _lastSelected = _grid.SelectedItem as TableRow;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rows currently shown (after filtering).</summary>
    public int VisibleRowCount { get; private set; }

    /// <summary>The rows currently shown, in the order they were given (not the order the user has sorted them into).</summary>
    public IReadOnlyList<TableRow> VisibleRows => _visible;

    private IReadOnlyList<TableRow> _visible = [];

    public void ApplyFilter(string? text)
    {
        var terms = (text ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rows = terms.Length == 0 ? _table.Rows : _table.Rows.Where(r => r.Matches(terms)).ToList();
        _grid.ItemsSource = rows;
        _visible = rows;
        VisibleRowCount = rows.Count;
        _count.Text = terms.Length == 0
            ? $"{_table.Rows.Count:N0} rows"
            : $"{rows.Count:N0} of {_table.Rows.Count:N0} rows";
    }
}
