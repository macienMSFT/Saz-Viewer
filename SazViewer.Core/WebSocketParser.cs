using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SazViewer.Core;

internal static class WebSocketParser
{
    private const int MaxHeaderBlockBytes = 64 * 1024;
    private const int MaxFramePrefixBytes = 64 * 1024;

    public static void Parse(
        ZipArchiveEntry entry,
        string sessionId,
        int archiveOrder,
        List<WebSocketMessage> messages,
        List<string> warnings)
    {
        try
        {
            using var source = entry.Open();
            var reader = new RecordReader(source);
            var recordIndex = 0;
            var firstBlock = true;

            while (true)
            {
                var headerResult = reader.ReadHeaderBlock(MaxHeaderBlockBytes);
                if (headerResult.EndOfStream)
                {
                    break;
                }

                if (!headerResult.Terminated)
                {
                    AddUndecoded(
                        messages,
                        sessionId,
                        recordIndex,
                        SourceOrder(archiveOrder, recordIndex),
                        entry.Length - reader.BytesRead + headerResult.Bytes.Length,
                        HeaderPreview(headerResult.Bytes),
                        "WebSocket header block is unterminated; the remaining bytes cannot be safely framed.");
                    warnings.Add("WebSocket header block is unterminated; parsing stopped.");
                    break;
                }

                var headers = ParseHeaders(headerResult.Bytes, out var headerWarning);
                var requestLength = Header(headers, "Request-Length");
                var responseLength = Header(headers, "Response-Length");
                var lengthValue = requestLength ?? responseLength;
                if (firstBlock && lengthValue is null)
                {
                    firstBlock = false;
                    if (headerWarning is not null)
                    {
                        warnings.Add($"WebSocket file header: {headerWarning}");
                    }
                    continue;
                }

                firstBlock = false;
                if (lengthValue is null)
                {
                    var remaining = Math.Max(0, entry.Length - reader.BytesRead);
                    var warning = "WebSocket record has no Request-Length or Response-Length; the remaining mixed binary stream cannot be safely resynchronized.";
                    AddUndecoded(
                        messages,
                        sessionId,
                        recordIndex,
                        SourceOrder(archiveOrder, recordIndex),
                        headerResult.Bytes.Length + remaining,
                        HeaderPreview(headerResult.Bytes),
                        warning);
                    warnings.Add(warning);
                    break;
                }

                if (!ulong.TryParse(lengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out var unsignedLength)
                    || unsignedLength > long.MaxValue)
                {
                    var warning = $"WebSocket record has invalid declared length '{Truncate(lengthValue, 80)}'; parsing stopped.";
                    AddUndecoded(
                        messages,
                        sessionId,
                        recordIndex,
                        SourceOrder(archiveOrder, recordIndex),
                        Math.Max(0, entry.Length - reader.BytesRead),
                        HeaderPreview(headerResult.Bytes),
                        warning);
                    warnings.Add(warning);
                    break;
                }

                var recordLength = (long)unsignedLength;
                var frameRead = reader.ReadPrefixAndDiscard(recordLength, MaxFramePrefixBytes);
                var frame = ParseFrame(
                    frameRead.Prefix,
                    recordLength,
                    frameRead.BytesRead,
                    headers,
                    sessionId,
                    recordIndex,
                    SourceOrder(archiveOrder, recordIndex),
                    requestLength is not null ? "Client" : "Server",
                    headerWarning);
                messages.Add(frame);
                if (frame.Warning is not null)
                {
                    warnings.Add($"WebSocket record {recordIndex}: {frame.Warning}");
                }

                recordIndex++;
                if (frameRead.BytesRead != recordLength)
                {
                    warnings.Add(
                        $"WebSocket record {recordIndex - 1} is truncated: expected {recordLength:N0} bytes, found {frameRead.BytesRead:N0}.");
                    break;
                }

                var delimiter = reader.ReadRecordDelimiter();
                if (delimiter == DelimiterResult.EndOfStream)
                {
                    warnings.Add($"WebSocket record {recordIndex - 1} is missing its trailing CRLF.");
                    break;
                }

                if (delimiter == DelimiterResult.Missing)
                {
                    warnings.Add($"WebSocket record {recordIndex - 1} is missing its trailing CRLF; parsing continued defensively.");
                }
            }

            if (recordIndex == 0 && messages.All(message => message.SessionId != sessionId))
            {
                warnings.Add($"WebSocket entry '{entry.FullName}' contained no decodable records.");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            warnings.Add($"WebSocket entry '{entry.FullName}' could not be read: {exception.Message}");
        }
    }

    private static WebSocketMessage ParseFrame(
        byte[] bytes,
        long outerLength,
        long actualLength,
        IReadOnlyList<HttpHeader> headers,
        string sessionId,
        int recordIndex,
        long sourceOrder,
        string direction,
        string? headerWarning)
    {
        var diagnostics = new List<string>();
        if (headerWarning is not null)
        {
            diagnostics.Add(headerWarning);
        }
        if (Header(headers, "Request-Length") is not null
            && Header(headers, "Response-Length") is not null)
        {
            diagnostics.Add("record declares both Request-Length and Response-Length; request direction was preferred");
        }

        var timestamp = ParseTimestamp(headers);
        if (actualLength != outerLength)
        {
            diagnostics.Add($"truncated outer record (declared {outerLength:N0}, found {actualLength:N0} bytes)");
        }

        if (bytes.Length < 2)
        {
            diagnostics.Add("record is too short for an RFC 6455 frame header");
            return UndecodedFrame(bytes, outerLength, sessionId, recordIndex, sourceOrder, timestamp, direction, diagnostics);
        }

        var first = bytes[0];
        var second = bytes[1];
        var final = (first & 0x80) != 0;
        var rsv = (first >> 4) & 0x07;
        var opcode = first & 0x0F;
        var masked = (second & 0x80) != 0;
        ulong payloadLength = (uint)(second & 0x7F);
        var offset = 2;

        if (payloadLength == 126)
        {
            if (bytes.Length < 4)
            {
                diagnostics.Add("record is truncated in the 16-bit payload length");
                return UndecodedFrame(bytes, outerLength, sessionId, recordIndex, sourceOrder, timestamp, direction, diagnostics);
            }
            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
            offset = 4;
            if (payloadLength < 126)
            {
                diagnostics.Add("payload length uses a non-minimal 16-bit encoding");
            }
        }
        else if (payloadLength == 127)
        {
            if (bytes.Length < 10)
            {
                diagnostics.Add("record is truncated in the 64-bit payload length");
                return UndecodedFrame(bytes, outerLength, sessionId, recordIndex, sourceOrder, timestamp, direction, diagnostics);
            }
            payloadLength = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(2, 8));
            offset = 10;
            if ((payloadLength & (1UL << 63)) != 0)
            {
                diagnostics.Add("64-bit payload length has the forbidden high bit set");
            }
            if (payloadLength <= ushort.MaxValue)
            {
                diagnostics.Add("payload length uses a non-minimal 64-bit encoding");
            }
        }

        byte[]? maskingKey = null;
        if (masked)
        {
            if (bytes.Length < offset + 4)
            {
                diagnostics.Add("record is truncated in the masking key");
                return UndecodedFrame(bytes, outerLength, sessionId, recordIndex, sourceOrder, timestamp, direction, diagnostics);
            }
            maskingKey = bytes.AsSpan(offset, 4).ToArray();
            offset += 4;
        }

        ulong expectedLength;
        try
        {
            expectedLength = checked((ulong)offset + payloadLength);
        }
        catch (OverflowException)
        {
            expectedLength = ulong.MaxValue;
            diagnostics.Add("frame length overflows the supported range");
        }

        if (expectedLength != (ulong)outerLength)
        {
            diagnostics.Add($"inner frame length {expectedLength:N0} does not match outer declared length {outerLength:N0}");
        }

        if (rsv != 0)
        {
            diagnostics.Add($"RSV bits are set ({rsv}); extension decoding is unavailable");
        }

        var control = opcode >= 8;
        if (control && !final)
        {
            diagnostics.Add("control frame is fragmented");
        }
        if (control && payloadLength > 125)
        {
            diagnostics.Add("control frame payload exceeds 125 bytes");
        }
        if (direction == "Client" && !masked)
        {
            diagnostics.Add("client-to-server frame is unexpectedly unmasked");
        }
        else if (direction == "Server" && masked)
        {
            diagnostics.Add("server-to-client frame is unexpectedly masked");
        }

        var availablePayload = bytes.Length > offset ? bytes.AsSpan(offset) : ReadOnlySpan<byte>.Empty;
        var previewLength = (int)Math.Min((ulong)availablePayload.Length, payloadLength);
        var previewBytes = availablePayload[..previewLength].ToArray();
        if (maskingKey is not null)
        {
            for (var i = 0; i < previewBytes.Length; i++)
            {
                previewBytes[i] ^= maskingKey[i % 4];
            }
        }

        var type = OpcodeName(opcode);
        var isText = opcode == 1 && final && rsv == 0;
        var previewTruncated = (ulong)previewBytes.Length < payloadLength;
        if (previewTruncated)
        {
            diagnostics.Add($"payload preview is limited to {previewBytes.Length:N0} bytes");
        }
        var preview = isText
            ? DecodeTextOrHex(previewBytes, previewTruncated, diagnostics)
            : HttpMessageParser.HexPreview(previewBytes);

        if (opcode == 1 && !final)
        {
            diagnostics.Add("fragmented text is shown as hex because UTF-8 may span frames");
        }
        if (opcode == 8)
        {
            ValidateClosePayload(previewBytes, diagnostics);
        }
        if (opcode is 3 or 4 or 5 or 6 or 7 or 11 or 12 or 13 or 14 or 15)
        {
            diagnostics.Add($"reserved opcode 0x{opcode:X}");
        }

        return new WebSocketMessage
        {
            SessionId = sessionId,
            RecordIndex = recordIndex,
            Timestamp = timestamp,
            Direction = direction,
            Type = final ? type : $"{type} (fragment)",
            PayloadLength = payloadLength > long.MaxValue ? long.MaxValue : (long)payloadLength,
            Preview = preview,
            IsBinary = !isText,
            IsDecoded = actualLength == outerLength && expectedLength == (ulong)outerLength,
            Warning = JoinDiagnostics(diagnostics),
            SourceOrder = sourceOrder
        };
    }

