using System.Globalization;
using System.Text;

namespace SazViewer.Core;

internal static class HttpMessageParser
{
    internal const int MaxEntryRead = MapiParseLimits.MaxPayloadBytes + (128 * 1024);
    internal const int MaxBodyPreview = 64 * 1024;

    public static HttpMessage? Parse(Stream stream, long entryLength, string label, List<string> warnings)
    {
        var bytes = ReadAtMost(stream, MaxEntryRead);
        var boundary = FindHeaderBoundary(bytes);
        if (boundary < 0)
        {
            warnings.Add($"{label}: no HTTP header/body boundary was found in the first {bytes.Length:N0} bytes.");
            return ParseHeaderOnly(bytes, entryLength, label, warnings);
        }

        var headerBytes = bytes.AsSpan(0, boundary);
        var bodyOffset = boundary + (bytes[boundary] == (byte)'\r' ? 4 : 2);
        var bodyLength = Math.Max(0, entryLength - bodyOffset);
        var availableBody = bytes.AsSpan(bodyOffset, Math.Max(0, bytes.Length - bodyOffset));
        var lines = DecodeHeaders(headerBytes, label, warnings);
        if (lines.Count == 0)
        {
            warnings.Add($"{label}: HTTP start line is empty.");
            return null;
        }

        var headers = ParseHeaders(lines, label, warnings);
        var retainNormalizedBody = IsMapiCandidate(headers);
        var message = new HttpMessage
        {
            StartLine = lines[0],
            Body = HttpBodyDecoder.CreatePreview(
                availableBody,
                bodyLength,
                headers,
                label,
                warnings,
                retainNormalizedBody)
        };
        message.Headers.AddRange(headers);
        return message;
    }

    private static bool IsMapiCandidate(IReadOnlyList<HttpHeader> headers) =>
        headers.Any(header =>
            header.Name.Equals("X-RequestType", StringComparison.OrdinalIgnoreCase)
            || header.Name.Equals("X-ResponseCode", StringComparison.OrdinalIgnoreCase)
            || (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                && header.Value.Split(';', 2)[0].Trim()
                    .Equals("application/mapi-http", StringComparison.OrdinalIgnoreCase)));

    private static HttpMessage? ParseHeaderOnly(
        byte[] bytes,
        long entryLength,
        string label,
        List<string> warnings)
    {
        var lines = DecodeHeaders(bytes, label, warnings);
        if (lines.Count == 0)
        {
            return null;
        }

        var headers = ParseHeaders(lines, label, warnings);
        var message = new HttpMessage
        {
            StartLine = lines[0],
            Body = new BodyPreview
            {
                Length = 0,
                CapturedLength = 0,
                Preview = string.Empty,
                IsTruncated = entryLength > bytes.Length
            }
        };
        message.Headers.AddRange(headers);
        return message;
    }

    private static List<HttpHeader> ParseHeaders(
        IReadOnlyList<string> lines,
        string label,
        List<string> warnings)
    {
        var headers = new List<HttpHeader>();
        HttpHeader? previous = null;
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && previous is not null)
            {
                headers[^1] = previous = previous with { Value = $"{previous.Value} {line.Trim()}" };
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                if (line.Length > 0)
                {
                    warnings.Add($"{label}: ignored malformed header line '{Truncate(line, 120)}'.");
                }

                continue;
            }

