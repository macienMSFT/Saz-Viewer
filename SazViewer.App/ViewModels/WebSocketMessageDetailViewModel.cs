using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The selected WebSocket message (the report's <c>renderWebSocketMessageDetail</c>): heading, summary, warning,
/// JSON / Text / Raw tabs with copy toolbars and the payload active-view search. Exposes the same tab surface as
/// <see cref="MessagePaneViewModel"/> so the shared tab strip view renders it.
/// </summary>
internal sealed class WebSocketMessageDetailViewModel : ObservableObject, ITabbedPane
{
    private MessageTabViewModel? selectedTab;

    public WebSocketMessageDetailViewModel(WebSocketMessageItem item, BodyFormatter formatter, IClipboardService clipboard)
    {
        Item = item;
        var message = item.Message;
        Heading = $"Selected {message.Type} message";
        Summary = string.Join(" \u2022 ",
            HtmlReportGenerator.FormatWebSocketTimestamp(message.Timestamp),
            item.DirectionLabel,
            $"{message.PayloadLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes",
            $"{message.Frames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} frame{(message.Frames.Count == 1 ? "" : "s")}");
        Warning = string.IsNullOrEmpty(message.Warning) ? null : message.Warning;
        var json = JsonText(message, formatter);
        var text = message.Text;
        Tabs =
        [
            new MessageTabViewModel("json", "JSON", json is not null, "JSON is unavailable for this message.",
                "Copy WebSocket JSON formatted text", "WebSocket JSON formatted text",
                json is null ? null : () => Observe(new StructuredBodyViewModel(BodyFormat.Json, null, "", () => json)), clipboard),
            new MessageTabViewModel("text", "Text", text is not null, "Decoded UTF-8 text is unavailable for this message.",
                "Copy WebSocket text message", "WebSocket text message",
                text is null ? null : () => new TextDocumentViewModel(LineDocument.FromText(text), () => CopyResult.Bounded(text)), clipboard),
            new MessageTabViewModel("raw", "Raw", true, "",
                "Copy WebSocket raw representation", "WebSocket raw representation",
                () =>
                {
                    var raw = HtmlReportGenerator.BuildWebSocketRaw(message);
                    return new TextDocumentViewModel(LineDocument.FromText(raw), () => CopyResult.Of(raw));
                }, clipboard)
        ];
        Search = new ActiveSearchViewModel(
            () => SelectedTab?.Content?.SearchTarget,
            "Search selected payload view...",
            "Search selected WebSocket payload view");
        // The report's tab priority for messages: JSON, then Text, then Raw.
        selectedTab = Tabs.First(tab => tab.IsEnabled);
    }

    public WebSocketMessageItem Item { get; }

    public string Heading { get; }

    public string Summary { get; }

    public string? Warning { get; }

    public IReadOnlyList<MessageTabViewModel> Tabs { get; }

    public bool HasAnyTab => true;

    public string EmptyText => "";

    public string TabListName => "WebSocket message views";

    /// <summary>Active-view search over the selected payload view; re-runs when the view changes.</summary>
    public ActiveSearchViewModel Search { get; }

    public MessageTabViewModel? SelectedTab
    {
        get => selectedTab;
        set
        {
            if (value is null || !value.IsEnabled || ReferenceEquals(value, selectedTab))
            {
                return;
            }
            selectedTab?.Deactivate();
            selectedTab = value;
            OnPropertyChanged();
            Search.Run();
        }
    }

    public MessageTabViewModel Tab(string key) => Tabs.First(tab => tab.Key == key);

    public bool Select(string key)
    {
        var tab = Tabs.FirstOrDefault(candidate => candidate.Key == key && candidate.IsEnabled);
        if (tab is null)
        {
            return false;
        }
        SelectedTab = tab;
        return true;
    }

    public void Deactivate()
    {
        Search.Reset();
        selectedTab?.Deactivate();
    }

    /// <summary>
    /// The report's JSON detection for a message: decoded text the body formatter recognizes as complete JSON
    /// with a buildable value tree.
    /// </summary>
    public static string? JsonText(WebSocketMessage message, BodyFormatter formatter)
    {
        if (message.Text is null)
        {
            return null;
        }
        var formatted = formatter.Format(new BodyPreview
        {
            Length = message.PayloadLength,
            CapturedLength = message.Payload.Length,
            Preview = message.Text,
            IsTruncated = message.IsPayloadTruncated
        }, null);
        return formatted.Format == BodyFormat.Json && !formatted.IsTruncated && HtmlReportGenerator.BuildJsonTree(formatted.Formatted) is not null
            ? formatted.Formatted
            : null;
    }

    private TabContentViewModel Observe(TabContentViewModel created)
    {
        created.SearchTargetChanged += (sender, _) =>
        {
            if (ReferenceEquals(selectedTab?.Content, sender))
            {
                Search.Run();
            }
        };
        return created;
    }
}
