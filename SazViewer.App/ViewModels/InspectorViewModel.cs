using SazViewer.App.Model;
using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>The collapsible "Session details" block (endpoints, timers, warnings).</summary>
internal sealed record SessionDetailsViewModel(string? Endpoints, string? Timers, IReadOnlyList<string> Warnings)
{
    public static SessionDetailsViewModel? Create(HttpSession session)
    {
        // Decoding a deferred body records that body's warnings on the session, so decode before reading them.
        _ = session.Request?.Body;
        _ = session.Response?.Body;
        var hasEndpoints = session.ClientEndpoint is not null || session.ServerEndpoint is not null;
        if (!hasEndpoints && session.Timers.Count == 0 && session.Warnings.Count == 0)
        {
            return null;
        }
        return new SessionDetailsViewModel(
            hasEndpoints ? $"{session.ClientEndpoint ?? "?"} -> {session.ServerEndpoint ?? "?"}" : null,
            session.Timers.Count > 0 ? string.Concat(session.Timers.Select(timer => $"{timer.Key}: {timer.Value}\n")).TrimEnd('\n') : null,
            session.Warnings.ToList());
    }

    public bool HasEndpoints => Endpoints is not null;

    public bool HasTimers => Timers is not null;
}

/// <summary>
/// The session inspector: the selected session, Previous/Next over the visible grid rows, the Request and
/// Response panes and the single-view active search. Per-session content is built only when a row loads.
/// </summary>
internal sealed class InspectorViewModel : ObservableObject
{
    public const string RequestSide = "request";
    public const string ResponseSide = "response";

    private readonly SessionListViewModel list;
    private readonly IClipboardService clipboard;
    private readonly BodyFormatter formatter = new();
    private SessionRow? row;
    private MessagePaneViewModel? request;
    private MessagePaneViewModel? response;
    private SessionDetailsViewModel? details;
    private WebSocketInspectorViewModel? webSocket;
    private string selectedSide = RequestSide;
    private string position = "";
    private bool isOpen;

    public InspectorViewModel(SessionListViewModel list, IClipboardService clipboard)
    {
        this.list = list;
        this.clipboard = clipboard;
        PreviousCommand = new RelayCommand(() => Navigate(-1), () => list.Neighbor(row, -1) is not null);
        NextCommand = new RelayCommand(() => Navigate(1), () => list.Neighbor(row, 1) is not null);
        CloseCommand = new RelayCommand(Close);
        Search = new ActiveSearchViewModel(
            () => ActivePane?.SelectedTab?.Content?.SearchTarget,
            "Search active Request/Response view...",
            "Search active Request or Response view");
        list.VisibleRowsChanged += (_, _) => UpdateNavigation();
    }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Active-view search in single view (follows the selected Request/Response side).</summary>
    public ActiveSearchViewModel Search { get; }

    public SessionRow? Row => row;

    public bool IsOpen
    {
        get => isOpen;
        private set => SetProperty(ref isOpen, value);
    }

    public string Title => row?.Summary ?? "";

    public string Position
    {
        get => position;
        private set => SetProperty(ref position, value);
    }

    public bool IsWebSocket => row?.IsWebSocket == true;

    public bool IsHttp => row is not null && !row.IsWebSocket;

    public SessionDetailsViewModel? Details => details;

    /// <summary>The WebSocket inspector for sessions that carry WebSocket messages.</summary>
    public WebSocketInspectorViewModel? WebSocket => webSocket;

    public MessagePaneViewModel? Request => request;

    public MessagePaneViewModel? Response => response;

    /// <summary>"request" or "response" (the primary tab in single view).</summary>
    public string SelectedSide
    {
        get => selectedSide;
        set
        {
            var side = value == ResponseSide ? ResponseSide : RequestSide;
            var previous = ActivePane;
            if (SetProperty(ref selectedSide, side))
            {
                // Leaving a side resets its view state (e.g. revealed auth values, live WebView previews).
                previous?.Deactivate();
                Search.Reset();
                OnPropertyChanged(nameof(SelectedSideIndex));
                OnPropertyChanged(nameof(ActivePane));
            }
        }
    }

    public int SelectedSideIndex
    {
        get => selectedSide == ResponseSide ? 1 : 0;
        set => SelectedSide = value == 1 ? ResponseSide : RequestSide;
    }

    public MessagePaneViewModel? ActivePane => selectedSide == ResponseSide ? response : request;

    /// <summary>Milliseconds spent building the last session's view models (diagnostics).</summary>
    public double LastLoadMilliseconds { get; private set; }

    /// <summary>Raised after a row is loaded (the view moves focus / scrolls the grid).</summary>
    public event EventHandler? Loaded;

    public void Load(SessionRow? target)
    {
        if (target is null)
        {
            return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Search.Reset();
        DetachPanes();
        row = target;
        var session = target.Session;
        details = SessionDetailsViewModel.Create(session);
        if (target.IsWebSocket)
        {
            webSocket = new WebSocketInspectorViewModel(target.WebSocketMessages, clipboard);
        }
        else
        {
            request = new MessagePaneViewModel("Request", MessageContent.Create(session.Request, formatter), session.Mapi?.Request, clipboard);
            response = new MessagePaneViewModel("Response", MessageContent.Create(session.Response, formatter), session.Mapi?.Response, clipboard);
            request.ViewChanged += OnPaneViewChanged;
            response.ViewChanged += OnPaneViewChanged;
        }
        selectedSide = RequestSide;
        IsOpen = true;
        if (!ReferenceEquals(list.SelectedRow, target))
        {
            list.SelectedRow = target;
        }
        OnPropertyChanged(string.Empty);
        UpdateNavigation();
        LastLoadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    public void Navigate(int delta)
    {
        if (list.Neighbor(row, delta) is { } target)
        {
            Load(target);
        }
    }

    public void Close()
    {
        Search.Reset();
        DetachPanes();
        row = null;
        details = null;
        IsOpen = false;
        OnPropertyChanged(string.Empty);
        UpdateNavigation();
    }

    private void DetachPanes()
    {
        foreach (var pane in new[] { request, response })
        {
            if (pane is not null)
            {
                pane.ViewChanged -= OnPaneViewChanged;
                pane.Deactivate();
            }
        }
        request = null;
        response = null;
        webSocket?.Deactivate();
        webSocket = null;
    }

    private void OnPaneViewChanged(object? sender, EventArgs e) => Search.Reset();

    private void UpdateNavigation()
    {
        var index = row is null ? -1 : list.VisibleRows.IndexOf(row);
        Position = index < 0 ? "" : $"{index + 1} of {list.VisibleRows.Count}";
        PreviousCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
    }
}
