using SazViewer.App.Model;

namespace SazViewer.App.ViewModels;

/// <summary>Builds the Headers and Raw text documents exactly as the report renders them.</summary>
internal static class MessageDocuments
{
    public const string BodyDisplayMarker = "[Body display truncated at the 256 KiB rendering limit.]";
    public const string CapturedDisplayMarker = "[Captured-byte display truncated at the 256 KiB rendering limit.]";
    public const string CapturedSectionTitle = "Captured bytes (pre-decode)";

    public static LineDocument Headers(MessageContent content)
    {
        try
        {
            return LineDocument.FromText(content.HeadersDisplayText());
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            return LineDocument.FromText($"Headers could not be loaded: {error.Message}", LineKind.Warning);
        }
    }

    public static LineDocument Raw(MessageContent content)
    {
        var builder = new LineDocumentBuilder();
        string headers;
        string body;
        string? captured = null;
        try
        {
            headers = content.HeadersDisplayText();
            body = content.DecodeBodyText();
            if (content.IsTruncated)
            {
                body += "\n" + MessageContent.BodyTruncatedMarker;
            }
            body = TextFormatting.BoundedDisplay(body, MessageContent.DisplayLimit, BodyDisplayMarker);
            if (content.ShowCapturedBytes)
            {
                captured = content.CapturedPreviewText();
                if (content.CapturedPreviewTruncated)
                {
                    captured += "\n[Captured byte preview truncated]";
                }
                captured = TextFormatting.BoundedDisplay(captured, MessageContent.DisplayLimit, CapturedDisplayMarker);
            }
        }
        catch (InvalidDataException error)
        {
            var failure = $"Content could not be displayed: {error.Message}";
            AddFrame(builder, content, failure, LineKind.Warning, failure, LineKind.Warning, content.ShowCapturedBytes ? failure : null);
            return builder.Build();
        }
        AddFrame(builder, content, headers, LineKind.Code, body, LineKind.Code, captured);
        return builder.Build();
    }

    private static void AddFrame(
        LineDocumentBuilder builder,
        MessageContent content,
        string headers,
        LineKind headersKind,
        string body,
        LineKind bodyKind,
        string? captured)
    {
        builder.Add("Original headers", LineKind.Heading);
        builder.Add(headers, headersKind);
        var heading = content.HasRemovedEncodings ? "Decoded body " : "Body ";
        var size = $"({content.BodySizeText})";
        builder.Add(heading + size, LineKind.Heading, [new StyledSpan(heading.Length, size.Length, SpanStyle.Muted)]);
        builder.Add(FormatMetaText(content.Label, content.Status, out var spans), LineKind.FormatMeta, spans);
        if (content.Body.DecodingStatus is { Length: > 0 } status)
        {
            builder.Add(status, content.HasRemovedEncodings ? LineKind.DecodeStatus : LineKind.Warning);
        }
        builder.Add(body, bodyKind);
        if (captured is not null)
        {
            builder.BeginSection(CapturedSectionTitle);
            builder.Add(captured, bodyKind);
            builder.EndSection();
        }
    }

    /// <summary>The report's <c>formatMeta</c> row: a badge with the format label followed by the status.</summary>
    public static string FormatMetaText(string label, string status, out IReadOnlyList<StyledSpan> spans)
    {
        spans = [new StyledSpan(0, label.Length, SpanStyle.Badge), new StyledSpan(label.Length + 2, status.Length, SpanStyle.Muted)];
        return $"{label}  {status}";
    }
}
