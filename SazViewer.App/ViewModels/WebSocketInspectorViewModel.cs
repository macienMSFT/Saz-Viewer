using System.Globalization;
using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The WebSocket session inspector (the report's <c>renderWebSocketInspector</c>): the chronological logical
/// message list with a payload filter, and the selected message detail. Message detail is built on selection.
/// </summary>
internal sealed class WebSocketInspectorViewModel : ObservableObject
{
    public const int MaxMessages = HtmlReportGenerator.MaxWebSocketMessagesPerSession;
    public const string NoMatchesText = "No WebSocket messages match this payload search.";
    public const string NoMessagesText = "No WebSocket logical messages are available for this session.";

    /// <summary>Traffic pane share of the width, remembered for the app session like the report's split ratio.</summary>
    public static double SplitRatio { get; set; } = .38;

    private readonly IClipboardService clipboard;
    private readonly BodyFormatter formatter = new();
    private IReadOnlyList<WebSocketMessageItem> visibleItems;
    private WebSocketMessageItem? selectedItem;
    private WebSocketMessageDetailViewModel? detail;
    private string query = "";
    private string searchStatus = "";

    public WebSocketInspectorViewModel(IReadOnlyList<WebSocketMessage> messages, IClipboardService clipboard)
    {
        this.clipboard = clipboard;
        Items = messages.OrderBy(message => message.RecordIndex).Take(MaxMessages)
            .Select((message, index) => new WebSocketMessageItem(message, index)).ToList();
        var omitted = messages.Count - Items.Count;
        OmittedWarning = omitted > 0
            ? $"{omitted.ToString(CultureInfo.InvariantCulture)} additional message(s) were omitted by the {MaxMessages.ToString("N0", CultureInfo.InvariantCulture)}-message safety limit."
            : null;
        SearchHelp = Items.Any(item => item.Message.IsPayloadTruncated && item.SearchKey is not null)
            ? "Searches retained decoded text and JSON payload content only. Binary payloads and bytes omitted by safety truncation are not searched."
            : "Searches retained decoded text and JSON payload content only. Binary payloads are not searched.";
        visibleItems = Items;
        SelectNextCommand = new RelayCommand(() => SelectRelative(1), () => visibleItems.Count > 0);
        SelectPreviousCommand = new RelayCommand(() => SelectRelative(-1), () => visibleItems.Count > 0);
        Apply();
    }

    public IReadOnlyList<WebSocketMessageItem> Items { get; }

    public IReadOnlyList<WebSocketMessageItem> VisibleItems
    {
        get => visibleItems;
        private set => SetProperty(ref visibleItems, value);
    }

    public string? OmittedWarning { get; }

    public string SearchHelp { get; }

    public RelayCommand SelectNextCommand { get; }

    public RelayCommand SelectPreviousCommand { get; }

    /// <summary>Payload filter (case-insensitive literal over decoded text messages).</summary>
    public string Query
    {
        get => query;
        set
        {
            if (SetProperty(ref query, value ?? ""))
            {
                Apply();
            }
        }
    }

    public string SearchStatus
    {
        get => searchStatus;
        private set => SetProperty(ref searchStatus, value);
    }

    /// <summary>Shown in place of the list rows / detail when nothing is available.</summary>
    public string? EmptyText => Items.Count == 0 ? NoMessagesText : visibleItems.Count == 0 ? NoMatchesText : null;

    public WebSocketMessageItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            // The list clears its selection while its items are replaced; keep the model's selection.
            if (value is null || ReferenceEquals(value, selectedItem) || !visibleItems.Contains(value))
            {
                return;
            }
            Select(value);
        }
    }

    public WebSocketMessageDetailViewModel? Detail
    {
        get => detail;
        private set => SetProperty(ref detail, value);
    }

    /// <summary>Selects the next / previous visible message, wrapping (Enter / Shift+Enter in the filter box).</summary>
    public void SelectRelative(int delta)
    {
        if (visibleItems.Count == 0)
        {
            return;
        }
        var position = selectedItem is null ? -1 : IndexOf(selectedItem);
        var next = position < 0
            ? (delta > 0 ? 0 : visibleItems.Count - 1)
            : (position + delta + visibleItems.Count) % visibleItems.Count;
        Select(visibleItems[next]);
    }

    public void Deactivate() => detail?.Deactivate();

    private void Apply()
    {
        var folded = query.Trim();
        VisibleItems = folded.Length == 0 ? Items : Items.Where(item => item.Matches(folded)).ToList();
        SearchStatus = $"{visibleItems.Count.ToString(CultureInfo.InvariantCulture)} of {Items.Count.ToString(CultureInfo.InvariantCulture)} messages";
        OnPropertyChanged(nameof(EmptyText));
        SelectNextCommand.RaiseCanExecuteChanged();
        SelectPreviousCommand.RaiseCanExecuteChanged();
        if (visibleItems.Count == 0)
        {
            detail?.Deactivate();
            selectedItem = null;
            Detail = null;
            OnPropertyChanged(nameof(SelectedItem));
        }
        else if (selectedItem is null || IndexOf(selectedItem) < 0)
        {
            Select(visibleItems[0]);
        }
        else
        {
            OnPropertyChanged(nameof(SelectedItem));
        }
    }

    private int IndexOf(WebSocketMessageItem item)
    {
        for (var index = 0; index < visibleItems.Count; index++)
        {
            if (ReferenceEquals(visibleItems[index], item))
            {
                return index;
            }
        }
        return -1;
    }

    private void Select(WebSocketMessageItem item)
    {
        detail?.Deactivate();
        selectedItem = item;
        Detail = new WebSocketMessageDetailViewModel(item, formatter, clipboard);
        OnPropertyChanged(nameof(SelectedItem));
    }
}