            previous = new HttpHeader(line[..colon].Trim(), line[(colon + 1)..].Trim());
            headers.Add(previous);
        }
        return headers;
    }

    private static List<string> DecodeHeaders(
        ReadOnlySpan<byte> bytes,
        string label,
        List<string> warnings)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[0].Length > 16 * 1024)
        {
            warnings.Add($"{label}: unusually long HTTP start line.");
        }

        return lines;
    }

    internal static BodyPreview CreateBodyPreview(
        ReadOnlySpan<byte> bytes,
        long bodyLength,
        long capturedLength,
        string? contentType)
    {
        var charset = ParseCharset(contentType);
        var textLike = IsTextContentType(contentType) || LooksLikeText(bytes);
        if (textLike && TryGetSafeEncoding(charset, out var encoding))
        {
            try
            {
                return new BodyPreview
                {
                    Length = bodyLength,
                    CapturedLength = capturedLength,
                    IsBinary = false,
                    IsTruncated = bodyLength > bytes.Length,
                    Charset = encoding.WebName,
                    Preview = DecodeTextPreview(bytes, encoding, bodyLength > bytes.Length)
                };
            }
            catch (DecoderFallbackException)
            {
                // A textual content type with invalid bytes is safer to preserve as hex.
            }
        }

        return new BodyPreview
        {
            Length = bodyLength,
            CapturedLength = capturedLength,
            IsBinary = true,
            IsTruncated = bodyLength > bytes.Length,
            Charset = charset,
            Preview = HexPreview(bytes)
        };
    }

    private static bool TryGetSafeEncoding(string? charset, out Encoding encoding)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            encoding = new UTF8Encoding(false, true);
            return true;
        }

        var normalized = charset.Trim().Trim('"', '\'').ToLowerInvariant();
        encoding = normalized switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false, true),
            "us-ascii" or "ascii" => Encoding.GetEncoding(
                Encoding.ASCII.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback),
            "iso-8859-1" or "latin1" or "latin-1" => Encoding.Latin1,
            "utf-16" or "unicode" => new UnicodeEncoding(false, true, true),
            "utf-16be" => new UnicodeEncoding(true, true, true),
            _ => null!
        };
        return encoding is not null;
    }

    private static string DecodeTextPreview(
        ReadOnlySpan<byte> bytes,
        Encoding encoding,
        bool truncated)
    {
        if (!truncated)
        {
            return encoding.GetString(bytes);
        }

        DecoderFallbackException? failure = null;
        for (var trim = 0; trim <= Math.Min(4, bytes.Length); trim++)
        {
            try
            {
                return encoding.GetString(bytes[..(bytes.Length - trim)]);
            }
            catch (DecoderFallbackException exception)
            {
                failure = exception;
            }
        }

        throw failure!;
    }

    private static string? ParseCharset(string? contentType)
    {
        if (contentType is null)
        {
            return null;
        }

        foreach (var part in contentType.Split(';').Skip(1))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    private static bool IsTextContentType(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("graphql", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        var sample = bytes[..Math.Min(bytes.Length, 4096)];
        var controlCount = 0;
        foreach (var value in sample)
        {
            if (value == 0)
            {
                return false;
            }

            if (value < 0x09 || value is > 0x0D and < 0x20)
            {
                controlCount++;
            }
        }

        return controlCount * 20 < sample.Length;
    }

    internal static string HexPreview(ReadOnlySpan<byte> bytes)
    {
        const int bytesPerLine = 16;
        var builder = new StringBuilder();
        for (var offset = 0; offset < bytes.Length; offset += bytesPerLine)
        {
            var line = bytes.Slice(offset, Math.Min(bytesPerLine, bytes.Length - offset));
            builder.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < bytesPerLine; i++)
            {
                builder.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) : "  ");
                builder.Append(i == 7 ? "  " : " ");
            }

            builder.Append(" |");
            foreach (var value in line)
            {
                builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }

            builder.AppendLine("|");
        }

        return builder.ToString();
    }

    private static int FindHeaderBoundary(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i;
            }
        }

        for (var i = 0; i <= bytes.Length - 2; i++)
        {
            if (bytes[i] == '\n' && bytes[i + 1] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] ReadAtMost(Stream stream, int limit)
    {
        using var output = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (output.Length < limit)
        {
            var wanted = (int)Math.Min(buffer.Length, limit - output.Length);
            var read = stream.Read(buffer, 0, wanted);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "...";
}
