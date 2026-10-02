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
internal sealed class SessionListViewModel : ObservableObject, IDisposable
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
    private readonly UiPreferences? preferences;
    private readonly PayloadSearchCache payloadCache;
    private readonly TimeSpan payloadDebounce;
    private string query = "";
    private SessionFilterOption filter = FilterOptions[0];
    private bool hideConnect;
    private SessionSortColumn sortColumn = SessionSortColumn.Index;
    private SessionColumnDefinition? sortDefinition;
    private ListSortDirection sortDirection = ListSortDirection.Ascending;
    private SessionRow? selectedRow;
    private bool searchPayloads;
    private bool isPayloadSearching;
    private string? payloadSearchStatus;
    private IReadOnlySet<SessionRow> payloadMatches = new HashSet<SessionRow>();
    private string? payloadResultQuery;
    private CancellationTokenSource? payloadSearchCancellation;

    public SessionListViewModel(
        IReadOnlyList<SessionRow> rows,
        UiPreferences? preferences = null,
        PayloadSearchCache? payloadCache = null,
        TimeSpan? payloadDebounce = null)
    {
        this.rows = rows;
        this.preferences = preferences;
        this.payloadCache = payloadCache ?? new PayloadSearchCache();
        this.payloadDebounce = payloadDebounce ?? TimeSpan.FromMilliseconds(250);
        searchPayloads = preferences?.SearchPayloads == true;
        GridColumns = SessionColumnCatalog.Resolve(preferences?.GridColumns ?? new UiPreferences(null).GridColumns);
        if (preferences is not null)
        {
            preferences.GridColumnsChanged += OnGridColumnsChanged;
        }
        VisibleRows = new BulkObservableCollection<SessionRow>(rows);
        CancelPayloadSearchCommand = new RelayCommand(CancelPayloadSearch, () => IsPayloadSearching);
    }

    public IReadOnlyList<SessionRow> AllRows => rows;

    public BulkObservableCollection<SessionRow> VisibleRows { get; }

    /// <summary>Raised after <see cref="VisibleRows"/> is recomputed.</summary>
    public event EventHandler? VisibleRowsChanged;

    public event EventHandler? GridColumnsChanged;

    public IReadOnlyList<SessionColumnDefinition> GridColumns { get; private set; }

    public RelayCommand CancelPayloadSearchCommand { get; }

    public string Query
    {
        get => query;
        set
        {
            if (SetProperty(ref query, value ?? ""))
            {
                Refresh();
                RestartPayloadSearch();
            }
        }
    }

    public bool SearchPayloads
    {
        get => searchPayloads;
        set
        {
            if (!SetProperty(ref searchPayloads, value))
            {
                return;
            }
            if (preferences is not null)
            {
                preferences.SearchPayloads = value;
            }
            RestartPayloadSearch();
        }
    }

    public bool IsPayloadSearching
    {
        get => isPayloadSearching;
        private set
        {
            if (SetProperty(ref isPayloadSearching, value))
            {
                CancelPayloadSearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? PayloadSearchStatus
    {
        get => payloadSearchStatus;
        private set => SetProperty(ref payloadSearchStatus, value);
    }

    internal Task PendingPayloadSearch { get; private set; } = Task.CompletedTask;

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
        sortDefinition = column == SessionSortColumn.Index
            ? null
            : SessionColumnCatalog.BuiltIns.FirstOrDefault(candidate => candidate.Id == column switch
            {
                SessionSortColumn.Time => "time",
                SessionSortColumn.Id => "id",
                SessionSortColumn.Result => "result",
                SessionSortColumn.Method => "method",
                SessionSortColumn.Url => "url",
                SessionSortColumn.Elapsed => "elapsed",
                SessionSortColumn.RequestSize => "request-size",
                SessionSortColumn.ResponseSize => "response-size",
                _ => ""
            });
        sortDirection = direction;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        Refresh();
    }

    public void Sort(SessionColumnDefinition? column, ListSortDirection direction)
    {
        sortDefinition = column;
        sortColumn = column is null ? SessionSortColumn.Index : SortColumnFor(column.Id);
        sortDirection = direction;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        Refresh();
    }

    public bool Matches(SessionRow row)
    {
        // The report lower-cases but does not trim the query.
        var folded = query.ToLowerInvariant();
        if (folded.Length > 0
            && !row.SearchText.Contains(folded, StringComparison.Ordinal)
            && !GridColumns.Where(column => column.Setting.Visible)
                .Any(column => row.ColumnValue(column).Display.Contains(folded, StringComparison.OrdinalIgnoreCase))
            && !(SearchPayloads
                 && payloadResultQuery == folded
                 && payloadMatches.Contains(row)))
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
        if (sortDefinition is not null)
        {
            visible = visible.Order(ColumnRowComparer.For(sortDefinition, sortDirection == ListSortDirection.Descending));
        }
        VisibleRows.ReplaceAll(visible.ToList());
        OnPropertyChanged(nameof(CountText));
        VisibleRowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestartPayloadSearch()
    {
        payloadSearchCancellation?.Cancel();
        payloadSearchCancellation?.Dispose();
        payloadSearchCancellation = null;
        payloadMatches = new HashSet<SessionRow>();
        payloadResultQuery = null;
        IsPayloadSearching = false;

        var folded = query.ToLowerInvariant();
        if (!SearchPayloads || folded.Length == 0)
        {
            PayloadSearchStatus = null;
            Refresh();
            PendingPayloadSearch = Task.CompletedTask;
            return;
        }

        var cancellation = new CancellationTokenSource();
        payloadSearchCancellation = cancellation;
        IsPayloadSearching = true;
        PayloadSearchStatus = "Waiting to search payloads\u2026";
        PendingPayloadSearch = RunPayloadSearchAsync(folded, cancellation);
    }

    private async Task RunPayloadSearchAsync(string foldedQuery, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(payloadDebounce, cancellation.Token);
            var progress = new Progress<PayloadSearchProgress>(value =>
            {
                if (ReferenceEquals(payloadSearchCancellation, cancellation))
                {
                    PayloadSearchStatus = $"Searching payloads\u2026 {value.Completed:N0}/{value.Total:N0}";
                }
            });
            var result = await payloadCache.SearchAsync(rows, foldedQuery, progress, cancellation.Token);
            if (!ReferenceEquals(payloadSearchCancellation, cancellation))
            {
                return;
            }
            payloadMatches = result.Matches;
            payloadResultQuery = foldedQuery;
            payloadSearchCancellation = null;
            cancellation.Dispose();
            IsPayloadSearching = false;
            Refresh();
            var truncated = result.TruncatedSessions > 0
                ? $"; {result.TruncatedSessions:N0} sessions reached a scan limit"
                : "";
            var failed = result.FailedSessions > 0
                ? $"; {result.FailedSessions:N0} sessions could not be decoded"
                : "";
            PayloadSearchStatus = $"{VisibleRows.Count:N0} matching sessions{truncated}{failed}.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(payloadSearchCancellation, cancellation))
            {
                IsPayloadSearching = false;
            }
        }
    }

    private void CancelPayloadSearch()
    {
        if (payloadSearchCancellation is null)
        {
            return;
        }
        payloadSearchCancellation.Cancel();
        payloadSearchCancellation.Dispose();
        payloadSearchCancellation = null;
        payloadMatches = new HashSet<SessionRow>();
        payloadResultQuery = null;
        IsPayloadSearching = false;
        PayloadSearchStatus = "Payload search canceled.";
        Refresh();
    }

    public void Dispose()
    {
        if (preferences is not null)
        {
            preferences.GridColumnsChanged -= OnGridColumnsChanged;
        }
        payloadSearchCancellation?.Cancel();
        payloadSearchCancellation?.Dispose();
        payloadSearchCancellation = null;
        IsPayloadSearching = false;
    }

    private void OnGridColumnsChanged(object? sender, EventArgs e)
    {
        GridColumns = SessionColumnCatalog.Resolve(preferences?.GridColumns ?? []);
        sortDefinition = sortDefinition is null
            ? null
            : GridColumns.FirstOrDefault(column => column.Id == sortDefinition.Id && column.Setting.Visible);
        if (sortDefinition is null)
        {
            sortColumn = SessionSortColumn.Index;
            sortDirection = ListSortDirection.Ascending;
        }
        OnPropertyChanged(nameof(GridColumns));
        Refresh();
        GridColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static SessionSortColumn SortColumnFor(string id) => id switch
    {
        "time" => SessionSortColumn.Time,
        "id" => SessionSortColumn.Id,
        "result" => SessionSortColumn.Result,
        "method" => SessionSortColumn.Method,
        "url" => SessionSortColumn.Url,
        "elapsed" => SessionSortColumn.Elapsed,
        "request-size" => SessionSortColumn.RequestSize,
        "response-size" => SessionSortColumn.ResponseSize,
        _ => SessionSortColumn.Index
    };

    /// <summary>Typed comparison with blanks last and chronological order as the stable tie-breaker.</summary>
    private sealed class ColumnRowComparer(SessionColumnDefinition column, bool descending) : IComparer<SessionRow>
    {
        public static ColumnRowComparer For(SessionColumnDefinition column, bool descending) => new(column, descending);

        public int Compare(SessionRow? x, SessionRow? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : 1) : -1;
            }
            var left = x.ColumnValue(column);
            var right = y.ColumnValue(column);
            var result = SessionColumnCatalog.Compare(left, right);
            if (result != 0)
            {
                return descending && !left.IsBlank && !right.IsBlank ? -result : result;
            }
            return x.Index.CompareTo(y.Index);
        }
    }
}
