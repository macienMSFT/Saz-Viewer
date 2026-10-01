using System.ComponentModel;
using SazViewer.App.Model;
using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

/// <summary>A grid filter choice (the report's filter dropdown).</summary>
internal sealed record SessionFilterOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Sortable grid columns.</summary>
internal enum SessionSortColumn
{
    Index,
    Time,
    Id,
    Result,
    Method,
    Url,
    Elapsed,
    RequestSize,
    ResponseSize
}

/// <summary>
/// The session grid: chronological rows filtered by the search text, the result/protocol filter and the
/// Hide CONNECT option, then sorted. Matches the report's <c>bindFilter</c> semantics.
/// </summary>
internal sealed class SessionListViewModel : ObservableObject
{
    public static readonly IReadOnlyList<SessionFilterOption> FilterOptions =
    [
        new("", "All sessions"),
        new("websocket", "WebSocket only"),
        new("mapi", "MAPI/NSPI only"),
        new("2", "2xx"),
        new("3", "3xx"),
        new("4", "4xx"),
        new("5", "5xx"),
        new("0", "Missing/other")
    ];

    private readonly IReadOnlyList<SessionRow> rows;
    private string query = "";
    private SessionFilterOption filter = FilterOptions[0];
    private bool hideConnect;
    private SessionSortColumn sortColumn = SessionSortColumn.Index;
    private ListSortDirection sortDirection = ListSortDirection.Ascending;
    private SessionRow? selectedRow;

    public SessionListViewModel(IReadOnlyList<SessionRow> rows)
    {
        this.rows = rows;
        VisibleRows = new BulkObservableCollection<SessionRow>(rows);
    }

    public IReadOnlyList<SessionRow> AllRows => rows;

    public BulkObservableCollection<SessionRow> VisibleRows { get; }

    /// <summary>Raised after <see cref="VisibleRows"/> is recomputed.</summary>
    public event EventHandler? VisibleRowsChanged;

    public string Query
    {
        get => query;
        set
        {
            if (SetProperty(ref query, value ?? ""))
            {
                Refresh();
            }
        }
    }

    public SessionFilterOption Filter
    {
        get => filter;
        set
        {
            if (SetProperty(ref filter, value ?? FilterOptions[0]))
            {
                Refresh();
            }
        }
    }

    public bool HideConnect
    {
        get => hideConnect;
        set
        {
            if (SetProperty(ref hideConnect, value))
            {
                Refresh();
            }
        }
    }

    public SessionSortColumn SortColumn => sortColumn;

    public ListSortDirection SortDirection => sortDirection;

    public SessionRow? SelectedRow
    {
        get => selectedRow;
        set => SetProperty(ref selectedRow, value);
    }

    public string CountText => VisibleRows.Count == rows.Count
        ? $"{rows.Count:N0} sessions"
        : $"{VisibleRows.Count:N0} of {rows.Count:N0} sessions";

    /// <summary>Applies a sort; <see cref="SessionSortColumn.Index"/> restores chronological order.</summary>
    public void Sort(SessionSortColumn column, ListSortDirection direction)
    {
        sortColumn = column;
        sortDirection = direction;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        Refresh();
    }

    public bool Matches(SessionRow row)
    {
        // The report lower-cases but does not trim the query.
        var folded = query.ToLowerInvariant();
        if (folded.Length > 0 && !row.SearchText.Contains(folded, StringComparison.Ordinal))
        {
            return false;
        }
        var matchesFilter = filter.Key switch
        {
            "" => true,
            "websocket" => row.IsWebSocket,
            "mapi" => row.IsMapi,
            var key => row.FilterKey == key
        };
        return matchesFilter && !(hideConnect && row.IsConnect);
    }

    /// <summary>The visible row <paramref name="delta"/> positions from <paramref name="row"/>, or null.</summary>
    public SessionRow? Neighbor(SessionRow? row, int delta)
    {
        var index = row is null ? -1 : VisibleRows.IndexOf(row);
        if (index < 0)
        {
            return null;
        }
        var target = index + delta;
        return target >= 0 && target < VisibleRows.Count ? VisibleRows[target] : null;
    }

    private void Refresh()
    {
        IEnumerable<SessionRow> visible = rows.Where(Matches);
        if (sortColumn != SessionSortColumn.Index || sortDirection != ListSortDirection.Ascending)
        {
            visible = visible.Order(RowComparer.For(sortColumn, sortDirection == ListSortDirection.Descending));
        }
        VisibleRows.ReplaceAll(visible.ToList());
        OnPropertyChanged(nameof(CountText));
        VisibleRowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Typed comparisons with chronological order as the tie-breaker in both directions; missing values sort first.</summary>
    private sealed class RowComparer(SessionSortColumn column, bool descending) : IComparer<SessionRow>
    {
        public static RowComparer For(SessionSortColumn column, bool descending) => new(column, descending);

        public int Compare(SessionRow? x, SessionRow? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }
            var result = column switch
            {
                SessionSortColumn.Time => Nullable.Compare(x.Session.Timestamp, y.Session.Timestamp),
                SessionSortColumn.Id => CompareIds(x.Id, y.Id),
                SessionSortColumn.Result => Nullable.Compare(x.ResultCode, y.ResultCode),
                SessionSortColumn.Method => string.Compare(x.Method, y.Method, StringComparison.OrdinalIgnoreCase),
                SessionSortColumn.Url => string.Compare(x.Url, y.Url, StringComparison.OrdinalIgnoreCase),
                SessionSortColumn.Elapsed => Nullable.Compare(x.Session.ElapsedMilliseconds, y.Session.ElapsedMilliseconds),
                SessionSortColumn.RequestSize => x.Session.RequestBytes.CompareTo(y.Session.RequestBytes),
                SessionSortColumn.ResponseSize => x.Session.ResponseBytes.CompareTo(y.Session.ResponseBytes),
                _ => 0
            };
            return result != 0 ? (descending ? -result : result) : x.Index.CompareTo(y.Index);
        }

        private static int CompareIds(string x, string y) =>
            long.TryParse(x, out var left) && long.TryParse(y, out var right)
                ? left.CompareTo(right)
                : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
