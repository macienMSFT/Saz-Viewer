using System.Globalization;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>One row of the WebSocket message list (the report's <c>ws-message-row</c>): ID, Type, Body and Preview.</summary>
internal sealed class WebSocketMessageItem
{
    public WebSocketMessageItem(WebSocketMessage message, int index)
    {
        Message = message;
        Index = index;
        LogicalId = index + 1;
        DirectionKey = message.Direction is "Client" or "Server" ? message.Direction : "Unknown";
        DirectionLabel = HtmlReportGenerator.WebSocketDirectionLabel(message.Direction);
        Arrow = message.Direction switch { "Client" => "\u2191", "Server" => "\u2193", _ => "\u2194" };
        ListType = HtmlReportGenerator.BuildWebSocketListType(message);
        IsLimited = message.IsPayloadTruncated || !message.IsComplete;
        var length = message.PayloadLength.ToString("N0", CultureInfo.InvariantCulture);
        Body = IsLimited ? length + "*" : length;
        BodyToolTip = IsLimited
            ? "Original declared logical payload bytes; retained content is truncated or the message is incomplete."
            : "Logical payload bytes.";
        Preview = HtmlReportGenerator.BuildWebSocketListPreview(message);
        AutomationName = $"{DirectionLabel}, logical message {LogicalId.ToString(CultureInfo.InvariantCulture)}, {ListType}, {length} bytes"
            + (IsLimited ? ", retained content is limited or partial" : "") + $", {Preview}";
        SearchKey = message.Type == "Text" ? message.Text : null;
    }

    public WebSocketMessage Message { get; }

    /// <summary>Position in the chronological (record order) list.</summary>
    public int Index { get; }

    public int LogicalId { get; }

    public string IdText => LogicalId.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Client", "Server" or "Unknown" (selects the arrow colour).</summary>
    public string DirectionKey { get; }

    public string DirectionLabel { get; }

    public string Arrow { get; }

    public string ListType { get; }

    public bool IsLimited { get; }

    public string Body { get; }

    public string BodyToolTip { get; }

    public string Preview { get; }

    public string AutomationName { get; }

    /// <summary>Decoded text searched by the payload filter; binary and control payloads are not searched.</summary>
    public string? SearchKey { get; }

    public bool Matches(string query) =>
        query.Length == 0 || (SearchKey is not null && SearchKey.Contains(query, StringComparison.OrdinalIgnoreCase));
}
