using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SazViewer.Core;

/// <summary>Reads an HTTP Archive 1.2 document into the same model used by SAZ captures.</summary>
public sealed class HarParser
{
    internal const long MaxFileBytes = 512L * 1024 * 1024;
    internal const int MaxEntries = 100_000;
    internal const int MaxBodyBytes = MapiParseLimits.MaxPayloadBytes;
    private const int MaxMetadataCharacters = 64 * 1024;
    private const int MaxWebSocketMessages = 100_000;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128
    };

    public SazReport Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"HAR file was not found: {fullPath}", fullPath);
        }

        var length = new FileInfo(fullPath).Length;
        if (length > MaxFileBytes)
        {
            throw new InvalidDataException(
                $"HAR file exceeds the {MaxFileBytes / (1024 * 1024):N0} MiB safety limit.");
        }

        using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(stream, Path.GetFileName(fullPath));
    }

    public SazReport Parse(Stream stream, string sourceName = "capture.har")
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The HAR stream must be readable.", nameof(stream));
        }
        if (stream.CanSeek && stream.Length - stream.Position > MaxFileBytes)
        {
            throw new InvalidDataException(
                $"HAR file exceeds the {MaxFileBytes / (1024 * 1024):N0} MiB safety limit.");
        }

        try
        {
            using var bounded = ReadBounded(stream);
            using var document = JsonDocument.Parse(bounded, DocumentOptions);
            return ParseDocument(document.RootElement, sourceName);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"'{sourceName}' is not a readable HAR JSON document: {exception.Message}",
                exception);
        }
    }

    private static SazReport ParseDocument(JsonElement root, string sourceName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("log", out var log)
            || log.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"'{sourceName}' is JSON but does not contain the required HAR 'log' object.");
        }

        var report = new SazReport
        {
            SourceName = sourceName,
            Format = CaptureFormat.Har,
            Creator = Product(log, "creator"),
            Browser = Product(log, "browser")
        };
        var pages = ParsePages(log, report.Warnings);
        if (!log.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            report.Warnings.Add("HAR log.entries is missing or is not an array.");
            return report;
        }
        if (entries.GetArrayLength() > MaxEntries)
        {
            throw new InvalidDataException($"HAR entry count exceeds the {MaxEntries:N0} safety limit.");
        }

        var archiveOrder = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var session = new HttpSession
            {
                Id = (archiveOrder + 1).ToString(CultureInfo.InvariantCulture),
                ArchiveOrder = archiveOrder
            };
            report.Sessions.Add(session);
            ParseEntry(entry, session, pages, report.WebSocketMessages);
            archiveOrder++;
        }

        report.Sessions.Sort(CompareSessions);
        report.WebSocketMessages.Sort(CompareWebSocketMessages);
        report.Mapi = MapiCaptureParser.Parse(report.Sessions);
        AggregateWarnings(report);
        if (report.Sessions.Count == 0)
        {
            report.Warnings.Add("HAR log.entries is empty.");
        }
        return report;
    }

    private static void ParseEntry(
        JsonElement entry,
        HttpSession session,
        IReadOnlyDictionary<string, string> pages,
        List<WebSocketMessage> webSocketMessages)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            session.Warnings.Add("entry is not an object and could not be parsed.");
            return;
        }

        session.Timestamp = Timestamp(entry, "startedDateTime", session.Warnings);
        var total = NonNegativeNumber(entry, "time");
        session.ElapsedMilliseconds = total is { } elapsed
            ? (long)Math.Round(elapsed, MidpointRounding.AwayFromZero)
            : null;
        AddTiming(entry, session, total);
        AddMetadata(entry, session, pages);

        if (entry.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.Object)
        {
            try
            {
                session.Request = ParseRequest(request, session);
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                session.Warnings.Add($"request could not be parsed: {exception.Message}");
            }
        }
        else
        {
            session.Warnings.Add("request is missing or is not an object.");
        }

        if (entry.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
        {
            try
            {
                session.Response = ParseResponse(response, session);
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                session.Warnings.Add($"response could not be parsed: {exception.Message}");
            }
        }
        else
        {
            session.Warnings.Add("response is missing or is not an object.");
        }

        session.ContentType = session.Response?.Header("Content-Type");
        session.ServerEndpoint = String(entry, "serverIPAddress");
        if (session.ServerEndpoint is not null)
        {
            session.Metadata["x-hostip"] = session.ServerEndpoint;
        }
        if (String(entry, "connection") is { } connection)
        {
            session.Metadata["connection"] = connection;
        }

        ParseWebSockets(entry, session, webSocketMessages);
    }

    private static HttpMessage ParseRequest(JsonElement request, HttpSession session)
    {
        session.Method = String(request, "method") ?? "GET";
        session.Url = String(request, "url") ?? string.Empty;
        var version = NormalizeHttpVersion(String(request, "httpVersion"));
        var headers = Headers(request, "headers", session.Warnings, "request");
        AddCookieHeader(request, headers, "Cookie", "request", session.Warnings);
        var body = RequestBody(request, headers, session.Warnings);
        var startLine = $"{session.Method} {session.Url} {version}";
        NotePseudoHeaders(headers, session, "request");
        var message = Message(startLine, headers, body);
        session.RequestBytes = MessageBytes(request, body.Length);
        return message;
    }

    private static HttpMessage ParseResponse(JsonElement response, HttpSession session)
    {
        session.StatusCode = Integer(response, "status");
        session.StatusText = String(response, "statusText");
        var version = NormalizeHttpVersion(String(response, "httpVersion"));
        var headers = Headers(response, "headers", session.Warnings, "response");
        AddCookieHeader(response, headers, "Set-Cookie", "response", session.Warnings);
        var body = ResponseBody(response, headers, session.Warnings);
        if (!headers.Any(header => header.Name.Equals("Location", StringComparison.OrdinalIgnoreCase))
            && String(response, "redirectURL") is { Length: > 0 } redirect)
        {
            headers.Add(new HttpHeader("Location", redirect));
        }
        NotePseudoHeaders(headers, session, "response");
        var status = session.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var message = Message(
            $"HTTP/{version["HTTP/".Length..]} {status}{(string.IsNullOrWhiteSpace(session.StatusText) ? "" : " " + session.StatusText)}",
            headers,
            body);
        session.ResponseBytes = MessageBytes(response, body.Length);
        if (NonNegativeNumber(response, "bodySize") is { } bodySize)
        {
            session.Metadata["x-responsebodytransferlength"] =
                Math.Round(bodySize, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        }
        if (response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            CopyNumber(content, "size", session.Metadata, "har.content.size");
            CopyNumber(content, "compression", session.Metadata, "har.content.compression");
        }
        return message;
    }

    private static HttpMessage Message(string startLine, IReadOnlyList<HttpHeader> headers, BodyPreview body)
    {
        var message = new HttpMessage { StartLine = startLine, Body = body };
        message.Headers.AddRange(headers);
        return message;
    }

    private static BodyPreview RequestBody(
        JsonElement request,
        List<HttpHeader> headers,
        List<string> warnings)
    {
        if (!request.TryGetProperty("postData", out var postData) || postData.ValueKind != JsonValueKind.Object)
        {
            return EmptyBody();
        }
        var mimeType = String(postData, "mimeType");
        AddContentType(headers, mimeType);
        var text = String(postData, "text");
        if (text is null && postData.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            text = string.Join("&", parameters.EnumerateArray()
                .Where(parameter => parameter.ValueKind == JsonValueKind.Object)
                .Select(parameter =>
                {
                    var name = String(parameter, "name") ?? string.Empty;
                    var value = String(parameter, "value")
                        ?? (String(parameter, "fileName") is { } file ? $"[file:{file}]" : string.Empty);
                    return $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
                }));
        }
        return DecodedBody(text, null, mimeType, warnings, "request");
    }

    private static BodyPreview ResponseBody(
        JsonElement response,
        List<HttpHeader> headers,
        List<string> warnings)
    {
        if (!response.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
        {
            return EmptyBody();
        }
        var mimeType = String(content, "mimeType");
        AddContentType(headers, mimeType);
        return DecodedBody(
            String(content, "text"),
            String(content, "encoding"),
            mimeType,
            warnings,
            "response",
            NonNegativeNumber(content, "size"));
    }

    private static BodyPreview DecodedBody(
        string? text,
        string? encoding,
        string? contentType,
        List<string> warnings,
        string label,
        double? declaredSize = null)
    {
        if (text is null)
        {
            var absentLength = declaredSize is { } size
                ? (long)Math.Round(size, MidpointRounding.AwayFromZero)
                : 0;
            return new BodyPreview
            {
                Length = absentLength,
                CapturedLength = 0,
                Preview = string.Empty,
                IsTruncated = absentLength > 0,
                SourceIsDecoded = true,
                DecodingStatus = absentLength > 0
                    ? "HAR declared decoded body bytes but did not retain content text."
                    : "HAR stores decoded content; original wire bytes are unavailable."
            };
        }

        byte[] retained;
        long length;
        if (encoding?.Equals("base64", StringComparison.OrdinalIgnoreCase) == true)
        {
            var maximumCharacters = ((MaxBodyBytes + 2) / 3 * 4) + 4;
            var source = text.Length > maximumCharacters
                ? text[..(maximumCharacters - (maximumCharacters % 4))]
                : text;
            try
            {
                retained = Convert.FromBase64String(source);
                length = declaredSize is { } size
                    ? Math.Max(retained.Length, (long)Math.Round(size, MidpointRounding.AwayFromZero))
                    : DecodedBase64Length(text);
            }
            catch (FormatException)
            {
                warnings.Add($"{label}: HAR content.encoding is base64 but content.text is invalid; UTF-8 text was retained instead.");
                retained = RetainedUtf8(text, out length);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(encoding))
            {
                warnings.Add($"{label}: unsupported HAR content encoding '{Truncate(encoding, 80)}'; text was treated as decoded UTF-8.");
            }
            retained = RetainedUtf8(text, out length);
            if (declaredSize is { } size)
            {
                length = Math.Max(length, (long)Math.Round(size, MidpointRounding.AwayFromZero));
            }
        }

        var preview = HttpMessageParser.CreateBodyPreview(
            retained,
            length,
            retained.Length,
            contentType);
        return new BodyPreview
        {
            Length = length,
            CapturedLength = 0,
            IsBinary = preview.IsBinary,
            IsTruncated = length > retained.Length,
            Charset = preview.Charset,
            Preview = preview.Preview,
            DecodingStatus = "HAR stores decoded content; original wire bytes and transfer encoding are unavailable.",
            SourceIsDecoded = true,
            DecodedBytes = retained,
            NormalizedBytes = retained
        };
    }

    private static byte[] RetainedUtf8(string text, out long length)
    {
        length = Encoding.UTF8.GetByteCount(text);
        if (length <= MaxBodyBytes)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        var maximumChars = Math.Min(text.Length, MaxBodyBytes);
        while (maximumChars > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, maximumChars)) > MaxBodyBytes)
        {
            maximumChars -= Math.Max(1, maximumChars / 32);
        }
        return Encoding.UTF8.GetBytes(text[..maximumChars]);
    }

    private static BodyPreview EmptyBody() => new()
    {
        Length = 0,
        CapturedLength = 0,
        Preview = string.Empty,
        SourceIsDecoded = true,
        DecodingStatus = "HAR stores decoded content; original wire bytes and transfer encoding are unavailable."
    };

    private static void AddTiming(JsonElement entry, HttpSession session, double? total)
    {
        if (entry.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
        {
            CopyTiming(timings, "blocked", session, "HAR.Blocked");
            CopyTiming(timings, "dns", session, "DNSTime");
            CopyTiming(timings, "connect", session, "TCPConnectTime");
            CopyTiming(timings, "ssl", session, "HTTPSHandshakeTime");
            CopyTiming(timings, "send", session, "HAR.Send");
            CopyTiming(timings, "wait", session, "HAR.Wait");
            CopyTiming(timings, "receive", session, "HAR.Receive");
        }
        if (session.Timestamp is not { } started)
        {
            return;
        }
        session.Timers["ClientBeginRequest"] = started.ToString("O", CultureInfo.InvariantCulture);
        if (total is >= 0)
        {
            session.Timers["ClientDoneResponse"] =
                started.AddMilliseconds(total.Value).ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private static void CopyTiming(JsonElement timings, string name, HttpSession session, string target)
    {
        if (NonNegativeNumber(timings, name) is { } value)
        {
            var formatted = value.ToString("0.###", CultureInfo.InvariantCulture);
            session.Timers[target] = formatted;
            session.Metadata[$"har.timing.{name}"] = formatted;
        }
    }

    private static void AddMetadata(
        JsonElement entry,
        HttpSession session,
        IReadOnlyDictionary<string, string> pages)
    {
        CopyMetadata(entry, "_resourceType", session);
        CopyMetadata(entry, "_priority", session);
        CopyMetadata(entry, "pageref", session);
        if (entry.TryGetProperty("_initiator", out var initiator))
        {
            session.Metadata["_initiator"] = BoundedJson(initiator);
        }
        if (session.Metadata.TryGetValue("pageref", out var pageReference)
            && pages.TryGetValue(pageReference, out var title))
        {
            session.Metadata["har.page.title"] = title;
        }
    }

    private static void CopyMetadata(JsonElement entry, string name, HttpSession session)
    {
        if (String(entry, name) is { } value)
        {
            session.Metadata[name] = value;
        }
    }

    private static Dictionary<string, string> ParsePages(JsonElement log, List<string> warnings)
    {
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!log.TryGetProperty("pages", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return pages;
        }
        foreach (var page in value.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("HAR pages contains a non-object item that was ignored.");
                continue;
            }
            if (String(page, "id") is { } id)
            {
                pages[id] = String(page, "title") ?? string.Empty;
            }
        }
        return pages;
    }

    private static void ParseWebSockets(
        JsonElement entry,
        HttpSession session,
        List<WebSocketMessage> messages)
    {
        if (!entry.TryGetProperty("_webSocketMessages", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        if (values.GetArrayLength() > MaxWebSocketMessages)
        {
            session.Warnings.Add(
                $"HAR WebSocket message count exceeds {MaxWebSocketMessages:N0}; additional messages were skipped.");
        }

        var index = 0;
        foreach (var value in values.EnumerateArray().Take(MaxWebSocketMessages))
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                session.Warnings.Add($"WebSocket message {index} is not an object and was skipped.");
                index++;
                continue;
            }
            var opcode = Integer(value, "opcode") ?? 1;
            var direction = String(value, "type")?.Equals("send", StringComparison.OrdinalIgnoreCase) == true
                ? "Client"
                : "Server";
            var data = String(value, "data") ?? string.Empty;
            byte[] payload;
            long payloadLength;
            var truncated = false;
            if (opcode == 2)
            {
                try
                {
                    var maxCharacters = ((MaxBodyBytes + 2) / 3 * 4) + 4;
                    var source = data.Length > maxCharacters
                        ? data[..(maxCharacters - (maxCharacters % 4))]
                        : data;
                    payload = Convert.FromBase64String(source);
                    payloadLength = DecodedBase64Length(data);
                    truncated = payloadLength > payload.Length;
                }
                catch (FormatException)
                {
                    payload = RetainedUtf8(data, out payloadLength);
                    truncated = payloadLength > payload.Length;
                    session.Warnings.Add($"WebSocket message {index} opcode 2 has invalid base64 data; UTF-8 bytes were retained.");
                }
            }
            else
            {
                payload = RetainedUtf8(data, out payloadLength);
                truncated = payloadLength > payload.Length;
            }

            var timestamp = WebSocketTimestamp(value);
            var type = WebSocketType(opcode);
            var isBinary = opcode == 2;
            var text = isBinary ? null : Encoding.UTF8.GetString(payload);
            var frame = new WebSocketFrame
            {
                RecordIndex = index,
                Timestamp = timestamp,
                Direction = direction,
                Opcode = opcode,
                Type = type,
                Final = true,
                PayloadLength = payloadLength,
                CapturedPayloadLength = payload.Length,
                IsDecoded = true,
                IsPayloadTruncated = truncated,
                Payload = payload
            };
            var message = new WebSocketMessage
            {
                SessionId = session.Id,
                MessageIndex = index,
                RecordIndex = index,
                Timestamp = timestamp,
                Direction = direction,
                Type = type,
                PayloadLength = payloadLength,
                Preview = isBinary ? Hex(payload) : Compact(text ?? string.Empty, 240),
                IsBinary = isBinary,
                IsDecoded = true,
                IsComplete = true,
                IsPayloadTruncated = truncated,
                Text = text,
                Payload = payload,
                SourceOrder = ((long)session.ArchiveOrder << 32) + index
            };
            message.Frames.Add(frame);
            messages.Add(message);
            index++;
        }
    }

    private static DateTimeOffset? WebSocketTimestamp(JsonElement value)
    {
        if (!value.TryGetProperty("time", out var time))
        {
            return null;
        }
        if (time.ValueKind == JsonValueKind.Number && time.TryGetDouble(out var seconds)
            && double.IsFinite(seconds))
        {
            try
            {
                return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return time.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
    }

    private static List<HttpHeader> Headers(
        JsonElement owner,
        string property,
        List<string> warnings,
        string label)
    {
        var headers = new List<HttpHeader>();
        if (!owner.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return headers;
        }
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object || String(value, "name") is not { Length: > 0 } name)
            {
                warnings.Add($"{label}: ignored malformed header at index {index}.");
            }
            else
            {
                headers.Add(new HttpHeader(name, String(value, "value") ?? string.Empty));
            }
            index++;
        }
        return headers;
    }

    private static void AddCookieHeader(
        JsonElement owner,
        List<HttpHeader> headers,
        string headerName,
        string label,
        List<string> warnings)
    {
        if (headers.Any(header => header.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            || !owner.TryGetProperty("cookies", out var cookies)
            || cookies.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        var values = new List<string>();
        foreach (var cookie in cookies.EnumerateArray())
        {
            if (cookie.ValueKind != JsonValueKind.Object || String(cookie, "name") is not { Length: > 0 } name)
            {
                warnings.Add($"{label}: ignored malformed cookie.");
                continue;
            }
            values.Add($"{name}={String(cookie, "value") ?? string.Empty}");
        }
        if (values.Count > 0)
        {
            headers.Add(new HttpHeader(headerName, string.Join(headerName == "Cookie" ? "; " : ", ", values)));
        }
    }

    private static void AddContentType(List<HttpHeader> headers, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType)
            && !headers.Any(header => header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new HttpHeader("Content-Type", mimeType));
        }
    }

    private static void NotePseudoHeaders(
        IEnumerable<HttpHeader> headers,
        HttpSession session,
        string label)
    {
        if (headers.Any(header => header.Name.StartsWith(':')))
        {
            session.Warnings.Add(
                $"{label}: HTTP/2 pseudo-headers were preserved in the synthesized raw HTTP view.");
        }
    }

    private static long MessageBytes(JsonElement message, long fallbackBodyLength)
    {
        var headers = NonNegativeNumber(message, "headersSize") ?? 0;
        var body = NonNegativeNumber(message, "bodySize") ?? fallbackBodyLength;
        return (long)Math.Round(headers + body, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeHttpVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "HTTP/1.1";
        }
        var trimmed = value.Trim();
        if (trimmed.Equals("h2", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP/2";
        }
        if (trimmed.Equals("h3", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP/3";
        }
        return trimmed.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            ? "HTTP/" + trimmed["HTTP/".Length..]
            : "HTTP/" + trimmed;
    }

    private static string? Product(JsonElement log, string property)
    {
        if (!log.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var name = String(value, "name");
        var version = String(value, "version");
        return string.Join(' ', new[] { name, version }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static DateTimeOffset? Timestamp(
        JsonElement owner,
        string property,
        List<string> warnings)
    {
        var value = String(owner, property);
        if (value is null)
        {
            return null;
        }
        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var timestamp))
        {
            return timestamp;
        }
        warnings.Add($"invalid {property} timestamp '{Truncate(value, 100)}'.");
        return null;
    }

    private static string? String(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
    }

    private static int? Integer(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private static double? NonNegativeNumber(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
        {
            return null;
        }
        return number;
    }

    private static void CopyNumber(
        JsonElement owner,
        string property,
        Dictionary<string, string> metadata,
        string target)
    {
        if (NonNegativeNumber(owner, property) is { } value)
        {
            metadata[target] = value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    private static string BoundedJson(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= MaxMetadataCharacters
            ? text
            : text[..MaxMetadataCharacters] + "…";
    }

    private static MemoryStream ReadBounded(Stream stream)
    {
        var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (output.Length <= MaxFileBytes)
        {
            var remaining = MaxFileBytes + 1 - output.Length;
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                output.Position = 0;
                return output;
            }
            output.Write(buffer, 0, read);
        }
        output.Dispose();
        throw new InvalidDataException(
            $"HAR file exceeds the {MaxFileBytes / (1024 * 1024):N0} MiB safety limit.");
    }

    private static long DecodedBase64Length(string value)
    {
        var length = value.Length;
        var padding = length > 0 && value[^1] == '=' ? 1 : 0;
        padding += length > 1 && value[^2] == '=' ? 1 : 0;
        return Math.Max(0, ((long)length / 4 * 3) - padding);
    }

    private static string WebSocketType(int opcode) => opcode switch
    {
        0 => "Continuation",
        1 => "Text",
        2 => "Binary",
        8 => "Close",
        9 => "Ping",
        10 => "Pong",
        _ => $"Opcode {opcode}"
    };

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var length = Math.Min(bytes.Length, 256);
        var output = new StringBuilder(length * 3);
        for (var index = 0; index < length; index++)
        {
            if (index > 0) output.Append(' ');
            output.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }
        if (bytes.Length > length) output.Append(" …");
        return output.ToString();
    }

    private static string Compact(string value, int limit)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= limit ? compact : compact[..limit] + "…";
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + "…";

    private static void AggregateWarnings(SazReport report)
    {
        foreach (var session in report.Sessions)
        {
            foreach (var warning in session.Warnings)
            {
                report.Warnings.Add($"Session {session.Id}: {warning}");
            }
        }
    }

    private static int CompareSessions(HttpSession left, HttpSession right)
    {
        if (left.Timestamp.HasValue && right.Timestamp.HasValue)
        {
            var compared = left.Timestamp.Value.CompareTo(right.Timestamp.Value);
            return compared != 0 ? compared : left.ArchiveOrder.CompareTo(right.ArchiveOrder);
        }
        if (left.Timestamp.HasValue) return -1;
        if (right.Timestamp.HasValue) return 1;
        return left.ArchiveOrder.CompareTo(right.ArchiveOrder);
    }

    private static int CompareWebSocketMessages(WebSocketMessage left, WebSocketMessage right)
    {
        if (left.Timestamp.HasValue && right.Timestamp.HasValue)
        {
            var compared = left.Timestamp.Value.CompareTo(right.Timestamp.Value);
            if (compared != 0) return compared;
        }
        else if (left.Timestamp.HasValue)
        {
            return -1;
        }
        else if (right.Timestamp.HasValue)
        {
            return 1;
        }
        return left.SourceOrder.CompareTo(right.SourceOrder);
    }
}
