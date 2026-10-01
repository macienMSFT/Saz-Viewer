using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

/// <summary>
/// One node of a value tree as displayed: a styled label, optional metadata column (MAPI kind and
/// <c>@offset +length</c>) and children. Status items are the budget notices ("+N more ...").
/// </summary>
internal sealed record TreeItem(string Text, IReadOnlyList<StyledSpan> Spans, IReadOnlyList<TreeItem> Children)
{
    public bool IsStatus { get; init; }

    public string? Meta { get; init; }

    /// <summary>Styling for <see cref="Meta"/> (e.g. the coloured MAPI kind).</summary>
    public IReadOnlyList<StyledSpan>? MetaSpans { get; init; }

    /// <summary>Overrides the UIA name (defaults to <c>Text, Meta</c>).</summary>
    public string? AccessibleName { get; init; }
}

/// <summary>A row of a <see cref="ValueTreeViewModel"/>; all rows exist up front, only visible ones are listed.</summary>
internal sealed class TreeRow : ObservableObject
{
    private bool isExpanded;

    public TreeRow(TreeItem item, TreeRow? parent, int depth)
    {
        Parent = parent;
        Depth = depth;
        Meta = item.Meta;
        IsStatus = item.IsStatus;
        Line = new DocumentLine(item.Text, item.IsStatus ? LineKind.Muted : LineKind.Code, item.Spans, 0, 0, null);
        if (item.Meta is not null)
        {
            MetaLine = new DocumentLine(item.Meta, LineKind.Muted, item.MetaSpans ?? [], 0, 0, null);
        }
        AccessibleName = item.AccessibleName ?? (Meta is null ? Text : $"{Text}, {Meta}");
    }

    public DocumentLine Line { get; }

    /// <summary>The metadata column as a searchable, highlightable line (null when there is no metadata).</summary>
    public DocumentLine? MetaLine { get; }

    public string Text => Line.Text;

    public string? Meta { get; }

    public bool IsStatus { get; }

    public TreeRow? Parent { get; }

    public int Depth { get; }

    public List<TreeRow> Children { get; } = [];

    public bool IsExpandable => Children.Count > 0;

    public bool IsExpanded
    {
        get => isExpanded;
        internal set
        {
            if (SetProperty(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpanderGlyph));
                OnPropertyChanged(nameof(ItemStatus));
            }
        }
    }

    public string ExpanderGlyph => IsExpanded ? "\u2212" : "+";

    /// <summary>UIA item status: expanded / collapsed for containers.</summary>
    public string ItemStatus => IsExpandable ? (IsExpanded ? "expanded" : "collapsed") : "";

    /// <summary>1-based position among siblings and the sibling count (UIA PositionInSet / SizeOfSet).</summary>
    public int PositionInSet { get; internal set; }

    public int SizeOfSet { get; internal set; }

    public string AccessibleName { get; }
}

/// <summary>
/// A virtualizable, searchable tree flattened to its visible rows (the report's <c>buildTree</c> for JSON,
/// XML and MAPI). Search reveals collapsed ancestors of matches and restores the expansion state afterwards.
/// </summary>
internal sealed class ValueTreeViewModel : ISearchableView
{
    private readonly List<TreeRow> roots = [];
    private readonly List<TreeRow> all = [];
    private List<TreeRow> matchRows = [];
    private List<DocumentLine> matchLines = [];
    private Dictionary<TreeRow, bool>? snapshot;
    private int currentMatch = -1;

    public ValueTreeViewModel(IEnumerable<TreeItem> items, string accessibleName, bool expanded = true)
    {
        AccessibleName = accessibleName;
        foreach (var item in items)
        {
            roots.Add(Create(item, null, 0));
        }
        SetPositions(roots);
        foreach (var row in all)
        {
            row.IsExpanded = expanded && row.IsExpandable;
        }
        VisibleRows = new BulkObservableCollection<TreeRow>(Flatten());
    }

    public string AccessibleName { get; }

    public BulkObservableCollection<TreeRow> VisibleRows { get; }

    public IReadOnlyList<TreeRow> Roots => roots;

    public IReadOnlyList<TreeRow> AllRows => all;

    /// <summary>Raised to bring a row into view (current search match).</summary>
    public event EventHandler<TreeRow>? ScrollRequested;

    public void Toggle(TreeRow row) => SetExpanded(row, !row.IsExpanded);

