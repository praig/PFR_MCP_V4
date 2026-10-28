using System.ComponentModel;

namespace Pfr.Gui.Tables;

/// <summary>
/// A row in a tree table: cells like <see cref="TableRow"/>, plus children and
/// expand/collapse state. Group rows (views, TLDs) are bold.
/// </summary>
public sealed class TreeRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public TreeRow(IReadOnlyList<string> cells, bool isGroup = false)
    {
        Cells = cells;
        IsGroup = isGroup;
        SearchText = string.Join("\u001f", cells).ToLowerInvariant();
    }

    public IReadOnlyList<string> Cells { get; }
    public bool IsGroup { get; }
    public string SearchText { get; }

    /// <summary>What the row stands for (e.g. a network), so a details panel can show more about it. Not displayed.</summary>
    public object? Tag { get; init; }

    public TreeRow? Parent { get; private set; }
    public List<TreeRow> Children { get; } = [];
    public int Depth { get; private set; }
    public bool HasChildren => Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TreeRow Add(TreeRow child)
    {
        child.Parent = this;
        child.SetDepth(Depth + 1);
        Children.Add(child);
        return child;
    }

    private void SetDepth(int depth)
    {
        Depth = depth;
        foreach (var c in Children) c.SetDepth(depth + 1);
    }

    public bool Matches(string[] terms) => terms.All(t => SearchText.Contains(t, StringComparison.Ordinal));

    public IEnumerable<TreeRow> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var d in c.SelfAndDescendants()) yield return d;
        }
    }
}

public sealed class TreeTableDefinition(
    string title, IReadOnlyList<TableColumn> columns, IReadOnlyList<TreeRow> roots, string itemNoun)
{
    public string Title { get; } = title;

    /// <summary>The first column is the tree column (indent + twisty).</summary>
    public IReadOnlyList<TableColumn> Columns { get; } = columns;

    public IReadOnlyList<TreeRow> Roots { get; } = roots;

    /// <summary>What a non-group row is, for the count label ("zones", "networks").</summary>
    public string ItemNoun { get; } = itemNoun;
}
