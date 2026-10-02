using System.Collections.Concurrent;
using System.Text;
using SazViewer.App.Model;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

internal readonly record struct PayloadSearchProgress(int Completed, int Total);

internal sealed record PayloadSearchResult(
    IReadOnlySet<SessionRow> Matches,
    int TruncatedSessions,
    int FailedSessions);

/// <summary>
/// Lazily extracts bounded, folded payload text and keeps it in a byte-budgeted LRU. Search-only decoding does
/// not retain its larger decoded byte range in the report model.
/// </summary>
internal sealed class PayloadSearchCache
{
    public const long DefaultByteBudget = 256L * 1024 * 1024;
    public const int MaxBodyScanBytes = HttpBodyDecoder.MaxDecodedBytes;
    private const int MaxMapiNodes = 100_000;
    private const int MaxSessionCharacters = 32 * 1024 * 1024;

    private readonly object gate = new();
    private readonly long byteBudget;
    private readonly Dictionary<SessionRow, CacheItem> items = [];
    private readonly LinkedList<SessionRow> lru = [];
    private long cachedBytes;
    private long cacheHits;
    private long cacheMisses;

    public PayloadSearchCache(long byteBudget = DefaultByteBudget)
    {
        this.byteBudget = Math.Max(0, byteBudget);
    }

    internal long CachedBytes
    {
        get
        {
            lock (gate)
            {
                return cachedBytes;
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return items.Count;
            }
        }
    }

    internal long CacheHits => Interlocked.Read(ref cacheHits);

    internal long CacheMisses => Interlocked.Read(ref cacheMisses);