    public void SetExpanded(TreeRow row, bool expanded)
    {
        if (!row.IsExpandable || row.IsExpanded == expanded)
        {
            return;
        }
        row.IsExpanded = expanded;
        Refresh();
    }

    public void ExpandAll() => SetAll(true);

    public void CollapseAll() => SetAll(false);

    public SearchOutcome Search(string foldedQuery)
    {
        ClearSearch();
        var capped = false;
        foreach (var row in all)
        {
            capped = !Highlight(row, row.Line, foldedQuery)
                || (row.MetaLine is not null && !Highlight(row, row.MetaLine, foldedQuery));
            if (capped)
            {
                break;
            }
        }
        if (matchRows.Count == 0)
        {
            return SearchOutcome.None;
        }
        snapshot = all.Where(row => row.IsExpandable).ToDictionary(row => row, row => row.IsExpanded);
        foreach (var row in matchRows.Distinct())
        {
            for (var ancestor = row.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                ancestor.IsExpanded = true;
            }
        }
        Refresh();
        return new SearchOutcome(matchRows.Count, capped);
    }

    public void ShowMatch(int index)
    {
        if (index < 0 || index >= matchRows.Count)
        {
            return;
        }
        if (currentMatch >= 0 && currentMatch < matchLines.Count)
        {
            matchLines[currentMatch].SetCurrentMatch(-1);
        }
        currentMatch = index;
        matchLines[index].SetCurrentMatch(index);
        ScrollRequested?.Invoke(this, matchRows[index]);
    }

    public void ClearSearch()
    {
        foreach (var line in matchLines.Distinct())
        {
            line.SetHighlights([], -1);
        }
        matchRows = [];
        matchLines = [];
        currentMatch = -1;
        if (snapshot is not null)
        {
            foreach (var (row, expanded) in snapshot)
            {
                row.IsExpanded = expanded;
            }
            snapshot = null;
            Refresh();
        }
    }

    /// <summary>Highlights matches in one line of <paramref name="row"/>; false once the match cap is reached.</summary>
    private bool Highlight(TreeRow row, DocumentLine line, string foldedQuery)
    {
        var highlights = new List<RowHighlight>();
        var withinCap = true;
        foreach (var start in SearchText.Matches(line.Text, foldedQuery))
        {
            if (matchRows.Count >= SearchText.MaxMatches)
            {
                withinCap = false;
                break;
            }
            highlights.Add(new RowHighlight(start, foldedQuery.Length, matchRows.Count));
            matchRows.Add(row);
            matchLines.Add(line);
        }
        if (highlights.Count > 0)
        {
            line.SetHighlights(highlights, -1);
        }
        return withinCap;
    }

    /// <summary>Plain-text outline of the visible rows (two spaces per level), used for row copy.</summary>
    public static string OutlineText(IEnumerable<TreeRow> rows) =>
        string.Join("\n", rows.Select(row => new string(' ', row.Depth * 2) + row.Text + (row.Meta is null ? "" : "  " + row.Meta)));

    private TreeRow Create(TreeItem item, TreeRow? parent, int depth)
    {
        var row = new TreeRow(item, parent, depth);
        all.Add(row);
        foreach (var child in item.Children)
        {
            row.Children.Add(Create(child, row, depth + 1));
        }
        SetPositions(row.Children);
        return row;
    }

    private static void SetPositions(List<TreeRow> siblings)
    {
        for (var index = 0; index < siblings.Count; index++)
        {
            siblings[index].PositionInSet = index + 1;
            siblings[index].SizeOfSet = siblings.Count;
        }
    }

    private void SetAll(bool expanded)
    {
        foreach (var row in all)
        {
            row.IsExpanded = expanded && row.IsExpandable;
        }
        Refresh();
    }

    private void Refresh() => VisibleRows.ReplaceAll(Flatten());

    private List<TreeRow> Flatten()
    {
        var visible = new List<TreeRow>(all.Count);
        var stack = new Stack<TreeRow>();
        for (var index = roots.Count - 1; index >= 0; index--)
        {
            stack.Push(roots[index]);
        }
        while (stack.Count > 0)
        {
            var row = stack.Pop();
            visible.Add(row);
            if (row.IsExpanded)
            {
                for (var index = row.Children.Count - 1; index >= 0; index--)
                {
                    stack.Push(row.Children[index]);
                }
            }
        }
        return visible;
    }
}
