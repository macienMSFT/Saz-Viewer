using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SazViewer.Core;

public enum BodyFormat
{
    Binary,
    Json,
    Xml,
    Text
}

public sealed record BodyPresentation(
    BodyFormat Format,
    string Label,
    string Status,
    string Raw,
    string Formatted,
    bool CanToggle,
    bool IsTruncated);

public sealed class BodyFormatter
{
    private const int MaxXmlCharacters = 256 * 1024;
    private const int MaxStructuredDepth = 64;
    private const int MaxFormattedCharacters = 256 * 1024;
    private const int MaxExpansionRatio = 8;
    private const int MinimumFormattedLimit = 4 * 1024;

    public BodyPresentation Format(BodyPreview body, string? contentType)
    {
        if (body.IsBinary)
        {
            return new BodyPresentation(
                BodyFormat.Binary,
                "Binary / hex",
                body.IsTruncated
                    ? "Binary body; showing a bounded, truncated hex preview."
                    : "Binary body; showing a bounded hex preview.",
                body.Preview,
                body.Preview,
                false,
                body.IsTruncated);
        }

        var raw = body.Preview;
        var trimmed = raw.AsSpan().TrimStart();
        var mediaType = MediaType(contentType);
        var declaredJson = IsJsonMediaType(mediaType);
        var declaredXml = IsXmlMediaType(mediaType);
        var declaredHtml = mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase);
        var looksJson = !trimmed.IsEmpty && trimmed[0] is '{' or '[';
        var looksXml = !declaredHtml && !trimmed.IsEmpty && trimmed[0] == '<';

        if (body.IsTruncated)
        {
            var hinted = looksJson || declaredJson
                ? "JSON"
                : looksXml || declaredXml
                    ? "XML"
                    : "text";
            return Text(
                body,
                hinted == "text" ? "Text preview" : $"{hinted} preview (truncated)",
                $"The body preview is truncated; {hinted} formatting was skipped to avoid showing an incomplete parse.");
        }

        if (looksJson && ExceedsJsonDepth(raw))
        {
            return FormattingLimited(
                body,
                BodyFormat.Json,
                "JSON-like",
                "JSON-like nesting exceeds the safe formatting depth; showing the original text.");
        }

        if (looksXml && ExceedsXmlDepth(raw))
        {
            return FormattingLimited(
                body,
                BodyFormat.Xml,
                "XML-like",
                "XML-like nesting exceeds the safe formatting depth; showing the original text.");
        }

        if (looksJson && TryFormatJson(raw, out var json))
        {
            return Structured(body, BodyFormat.Json, "JSON", DetectionStatus("JSON", declaredJson), json);
        }

        if (looksXml && TryFormatXml(raw, out var xml))
        {
            return Structured(body, BodyFormat.Xml, "XML", DetectionStatus("XML", declaredXml), xml);
        }

        if (declaredJson && TryFormatJson(raw, out json))
        {
            return Structured(body, BodyFormat.Json, "JSON", "Parsed as JSON from Content-Type.", json);
        }

        if (declaredXml && TryFormatXml(raw, out xml))
        {
            return Structured(body, BodyFormat.Xml, "XML", "Parsed as XML from Content-Type.", xml);
        }

        if (declaredJson || looksJson)
        {
            return Text(
                body,
                "Text (invalid JSON)",
                "The body was declared as or resembled JSON, but parsing failed; showing the original text.");
        }

        if (declaredXml || looksXml)
        {
            return Text(
                body,
                "Text (invalid XML)",
                "The body was declared as or resembled XML, but secure parsing failed; showing the original text.");
        }

        return Text(body, "Text", "Text body; no structured format was detected.");
    }

    private static BodyPresentation Structured(
        BodyPreview body,
        BodyFormat format,
        string label,
        string status,
        string formatted)
    {
        var relativeLimit = Math.Max(MinimumFormattedLimit, (long)body.Preview.Length * MaxExpansionRatio);
        if (formatted.Length > MaxFormattedCharacters || formatted.Length > relativeLimit)
        {
            return FormattingLimited(
                body,
                format,
                label,
                $"Valid {label} exceeded safe formatting expansion limits; showing the original text.");
        }

        return new BodyPresentation(
            format,
            label,
            status,
            body.Preview,
            formatted,
            true,
            body.IsTruncated);
    }

    private static BodyPresentation FormattingLimited(
        BodyPreview body,
        BodyFormat format,
        string label,
        string status) =>
        new(
            format,
            $"{label} (raw)",
            status,
            body.Preview,
            body.Preview,
            false,
            body.IsTruncated);

    private static BodyPresentation Text(BodyPreview body, string label, string status) =>
        new(
            BodyFormat.Text,
            label,
            status,
            body.Preview,
            body.Preview,
            true,
            body.IsTruncated);

    private static string DetectionStatus(string format, bool declared) =>
        declared
            ? $"Parsed as {format} from Content-Type and body content."
            : $"Detected and parsed as {format} from body content.";

    private static string MediaType(string? contentType) =>
        contentType?.Split(';', 2)[0].Trim() ?? string.Empty;

    private static bool IsJsonMediaType(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private static bool IsXmlMediaType(string mediaType) =>
        mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);

    private static bool TryFormatJson(string raw, out string formatted)
    {
        try
        {
            using var document = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxStructuredDepth
                });
            formatted = JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true, MaxDepth = MaxStructuredDepth });
            return true;
        }
        catch (JsonException)
        {
            formatted = string.Empty;
            return false;
        }
    }

    private static bool TryFormatXml(string raw, out string formatted)
    {
        try
        {
            using var text = new StringReader(raw);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlCharacters
            };
            using var reader = XmlReader.Create(text, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            formatted = document.ToString(SaveOptions.None);
            return true;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            formatted = string.Empty;
            return false;
        }
    }

    private static bool ExceedsJsonDepth(string raw)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        foreach (var character in raw)
        {
            if (inString)
            {
                if (character == '"' && !escaped)
                {
                    inString = false;
                }
                escaped = character == '\\' && !escaped;
                if (character != '\\')
                {
                    escaped = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character is '{' or '[')
            {
                if (++depth > MaxStructuredDepth)
                {
                    return true;
                }
            }
            else if (character is '}' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
        }
        return false;
    }

    private static bool ExceedsXmlDepth(string raw)
    {
        try
        {
            using var text = new StringReader(raw);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlCharacters
            };
            using var reader = XmlReader.Create(text, settings);
            while (reader.Read())
            {
                if (reader.Depth > MaxStructuredDepth)
                {
                    return true;
                }
            }
        }
        catch (XmlException)
        {
        }
        return false;
    }
}
