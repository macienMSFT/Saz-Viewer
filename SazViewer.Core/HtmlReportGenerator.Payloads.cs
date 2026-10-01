using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace SazViewer.Core;

public sealed partial class HtmlReportGenerator
{
    private void AppendHttpSection(
        StringBuilder html,
        IReadOnlyList<HttpSession> sessions,
        IReadOnlyList<WebSocketMessage> webSocketMessages)
    {
        var webSocketsBySession = webSocketMessages
            .GroupBy(message => message.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WebSocketMessage>)group.ToArray(), StringComparer.Ordinal);
        html.Append("""
<section class="http-workspace" aria-label="HTTP sessions">
<div class="controls"><input id="httpSearch" type="search" aria-label="Search HTTP sessions" placeholder="Search method, URL, result, elapsed time, content type, endpoints...">
<select id="httpFilter" aria-label="Filter HTTP status or protocol"><option value="">All sessions</option><option value="websocket">WebSocket only</option><option value="mapi">MAPI/NSPI only</option><option value="2">2xx</option><option value="3">3xx</option><option value="4">4xx</option><option value="5">5xx</option><option value="0">Missing/other</option></select>
<label class="filter-toggle" for="hideConnect"><input id="hideConnect" type="checkbox">Hide CONNECT</label>
<button type="button" class="theme-toggle" aria-label="Switch theme" title="Switch theme" aria-pressed="false"><svg aria-hidden="true" viewBox="0 0 24 24"><path d="M9 18h6M10 21h4M8.5 15.5A6 6 0 1 1 15.5 15.5C14.6 16.2 14 17 14 18h-4c0-1-.6-1.8-1.5-2.5Z"/><circle class="theme-bulb-core" cx="12" cy="11" r="2.4"/></svg></button></div>
<div id="reportStatus" class="warning hidden" role="status" aria-live="polite"></div>
<div class="http-table-scroll"><table id="httpTable"><thead><tr><th class="http-time">Time</th><th class="http-id">ID</th><th class="http-result num">Result</th><th class="http-method">Method</th><th class="http-url">URL</th><th class="http-elapsed num">Elapsed Time</th><th class="http-bytes num">Req</th><th class="http-bytes num">Resp</th></tr></thead><tbody>
""");
        for (var index = 0; index < sessions.Count; index++)
        {
            webSocketsBySession.TryGetValue(sessions[index].Id, out var sessionWebSockets);
            AppendHttpRow(html, sessions[index], index, sessionWebSockets ?? []);
        }
        html.Append("</tbody></table></div>\n<div class=\"session-templates\" hidden>\n");
        for (var index = 0; index < sessions.Count; index++)
        {
            webSocketsBySession.TryGetValue(sessions[index].Id, out var sessionWebSockets);
            AppendSessionTemplate(html, sessions[index], index, sessionWebSockets ?? []);
        }
        html.Append("</div></section>");
    }

    private static void AppendHttpRow(
        StringBuilder html,
        HttpSession session,
        int index,
        IReadOnlyList<WebSocketMessage> webSocketMessages)
    {
        var resultCode = session.Response is null ? null : session.StatusCode;
        var filter = resultCode is >= 200 and <= 599
            ? (resultCode.Value / 100).ToString(CultureInfo.InvariantCulture)
            : "0";
        var search = string.Join(' ', new[]
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
        var summary = $"Session {session.Id}: {session.Method ?? "-"} {session.Url ?? "-"}";
        html.Append("<tr tabindex=\"0\" aria-selected=\"false\" aria-label=\"Inspect HTTP session ");
        Attribute(html, session.Id);
        html.Append("\" data-detail=\"http-detail-").Append(index).Append("\" data-filter=\"")
            .Append(filter).Append("\" data-mapi=\"").Append(session.Mapi is not null ? "true" : "false")
            .Append("\" data-websocket=\"").Append(webSocketMessages.Count > 0 ? "true" : "false")
            .Append("\" data-method=\"");
        Attribute(html, (session.Method ?? string.Empty).ToLowerInvariant());
        html.Append("\" data-summary=\"");
        Attribute(html, summary);
        html.Append("\" data-search=\"");
        Attribute(html, search);
        html.Append("\"><td class=\"http-time\">");
        AppendHttpTimestamp(html, session.Timestamp);
        html.Append("</td><td class=\"http-id\">");
        Text(html, session.Id);
        html.Append("</td><td class=\"http-result num\"");
        if (!string.IsNullOrWhiteSpace(session.StatusText))
        {
            html.Append(" title=\"");
            Attribute(
                html,
                resultCode is int status
                    ? $"HTTP {status.ToString(CultureInfo.InvariantCulture)} {session.StatusText}"
                    : session.StatusText);
            html.Append('"');
        }
        html.Append('>');
        Text(html, resultCode?.ToString(CultureInfo.InvariantCulture) ?? "\u2014");
        html.Append("</td><td class=\"http-method\"><span class=\"badge\">");
        Text(html, session.Method ?? "-");
        html.Append("</span></td><td class=\"http-url\" title=\"");
        Attribute(html, session.Url ?? "-");
        html.Append("\"><span class=\"http-url-value\">");
        Text(html, session.Url ?? "-");
        html.Append("</span></td><td class=\"http-elapsed num\">");
        Text(
            html,
            session.ElapsedMilliseconds is long elapsedMilliseconds
                ? $"{elapsedMilliseconds.ToString("N0", CultureInfo.InvariantCulture)} ms"
                : "\u2014");
        html.Append("</td><td class=\"http-bytes num\">").Append(FormatBytes(session.RequestBytes))
            .Append("</td><td class=\"http-bytes num\">").Append(FormatBytes(session.ResponseBytes))
            .Append("</td></tr>");
    }

    private void AppendSessionTemplate(
        StringBuilder html,
        HttpSession session,
        int index,
        IReadOnlyList<WebSocketMessage> webSocketMessages)
    {
        html.Append("<template id=\"http-detail-").Append(index).Append("\">");
        if (webSocketMessages.Count > 0)
        {
            AppendWebSocketInspector(html, webSocketMessages);
            html.Append("</template>");
            return;
        }
        html.Append("<div class=\"http-session-source\"");
        AppendSessionPayloadAttributes(html, session);
        html.Append('>');
        AppendSessionDetails(html, session);
        AppendProtocolSource(html, "request", session.Mapi?.Request);
        AppendProtocolSource(html, "response", session.Mapi?.Response);
        html.Append("<div class=\"http-session-loading\" role=\"status\">Loading Request and Response inspector...</div></div></template>");
    }

    private static void AppendProtocolSource(
        StringBuilder html,
        string side,
        MapiMessageParse? protocol)
    {
        if (protocol is null)
        {
            return;
        }
        var warnings = protocol.Warnings.Take(50).ToArray();
        var compressed = CreateProtocolPayload(protocol, warnings);
        html.Append("<div class=\"mapi-source\" data-message-side=\"").Append(side).Append('"');
        if (compressed is not null)
        {
            AppendCompressedPayloadAttributes(html, compressed);
        }
        else
        {
            html.Append(" data-payload-error=\"Protocol tree exceeds the 32 MiB report safety limit.\"");
        }
        html.Append(" hidden></div>");
    }

    private void AppendSessionPayloadAttributes(StringBuilder html, HttpSession session)
    {
        var model = new SessionPayload(
            1,
            BuildMessagePayload(session.Request),
            BuildMessagePayload(session.Response));
        var payload = CreateCompressedPayload(
            "http-session",
            JsonSerializer.SerializeToUtf8Bytes(model, SessionPayloadOptions),
            HttpSessionPayloadMaxDecodedBytes);
        if (payload is not null)
        {
            AppendCompressedPayloadAttributes(html, payload);
        }
        else
        {
            html.Append(" data-payload-error=\"Session data exceeds the compressed report safety limit.\"");
        }
    }

    private MessagePayload? BuildMessagePayload(HttpMessage? message)
    {
        if (message is null)
        {
            return null;
        }

        var body = message.Body;
        var captured = body.WasDecoded
            ? body.CapturedBytes
            : !body.DecodedBytes.IsEmpty
                ? body.DecodedBytes
                : body.CapturedBytes;
        var decoded = body.WasDecoded ? body.DecodedBytes : ReadOnlyMemory<byte>.Empty;
        if (!decoded.IsEmpty && decoded.Span.SequenceEqual(captured.Span))
        {
            decoded = ReadOnlyMemory<byte>.Empty;
        }

        var presentation = bodyFormatter.Format(body, message.Header("Content-Type"));
        var fallbackText = captured.IsEmpty && body.Preview.Length > 0
            ? body.Preview[..Math.Min(body.Preview.Length, 65536)]
            : null;
        var fallbackCapturedText = captured.IsEmpty && body.CapturedBytesPreview is not null
            ? body.CapturedBytesPreview[..Math.Min(body.CapturedBytesPreview.Length, MaxHydratedDisplayCharacters)]
            : null;
        return new MessagePayload(
            message.StartLine,
            message.Headers.ToArray(),
            new BodyPayload(
                body.Length,
                body.CapturedLength,
                body.IsBinary,
                body.IsTruncated || (fallbackText is not null && fallbackText.Length < body.Preview.Length),
                body.Charset,
                body.RemovedEncodings.ToArray(),
                body.DecodingStatus,
                Convert.ToBase64String(captured.Span),
                decoded.IsEmpty ? null : Convert.ToBase64String(decoded.Span),
                fallbackText,
                fallbackCapturedText,
                captured.IsEmpty && body.CapturedBytesPreview is not null
                    ? body.CapturedBytesPreviewTruncated || fallbackCapturedText!.Length < body.CapturedBytesPreview.Length
                    : null,
                body.CapturedBytesPreview is not null),
            presentation.Format.ToString().ToLowerInvariant(),
            presentation.Label,
            presentation.Status,
            presentation.CanToggle,
            DetectImageView(message),
            SafeHtmlPreviewBuilder.TryCreate(message)?.Detection,
            BuildAuthView(message) is not null);
    }

    private void AppendWebSocketInspector(
        StringBuilder html,
        IReadOnlyList<WebSocketMessage> messages)
    {
        var bounded = new List<WebSocketPayloadMessage>();
        var estimatedBytes = 0;
        foreach (var message in messages.OrderBy(message => message.RecordIndex).Take(MaxWebSocketMessagesPerSession))
        {
            var candidate = BuildWebSocketPayloadMessage(message);
            var candidateBytes = EstimateWebSocketPayloadBytes(candidate);
            if (estimatedBytes + candidateBytes > WebSocketPayloadContentBudgetBytes)
            {
                break;
            }
            bounded.Add(candidate);
            estimatedBytes += candidateBytes;
        }
        var payloadBytes = SerializeWebSocketPayload(bounded, messages.Count);
        while (payloadBytes.Length > WebSocketPayloadMaxDecodedBytes && bounded.Count > 0)
        {
            var keep = bounded.Count / 2;
            bounded.RemoveRange(keep, bounded.Count - keep);
            payloadBytes = SerializeWebSocketPayload(bounded, messages.Count);
        }
        var payload = CreateCompressedPayload(
            "websocket-session",
            payloadBytes,
            WebSocketPayloadMaxDecodedBytes);

        html.Append("<div class=\"websocket-inspector\"");
        if (payload is not null)
        {
            AppendCompressedPayloadAttributes(html, payload);
            html.Append("><div class=\"ws-loading\" role=\"status\">Loading WebSocket traffic...</div>");
        }
        else
        {
            html.Append(" data-payload-error=\"WebSocket traffic exceeds the 32 MiB report safety limit.\">")
                .Append("<div class=\"warning\">WebSocket traffic is too large to include safely in this report.</div>");
        }
        html.Append("</div>");
    }

    private static byte[] SerializeWebSocketPayload(
        IReadOnlyList<WebSocketPayloadMessage> messages,
        int totalMessageCount) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            messages,
            omittedMessages = Math.Max(0, totalMessageCount - messages.Count)
        }, TreePayloadOptions);

    private static int EstimateWebSocketPayloadBytes(WebSocketPayloadMessage message)
    {
        var total = 1024L + (message.Frames.Count * 320L);
        foreach (var value in new[]
        {
            message.Timestamp, message.Direction, message.Type, message.Preview, message.Warning,
            message.Text, message.JsonPretty, message.JsonTree, message.Raw
        })
        {
            if (value is not null) total += Encoding.UTF8.GetByteCount(value);
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private WebSocketPayloadMessage BuildWebSocketPayloadMessage(WebSocketMessage message)
    {
        BodyPresentation? json = null;
        if (message.Text is not null)
        {
            var body = new BodyPreview
            {
                Length = message.PayloadLength,
                CapturedLength = message.Payload.Length,
                Preview = message.Text,
                IsTruncated = message.IsPayloadTruncated
            };
            var formatted = bodyFormatter.Format(body, null);
            if (formatted.Format == BodyFormat.Json
                && !formatted.IsTruncated
                && BuildJsonTreePayload(formatted.Formatted) is { } tree)
            {
                json = formatted with { Raw = tree };
            }
        }

        return new WebSocketPayloadMessage(
            message.MessageIndex,
            message.RecordIndex,
            FormatWebSocketTimestamp(message.Timestamp),
            message.Direction,
            message.Type,
            BuildWebSocketListType(message),
            message.PayloadLength,
            message.PayloadLength.ToString("N0", CultureInfo.InvariantCulture),
            message.Frames.Count,
            message.IsComplete,
            message.IsFragmented,
            message.IsPayloadTruncated,
            message.Preview,
            BuildWebSocketListPreview(message),
            message.Warning,
            message.Text,
            json?.Formatted,
            json?.Raw,
            BuildWebSocketRaw(message),
            message.Frames.Select(frame => new WebSocketPayloadFrame(
                frame.RecordIndex,
                frame.FiddlerId,
                frame.BitFlags,
                FormatWebSocketTimestamp(frame.Timestamp),
                frame.Direction,
                frame.Opcode,
                frame.Type,
                frame.Final,
                frame.Masked,
                frame.PayloadLength,
                frame.CapturedPayloadLength,
                frame.IsDecoded,
                frame.IsPayloadTruncated,
                frame.Warning)).ToArray());
    }

    private static string BuildWebSocketListPreview(WebSocketMessage message)
    {
        const int maxCharacters = 180;
        string preview;
        if (message.Text is not null)
        {
            preview = CompactWebSocketWhitespace(message.Text);
            if (preview.Length == 0) preview = "(empty text message)";
        }

        else if (message.Type is "Binary")
        {
            preview = CompactWebSocketHex(message.Payload.Span, "Binary");
        }
        else if (message.Type is "Ping" or "Pong" or "Close")
        {
            preview = CompactWebSocketHex(message.Payload.Span, $"{message.Type} control");
        }
        else if (!string.IsNullOrWhiteSpace(message.Warning))
        {
            preview = $"Invalid/partial: {CompactWebSocketWhitespace(message.Warning)}";
        }
        else
        {
            preview = "Invalid or partial WebSocket message";
        }

        return preview.Length <= maxCharacters
            ? preview
            : string.Concat(preview.AsSpan(0, maxCharacters - 1), "\u2026");
    }

    private static string BuildWebSocketListType(WebSocketMessage message)
    {
        if (message.Type is not ("Text" or "Binary" or "Ping" or "Pong" or "Close"))
        {
            return "Invalid";
        }
        return message.IsComplete ? message.Type : "Partial";
    }

    private static string CompactWebSocketWhitespace(string value)
    {
        var output = new StringBuilder(Math.Min(value.Length, 256));
        var whitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace = output.Length > 0;
                continue;
            }
            if (whitespace)
            {
                output.Append(' ');
                whitespace = false;
            }
            output.Append(character);
            if (output.Length > 512) break;
        }
        return output.ToString().Trim();
    }

    private static string CompactWebSocketHex(ReadOnlySpan<byte> payload, string label)
    {
        const int maxBytes = 16;
        var shown = Math.Min(payload.Length, maxBytes);
        var output = new StringBuilder(label).Append(" (")
            .Append(payload.Length.ToString("N0", CultureInfo.InvariantCulture))
            .Append(payload.Length == 1 ? " byte)" : " bytes)");
        if (shown > 0)
        {
            output.Append(": ");
            for (var index = 0; index < shown; index++)
            {
                if (index > 0) output.Append(' ');
                output.Append(payload[index].ToString("X2", CultureInfo.InvariantCulture));
            }
            if (shown < payload.Length) output.Append(" \u2026");
        }
        return output.ToString();
    }

    private static string BuildWebSocketRaw(WebSocketMessage message)
    {
        var text = new StringBuilder();
        text.Append("Direction: ").Append(WebSocketDirectionLabel(message.Direction)).Append('\n')
            .Append("Type: ").Append(message.Type).Append('\n')
            .Append("Timestamp: ").Append(FormatWebSocketTimestamp(message.Timestamp)).Append('\n')
            .Append("Logical payload length: ").Append(message.PayloadLength.ToString("N0", CultureInfo.InvariantCulture)).Append(" bytes\n")
            .Append("Frames: ").Append(message.Frames.Count.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("Fragmented: ").Append(message.IsFragmented ? "yes" : "no").Append('\n')
            .Append("Complete: ").Append(message.IsComplete ? "yes" : "no").Append('\n');
        if (!string.IsNullOrWhiteSpace(message.Warning))
        {
            text.Append("Warnings: ").Append(message.Warning).Append('\n');
        }

        text.Append("\nFrame metadata:\n");
        foreach (var frame in message.Frames)
        {
            text.Append("  Frame ").Append(frame.RecordIndex.ToString(CultureInfo.InvariantCulture))
                .Append(" ID=").Append(frame.FiddlerId?.ToString(CultureInfo.InvariantCulture) ?? "?")
                .Append(" BitFlags=").Append(frame.BitFlags?.ToString(CultureInfo.InvariantCulture) ?? "?")
                .Append(": ").Append(frame.Type)
                .Append(" opcode=0x").Append(frame.Opcode < 0 ? "?" : frame.Opcode.ToString("X", CultureInfo.InvariantCulture))
                .Append(" FIN=").Append(frame.Final ? '1' : '0')
                .Append(" masked=").Append(frame.Masked ? "yes" : "no")
                .Append(" payload=").Append(frame.PayloadLength.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" captured=").Append(frame.CapturedPayloadLength.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" direction=").Append(WebSocketDirectionLabel(frame.Direction))
                .Append(" timestamp=").Append(FormatWebSocketTimestamp(frame.Timestamp));
            if (!string.IsNullOrWhiteSpace(frame.Warning))
            {
                text.Append(" warning=").Append(frame.Warning);
            }
            text.Append('\n');
        }

        text.Append("\nPayload:\n");
        if (message.Type == "Text" && message.Text is not null)
        {
            AppendBoundedWebSocketText(text, message.Text);
        }
        else
        {
            var bytes = message.Payload.Span;
            var shown = Math.Min(bytes.Length, MaxWebSocketRawPayloadBytes);
            text.Append(HttpMessageParser.HexPreview(bytes[..shown]));
            if (shown < bytes.Length)
            {
                text.Append("[Payload hex truncated after ")
                    .Append(shown.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" of ").Append(bytes.Length.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" retained bytes]\n");
            }
        }
        if (message.IsPayloadTruncated)
        {
            text.Append("[Captured logical payload was truncated by report safety limits]\n");
        }
        return text.Length <= MaxCopyCharacters
            ? text.ToString()
            : string.Concat(
                text.ToString(0, MaxCopyCharacters - 80),
                "\n[Raw WebSocket representation truncated at the 1 MiB copy safety limit]\n");
    }

    private static void AppendBoundedWebSocketText(StringBuilder output, string value)
    {
        var remaining = Math.Max(0, MaxCopyCharacters - output.Length - 96);
        if (value.Length <= remaining)
        {
            output.Append(value);
            if (!value.EndsWith('\n')) output.Append('\n');
            return;
        }
        output.Append(value.AsSpan(0, remaining))
            .Append("\n[Text payload truncated in Raw view at the 1 MiB copy safety limit]\n");
    }

    private static string FormatWebSocketTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) ?? "Unknown";

    private static string WebSocketDirectionLabel(string direction) => direction switch
    {
        "Client" => "Client to server",
        "Server" => "Server to client",
        _ => "Unknown direction"
    };

    private sealed record WebSocketPayloadMessage(
        int MessageIndex,
        int RecordIndex,
        string Timestamp,
        string Direction,
        string Type,
        string ListType,
        long PayloadLength,
        string PayloadLengthText,
        int FrameCount,
        bool IsComplete,
        bool IsFragmented,
        bool IsPayloadTruncated,
        string Preview,
        string ListPreview,
        string? Warning,
        string? Text,
        string? JsonPretty,
        string? JsonTree,
        string Raw,
        IReadOnlyList<WebSocketPayloadFrame> Frames);

    private sealed record WebSocketPayloadFrame(
        int RecordIndex,
        int? FiddlerId,
        int? BitFlags,
        string Timestamp,
        string Direction,
        int Opcode,
        string Type,
        bool Final,
        bool Masked,
        long PayloadLength,
        long CapturedPayloadLength,
        bool IsDecoded,
        bool IsPayloadTruncated,
        string? Warning);

    private static void AppendSessionDetails(StringBuilder html, HttpSession session)
    {
        var hasEndpoints = session.ClientEndpoint is not null || session.ServerEndpoint is not null;
        if (!hasEndpoints && session.Timers.Count == 0 && session.Warnings.Count == 0)
        {
            return;
        }

        html.Append("<details class=\"session-details\"><summary>Session details</summary>");
        if (hasEndpoints)
        {
            html.Append("<p><b>Endpoints:</b> ");
            Text(html, $"{session.ClientEndpoint ?? "?"} -> {session.ServerEndpoint ?? "?"}");
            html.Append("</p>");
        }
        if (session.Timers.Count > 0)
        {
            html.Append("<details><summary>Timers</summary><pre>");
            foreach (var timer in session.Timers)
            {
                Text(html, $"{timer.Key}: {timer.Value}\n");
            }
            html.Append("</pre></details>");
        }
        foreach (var warning in session.Warnings)
        {
            html.Append("<div class=\"warning\">");
            Text(html, warning);
            html.Append("</div>");
        }
        html.Append("</details>");
    }

    private static ImageViewInfo? DetectImageView(HttpMessage message)
    {
        var body = message.Body;
        var bytes = body.DecodedBytes.Span;
        if (body.IsTruncated || bytes.IsEmpty || body.Length != bytes.Length)
        {
            return null;
        }

        var sniffed = SniffImageMime(bytes);
        if (sniffed is null)
        {
            return null;
        }

        var declared = NormalizeMediaType(message.Header("Content-Type"));
        var warning = declared.Length > 0 && !declared.Equals(sniffed, StringComparison.OrdinalIgnoreCase)
            ? $"Declared Content-Type '{declared}' does not match the retained bytes; rendering as '{sniffed}'."
            : null;
        var detection = declared.Equals(sniffed, StringComparison.OrdinalIgnoreCase)
            ? $"Content-Type and retained-byte signature agree on {sniffed}."
            : $"Detected {sniffed} from the retained-byte signature.";
        return new ImageViewInfo(sniffed, detection, ImageAnimation(bytes, sniffed), warning);
    }

    private static string? SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            && ValidatePng(bytes))
        {
            return "image/png";
        }
        if (ValidateJpeg(bytes))
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 6
            && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))
            && ValidateGif(bytes))
        {
            return "image/gif";
        }
        if (ValidateWebP(bytes))
        {
            return "image/webp";
        }
        if (ValidateBmp(bytes))
        {
            return "image/bmp";
        }
        if (ValidateIco(bytes))
        {
            return "image/x-icon";
        }
        return null;
    }

    private static bool ValidatePng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20
            || !bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return false;
        }
        var offset = 8;
        var first = true;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > int.MaxValue)
            {
                return false;
            }
            var end = offset + 12L + length;
            if (end > bytes.Length)
            {
                return false;
            }
            var type = bytes.Slice(offset + 4, 4);
            if (first && (!type.SequenceEqual("IHDR"u8) || length != 13))
            {
                return false;
            }
            first = false;
            offset = checked((int)end);
            if (type.SequenceEqual("IEND"u8))
            {
                return length == 0 && offset == bytes.Length;
            }
        }
        return false;
    }

    private static bool ValidateJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return false;
        }
        var offset = 2;
        var hasFrame = false;
        var hasScan = false;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xFF)
            {
                return false;
            }
            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                offset++;
            }
            if (offset >= bytes.Length)
            {
                return false;
            }
            var marker = bytes[offset++];
            if (marker == 0xD9)
            {
                return hasFrame && hasScan && offset == bytes.Length;
            }
            if (marker == 0xD8 || marker == 0x00)
            {
                return false;
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }
            if (offset > bytes.Length - 2)
            {
                return false;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || offset + length > bytes.Length)
            {
                return false;
            }
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                hasFrame = true;
            }
            offset += length;
            if (marker != 0xDA)
            {
                continue;
            }
            hasScan = true;
            while (offset < bytes.Length)
            {
                if (bytes[offset] != 0xFF)
                {
                    offset++;
                    continue;
                }
                if (offset + 1 >= bytes.Length)
                {
                    return false;
                }
                var next = bytes[offset + 1];
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
                {
                    offset += 2;
                    continue;
                }
                break;
            }
        }
        return false;
    }

    private static bool ValidateGif(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14)
        {
            return false;
        }
        var offset = 13;
        if ((bytes[10] & 0x80) != 0)
        {
            offset += 3 * (1 << ((bytes[10] & 0x07) + 1));
        }
        var images = 0;
        while (offset < bytes.Length)
        {
            var introducer = bytes[offset++];
            if (introducer == 0x3B)
            {
                return images > 0 && offset == bytes.Length;
            }
            if (introducer == 0x21)
            {
                if (offset >= bytes.Length)
                {
                    return false;
                }
                offset++;
                if (!SkipGifSubBlocks(bytes, ref offset))
                {
                    return false;
                }
                continue;
            }
            if (introducer != 0x2C || offset > bytes.Length - 9)
            {
                return false;
            }
            var packed = bytes[offset + 8];
            offset += 9;
            if ((packed & 0x80) != 0)
            {
                offset += 3 * (1 << ((packed & 0x07) + 1));
            }
            if (offset >= bytes.Length)
            {
                return false;
            }
            offset++;
            if (!SkipGifSubBlocks(bytes, ref offset))
            {
                return false;
            }
            images++;
        }
        return false;
    }

    private static bool SkipGifSubBlocks(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            var size = bytes[offset++];
            if (size == 0)
            {
                return true;
            }
            if (offset > bytes.Length - size)
            {
                return false;
            }
            offset += size;
        }
        return false;
    }

    private static bool ValidateWebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes[8..12].SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) + 8L != bytes.Length)
        {
            return false;
        }
        var offset = 12;
        var imageChunk = false;
        while (offset <= bytes.Length - 8)
        {
            var type = bytes.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            var end = offset + 8L + length + (length & 1);
            if (end > bytes.Length)
            {
                return false;
            }
            imageChunk |= type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8) || type.SequenceEqual("VP8X"u8);
            offset = checked((int)end);
        }
        return imageChunk && offset == bytes.Length;
    }

    private static bool ValidateBmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || !bytes[..2].SequenceEqual("BM"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]) != bytes.Length)
        {
            return false;
        }
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        return dibSize is >= 12 and <= 124
            && 14L + dibSize <= pixelOffset
            && pixelOffset < bytes.Length;
    }

    private static bool ValidateIco(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 22 || !bytes[..4].SequenceEqual(new byte[] { 0, 0, 1, 0 }))
        {
            return false;
        }
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var directoryEnd = 6 + (count * 16);
        if (count is 0 or > 256 || directoryEnd > bytes.Length)
        {
            return false;
        }
        var ranges = new List<(uint Offset, uint End)>(count);
        for (var index = 0; index < count; index++)
        {
            var entry = bytes[(6 + (index * 16))..];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (size == 0 || offset < directoryEnd || offset + (ulong)size > (ulong)bytes.Length)
            {
                return false;
            }
            var image = bytes.Slice(checked((int)offset), checked((int)size));
            if (!ValidatePng(image) && !ValidateIcoDib(image))
            {
                return false;
            }
            ranges.Add((offset, checked(offset + size)));
        }
        ranges.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        if (ranges[0].Offset != directoryEnd)
        {
            return false;
        }
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Offset != ranges[index - 1].End)
            {
                return false;
            }
        }
        return ranges[^1].End == bytes.Length;
    }

    private static bool ValidateIcoDib(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 40)
        {
            return false;
        }
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        return headerSize is >= 40 and <= 124
            && headerSize <= bytes.Length
            && width > 0
            && height != 0;
    }

    private static string ImageAnimation(ReadOnlySpan<byte> bytes, string mimeType)
    {
        if (mimeType == "image/gif")
        {
            return "Animation status is not determined for GIF.";
        }
        if (mimeType == "image/png")
        {
            return bytes.IndexOf("acTL"u8) >= 0 ? "Animated image." : "No animation detected.";
        }
        if (mimeType == "image/webp")
        {
            return bytes.IndexOf("ANIM"u8) >= 0 ? "Animated image." : "No animation detected.";
        }
        return "No animation detected.";
    }

    private static AuthViewData? BuildAuthView(HttpMessage message)
    {
        var headers = message.Headers.Where(header => AuthHeaderNames.Contains(header.Name)).ToArray();
        if (headers.Length == 0)
        {
            return null;
        }
        var redacted = new StringBuilder();
        var full = new StringBuilder();
        foreach (var header in headers)
        {
            redacted.Append(header.Name).Append(": ").Append(RedactAuthValue(header.Value)).Append('\n');
            full.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
        }
        return new AuthViewData(redacted.ToString(), full.ToString());
    }

    private static string RedactAuthValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "[redacted]";
        }
        var separator = trimmed.IndexOfAny([' ', '\t', ',']);
        var candidate = separator < 0 ? trimmed : trimmed[..separator];
        var scheme = candidate.ToLowerInvariant() switch
        {
            "basic" => "Basic",
            "bearer" => "Bearer",
            "digest" => "Digest",
            "ntlm" => "NTLM",
            "negotiate" => "Negotiate",
            _ => null
        };
        if (scheme is null)
        {
            return "[redacted]";
        }
        if (scheme != "Digest")
        {
            return $"{scheme} [redacted]";
        }
        var parameterNames = DigestParameterNames(separator < 0 ? string.Empty : trimmed[(separator + 1)..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .Select(name => $"{name}=[redacted]")
            .ToArray();
        return parameterNames.Length == 0
            ? $"{scheme} [redacted]"
            : $"{scheme} {string.Join(", ", parameterNames)}";
    }

    private static IEnumerable<string> DigestParameterNames(string value)
    {
        var segmentStart = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length)
            {
                var character = value[index];
                if (quoted)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        quoted = false;
                    }
                    continue;
                }
                if (character == '"')
                {
                    quoted = true;
                    continue;
                }
                if (character != ',')
                {
                    continue;
                }
            }

            var segment = value.AsSpan(segmentStart, index - segmentStart).Trim();
            var equals = segment.IndexOf('=');
            var name = equals > 0 ? segment[..equals].Trim() : [];
            if (name.Length is > 0 and <= 64 && IsAuthToken(name))
            {
                yield return name.ToString();
            }
            segmentStart = index + 1;
        }
    }

    private static bool IsAuthToken(ReadOnlySpan<char> value)
    {
        if (!char.IsAsciiLetter(value[0]))
        {
            return false;
        }
        foreach (var character in value[1..])
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not '!' and not '#' and not '$' and not '%' and not '&' and not '\''
                    and not '*' and not '+' and not '-' and not '.' and not '^' and not '_' and not '`'
                    and not '|' and not '~')
            {
                return false;
            }
        }
        return true;
    }

    private static string NormalizeMediaType(string? contentType) =>
        contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;

    private static CompressedPayload? CreateCompressedPayload(
        string type,
        ReadOnlySpan<byte> json,
        int maxDecodedBytes)
    {
        if (json.Length > maxDecodedBytes)
        {
            return null;
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(json);
        }
        return new CompressedPayload(
            type,
            Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length)),
            json.Length);
    }

    private static CompressedPayload? CreateProtocolPayload(
        MapiMessageParse protocol,
        IReadOnlyList<string> warnings)
    {
        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("root");
            WriteProtocolNode(writer, protocol.Root);
            writer.WriteBoolean("complete", protocol.Complete);
            writer.WriteNumber("parsedBytes", protocol.ParsedBytes);
            writer.WriteNumber("totalBytes", protocol.TotalBytes);
            writer.WriteStartArray("warnings");
            foreach (var warning in warnings)
            {
                writer.WriteStringValue(warning);
            }
            writer.WriteEndArray();
            writer.WriteNumber("omittedWarnings", protocol.Warnings.Length - warnings.Count);
            writer.WriteEndObject();
        }
        return CreateCompressedPayload(
            "mapi-protocol",
            json.WrittenSpan,
            ProtocolPayloadMaxDecodedBytes);
    }

    private static void AppendCompressedPayloadAttributes(StringBuilder html, CompressedPayload payload)
    {
        html.Append(" data-compressed-payload=\"").Append(payload.Base64)
            .Append("\" data-payload-type=\"");
        Attribute(html, payload.Type);
        html.Append("\" data-payload-version=\"").Append(PayloadVersion)
            .Append("\" data-payload-decoded-bytes=\"").Append(payload.DecodedBytes).Append('"');
    }

    private static void WriteProtocolNode(Utf8JsonWriter writer, MapiNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);
        writer.WriteString("kind", node.Kind.ToString());
        writer.WriteNumber("offset", node.Offset);
        writer.WriteNumber("length", node.Length);
        if (node.Value is null)
        {
            writer.WriteNull("value");
        }
        else
        {
            writer.WriteString("value", node.Value);
        }
        writer.WriteStartArray("children");
        foreach (var child in node.Children)
        {
            WriteProtocolNode(writer, child);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed class TreeNode
    {
        public required string Kind { get; init; }
        public string? Name { get; init; }
        public bool IsIndex { get; init; }
        public string? Value { get; init; }
        public int? Count { get; init; }
        public int Omitted { get; init; }
        public bool Truncated { get; init; }
        public bool DepthLimited { get; init; }
        public List<TreeNode>? Attrs { get; init; }
        public List<TreeNode>? Children { get; init; }
    }

    private sealed class TreeBudget
    {
        public int NodeCount;
    }

    private static string? BuildJsonTreePayload(string formattedJson)
    {
        try
        {
            using var document = JsonDocument.Parse(formattedJson);
            var budget = new TreeBudget();
            var root = BuildJsonNode(document.RootElement, null, false, 0, budget);
            return JsonSerializer.Serialize(root, TreePayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TreeNode BuildJsonNode(JsonElement element, string? name, bool isIndex, int depth, TreeBudget budget)
    {
        budget.NodeCount++;

        if (depth >= TreeMaxDepth && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // The subtree itself is not descended into (that's the whole point of the depth
            // limit), but the immediate property/item count is cheap to report accurately here
            // (no recursion needed) so the truncated label doesn't falsely read "0 properties"/
            // "0 items".
            var immediateCount = element.ValueKind == JsonValueKind.Object
                ? element.EnumerateObject().Count()
                : element.GetArrayLength();
            return new TreeNode
            {
                Kind = element.ValueKind == JsonValueKind.Object ? "object" : "array",
                Name = name,
                IsIndex = isIndex,
                Count = immediateCount,
                DepthLimited = true
            };
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var properties = element.EnumerateObject().ToList();
                    var children = new List<TreeNode>();
                    var omitted = 0;
                    foreach (var property in properties)
                    {
                        if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
                        {
                            omitted = properties.Count - children.Count;
                            break;
                        }
                        children.Add(BuildJsonNode(property.Value, property.Name, false, depth + 1, budget));
                    }
                    return new TreeNode { Kind = "object", Name = name, IsIndex = isIndex, Count = properties.Count, Omitted = omitted, Children = children };
                }
            case JsonValueKind.Array:
                {
                    var items = element.EnumerateArray().ToList();
                    var children = new List<TreeNode>();
                    var omitted = 0;
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
                        {
                            omitted = items.Count - children.Count;
                            break;
                        }
                        children.Add(BuildJsonNode(items[i], i.ToString(CultureInfo.InvariantCulture), true, depth + 1, budget));
                    }
                    return new TreeNode { Kind = "array", Name = name, IsIndex = isIndex, Count = items.Count, Omitted = omitted, Children = children };
                }
            case JsonValueKind.String:
                {
                    var (value, truncated) = BoundScalar(element.GetString() ?? string.Empty);
                    return new TreeNode { Kind = "string", Name = name, IsIndex = isIndex, Value = value, Truncated = truncated };
                }
            case JsonValueKind.Number:
                {
                    var (value, truncated) = BoundScalar(element.GetRawText());
                    return new TreeNode { Kind = "number", Name = name, IsIndex = isIndex, Value = value, Truncated = truncated };
                }
            case JsonValueKind.True:
            case JsonValueKind.False:
                return new TreeNode { Kind = "boolean", Name = name, IsIndex = isIndex, Value = element.GetRawText() };
            default:
                return new TreeNode { Kind = "null", Name = name, IsIndex = isIndex, Value = "null" };
        }
    }

    private static (string Value, bool Truncated) BoundScalar(string raw) =>
        raw.Length <= TreeMaxScalarLength ? (raw, false) : (raw[..TreeMaxScalarLength], true);

    private static string? BuildXmlTreePayload(string formattedXml)
    {
        try
        {
            using var textReader = new StringReader(formattedXml);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = XmlReader.Create(textReader, settings);
            var budget = new TreeBudget();
            var children = new List<TreeNode>();
            var count = 0;
            var omitted = 0;
            while (reader.Read())
            {
                if (!IsRenderableXmlNode(reader))
                {
                    continue;
                }
                count++;
                if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
                {
                    omitted++;
                    if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                    {
                        SkipElementSubtree(reader);
                    }
                    continue;
                }
                children.Add(BuildXmlNode(reader, 0, budget));
            }
            var root = new TreeNode { Kind = "document", Count = count, Omitted = omitted, Children = children };
            return JsonSerializer.Serialize(root, TreePayloadOptions);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static TreeNode BuildXmlNode(XmlReader reader, int depth, TreeBudget budget)
    {
        budget.NodeCount++;
        switch (reader.NodeType)
        {
            case XmlNodeType.Comment:
                {
                    var (value, truncated) = BoundScalar(reader.Value);
                    return new TreeNode { Kind = "comment", Value = value, Truncated = truncated };
                }
            case XmlNodeType.CDATA:
                {
                    var (value, truncated) = BoundScalar(reader.Value);
                    return new TreeNode { Kind = "cdata", Value = value, Truncated = truncated };
                }
            case XmlNodeType.Text:
            case XmlNodeType.SignificantWhitespace:
                {
                    var (value, truncated) = BoundScalar(reader.Value);
                    return new TreeNode { Kind = "text", Value = value, Truncated = truncated };
                }
            case XmlNodeType.Element:
                return BuildXmlElement(reader, depth, budget);
            default:
                return new TreeNode { Kind = "unknown" };
        }
    }

    private static TreeNode BuildXmlElement(XmlReader reader, int depth, TreeBudget budget)
    {
        var name = string.IsNullOrEmpty(reader.Prefix) ? reader.LocalName : $"{reader.Prefix}:{reader.LocalName}";
        List<TreeNode>? attrs = null;
        if (reader.HasAttributes)
        {
            attrs = new List<TreeNode>();
            reader.MoveToFirstAttribute();
            do
            {
                var attrName = string.IsNullOrEmpty(reader.Prefix) ? reader.LocalName : $"{reader.Prefix}:{reader.LocalName}";
                var (attrValue, attrTruncated) = BoundScalar(reader.Value);
                attrs.Add(new TreeNode { Kind = "attribute", Name = attrName, Value = attrValue, Truncated = attrTruncated });
            } while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            return new TreeNode { Kind = "element", Name = name, Attrs = attrs, Count = 0, Children = [] };
        }

        if (depth >= TreeMaxDepth)
        {
            // As with the JSON depth limit, report the true immediate child count while skipping
            // the (not descended into) subtree, instead of leaving it unset and rendering a
            // misleading "0 children" label.
            var immediateCount = SkipElementSubtreeCountingImmediateChildren(reader);
            return new TreeNode { Kind = "element", Name = name, Attrs = attrs, Count = immediateCount, DepthLimited = true };
        }

        var children = new List<TreeNode>();
        var count = 0;
        var omitted = 0;
        while (reader.Read() && reader.NodeType != XmlNodeType.EndElement)
        {
            if (!IsRenderableXmlNode(reader))
            {
                continue;
            }
            count++;
            if (children.Count >= TreeMaxChildrenPerNode || budget.NodeCount >= TreeMaxNodes)
            {
                omitted++;
                if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
                {
                    SkipElementSubtree(reader);
                }
                continue;
            }
            children.Add(BuildXmlNode(reader, depth + 1, budget));
        }

        return new TreeNode { Kind = "element", Name = name, Attrs = attrs, Count = count, Omitted = omitted, Children = children };
    }

    private static void SkipElementSubtree(XmlReader reader)
    {
        var depth = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
            {
                depth++;
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (depth == 0)
                {
                    return;
                }
                depth--;
            }
        }
    }

    private static int SkipElementSubtreeCountingImmediateChildren(XmlReader reader)
    {
        var depth = 0;
        var immediateCount = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (depth == 0)
                {
                    return immediateCount;
                }
                depth--;
                continue;
            }
            if (depth == 0 && IsRenderableXmlNode(reader))
            {
                immediateCount++;
            }
            if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
            {
                depth++;
            }
        }
        return immediateCount;
    }

    private static bool IsRenderableXmlNode(XmlReader reader) => reader.NodeType switch
    {
        XmlNodeType.Element or XmlNodeType.Comment or XmlNodeType.CDATA => true,
        // Without a DTD/schema, XmlReader cannot tell whether inter-element whitespace is
        // significant, so pretty-printed indentation is reported as (Significant)Whitespace.
        // Skip whitespace-only text so it doesn't flood the tree with indentation noise;
        // Pretty Text remains available for exact formatting fidelity.
        XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace => !string.IsNullOrWhiteSpace(reader.Value),
        _ => false
    };

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) ?? "-";

    private static void AppendHttpTimestamp(StringBuilder html, DateTimeOffset? value)
    {
        if (value is null)
        {
            html.Append('-');
            return;
        }

        var fullTimestamp = FormatTimestamp(value);
        html.Append("<time datetime=\"");
        Attribute(html, value.Value.ToString("O", CultureInfo.InvariantCulture));
        html.Append("\" title=\"Captured timestamp: ");
        Attribute(html, fullTimestamp);
        html.Append("\" aria-label=\"Captured timestamp ");
        Attribute(html, fullTimestamp);
        html.Append("\">");
        Text(html, value.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        html.Append("</time>");
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{size.ToString("N1", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static void Text(StringBuilder html, string? value) =>
        html.Append(WebUtility.HtmlEncode(value ?? string.Empty));

    private static void Attribute(StringBuilder html, string? value) =>
        html.Append(WebUtility.HtmlEncode(value ?? string.Empty));
}
