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
/// Response panes, the single/split layout and the active-view searches. Per-session content is built only when
/// a row loads.
/// </summary>
internal sealed class InspectorViewModel : ObservableObject
{
    public const string RequestSide = "request";
    public const string ResponseSide = "response";

    private readonly SessionListViewModel list;
    private readonly IClipboardService clipboard;
    private readonly bool followsGrid;
    private readonly UiPreferences preferences;
    private readonly BodyFormatter formatter = new();
    private SessionRow? row;
    private MessagePaneViewModel? request;
    private MessagePaneViewModel? response;
    private SessionDetailsViewModel? details;
    private WebSocketInspectorViewModel? webSocket;
    private string selectedSide = RequestSide;
    private string position = "";
    private bool isOpen;
    private LayoutWidthClass widthClass = LayoutWidthClass.Wide;
    private InspectorLayout layout;
    private bool hasTemporaryLayoutOverride;

    /// <param name="followsGrid">
    /// True for the inspector docked under the grid (loading a row selects it in the grid); false for a pop-out
    /// window, which navigates the same visible rows without moving the grid selection.
    /// </param>
    public InspectorViewModel(SessionListViewModel list, IClipboardService clipboard, bool followsGrid = true, UiPreferences? preferences = null)
    {
        this.list = list;
        this.clipboard = clipboard;
        this.followsGrid = followsGrid;
        this.preferences = preferences ?? UiPreferences.Current;
        layout = this.preferences.GetDefaultLayout(widthClass);
        PreviousCommand = new RelayCommand(() => Navigate(-1), () => list.Neighbor(row, -1) is not null);
        NextCommand = new RelayCommand(() => Navigate(1), () => list.Neighbor(row, 1) is not null);
        CloseCommand = new RelayCommand(Close);
        ToggleLayoutCommand = new RelayCommand(ToggleLayout);
        PopOutCommand = new RelayCommand(PopOut, () => followsGrid && row is not null);
        Search = new ActiveSearchViewModel(
            () => ActivePane?.SelectedTab?.Content?.SearchTarget,
            "Search active Request/Response view...",
            "Search active Request or Response view");
        RequestSearch = new ActiveSearchViewModel(
            () => request?.SelectedTab?.Content?.SearchTarget,
            "Search active Request view...",
            "Search active Request view");
        ResponseSearch = new ActiveSearchViewModel(
            () => response?.SelectedTab?.Content?.SearchTarget,
            "Search active Response view...",
            "Search active Response view");
        list.VisibleRowsChanged += (_, _) => UpdateNavigation();
    }

    public RelayCommand PreviousCommand { get; }

    public RelayCommand NextCommand { get; }

    public RelayCommand CloseCommand { get; }

    /// <summary>Switches between single and split view (remembered per width class).</summary>
    public RelayCommand ToggleLayoutCommand { get; }

    /// <summary>Opens the current session in its own inspector window (docked inspector only).</summary>
    public RelayCommand PopOutCommand { get; }

    /// <summary>Raised with the current row when the user pops the inspector out into a window.</summary>
    public event EventHandler<SessionRow>? PopOutRequested;

    public bool CanPopOut => followsGrid;

    internal UiPreferences Preferences => preferences;

    /// <summary>Active-view search in single view (follows the selected Request/Response side).</summary>
    public ActiveSearchViewModel Search { get; }

    /// <summary>Split view: the Request pane's own active-view search.</summary>
    public ActiveSearchViewModel RequestSearch { get; }

    /// <summary>Split view: the Response pane's own active-view search.</summary>
    public ActiveSearchViewModel ResponseSearch { get; }

    /// <summary>The inspector's width class; each class remembers its own layout (split by default when wide).</summary>
    public LayoutWidthClass WidthClass
    {
        get => widthClass;
        set
        {
            if (SetProperty(ref widthClass, value))
            {
                OnPropertyChanged(nameof(IsNarrow));
                if (!hasTemporaryLayoutOverride)
                {
                    ApplyLayout(preferences.GetDefaultLayout(value));
                }
            }
        }
    }

    public bool IsNarrow => widthClass == LayoutWidthClass.Narrow;

    public InspectorLayout Layout => layout;

    /// <summary>True when an HTTP session shows Request and Response together (stacked when narrow).</summary>
    public bool IsSplit => layout == InspectorLayout.Split && IsHttp;

    /// <summary>True when an HTTP session shows one side, chosen by the Request/Response tabs.</summary>
    public bool IsSingle => layout == InspectorLayout.Single && IsHttp;

    /// <summary>The layout toggle's label: the layout it switches to.</summary>
    public string LayoutToggleLabel => layout == InspectorLayout.Split ? "Single view" : "Split view";

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

    /// <summary>"request" or "response" (the side shown in single view; kept while split).</summary>
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
                if (!IsSplit)
                {
                    previous?.Deactivate();
                }
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
        ResetSearches();
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
        if (followsGrid && !ReferenceEquals(list.SelectedRow, target))
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
        ResetSearches();
        DetachPanes();
        row = null;
        details = null;
        IsOpen = false;
        OnPropertyChanged(string.Empty);
        UpdateNavigation();
    }

    private void ToggleLayout()
    {
        var next = layout == InspectorLayout.Split ? InspectorLayout.Single : InspectorLayout.Split;
        if (preferences.DefaultInspectorLayout == InspectorLayoutMode.Automatic)
        {
            preferences.SetLayout(widthClass, next);
        }
        else
        {
            hasTemporaryLayoutOverride = true;
        }
        ApplyLayout(next);
    }

    /// <summary>Re-applies the app default, clearing a temporary override made by this inspector's toggle.</summary>
    public void ApplyDefaultLayoutPreference()
    {
        hasTemporaryLayoutOverride = false;
        ApplyLayout(preferences.GetDefaultLayout(widthClass));
    }

    private void ApplyLayout(InspectorLayout next)
    {
        if (layout == next)
        {
            return;
        }
        ResetSearches();
        layout = next;
        if (next == InspectorLayout.Single)
        {
            // Single view restores the side selected before splitting; the now-hidden side is reset.
            (selectedSide == ResponseSide ? request : response)?.Deactivate();
        }
        OnPropertyChanged(nameof(Layout));
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(LayoutToggleLabel));
    }

    private void PopOut()
    {
        if (row is { } current)
        {
            PopOutRequested?.Invoke(this, current);
        }
    }

    private void ResetSearches()
    {
        Search.Reset();
        RequestSearch.Reset();
        ResponseSearch.Reset();
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

    private void OnPaneViewChanged(object? sender, EventArgs e)
    {
        Search.Reset();
        (ReferenceEquals(sender, request) ? RequestSearch : ResponseSearch).Reset();
    }

    private void UpdateNavigation()
    {
        var index = row is null ? -1 : list.VisibleRows.IndexOf(row);
        Position = index < 0 ? "" : $"{index + 1} of {list.VisibleRows.Count}";
        PreviousCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        PopOutCommand.RaiseCanExecuteChanged();
    }
}