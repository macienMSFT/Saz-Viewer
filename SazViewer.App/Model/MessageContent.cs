using System.Globalization;
using System.Text;
using SazViewer.Core;

namespace SazViewer.App.Model;

/// <summary>
/// The display model of one HTTP request or response: a native port of the report's per-message payload
/// (<c>BuildMessagePayload</c>) and of the JavaScript helpers that turn it into Headers/Raw text. Built on
/// demand when a session is selected; nothing here is computed when the capture is opened.
/// </summary>
internal sealed class MessageContent
{
    public const int MaxCopyCharacters = HtmlReportGenerator.MaxCopyCharacters;
    public const int DisplayLimit = HtmlReportGenerator.MaxHydratedDisplayCharacters;
    public const string BodyTruncatedMarker = "[Body preview truncated; the complete body is not retained in this report.]";
    private const int MaxFallbackTextCharacters = 65536;

    private MessageContent(HttpMessage message, BodyPresentation presentation)
    {
        Message = message;
        Presentation = presentation;
    }

    public HttpMessage Message { get; }

    public string StartLine => Message.StartLine;

    public IReadOnlyList<HttpHeader> Headers => Message.Headers;

    public BodyPreview Body => Message.Body;

    public BodyPresentation Presentation { get; }

    /// <summary>Captured body bytes (pre-decode when content codings were removed).</summary>
    public ReadOnlyMemory<byte> Captured { get; private init; }

    /// <summary>Decoded body bytes, or null when no decoding happened (or it produced the same bytes).</summary>
    public ReadOnlyMemory<byte>? Decoded { get; private init; }

    public string? FallbackText { get; private init; }

    public string? FallbackCapturedText { get; private init; }

    public bool? FallbackCapturedTruncated { get; private init; }

    public bool IsTruncated { get; private init; }

    public bool ShowCapturedBytes => Body.CapturedBytesPreview is not null;

    public HtmlReportGenerator.ImageViewInfo? Image { get; private init; }

    public SafeHtmlPreview? HtmlPreview { get; private init; }

    public bool HasAuth { get; private init; }

    public string Label => Presentation.Label;

    public string Status => Presentation.Status;

    public bool HasRemovedEncodings => Body.RemovedEncodings.Count > 0;

    public static MessageContent? Create(HttpMessage? message, BodyFormatter formatter)
    {
        if (message is null)
        {
            return null;
        }
        var body = message.Body;
        var captured = body.SourceIsDecoded
            ? ReadOnlyMemory<byte>.Empty
            : body.WasDecoded
            ? body.CapturedBytes
            : !body.DecodedBytes.IsEmpty ? body.DecodedBytes : body.CapturedBytes;
        var decoded = body.SourceIsDecoded || body.WasDecoded ? body.DecodedBytes : ReadOnlyMemory<byte>.Empty;
        if (!decoded.IsEmpty && decoded.Span.SequenceEqual(captured.Span))
        {
            decoded = ReadOnlyMemory<byte>.Empty;
        }
        var fallbackText = captured.IsEmpty && body.Preview.Length > 0
            ? body.Preview[..Math.Min(body.Preview.Length, MaxFallbackTextCharacters)]
            : null;
        var fallbackCapturedText = captured.IsEmpty && body.CapturedBytesPreview is not null
            ? body.CapturedBytesPreview[..Math.Min(body.CapturedBytesPreview.Length, DisplayLimit)]
            : null;
        return new MessageContent(message, formatter.Format(body, message.Header("Content-Type")))
        {
            Captured = captured,
            Decoded = decoded.IsEmpty ? (ReadOnlyMemory<byte>?)null : decoded,
            FallbackText = fallbackText,
            FallbackCapturedText = fallbackCapturedText,
            FallbackCapturedTruncated = captured.IsEmpty && body.CapturedBytesPreview is not null
                ? body.CapturedBytesPreviewTruncated || fallbackCapturedText!.Length < body.CapturedBytesPreview.Length
                : null,
            IsTruncated = body.IsTruncated || (fallbackText is not null && fallbackText.Length < body.Preview.Length),
            Image = HtmlReportGenerator.DetectImageView(message),
            HtmlPreview = SafeHtmlPreviewBuilder.TryCreate(message),
            HasAuth = HtmlReportGenerator.BuildAuthView(message) is not null
        };
    }

    /// <summary>The decoded body when present, else the captured bytes (the report's <c>messageBodyBytes</c>).</summary>
    public ReadOnlyMemory<byte> BodyBytes => Decoded ?? Captured;

