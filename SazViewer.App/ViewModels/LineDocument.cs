using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

/// <summary>Visual role of a text row; the view maps each to a theme style.</summary>
internal enum LineKind
{
    Code,
    Heading,
    FormatMeta,
    Warning,
    DecodeStatus,
    Muted,
    Toggle
}

/// <summary>Syntax / semantic colour of a span within a row; the view maps each to a theme brush.</summary>
internal enum SpanStyle
{
    Muted,
    Badge,
    Key,
    String,
    Number,
    Literal,
    Punctuation,
    Tag,
    Attribute,
    Comment,
    Value,
    Accent,
    Bold,
    Warning,
    /// <summary>Tree node name: accent colour, semibold.</summary>
    Name
}

internal readonly record struct StyledSpan(int Start, int Length, SpanStyle Style)
{
    public int End => Start + Length;
}

/// <summary>A highlighted search match fragment within one row.</summary>
internal readonly record struct RowHighlight(int Start, int Length, int MatchIndex);

/// <summary>A collapsible group of rows (the report's <c>&lt;details&gt;</c>).</summary>
internal sealed class DocumentSection(string title)
{
    public string Title { get; } = title;

    public bool IsExpanded { get; set; }

    public DocumentLine? Header { get; set; }
}

/// <summary>
/// One display row of a <see cref="LineDocument"/>. Long logical lines are split into several rows so the
/// virtualized list never lays out a huge single text run.
/// </summary>
internal sealed class DocumentLine : ObservableObject
{
    private IReadOnlyList<RowHighlight> highlights = [];
    private int currentMatch = -1;
    private int version;

    public DocumentLine(string text, LineKind kind, IReadOnlyList<StyledSpan> spans, int logicalIndex, int offset, DocumentSection? section)
    {
        Text = text;
        Kind = kind;
        Spans = spans;
        LogicalIndex = logicalIndex;
        Offset = offset;
        Section = section;
    }

    public string Text { get; }

    public LineKind Kind { get; }

    public IReadOnlyList<StyledSpan> Spans { get; }

    public int LogicalIndex { get; }

    /// <summary>Offset of <see cref="Text"/> within its logical line (non-zero for continuation rows).</summary>
    public int Offset { get; }

    public bool IsContinuation => Offset > 0;

    /// <summary>The section this row belongs to (hidden while it is collapsed).</summary>
    public DocumentSection? Section { get; }

    /// <summary>For <see cref="LineKind.Toggle"/> rows: the section they expand or collapse.</summary>
    public DocumentSection? ToggleSection { get; init; }

    public bool IsToggle => ToggleSection is not null;

    public bool IsExpanded => ToggleSection?.IsExpanded ?? false;

    public IReadOnlyList<RowHighlight> Highlights => highlights;

    public int CurrentMatch => currentMatch;

    /// <summary>Bumped whenever highlights or toggle state change so the row re-renders.</summary>
    public int Version => version;

    internal void SetHighlights(IReadOnlyList<RowHighlight> value, int current)
    {
        highlights = value;
        currentMatch = current;
        Bump();
    }

    internal void SetCurrentMatch(int current)
    {
        currentMatch = current;
        Bump();
    }

    internal void Bump()
    {
        version++;
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(IsExpanded));
    }
}

/// <summary>Builds a <see cref="LineDocument"/> block by block.</summary>
internal sealed class LineDocumentBuilder
{
    /// <summary>Maximum characters per display row.</summary>
    public const int RowLength = 1000;

    private readonly List<DocumentLine> rows = [];
    private readonly List<string> logical = [];
    private readonly List<DocumentSection> sections = [];
    private DocumentSection? section;

