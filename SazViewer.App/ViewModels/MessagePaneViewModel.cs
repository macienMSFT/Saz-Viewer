using SazViewer.App.Model;
using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The Request or Response side of the HTTP inspector: the fixed-order per-side tabs (the report's
/// <c>createMessagePanel</c>) plus the pane's own active-view search used in split view.
/// </summary>
internal sealed class MessagePaneViewModel : ObservableObject, ITabbedPane
{
    public static readonly IReadOnlyList<(string Key, string Label)> TabOrder =
    [
        ("json", "JSON"), ("xml", "XML"), ("mapi", "MAPI"), ("image", "Image"), ("webview", "WebView"),
        ("hex", "HexView"), ("auth", "Auth"), ("headers", "Headers"), ("raw", "Raw")
    ];

    private MessageTabViewModel? selectedTab;

    public MessagePaneViewModel(string title, MessageContent? content, MapiMessageParse? protocol, IClipboardService clipboard)
    {
        Title = title;
        Content = content;
        Protocol = protocol;
        var lower = title.ToLowerInvariant();
        var flags = Flags(content, protocol);
        Tabs = TabOrder.Select(tab => new MessageTabViewModel(
            tab.Key,
            tab.Label,
            flags[tab.Key],
            UnavailableText(tab.Key, lower, content is not null),
            CopyAccessibleName(tab.Key, lower),
            CopyDescription(tab.Key, lower),
            flags[tab.Key] ? () => Observe(CreateContent(tab.Key)) : null,
            clipboard)).ToList();
        HasAnyTab = Tabs.Any(tab => tab.IsEnabled);
        EmptyText = $"No {lower} entry was captured.";
        Search = new ActiveSearchViewModel(() => SelectedTab?.Content?.SearchTarget, $"Search {title} view...", $"Search active {title} view");
        selectedTab = Tabs.FirstOrDefault(tab => tab.Key == InitialTabKey(flags));
    }

    public string Title { get; }

    public MessageContent? Content { get; }

    public MapiMessageParse? Protocol { get; }

    public IReadOnlyList<MessageTabViewModel> Tabs { get; }

    public bool HasAnyTab { get; }

    public string EmptyText { get; }

    public string TabListName => $"{Title} detail views";

    /// <summary>Search used when this pane is shown on its own in split view.</summary>
    public ActiveSearchViewModel Search { get; }

    /// <summary>Raised when the visible view changes (tab switch); active-view searches reset.</summary>
    public event EventHandler? ViewChanged;

    public MessageTabViewModel? SelectedTab
    {
        get => selectedTab;
        set
        {
            if (value is null || !value.IsEnabled || ReferenceEquals(value, selectedTab))
            {
                return;
            }
            Search.Reset();
            selectedTab?.Deactivate();
            selectedTab = value;
            OnPropertyChanged();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public MessageTabViewModel Tab(string key) => Tabs.First(tab => tab.Key == key);

    /// <summary>Selects the tab with <paramref name="key"/> when it is enabled.</summary>
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

    public static Dictionary<string, bool> Flags(MessageContent? content, MapiMessageParse? protocol) => new()
    {
        ["json"] = content is { Presentation: { Format: BodyFormat.Json, CanToggle: true } },
        ["xml"] = content is { Presentation: { Format: BodyFormat.Xml, CanToggle: true } },
        ["mapi"] = protocol is not null,
        ["image"] = content?.Image is not null,
        ["webview"] = content?.HtmlPreview is not null,
        ["hex"] = content is not null && (!content.Captured.IsEmpty || content.Decoded is not null),
        ["auth"] = content?.HasAuth == true,
        ["headers"] = content is not null && content.Headers.Count > 0,
        ["raw"] = content is not null
    };

    /// <summary>The report's initial tab priority: MAPI, JSON, XML, Raw, Headers.</summary>
    public static string? InitialTabKey(IReadOnlyDictionary<string, bool> flags) =>
        new[] { "mapi", "json", "xml", "raw", "headers" }.FirstOrDefault(key => flags[key]);

    public static string UnavailableText(string key, string lower, bool hasModel) => key switch
    {
        "json" => $"JSON view is not available: the {lower} body is not recognized, valid JSON.",
        "xml" => $"XML view is not available: the {lower} body is not recognized, valid XML.",
        "mapi" => $"MAPI view is not available: no protocol tree was parsed for this {lower}.",
        "image" => $"Image view is not available: the {lower} body is not a complete retained PNG, JPEG, GIF, WebP, BMP, or ICO image.",
        "webview" => $"WebView is not available: the {lower} body is not complete retained HTML or XHTML.",
        "hex" => $"HexView is not available: no {lower} body bytes were retained.",
        "auth" => $"Auth view is not available: no Authorization, Proxy-Authorization, WWW-Authenticate, or Proxy-Authenticate header was captured for this {lower}.",
        "headers" => hasModel ? $"No headers were captured for this {lower}." : $"No {lower} entry was captured.",
        _ => $"No {lower} entry was captured."
    };

    public static string CopyDescription(string key, string lower) => key switch
    {
        "json" => $"{lower} JSON formatted text",
        "xml" => $"{lower} XML formatted text",
        "mapi" => $"{lower} MAPI protocol tree",
        "image" => $"{lower} image metadata",
        "webview" => $"{lower} HTML source",
        "hex" => $"{lower} hex view",
        "auth" => $"redacted {lower} authentication headers",
        "headers" => $"{lower} headers",
        _ => $"{lower} raw message"
    };

    public static string CopyAccessibleName(string key, string lower) => $"Copy {CopyDescription(key, lower)}";

    private TabContentViewModel CreateContent(string key)
    {
        var content = Content!;
        return key switch
        {
            "json" or "xml" => Structured(key == "json" ? BodyFormat.Json : BodyFormat.Xml, content),
            "headers" => new TextDocumentViewModel(MessageDocuments.Headers(content), () => CopyResult.Of(content.FullHeadersText())),
            "raw" => new TextDocumentViewModel(MessageDocuments.Raw(content), () => RawCopy(content)),
            "hex" => new HexViewModel(content),
            "auth" => new AuthViewModel(HtmlReportGenerator.BuildAuthView(content.Message)!),
            "image" => new ImageViewModel(content.Image!, content.BodyBytes, content.Body.Length),
            "webview" => new WebPreviewViewModel(content.HtmlPreview!, content.DecodeBodyText),
            "mapi" => new MapiViewModel(Protocol!),
            _ => new PlaceholderViewModel($"The native {TabOrder.First(tab => tab.Key == key).Label} view is not available yet.")
        };
    }

    private static StructuredBodyViewModel Structured(BodyFormat format, MessageContent content) =>
        new(format, content.Label, content.Status, content.DecodeBodyText);

    private TabContentViewModel Observe(TabContentViewModel created)
    {
        created.SearchTargetChanged += (sender, _) =>
        {
            if (ReferenceEquals(selectedTab?.Content, sender))
            {
                Search.Reset();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        return created;
    }

    private static CopyResult RawCopy(MessageContent content)
    {
        try
        {
            return CopyResult.Of(content.RawCopyText());
        }
        catch (InvalidDataException error)
        {
            return CopyResult.Fail($"Copy source could not be decoded. {error.Message}");
        }
    }
}

/// <summary>A tab strip view-model rendered by <c>MessagePaneView</c> (HTTP sides and WebSocket message detail).</summary>
internal interface ITabbedPane
{
    MessageTabViewModel? SelectedTab { get; set; }
}