    private static void ValidateClosePayload(byte[] payload, List<string> diagnostics)
    {
        if (payload.Length == 1)
        {
            diagnostics.Add("close frame payload has invalid length 1");
        }
        else if (payload.Length >= 2)
        {
            var code = BinaryPrimitives.ReadUInt16BigEndian(payload);
            if (!IsValidCloseCode(code))
            {
                diagnostics.Add($"close frame uses invalid or reserved status code {code}");
            }
            if (payload.Length > 2)
            {
                try
                {
                    _ = new UTF8Encoding(false, true).GetString(payload, 2, payload.Length - 2);
                }
                catch (DecoderFallbackException)
                {
                    diagnostics.Add("close reason is not valid UTF-8");
                }
            }
        }
    }

    private static bool IsValidCloseCode(ushort code) =>
        code is >= 1000 and <= 1014 && code is not (1004 or 1005 or 1006)
        || code is >= 3000 and <= 4999;

    private static string DecodeTextOrHex(
        byte[] payload,
        bool previewTruncated,
        List<string> diagnostics)
    {
        var encoding = new UTF8Encoding(false, true);
        var maxTrim = previewTruncated ? Math.Min(3, payload.Length) : 0;
        for (var trim = 0; trim <= maxTrim; trim++)
        {
            try
            {
                return encoding.GetString(payload, 0, payload.Length - trim);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        diagnostics.Add("text payload is not valid UTF-8 and is shown as hex");
        return HttpMessageParser.HexPreview(payload);
    }

    private static WebSocketMessage UndecodedFrame(
        byte[] bytes,
        long outerLength,
        string sessionId,
        int recordIndex,
        long sourceOrder,
        DateTimeOffset? timestamp,
        string direction,
        List<string> diagnostics) =>
        new()
        {
            SessionId = sessionId,
            RecordIndex = recordIndex,
            Timestamp = timestamp,
            Direction = direction,
            Type = "Undecoded",
            PayloadLength = outerLength,
            Preview = HttpMessageParser.HexPreview(bytes),
            IsBinary = true,
            IsDecoded = false,
            Warning = JoinDiagnostics(diagnostics),
            SourceOrder = sourceOrder
        };

    private static void AddUndecoded(
        List<WebSocketMessage> messages,
        string sessionId,
        int recordIndex,
        long sourceOrder,
        long length,
        string preview,
        string warning) =>
        messages.Add(new WebSocketMessage
        {
            SessionId = sessionId,
            RecordIndex = recordIndex,
            Direction = "Unknown",
            Type = "Undecoded",
            PayloadLength = Math.Max(0, length),
            Preview = preview,
            IsBinary = true,
            IsDecoded = false,
            Warning = warning,
            SourceOrder = sourceOrder
        });

    private static long SourceOrder(int archiveOrder, int recordIndex) =>
        ((long)archiveOrder << 32) | (uint)recordIndex;

    private static List<HttpHeader> ParseHeaders(byte[] bytes, out string? warning)
    {
        var diagnostics = new List<string>();
        var text = Encoding.Latin1.GetString(bytes);
        if (text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            text = text[..^4];
        }
        else if (text.EndsWith("\n\n", StringComparison.Ordinal))
        {
            text = text[..^2];
            diagnostics.Add("header block used LF-only delimiters");
        }

        var headers = new List<HttpHeader>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diagnostics.Add($"ignored malformed pseudo-header '{Truncate(line, 80)}'");
                continue;
            }
            headers.Add(new HttpHeader(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        warning = JoinDiagnostics(diagnostics);
        return headers;
    }

    private static DateTimeOffset? ParseTimestamp(IReadOnlyList<HttpHeader> headers)
    {
        foreach (var name in new[] { "DoneRead", "BeginSend", "DoneSend" })
        {
            var value = Header(headers, name);
            if (value is not null && SazParser.TryParseTimestamp(value, out var timestamp))
            {
                return timestamp;
            }
        }
        return null;
    }

    private static string? Header(IReadOnlyList<HttpHeader> headers, string name) =>
        headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string HeaderPreview(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        return text.Length <= 4096 ? text : text[..4096] + "\n[header preview truncated]";
    }

    private static string OpcodeName(int opcode) => opcode switch
    {
        0 => "Continuation",
        1 => "Text",
        2 => "Binary",
        8 => "Close",
        9 => "Ping",
        10 => "Pong",
        _ => $"Reserved (0x{opcode:X})"
    };

    private static string? JoinDiagnostics(List<string> diagnostics) =>
        diagnostics.Count == 0 ? null : string.Join("; ", diagnostics);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "...";

    private enum DelimiterResult
    {
        Present,
        Missing,
        EndOfStream
    }

    private sealed class RecordReader(Stream stream)
    {
        private readonly Stack<byte> pushback = new(2);

        public long BytesRead { get; private set; }

        public HeaderReadResult ReadHeaderBlock(int limit)
        {
            using var output = new MemoryStream();
            var previous = -1;
            var beforePrevious = -1;
            var thirdPrevious = -1;
            while (output.Length < limit)
            {
                var current = ReadByte();
                if (current < 0)
                {
                    return new HeaderReadResult(output.ToArray(), false, output.Length == 0);
                }
                output.WriteByte((byte)current);
                if ((thirdPrevious == '\r' && beforePrevious == '\n' && previous == '\r' && current == '\n')
                    || (previous == '\n' && current == '\n'))
                {
                    return new HeaderReadResult(output.ToArray(), true, false);
                }
                thirdPrevious = beforePrevious;
                beforePrevious = previous;
                previous = current;
            }
            return new HeaderReadResult(output.ToArray(), false, false);
        }

        public FrameReadResult ReadPrefixAndDiscard(long length, int prefixLimit)
        {
            using var prefix = new MemoryStream((int)Math.Min(length, prefixLimit));
            var buffer = new byte[16 * 1024];
            long total = 0;
            while (total < length)
            {
                var wanted = (int)Math.Min(buffer.Length, length - total);
                var read = Read(buffer.AsSpan(0, wanted));
                if (read == 0)
                {
                    break;
                }
                var prefixWanted = Math.Min(read, prefixLimit - (int)prefix.Length);
                if (prefixWanted > 0)
                {
                    prefix.Write(buffer, 0, prefixWanted);
                }
                total += read;
            }
            return new FrameReadResult(prefix.ToArray(), total);
        }

        public DelimiterResult ReadRecordDelimiter()
        {
            var first = ReadByte();
            if (first < 0)
            {
                return DelimiterResult.EndOfStream;
            }
            var second = ReadByte();
            if (first == '\r' && second == '\n')
            {
                return DelimiterResult.Present;
            }
            if (first == '\n')
            {
                if (second >= 0) PushBack((byte)second);
                return DelimiterResult.Present;
            }
            if (second >= 0) PushBack((byte)second);
            PushBack((byte)first);
            return DelimiterResult.Missing;
        }

        private int ReadByte()
        {
            if (pushback.TryPop(out var value))
            {
                return value;
            }
            var result = stream.ReadByte();
            if (result >= 0) BytesRead++;
            return result;
        }

        private int Read(Span<byte> buffer)
        {
            var offset = 0;
            while (pushback.TryPop(out var value) && offset < buffer.Length)
            {
                buffer[offset++] = value;
            }
            if (offset < buffer.Length)
            {
                var read = stream.Read(buffer[offset..]);
                offset += read;
                BytesRead += read;
            }
            return offset;
        }

        private void PushBack(byte value) => pushback.Push(value);
    }

    private sealed record HeaderReadResult(byte[] Bytes, bool Terminated, bool EndOfStream);
    private sealed record FrameReadResult(byte[] Prefix, long BytesRead);
}