    /// <summary>Adds <paramref name="text"/> (split at '\n'; one trailing newline is not a row) with optional block-relative spans.</summary>
    public LineDocumentBuilder Add(string text, LineKind kind = LineKind.Code, IReadOnlyList<StyledSpan>? spans = null)
    {
        var length = text.Length > 0 && text[^1] == '\n' ? text.Length - 1 : text.Length;
        var start = 0;
        var spanIndex = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start, length - start);
            var end = newline < 0 ? length : newline;
            AddLogical(text, start, end, kind, spans, ref spanIndex);
            if (newline < 0)
            {
                break;
            }
            start = newline + 1;
        }
        return this;
    }

    public LineDocumentBuilder BeginSection(string title, bool expanded = false)
    {
        var created = new DocumentSection(title) { IsExpanded = expanded };
        var header = new DocumentLine(title, LineKind.Toggle, [], logical.Count, 0, section) { ToggleSection = created };
        created.Header = header;
        logical.Add(title);
        rows.Add(header);
        sections.Add(created);
        section = created;
        return this;
    }

    public LineDocumentBuilder EndSection()
    {
        section = null;
        return this;
    }

    public LineDocument Build() => new(rows, logical, sections);

    private void AddLogical(string text, int start, int end, LineKind kind, IReadOnlyList<StyledSpan>? spans, ref int spanIndex)
    {
        var logicalIndex = logical.Count;
        logical.Add(text[start..end]);
        var offset = start;
        do
        {
            var stop = Math.Min(end, offset + RowLength);
            // Keep surrogate pairs on one row.
            if (stop < end && char.IsHighSurrogate(text[stop - 1]))
            {
                stop--;
            }
            rows.Add(new DocumentLine(text[offset..stop], kind, SliceSpans(spans, offset, stop, ref spanIndex), logicalIndex, offset - start, section));
            offset = stop;
        }
        while (offset < end);
    }

    private static IReadOnlyList<StyledSpan> SliceSpans(IReadOnlyList<StyledSpan>? spans, int start, int end, ref int index)
    {
        if (spans is null || spans.Count == 0)
        {
            return [];
        }
        while (index < spans.Count && spans[index].End <= start)
        {
            index++;
        }
        List<StyledSpan>? result = null;
        for (var scan = index; scan < spans.Count && spans[scan].Start < end; scan++)
        {
            var span = spans[scan];
            var from = Math.Max(start, span.Start);
            var to = Math.Min(end, span.End);
            if (to > from)
            {
                (result ??= []).Add(new StyledSpan(from - start, to - from, span.Style));
            }
        }
        return (IReadOnlyList<StyledSpan>?)result ?? [];
    }
}

/// <summary>
/// A searchable, virtualizable text document made of rows (headings, metadata, code text and collapsible
/// sections). Used by the Headers, Raw, Formatted Text, HexView, Auth and other text views.
/// </summary>
internal sealed class LineDocument : ISearchableView
{
    private readonly List<DocumentLine> rows;
    private readonly List<string> logical;
    private readonly List<DocumentSection> sections;
    private readonly List<List<DocumentLine>> rowsByLogical;
    private List<List<DocumentLine>> matchRows = [];
    private Dictionary<DocumentSection, bool>? snapshot;
    private int currentMatch = -1;

    public LineDocument(List<DocumentLine> rows, List<string> logical, List<DocumentSection> sections)
    {
        this.rows = rows;
        this.logical = logical;
        this.sections = sections;
        rowsByLogical = logical.Select(_ => new List<DocumentLine>(1)).ToList();
        foreach (var row in rows)
        {
            rowsByLogical[row.LogicalIndex].Add(row);
        }
        VisibleRows = new BulkObservableCollection<DocumentLine>(rows.Where(IsVisible));
    }

    public static LineDocument FromText(string text, LineKind kind = LineKind.Code, IReadOnlyList<StyledSpan>? spans = null) =>
        new LineDocumentBuilder().Add(text, kind, spans).Build();

    public BulkObservableCollection<DocumentLine> VisibleRows { get; }

    public IReadOnlyList<DocumentLine> AllRows => rows;

    public IReadOnlyList<DocumentSection> Sections => sections;

    public int LogicalLineCount => logical.Count;