    public async Task<PayloadSearchResult> SearchAsync(
        IReadOnlyList<SessionRow> rows,
        string foldedQuery,
        IProgress<PayloadSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var matches = new ConcurrentBag<SessionRow>();
        var completed = 0;
        var truncated = 0;
        var failed = 0;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8))
        };
        await Parallel.ForEachAsync(rows, options, (row, token) =>
        {
            try
            {
                var text = GetOrCreate(row, token);
                if (text.FoldedText.Contains(foldedQuery, StringComparison.Ordinal))
                {
                    matches.Add(row);
                }
                if (text.Truncated)
                {
                    Interlocked.Increment(ref truncated);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or IndexOutOfRangeException)
            {
                Interlocked.Increment(ref failed);
            }
            progress?.Report(new PayloadSearchProgress(Interlocked.Increment(ref completed), rows.Count));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return new PayloadSearchResult(matches.ToHashSet(), truncated, failed);
    }

    internal string GetBodyText(SessionRow row, bool request, CancellationToken cancellationToken)
    {
        var payload = GetOrCreate(row, cancellationToken);
        return request ? payload.RequestBody : payload.ResponseBody;
    }

    private SearchablePayload GetOrCreate(SessionRow row, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (items.TryGetValue(row, out var found))
            {
                Interlocked.Increment(ref cacheHits);
                Touch(found);
                return found.Payload;
            }
        }

        Interlocked.Increment(ref cacheMisses);
        var created = Extract(row, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var size = checked((long)(created.FoldedText.Length + created.RequestBody.Length + created.ResponseBody.Length)
            * sizeof(char) + 128);
        lock (gate)
        {
            if (items.TryGetValue(row, out var raced))
            {
                Interlocked.Increment(ref cacheHits);
                Touch(raced);
                return raced.Payload;
            }
            if (size > byteBudget)
            {
                return created;
            }
            while (cachedBytes + size > byteBudget && lru.Last is { } oldest)
            {
                var key = oldest.Value;
                lru.RemoveLast();
                if (items.Remove(key, out var removed))
                {
                    cachedBytes -= removed.Bytes;
                }
            }
            var node = lru.AddFirst(row);
            items[row] = new CacheItem(created, size, node);
            cachedBytes += size;
            return created;
        }
    }

    private void Touch(CacheItem item)
    {
        lru.Remove(item.Node);
        lru.AddFirst(item.Node);
    }

    private static SearchablePayload Extract(SessionRow row, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var truncated = false;
        var requestBody = AppendMessage(row.Session.Request, text, ref truncated, cancellationToken);
        BoundSessionText(text, ref truncated);
        var responseBody = AppendMessage(row.Session.Response, text, ref truncated, cancellationToken);
        BoundSessionText(text, ref truncated);
        foreach (var message in row.WebSocketMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.Text is { } decodedText)
            {
                text.Append(decodedText).Append('\n');
            }
            else if (message.IsDecoded && !message.Payload.IsEmpty
                     && TryDecodeUtf8(message.Payload.Span, allowTruncatedTail: message.IsPayloadTruncated, out var binaryText))
            {
                text.Append(binaryText).Append('\n');
            }
            truncated |= message.IsPayloadTruncated;
            if (text.Length >= MaxSessionCharacters)
            {
                truncated = true;
                break;
            }
        }
        AppendMapi(row.Session.Mapi?.Request?.Root, text, ref truncated, cancellationToken);
        BoundSessionText(text, ref truncated);
        AppendMapi(row.Session.Mapi?.Response?.Root, text, ref truncated, cancellationToken);
        BoundSessionText(text, ref truncated);
        return new SearchablePayload(SearchText.Fold(text.ToString()), requestBody, responseBody, truncated);
    }

    private static void BoundSessionText(StringBuilder text, ref bool truncated)
    {
        if (text.Length <= MaxSessionCharacters)
        {
            return;
        }
        text.Length = MaxSessionCharacters;
        truncated = true;
    }

    private static string AppendMessage(
        HttpMessage? message,
        StringBuilder text,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        if (message is null)
        {
            return "";
        }
        text.Append(message.StartLine).Append('\n');
        foreach (var header in message.Headers)
        {
            text.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
        }
        cancellationToken.ThrowIfCancellationRequested();
        var body = message.BodyForSearch(MaxBodyScanBytes);
        var bytes = !body.DecodedBytes.IsEmpty ? body.DecodedBytes : body.CapturedBytes;
        if (bytes.IsEmpty)
        {
            text.Append(body.Preview).Append('\n');
            truncated |= body.IsTruncated;
            return body.Preview;
        }

        var bodyText = new StringBuilder();
        if (!body.IsBinary && TryDecodeDeclaredText(bytes.Span, body.Charset, body.IsTruncated, out var declared))
        {
            bodyText.Append(declared);
        }
        else
        {
            if (TryDecodeUtf8(bytes.Span, body.IsTruncated, out var utf8))
            {
                bodyText.Append(utf8);
            }
            if (bodyText.Length > 0)
            {
                bodyText.Append('\n');
            }
            bodyText.Append(Encoding.Latin1.GetString(bytes.Span));
        }
        text.Append(bodyText).Append('\n');
        truncated |= body.IsTruncated || body.Length > bytes.Length;
        return bodyText.ToString();
    }

    private static bool TryDecodeDeclaredText(
        ReadOnlySpan<byte> bytes,
        string? charset,
        bool truncated,
        out string text)
    {
        var normalized = (charset ?? "utf-8").Trim().Trim('"', '\'').ToLowerInvariant();
        Encoding? encoding = normalized switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false, true),
            "us-ascii" or "ascii" => Encoding.GetEncoding(
                Encoding.ASCII.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback),
            "iso-8859-1" or "latin1" or "latin-1" => Encoding.Latin1,
            "utf-16" or "utf-16le" or "unicode" => new UnicodeEncoding(false, false, true),
            "utf-16be" => new UnicodeEncoding(true, false, true),
            _ => null
        };
        return TryDecode(bytes, encoding, truncated, out text);
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, bool allowTruncatedTail, out string text) =>
        TryDecode(bytes, new UTF8Encoding(false, true), allowTruncatedTail, out text);

    private static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        Encoding? encoding,
        bool allowTruncatedTail,
        out string text)
    {
        if (encoding is null)
        {
            text = "";
            return false;
        }
        var maxTrim = Math.Min(allowTruncatedTail ? 4 : 0, bytes.Length);
        for (var trim = 0; trim <= maxTrim; trim++)
        {
            try
            {
                text = encoding.GetString(bytes[..(bytes.Length - trim)]);
                if (text.Length > 0 && text[0] == '\uFEFF')
                {
                    text = text[1..];
                }
                return true;
            }
            catch (DecoderFallbackException)
            {
            }
        }
        text = "";
        return false;
    }

    private static void AppendMapi(
        MapiNode? root,
        StringBuilder text,
        ref bool truncated,
        CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return;
        }
        var stack = new Stack<MapiNode>();
        stack.Push(root);
        var count = 0;
        while (stack.TryPop(out var node))
        {
            if ((count++ & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (count > MaxMapiNodes)
            {
                truncated = true;
                break;
            }
            text.Append(node.Name).Append(' ');
            if (node.Value is { Length: > 0 } value)
            {
                text.Append(value);
            }
            text.Append('\n');
            if (text.Length >= MaxSessionCharacters)
            {
                truncated = true;
                break;
            }
            for (var index = node.Children.Length - 1; index >= 0; index--)
            {
                stack.Push(node.Children[index]);
            }
        }
    }

    private sealed record SearchablePayload(
        string FoldedText,
        string RequestBody,
        string ResponseBody,
        bool Truncated);

    private sealed record CacheItem(
        SearchablePayload Payload,
        long Bytes,
        LinkedListNode<SessionRow> Node);
}
