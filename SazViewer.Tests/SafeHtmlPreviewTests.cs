using System.Text;
using System.Diagnostics;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class SafeHtmlPreviewTests
{
    [Fact]
    public void ReconstructsOnlySafeStructuralHtmlBehindDenyAllCsp()
    {
        const string source = """
            <!doctype html><html><head>
            <base href="https://base.invalid/"><meta http-equiv="refresh" content="0;url=https://refresh.invalid/">
            <link rel="stylesheet" href="https://css.invalid/x.css"><style>@import "https://import.invalid/x";p{background:url(https://style.invalid/x)}</style>
            <script>globalThis.executed=true;fetch("https://script.invalid/")</script>
            <script/>self-closing-script-text</script>
            </head><body onload="globalThis.loaded=true" style="background:url(https://attr.invalid/x)">
            <h1 id="captured" onclick="globalThis.clicked=true">Safe title</h1>
            <a href="javascript:alert(1)" ping="https://ping.invalid/" target="_top" download>Inert link</a>
            <img src="https://img.invalid/a.png" srcset="https://img2.invalid/a.png 2x" onerror="globalThis.imageError=true">
            <form action="https://form.invalid/" target="_top"><input formaction="https://button.invalid/"><button>Submit</button></form>
            <iframe src="https://frame.invalid/" srcdoc="<script>parent.pwned=true</script>"></iframe>
            <object data="https://object.invalid/"><embed src="https://embed.invalid/"></object>
            <video poster="https://poster.invalid/"><source src="https://media.invalid/"></video>
            <svg><script>globalThis.svg=true</script><a href="https://svg.invalid/">SVG</a></svg>
            <math><a href="https://math.invalid/">Math</a></math>
            <table class="captured"><tr><td colspan="9">Cell</td></tr></table>
            </body></html>
            """;

        var document = SafeHtmlPreviewBuilder.BuildDocument(source);
        var csp = document.IndexOf("Content-Security-Policy", StringComparison.Ordinal);
        var body = document.IndexOf("<body>", StringComparison.Ordinal);

        Assert.True(csp > 0 && csp < body);
        Assert.Contains("default-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", document, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", document, StringComparison.Ordinal);
        Assert.Contains("<h1>Safe title</h1>", document, StringComparison.Ordinal);
        Assert.Contains("<a>Inert link</a>", document, StringComparison.Ordinal);
        Assert.Contains("<span>[image omitted]</span>", document, StringComparison.Ordinal);
        Assert.Contains("<table><tr><td>Cell</td></tr></table>", document, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid/", document, StringComparison.Ordinal);
        Assert.DoesNotContain("globalThis", document, StringComparison.Ordinal);
        Assert.DoesNotContain("self-closing-script-text", document, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style>@import", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<math", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" onclick=", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" href=", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" src=", document, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("text/html; charset=utf-8", "<p>fragment</p>", true)]
    [InlineData("application/xhtml+xml", "<html><body>XHTML</body></html>", true)]
    [InlineData(null, "<!doctype html><html><body>sniffed</body></html>", true)]
    [InlineData("text/plain", "<html><body>wrong header</body></html>", true)]
    [InlineData(null, "<p>not enough evidence</p>", false)]
    [InlineData("application/xml", "<html><body>XML is excluded</body></html>", false)]
    [InlineData("image/svg+xml", "<html><svg><script>bad()</script></svg></html>", false)]
    public void EligibilityUsesCompleteTextAndConservativeHtmlEvidence(
        string? contentType,
        string source,
        bool expected)
    {
        var message = Message(source, contentType);

        Assert.Equal(expected, SafeHtmlPreviewBuilder.TryCreate(message) is not null);
    }

    [Fact]
    public void RejectsBinaryTruncatedAndIncompleteRetainedBodies()
    {
        var binary = Message("<html><body>binary</body></html>", "text/html");
        binary = new HttpMessage
        {
            StartLine = binary.StartLine,
            Body = CopyBody(binary.Body, isBinary: true)
        };
        binary.Headers.Add(new HttpHeader("Content-Type", "text/html"));

        var truncated = Message("<html><body>truncated</body></html>", "text/html");
        truncated = new HttpMessage
        {
            StartLine = truncated.StartLine,
            Body = CopyBody(truncated.Body, isTruncated: true)
        };
        truncated.Headers.Add(new HttpHeader("Content-Type", "text/html"));

        var incomplete = Message("<html><body>incomplete</body></html>", "text/html");
        incomplete = new HttpMessage
        {
            StartLine = incomplete.StartLine,
            Body = CopyBody(incomplete.Body, decodedBytes: "short"u8.ToArray())
        };
        incomplete.Headers.Add(new HttpHeader("Content-Type", "text/html"));

        Assert.Null(SafeHtmlPreviewBuilder.TryCreate(binary));
        Assert.Null(SafeHtmlPreviewBuilder.TryCreate(truncated));
        Assert.Null(SafeHtmlPreviewBuilder.TryCreate(incomplete));
    }

    [Fact]
    public void PreservesBodyAndFormContentWhenOptionalClosingHeadIsMissing()
    {
        const string source =
            "<!doctype html><html><head><title>hidden title</title><meta charset=\"utf-8\">"
            + "<body><h1>Visible</h1><form action=\"https://blocked.invalid/\">"
            + "<table><tr><td>Login content</td></tr></table><button formaction=\"https://blocked.invalid/submit\">Submit</button>"
            + "</form><p>After</p></body></html>";

        var sanitized = SafeHtmlPreviewBuilder.SanitizeFragment(source);

        Assert.Equal(
            "<h1>Visible</h1><table><tr><td>Login content</td></tr></table>Submit<p>After</p>",
            sanitized);
        Assert.DoesNotContain("hidden title", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked.invalid", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedTrailingTagsAreHandledInLinearBoundedTime()
    {
        var source = "<html><body><p>Safe</p>" + new string('<', 64 * 1024);
        var timer = Stopwatch.StartNew();

        var sanitized = SafeHtmlPreviewBuilder.SanitizeFragment(source);

        timer.Stop();
        Assert.StartsWith("<p>Safe</p>&lt;", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("<", sanitized["<p>Safe</p>".Length..], StringComparison.Ordinal);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Sanitization took {timer.Elapsed}.");
    }

    [Fact]
    public void SelfClosingForeignHtmlAndXhtmlElementsDoNotSwallowTrailingContent()
    {
        var html = SafeHtmlPreviewBuilder.SanitizeFragment(
            "<html><body><p>before</p><svg/><p>after svg</p><script/>hidden</script><p>tail</p></body></html>");
        var xhtml = Message(
            "<html><body><p>before</p><script src=\"https://blocked.invalid/\"/><style/><p>after XHTML</p></body></html>",
            "application/xhtml+xml");
        var preview = SafeHtmlPreviewBuilder.TryCreate(xhtml);

        Assert.Equal("<p>before</p><p>after svg</p><p>tail</p>", html);
        Assert.NotNull(preview);
        Assert.Contains("<p>after XHTML</p>", preview.Document, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked.invalid", preview.Document, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", preview.Document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScriptTextThatLooksLikeANestedTagDoesNotSwallowTrailingContent()
    {
        var sanitized = SafeHtmlPreviewBuilder.SanitizeFragment(
            "<script>const example = \"<script>\";</script><p>Trailing content</p>");

        Assert.Equal("<p>Trailing content</p>", sanitized);
    }

    private static HttpMessage Message(string source, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var message = new HttpMessage
        {
            StartLine = "HTTP/1.1 200 OK",
            Body = new BodyPreview
            {
                Length = bytes.Length,
                CapturedLength = bytes.Length,
                Preview = source,
                Charset = "utf-8",
                CapturedBytes = bytes,
                DecodedBytes = bytes
            }
        };
        if (contentType is not null)
        {
            message.Headers.Add(new HttpHeader("Content-Type", contentType));
        }
        return message;
    }

    private static BodyPreview CopyBody(
        BodyPreview body,
        bool? isBinary = null,
        bool? isTruncated = null,
        byte[]? decodedBytes = null) =>
        new()
        {
            Length = body.Length,
            CapturedLength = body.CapturedLength,
            IsBinary = isBinary ?? body.IsBinary,
            IsTruncated = isTruncated ?? body.IsTruncated,
            Charset = body.Charset,
            Preview = body.Preview,
            CapturedBytes = body.CapturedBytes,
            DecodedBytes = decodedBytes ?? body.DecodedBytes
        };
}