    /// <summary>Port of the report's <c>decodeBodyText</c>; throws <see cref="InvalidDataException"/> when the body can't be decoded.</summary>
    public string DecodeBodyText()
    {
        if (FallbackText is not null)
        {
            return FallbackText;
        }
        var bytes = BodyBytes.Span;
        if (Body.IsBinary)
        {
            return TextFormatting.RawHexPreview(bytes);
        }
        var charset = (Body.Charset ?? "utf-8").Trim().ToLowerInvariant();
        if (charset is "iso-8859-1" or "latin1" or "latin-1")
        {
            return Encoding.Latin1.GetString(bytes);
        }
        if (charset is "us-ascii" or "ascii")
        {
            foreach (var value in bytes)
            {
                if (value > 127)
                {
                    throw new InvalidDataException("retained textual body is not valid US-ASCII");
                }
            }
            return Encoding.ASCII.GetString(bytes);
        }
        Encoding encoding = charset switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false, true),
            "utf-16" or "utf-16le" => new UnicodeEncoding(false, false, true),
            "utf-16be" => new UnicodeEncoding(true, false, true),
            _ => throw new InvalidDataException("retained textual body uses an unsupported charset")
        };
        var maxTrim = Math.Min(IsTruncated ? 4 : 0, bytes.Length);
        for (var trim = 0; trim <= maxTrim; trim++)
        {
            try
            {
                var text = encoding.GetString(bytes[..(bytes.Length - trim)]);
                // TextDecoder drops a leading byte order mark by default.
                return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
            }
            catch (DecoderFallbackException)
            {
            }
        }
        throw new InvalidDataException("retained textual body could not be decoded");
    }

    /// <summary>Start line plus headers, or null when it exceeds the copy limit.</summary>
    public string? FullHeadersText()
    {
        var text = new StringBuilder(StartLine).Append('\n');
        foreach (var header in Headers)
        {
            text.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
            if (text.Length > MaxCopyCharacters)
            {
                return null;
            }
        }
        return text.ToString();
    }

    /// <summary>The headers text as displayed (bounded to the 256 KiB rendering limit).</summary>
    public string HeadersDisplayText()
    {
        var source = FullHeadersText();
        if (source is null)
        {
            var builder = new StringBuilder(StartLine).Append('\n');
            foreach (var header in Headers)
            {
                builder.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
                if (builder.Length > DisplayLimit + 1024)
                {
                    break;
                }
            }
            source = builder.ToString();
        }
        return TextFormatting.BoundedDisplay(source, DisplayLimit, "[Header display truncated at the 256 KiB rendering limit.]\n");
    }

    public string BodyHeading => HasRemovedEncodings ? "Decoded body" : "Body";

    public string BodySizeText => HasRemovedEncodings
        ? $"{TextFormatting.ByteCount(Body.Length)} decoded; {TextFormatting.ByteCount(Body.CapturedLength)} captured"
        : TextFormatting.ByteCount(Body.Length);

    public string CapturedPreviewText() =>
        FallbackCapturedText ?? TextFormatting.RawHexPreview(Captured.Span);

    public bool CapturedPreviewTruncated => FallbackCapturedTruncated == true || Captured.Length < Body.CapturedLength;

    /// <summary>Port of the report's <c>rawCopyText</c>; null when it exceeds the copy limit.</summary>
    public string? RawCopyText()
    {
        var headers = FullHeadersText();
        if (headers is null)
        {
            return null;
        }
        var text = new StringBuilder()
            .Append("Original headers\n").Append(headers).Append('\n')
            .Append(BodyHeading).Append(" (").Append(BodySizeText).Append(")\n")
            .Append("Format: ").Append(Label).Append('\n')
            .Append("Status: ").Append(Status).Append('\n');
        if (Body.DecodingStatus is { Length: > 0 } decodingStatus)
        {
            text.Append("Decode status: ").Append(decodingStatus).Append('\n');
        }
        text.Append(DecodeBodyText());
        if (IsTruncated)
        {
            text.Append('\n').Append(BodyTruncatedMarker);
        }
        if (ShowCapturedBytes)
        {
            text.Append("\n\nCaptured bytes (pre-decode)\n").Append(CapturedPreviewText());
            if (CapturedPreviewTruncated)
            {
                text.Append("\n[Captured byte preview truncated]");
            }
        }
        return text.Length <= MaxCopyCharacters ? text.ToString() : null;
    }
}

/// <summary>Formatting helpers shared by the native views (ports of the report runtime's helpers).</summary>
internal static class TextFormatting
{
    /// <summary>The report's <c>formatRawHexPreview</c>: offset, two 8-byte hex groups and an ASCII gutter.</summary>
    public static string RawHexPreview(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }
        var text = new StringBuilder(((bytes.Length + 15) / 16) * 78);
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var count = Math.Min(16, bytes.Length - offset);
            text.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var index = 0; index < 16; index++)
            {
                if (index == 8)
                {
                    text.Append("  ");
                }
                else if (index > 0)
                {
                    text.Append(' ');
                }
                text.Append(index < count ? bytes[offset + index].ToString("X2", CultureInfo.InvariantCulture) : "  ");
            }
            text.Append(" |");
            for (var index = 0; index < count; index++)
            {
                var value = bytes[offset + index];
                text.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }
            text.Append("|\n");
        }
        return text.ToString();
    }

    /// <summary>The report's <c>formatByteCount</c> (B, KiB, MiB, GiB; en-US grouping).</summary>
    public static string ByteCount(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double size = value;
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

    /// <summary>The report's <c>boundedDisplay</c>: truncates with a marker line when over <paramref name="limit"/>.</summary>
    public static string BoundedDisplay(string value, int limit, string marker)
    {
        if (value.Length <= limit)
        {
            return value;
        }
        var prefix = Math.Max(0, limit - marker.Length - 1);
        return $"{value[..prefix]}\n{marker}";
    }
}
