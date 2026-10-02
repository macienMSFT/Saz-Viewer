namespace SazViewer.Core;

public enum CaptureFormat
{
    Saz,
    Har
}

public sealed class SazReport
{
    public required string SourceName { get; init; }
    public CaptureFormat Format { get; init; } = CaptureFormat.Saz;
    public string? Creator { get; init; }
    public string? Browser { get; init; }
    public List<HttpSession> Sessions { get; } = [];
    public List<WebSocketMessage> WebSocketMessages { get; } = [];
    public List<string> Warnings { get; } = [];
    public MapiCapture? Mapi { get; internal set; }
    public AuthScrubSummary? AuthScrub { get; internal set; }

    /// <summary>
    /// True while bodies decoded on demand still have to be decoded and their warnings aggregated into
    /// <see cref="Warnings"/>; see <see cref="SazParser.CompleteDeferred"/>.
    /// </summary>
    public bool HasDeferredWork
    {
        get => Volatile.Read(ref hasDeferredWork);
        internal set => Volatile.Write(ref hasDeferredWork, value);
    }

    private bool hasDeferredWork;

    internal object DeferredGate { get; } = new();
}

public sealed record AuthScrubSummary(IReadOnlyDictionary<string, int> Counts)
{
    public int Total => Counts.Values.Sum();
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
    public long? ElapsedMilliseconds { get; set; }
    public Dictionary<string, string> Timers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = [];
    public MapiSession? Mapi { get; internal set; }
}

public sealed class HttpMessage
{
    private readonly BodyPreview? body;
    private readonly Lazy<BodyPreview>? deferredBody;
    private readonly Func<int, BodyPreview>? deferredSearchBody;

    public HttpMessage()
    {
    }

    /// <summary>Creates a message whose body is decoded on first access (see <see cref="SazParser.DeferBodyDecoding"/>).</summary>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
#pragma warning disable CS8618 // Body is provided by the deferred decoder.
    internal HttpMessage(
        string startLine,
        Func<BodyPreview> decodeBody,
        Func<int, BodyPreview>? decodeSearchBody = null)
#pragma warning restore CS8618
    {
        StartLine = startLine;
        deferredBody = new Lazy<BodyPreview>(decodeBody, LazyThreadSafetyMode.ExecutionAndPublication);
        deferredSearchBody = decodeSearchBody;
    }

    public required string StartLine { get; init; }
    public List<HttpHeader> Headers { get; } = [];

    public required BodyPreview Body
    {
        get => body ?? deferredBody!.Value;
        init => body = value;
    }

    /// <summary>False until a deferred body has been decoded.</summary>
    public bool IsBodyDecoded => body is not null || deferredBody!.IsValueCreated;

    /// <summary>
    /// Decodes a bounded body specifically for desktop payload search without retaining the larger byte range
    /// in the report model. Eager/scrubbed messages return their already-safe body so original bytes cannot leak.
    /// </summary>
    internal BodyPreview BodyForSearch(int retainedDecodedBytesLimit) =>
        deferredSearchBody?.Invoke(retainedDecodedBytesLimit) ?? Body;

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public IEnumerable<string> HeaderValues(string name) =>
        Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
}

public sealed record HttpHeader(string Name, string Value);

public sealed class BodyPreview
{
    public long Length { get; init; }
    public long CapturedLength { get; init; }
    public bool IsBinary { get; init; }
    public bool IsTruncated { get; init; }
    public string? Charset { get; init; }
    public required string Preview { get; init; }
    public string? CapturedBytesPreview { get; init; }
    public bool CapturedBytesPreviewTruncated { get; init; }
    public IReadOnlyList<string> RemovedEncodings { get; init; } = [];
    public string? DecodingStatus { get; init; }
    /// <summary>True when the source supplied only an already-decoded representation, as HAR does.</summary>
    public bool SourceIsDecoded { get; init; }
    public bool WasDecoded => RemovedEncodings.Count > 0;
    internal ReadOnlyMemory<byte> CapturedBytes { get; init; }
    internal ReadOnlyMemory<byte> DecodedBytes { get; init; }
    internal ReadOnlyMemory<byte> NormalizedBytes { get; init; }
}

public sealed class WebSocketMessage
{
    public required string SessionId { get; init; }
    public int MessageIndex { get; init; }
    public int RecordIndex { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public required string Direction { get; init; }
    public required string Type { get; init; }
    public long PayloadLength { get; init; }
    public required string Preview { get; init; }
    public bool IsBinary { get; init; }
    public bool IsDecoded { get; init; }
    public bool IsComplete { get; init; }
    public bool IsFragmented { get; init; }
    public bool IsPayloadTruncated { get; init; }
    public string? Text { get; init; }
    public string? Warning { get; init; }
    public List<WebSocketFrame> Frames { get; } = [];
    public ReadOnlyMemory<byte> Payload { get; init; }
    internal long SourceOrder { get; init; }
}

public sealed class WebSocketFrame
{
    public int RecordIndex { get; init; }
    public int? FiddlerId { get; init; }
    public int? BitFlags { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public required string Direction { get; init; }
    public int Opcode { get; init; }
    public required string Type { get; init; }
    public bool Final { get; init; }
    public bool Masked { get; init; }
    public long PayloadLength { get; init; }
    public long CapturedPayloadLength { get; init; }
    public bool IsDecoded { get; init; }
    public bool IsPayloadTruncated { get; init; }
    public string? Warning { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
}