    /// <summary>Raised to bring a row into view (current search match).</summary>
    public event EventHandler<DocumentLine>? ScrollRequested;

    public void Toggle(DocumentSection section)
    {
        SetExpanded(section, !section.IsExpanded);
        RefreshVisible();
    }

    /// <summary>The text of the given rows, rejoining continuation rows and separating logical lines with newlines.</summary>
    public static string JoinRows(IEnumerable<DocumentLine> selected)
    {
        var text = new System.Text.StringBuilder();
        DocumentLine? previous = null;
        foreach (var row in selected)
        {
            if (previous is not null && !(row.IsContinuation && row.LogicalIndex == previous.LogicalIndex))
            {
                text.Append('\n');
            }
            text.Append(row.Text);
            previous = row;
        }
        return text.ToString();
    }

    public SearchOutcome Search(string foldedQuery)
    {
        ClearSearch();
        var capped = false;
        var perRow = new Dictionary<DocumentLine, List<RowHighlight>>();
        matchRows = [];
        for (var index = 0; index < logical.Count && !capped; index++)
        {
            foreach (var start in SearchText.Matches(logical[index], foldedQuery))
            {
                if (matchRows.Count >= SearchText.MaxMatches)
                {
                    capped = true;
                    break;
                }
                var matchIndex = matchRows.Count;
                var touched = new List<DocumentLine>(1);
                var end = start + foldedQuery.Length;
                foreach (var row in rowsByLogical[index])
                {
                    var from = Math.Max(start, row.Offset);
                    var to = Math.Min(end, row.Offset + row.Text.Length);
                    if (to <= from)
                    {
                        continue;
                    }
                    if (!perRow.TryGetValue(row, out var list))
                    {
                        perRow[row] = list = [];
                    }
                    list.Add(new RowHighlight(from - row.Offset, to - from, matchIndex));
                    touched.Add(row);
                }
                matchRows.Add(touched);
            }
        }
        if (matchRows.Count == 0)
        {
            return SearchOutcome.None;
        }
        snapshot = sections.ToDictionary(section => section, section => section.IsExpanded);
        foreach (var (row, list) in perRow)
        {
            row.SetHighlights(list, -1);
            if (row.Section is { IsExpanded: false } collapsed)
            {
                SetExpanded(collapsed, true);
            }
        }
        RefreshVisible();
        return new SearchOutcome(matchRows.Count, capped);
    }

    public void ShowMatch(int index)
    {
        if (index < 0 || index >= matchRows.Count)
        {
            return;
        }
        if (currentMatch >= 0 && currentMatch < matchRows.Count)
        {
            foreach (var row in matchRows[currentMatch])
            {
                row.SetCurrentMatch(-1);
            }
        }
        currentMatch = index;
        foreach (var row in matchRows[index])
        {
            row.SetCurrentMatch(index);
        }
        if (matchRows[index].Count > 0)
        {
            ScrollRequested?.Invoke(this, matchRows[index][0]);
        }
    }

    public void ClearSearch()
    {
        foreach (var row in matchRows.SelectMany(list => list).Distinct())
        {
            row.SetHighlights([], -1);
        }
        matchRows = [];
        currentMatch = -1;
        if (snapshot is not null)
        {
            foreach (var (section, expanded) in snapshot)
            {
                SetExpanded(section, expanded);
            }
            snapshot = null;
            RefreshVisible();
        }
    }

    /// <summary>Number of rows currently carrying a highlight (for tests).</summary>
    internal int HighlightedRowCount => rows.Count(row => row.Highlights.Count > 0);

    private static void SetExpanded(DocumentSection section, bool expanded)
    {
        if (section.IsExpanded == expanded)
        {
            return;
        }
        section.IsExpanded = expanded;
        section.Header?.Bump();
    }

    private static bool IsVisible(DocumentLine row) => row.Section is null || row.Section.IsExpanded;

    private void RefreshVisible() => VisibleRows.ReplaceAll(rows.Where(IsVisible));
}
