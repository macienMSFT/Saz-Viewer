using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HtmlReportGeneratorTests
{
    [Fact]
    public void GeneratesAccessibleStickyDetailPaneAndSafeFormattedBodies()
    {
        const string attack = "</script><img src=x onerror=globalThis.pwned=true>";
        var report = new SazReport { SourceName = attack };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "7",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/api",
                StatusCode = 200,
                ContentType = "application/xml",
                Request = Message(
                    "POST /api HTTP/1.1",
                    "application/json",
                    $$"""{"payload":"{{attack}}","value":1}"""),
                Response = Message(
                    "HTTP/1.1 200 OK",
                    "text/plain",
                    "<root><value>safe</value></root>")
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains("id=\"httpDetails\" class=\"detail-pane\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"detailResizer\"", html, StringComparison.Ordinal);
        Assert.Contains("<template id=\"http-detail-0\">", html, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"0\" aria-selected=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"message-grid\"", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Request</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Response</h3>", html, StringComparison.Ordinal);
        Assert.Contains("data-body-view=\"raw\"", html, StringComparison.Ordinal);
        Assert.Contains("template.content.cloneNode(true)", html, StringComparison.Ordinal);
        Assert.Contains("event.key==='Enter'||event.key===' '", html, StringComparison.Ordinal);
        Assert.Contains("selected session is hidden by the active filter", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("syn-key", html, StringComparison.Ordinal);
        Assert.Contains("syn-tag", html, StringComparison.Ordinal);
        Assert.Contains("document.createElement('span')", html, StringComparison.Ordinal);
        Assert.Contains("span.textContent=text", html, StringComparison.Ordinal);
        Assert.Contains("highlightSelected(detailContent)", html, StringComparison.Ordinal);
        Assert.Contains("aria-valuenow=\"320\"", html, StringComparison.Ordinal);
        Assert.Contains("detailPane.style.setProperty('--detail-height'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"syn-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details><summary>Inspect", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        Assert.DoesNotContain(attack, html, StringComparison.Ordinal);
        Assert.Contains("&lt;/script&gt;&lt;img src=x onerror=globalThis.pwned=true&gt;", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsLargeStructuredReportsCompactByHighlightingOnSelection()
    {
        var payload = $"{{\"values\":[{string.Join(',', Enumerable.Range(0, 2_000))}]}}";
        var report = new SazReport { SourceName = "large.saz" };
        for (var index = 0; index < 25; index++)
        {
            report.Sessions.Add(
                new HttpSession
                {
                    Id = index.ToString(),
                    ArchiveOrder = index,
                    Method = "GET",
                    Url = $"https://example.test/{index}",
                    StatusCode = 200,
                    Response = Message("HTTP/1.1 200 OK", "application/json", payload)
                });
        }

        var html = new HtmlReportGenerator().Generate(report);
        var capturedCharacters = payload.Length * report.Sessions.Count;

        Assert.True(
            html.Length < capturedCharacters * 5,
            $"Expected compact lazy highlighting, but {capturedCharacters:N0} captured characters produced {html.Length:N0} HTML characters.");
        Assert.DoesNotContain("<span class=\"syn-number\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void StartsAtHttpControlsWithoutSummaryHeaderOrCards()
    {
        var report = new SazReport { SourceName = "capture.saz" };
        report.Warnings.Add("Synthetic warning");
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/",
                StatusCode = 200
            });
        report.WebSocketMessages.Add(
            new WebSocketMessage
            {
                SessionId = "1",
                Direction = "Server",
                Type = "Text",
                Preview = "message"
            });

        var html = new HtmlReportGenerator().Generate(report);
        var workspace = html.IndexOf("<section class=\"http-workspace\"", StringComparison.Ordinal);
        var search = html.IndexOf("id=\"httpSearch\"", StringComparison.Ordinal);
        var warnings = html.IndexOf("<h2>Warnings</h2>", StringComparison.Ordinal);
        var webSockets = html.IndexOf("<h2>WebSocket messages</h2>", StringComparison.Ordinal);

        Assert.True(workspace >= 0 && search > workspace);
        Assert.True(warnings > search);
        Assert.True(webSockets > warnings);
        Assert.DoesNotContain("<header", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"cards\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Total HTTP bytes", html, StringComparison.Ordinal);
        Assert.Contains("main{width:100%;padding:4px}", html, StringComparison.Ordinal);
        Assert.Contains("height:calc(100dvh - 8px)", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"HTTP sessions\"", html, StringComparison.Ordinal);
    }

    private static HttpMessage Message(string startLine, string contentType, string body)
    {
        var message = new HttpMessage
        {
            StartLine = startLine,
            Body = new BodyPreview
            {
                Length = body.Length,
                Preview = body
            }
        };
        message.Headers.Add(new HttpHeader("Content-Type", contentType));
        return message;
    }
}
