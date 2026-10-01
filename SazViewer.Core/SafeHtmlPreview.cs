using System.Net;
using System.Text;

namespace SazViewer.Core;

internal sealed record SafeHtmlPreview(string Source, string Document, string Detection);

internal static class SafeHtmlPreviewBuilder
{
    private const int MaxSourceCharacters = HttpMessageParser.MaxBodyPreview;
    private const string FrameCsp =
        "default-src 'none'; img-src 'none'; media-src 'none'; font-src 'none'; "
        + "style-src 'unsafe-inline'; script-src 'none'; connect-src 'none'; frame-src 'none'; "
        + "child-src 'none'; object-src 'none'; form-action 'none'; base-uri 'none'";

    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "article", "aside", "b", "bdi", "bdo", "blockquote", "br", "caption",
        "cite", "code", "col", "colgroup", "data", "dd", "del", "details", "dfn", "div", "dl",
        "dt", "em", "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hgroup", "hr", "i", "ins", "kbd", "li", "main", "mark", "nav", "ol", "p",
        "pre", "q", "s", "samp", "section", "small", "span", "strong", "sub", "summary", "sup",
        "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr", "u", "ul", "var", "wbr"
    };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "col", "hr", "wbr"
    };

    private static readonly HashSet<string> RemovedSubtrees = new(StringComparer.OrdinalIgnoreCase)
    {
        "applet", "audio", "base", "embed", "fencedframe", "frame", "frameset",
        "iframe", "math", "mathml", "menu", "meta", "noscript", "object", "picture", "portal",
        "script", "style", "svg", "template", "title", "video"
    };

    private static readonly HashSet<string> RemovedVoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "embed", "img", "input", "link", "meta", "param", "source", "track"
    };

    private static readonly HashSet<string> HtmlForeignSelfClosingElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "math", "mathml", "svg"
    };

    private static readonly HashSet<string> RawTextRemovedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "title"
    };

    public static SafeHtmlPreview? TryCreate(HttpMessage message)
    {
        var body = message.Body;
        if (body.IsBinary || body.IsTruncated || body.Length <= 0 || body.Length > HttpMessageParser.MaxBodyPreview
            || body.DecodedBytes.Length != body.Length || body.Preview.Length > MaxSourceCharacters)
        {
            return null;
        }

        var mediaType = message.Header("Content-Type")?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
        if (mediaType is "image/svg+xml" or "application/xml" or "text/xml"
            || (mediaType.EndsWith("+xml", StringComparison.Ordinal) && mediaType != "application/xhtml+xml"))
        {
            return null;
        }
        var recognizedType = mediaType is "text/html" or "application/xhtml+xml";
        var sniffed = LooksLikeHtmlDocument(body.Preview);
        if (!recognizedType && !sniffed)
        {
            return null;
        }

        var detection = recognizedType
            ? $"Complete retained {mediaType} body; executable and external-resource content is removed."
            : "Complete retained body conservatively recognized as an HTML document; executable and external-resource content is removed.";
        return new SafeHtmlPreview(
            body.Preview,
            BuildDocument(body.Preview, mediaType == "application/xhtml+xml"),
            detection);
    }

    internal static string BuildDocument(string source, bool xmlSelfClosing = false)
    {
        var sanitized = SanitizeFragment(source, xmlSelfClosing);
        var document = new StringBuilder(sanitized.Length + 1024);
        document.Append("<!doctype html><html data-theme=\"system\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"")
            .Append(FrameCsp)
            .Append("\"><meta name=\"referrer\" content=\"no-referrer\"><style>")
            .Append("""
:root{color-scheme:dark;--bg:#0d1117;--panel:#161b22;--text:#e6edf3;--muted:#8b949e;--line:#30363d;--accent:#58a6ff}
:root[data-theme=light]{color-scheme:light;--bg:#fff;--panel:#f6f8fa;--text:#1f2328;--muted:#59636e;--line:#d0d7de;--accent:#0969da}
*{box-sizing:border-box}html,body{margin:0;min-height:100%;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,Segoe UI,sans-serif}
body{padding:12px}a{color:var(--accent);text-decoration:none;pointer-events:none}table{border-collapse:collapse;max-width:100%}th,td{border:1px solid var(--line);padding:5px 7px;vertical-align:top}
pre,code,kbd,samp{font-family:ui-monospace,Consolas,monospace}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:var(--panel);border:1px solid var(--line);padding:9px}
blockquote{border-left:3px solid var(--line);margin-left:0;padding-left:12px;color:var(--muted)}hr{border:0;border-top:1px solid var(--line)}
""")
            .Append("</style></head><body>")
            .Append(sanitized)
            .Append("</body></html>");
        return document.ToString();
    }

    internal static string SanitizeFragment(string source, bool xmlSelfClosing = false)
    {
        var output = new StringBuilder(Math.Min(source.Length, MaxSourceCharacters));
        string? skippedElement = null;
        var skippedDepth = 0;
        var position = 0;
        while (position < source.Length)
        {
            var tagStart = source.IndexOf('<', position);
            if (tagStart < 0)
            {
                if (skippedElement is null)
                {
                    output.Append(source, position, source.Length - position);
                }
                break;
            }
            if (skippedElement is null && tagStart > position)
            {
                output.Append(source, position, tagStart - position);
            }
            if (source.AsSpan(tagStart).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = source.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                position = commentEnd < 0 ? source.Length : commentEnd + 3;
                continue;
            }

            var tagEnd = FindTagEnd(source, tagStart + 1);
            if (tagEnd < 0)
            {
                if (skippedElement is null)
                {
                    output.Append("&lt;").Append(WebUtility.HtmlEncode(source[(tagStart + 1)..]));
                }
                break;
            }

            var token = source.AsSpan(tagStart + 1, tagEnd - tagStart - 1);
            if (!TryReadTag(token, out var name, out var closing, out var selfClosing))
            {
                position = tagEnd + 1;
                continue;
            }

            if (skippedElement is not null)
            {
                if (name.Equals(skippedElement, StringComparison.OrdinalIgnoreCase))
                {
                    if (closing)
                    {
                        skippedDepth--;
                        if (skippedDepth == 0)
                        {
                            skippedElement = null;
                        }
                    }
                    else if (!selfClosing && !RawTextRemovedElements.Contains(skippedElement))
                    {
                        skippedDepth++;
                    }
                }
                position = tagEnd + 1;
                continue;
            }

            if (RemovedVoidElements.Contains(name))
            {
                if (name.Equals("img", StringComparison.OrdinalIgnoreCase))
                {
                    output.Append("<span>[image omitted]</span>");
                }
                position = tagEnd + 1;
                continue;
            }
            if (RemovedSubtrees.Contains(name))
            {
                var closesImmediately = selfClosing
                    && (xmlSelfClosing || HtmlForeignSelfClosingElements.Contains(name));
                if (!closing && !closesImmediately)
                {
                    skippedElement = name;
                    skippedDepth = 1;
                }
                position = tagEnd + 1;
                continue;
            }
            if (AllowedElements.Contains(name))
            {
                if (closing)
                {
                    if (!VoidElements.Contains(name))
                    {
                        output.Append("</").Append(name.ToLowerInvariant()).Append('>');
                    }
                }
                else
                {
                    output.Append('<').Append(name.ToLowerInvariant()).Append('>');
                }
            }
            position = tagEnd + 1;
        }
        return output.ToString();
    }

    private static bool LooksLikeHtmlDocument(string source)
    {
        var span = source.AsSpan().TrimStart();
        if (!span.IsEmpty && span[0] == '\uFEFF')
        {
            span = span[1..].TrimStart();
        }
        return span.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindTagEnd(string source, int position)
    {
        var quote = '\0';
        for (var index = position; index < source.Length; index++)
        {
            var character = source[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }
        return -1;
    }

    private static bool TryReadTag(
        ReadOnlySpan<char> token,
        out string name,
        out bool closing,
        out bool selfClosing)
    {
        name = string.Empty;
        token = token.Trim();
        closing = !token.IsEmpty && token[0] == '/';
        if (closing)
        {
            token = token[1..].TrimStart();
        }
        if (token.IsEmpty || token[0] is '!' or '?')
        {
            selfClosing = false;
            return false;
        }
        var length = 0;
        while (length < token.Length && (char.IsAsciiLetterOrDigit(token[length]) || token[length] is '-' or ':'))
        {
            length++;
        }
        if (length == 0)
        {
            selfClosing = false;
            return false;
        }
        name = token[..length].ToString();
        selfClosing = token.TrimEnd().EndsWith("/", StringComparison.Ordinal);
        return true;
    }
}
