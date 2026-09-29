namespace SazViewer.Core;

public sealed class SazReport
{
    public required string SourceName { get; init; }
    public List<HttpSession> Sessions { get; } = [];
    public List<WebSocketMessage> WebSocketMessages { get; } = [];
    public List<string> Warnings { get; } = [];
}

public sealed class HttpSession
{
    public required string Id { get; init; }
    public int ArchiveOrder { get; init; }
    public DateTimeOffset? Timestamp { get; set; }
    public HttpMessage? Request { get; set; }
    public HttpMessage? Response { get; set; }
    public string? Method { get; set; }
    public string? Url { get; set; }
    public int? StatusCode { get; set; }
    public string? StatusText { get; set; }
    public string? ContentType { get; set; }
    public string? ClientEndpoint { get; set; }
    public string? ServerEndpoint { get; set; }
    public long RequestBytes { get; set; }
    public long ResponseBytes { get; set; }
    public Dictionary<string, string> Timers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];
}

public sealed class HttpMessage
{
    public required string StartLine { get; init; }
    public List<HttpHeader> Headers { get; } = [];
    public required BodyPreview Body { get; init; }

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}

public sealed record HttpHeader(string Name, string Value);

public sealed class BodyPreview
{
    public long Length { get; init; }
    public bool IsBinary { get; init; }
    public bool IsTruncated { get; init; }
    public string? Charset { get; init; }
    public required string Preview { get; init; }
}

public sealed class WebSocketMessage
{
    public required string SessionId { get; init; }
    public int RecordIndex { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public required string Direction { get; init; }
    public required string Type { get; init; }
    public long PayloadLength { get; init; }
    public required string Preview { get; init; }
    public bool IsBinary { get; init; }
    public bool IsDecoded { get; init; }
    public string? Warning { get; init; }
    internal long SourceOrder { get; init; }
}
