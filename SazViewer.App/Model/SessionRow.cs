using System.Globalization;
using SazViewer.Core;

namespace SazViewer.App.Model;

/// <summary>Immutable grid row for one HTTP session; mirrors the report's <c>AppendHttpRow</c> cell text and filter data.</summary>
internal sealed class SessionRow
{
    private readonly Dictionary<string, SessionColumnValue> columnValues = new(StringComparer.Ordinal);

    public SessionRow(HttpSession session, int index, IReadOnlyList<WebSocketMessage> webSocketMessages)
    {
        Session = session;
        Index = index;
        WebSocketMessages = webSocketMessages;
        var resultCode = session.Response is null ? null : session.StatusCode;
        ResultCode = resultCode;
        FilterKey = resultCode is >= 200 and <= 599
            ? (resultCode.Value / 100).ToString(CultureInfo.InvariantCulture)
            : "0";
        Time = FormatTime(session.Timestamp);
        TimeToolTip = session.Timestamp is null ? null : $"Captured timestamp: {HtmlReportGenerator.FormatTimestamp(session.Timestamp)}";
        Result = resultCode?.ToString(CultureInfo.InvariantCulture) ?? "\u2014";
        ResultToolTip = string.IsNullOrWhiteSpace(session.StatusText)
            ? null
            : resultCode is int status
                ? $"HTTP {status.ToString(CultureInfo.InvariantCulture)} {session.StatusText}"
                : session.StatusText;
        Method = session.Method ?? "-";
        Url = session.Url ?? "-";
        Elapsed = FormatElapsed(session.ElapsedMilliseconds);
        RequestSize = HtmlReportGenerator.FormatBytes(session.RequestBytes);
        ResponseSize = HtmlReportGenerator.FormatBytes(session.ResponseBytes);
        Summary = $"Session {session.Id}: {session.Method ?? "-"} {session.Url ?? "-"}";
        IsConnect = string.Equals(session.Method, "CONNECT", StringComparison.OrdinalIgnoreCase);
        SearchText = BuildSearchText(session, resultCode, webSocketMessages);
    }

    public HttpSession Session { get; }

    /// <summary>Chronological position (the report's row order).</summary>
    public int Index { get; }

    public IReadOnlyList<WebSocketMessage> WebSocketMessages { get; }

    public string Id => Session.Id;

    public string Time { get; }

    public string? TimeToolTip { get; }

    public int? ResultCode { get; }

    public string Result { get; }

    public string? ResultToolTip { get; }

    public string Method { get; }

    public string Url { get; }

    public string Elapsed { get; }

    public string RequestSize { get; }

    public string ResponseSize { get; }

    public string Summary { get; }

    /// <summary>"2".."5" for 2xx..5xx results, otherwise "0" (missing/other).</summary>
    public string FilterKey { get; }

    public bool IsMapi => Session.Mapi is not null;

    public bool IsWebSocket => WebSocketMessages.Count > 0;

    public bool IsConnect { get; }

    /// <summary>Lower-cased text the grid search matches against (same fields as the report).</summary>
    public string SearchText { get; }

    public string AutomationName => $"Inspect HTTP session {Id}";

    public SessionColumnValue ColumnValue(SessionColumnDefinition column)
    {
        var cacheKey = $"{column.Id}\0{column.Setting.Kind}\0{column.Setting.Source}";
        if (!columnValues.TryGetValue(cacheKey, out var value))
        {
            value = column.Read(this);
            columnValues[cacheKey] = value;
        }
        return value;
    }

    public static string FormatTime(DateTimeOffset? timestamp) =>
        timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

    public static string FormatElapsed(long? milliseconds) =>
        milliseconds is long value ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} ms" : "\u2014";

    private static string BuildSearchText(HttpSession session, int? resultCode, IReadOnlyList<WebSocketMessage> webSocketMessages) =>
        string.Join(' ', new[]
        {
            session.Id, session.Method, session.Url, resultCode?.ToString(CultureInfo.InvariantCulture),
            session.StatusText, session.ContentType, session.ClientEndpoint, session.ServerEndpoint,
            session.ElapsedMilliseconds?.ToString(CultureInfo.InvariantCulture),
            session.ElapsedMilliseconds is long elapsed ? $"{elapsed.ToString("N0", CultureInfo.InvariantCulture)} ms" : null,
            session.Mapi?.RequestType, session.Mapi?.Endpoint.ToString(),
            webSocketMessages.Count > 0 ? "websocket" : null,
            string.Join(' ', webSocketMessages.Take(100).Select(message =>
                $"{message.Direction} {message.Type} {message.Preview} {message.Warning}"))
        }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
}
