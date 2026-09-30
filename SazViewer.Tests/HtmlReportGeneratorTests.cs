using SazViewer.Core;
using System.Collections.Immutable;

namespace SazViewer.Tests;

public sealed class HtmlReportGeneratorTests
{
    [Fact]
    public void GeneratesAccessibleTabStripAndSafeFormattedBodies()
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
                    "text/xml",
                    "<root><value>safe</value></root>")
            });

        var html = new HtmlReportGenerator().Generate(report);

        // Sticky/resizable detail pane, row selection, and workspace layout are preserved.
        Assert.Contains("id=\"httpDetails\" class=\"detail-pane\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"detailResizer\"", html, StringComparison.Ordinal);
        Assert.Contains("<template id=\"http-detail-0\">", html, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"0\" aria-selected=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"message-grid\"", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Request</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Response</h3>", html, StringComparison.Ordinal);
        Assert.Contains("template.content.cloneNode(true)", html, StringComparison.Ordinal);
        Assert.Contains("event.key==='Enter'||event.key===' '", html, StringComparison.Ordinal);
        Assert.Contains("selected session is hidden by the active filter", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aria-valuenow=\"320\"", html, StringComparison.Ordinal);
        Assert.Contains("detailPane.style.setProperty('--detail-height'", html, StringComparison.Ordinal);

        // Both sides render the full, always-visible five-tab strip with correct roles.
        Assert.Contains("role=\"tablist\" aria-label=\"Request detail views\"", html, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\" aria-label=\"Response detail views\"", html, StringComparison.Ordinal);
        foreach (var side in new[] { "request", "response" })
        {
            foreach (var key in new[] { "json", "xml", "mapi", "headers", "raw" })
            {
                Assert.Contains($"role=\"tab\" id=\"{side}-tab-{key}\"", html, StringComparison.Ordinal);
                Assert.Contains($"aria-controls=\"{side}-panel-{key}\"", html, StringComparison.Ordinal);
                Assert.Contains(
                    $"role=\"tabpanel\" id=\"{side}-panel-{key}\" aria-labelledby=\"{side}-tab-{key}\"",
                    html,
                    StringComparison.Ordinal);
            }
        }

        // Request body is JSON: JSON tab enabled/selected, XML/MAPI disabled.
        Assert.Contains(
            "id=\"request-tab-json\" aria-controls=\"request-panel-json\" aria-selected=\"true\" data-tab=\"json\" tabindex=\"0\">JSON",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"request-tab-xml\" aria-controls=\"request-panel-xml\" aria-selected=\"false\" data-tab=\"xml\" tabindex=\"-1\" disabled aria-disabled=\"true\">XML",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"request-tab-mapi\" aria-controls=\"request-panel-mapi\" aria-selected=\"false\" data-tab=\"mapi\" tabindex=\"-1\" disabled aria-disabled=\"true\">MAPI",
            html,
            StringComparison.Ordinal);

        // Response body is XML: XML tab enabled/selected, JSON/MAPI disabled.
        Assert.Contains(
            "id=\"response-tab-xml\" aria-controls=\"response-panel-xml\" aria-selected=\"true\" data-tab=\"xml\" tabindex=\"0\">XML",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"response-tab-json\" aria-controls=\"response-panel-json\" aria-selected=\"false\" data-tab=\"json\" tabindex=\"-1\" disabled aria-disabled=\"true\">JSON",
            html,
            StringComparison.Ordinal);

        // Syntax highlighting hooks exist for JSON/XML pretty views only.
        Assert.Contains("syn-key", html, StringComparison.Ordinal);
        Assert.Contains("syn-tag", html, StringComparison.Ordinal);
        Assert.Contains("document.createElement('span')", html, StringComparison.Ordinal);
        Assert.Contains("span.textContent=text", html, StringComparison.Ordinal);
        Assert.Contains("highlightSelected(detailContent)", html, StringComparison.Ordinal);

        // Global Formatted/Decoded/Captured toggle controls are removed in favor of tabs.
        Assert.DoesNotContain("data-body-view", html, StringComparison.Ordinal);
        Assert.DoesNotContain("body-toggle", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"body-toolbar\"", html, StringComparison.Ordinal);

        Assert.DoesNotContain("<span class=\"syn-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details><summary>Inspect", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        Assert.DoesNotContain(attack, html, StringComparison.Ordinal);
        Assert.Contains("&lt;/script&gt;&lt;img src=x onerror=globalThis.pwned=true&gt;", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void WiresFullKeyboardAndRovingTabindexBehaviorForTabs()
    {
        var report = new SazReport { SourceName = "keyboard.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/",
                StatusCode = 200,
                Request = Message("GET / HTTP/1.1", "text/plain", "hello")
            });

        var html = new HtmlReportGenerator().Generate(report);

        // Deterministic priority order used both for initial-tab selection and keyboard iteration.
        Assert.Contains("['mapi','json','xml','raw','headers']", html, StringComparison.Ordinal);

        // Roving tabindex: only the active tab is focusable, updated on activation.
        Assert.Contains("tab.tabIndex=active?0:-1", html, StringComparison.Ordinal);

        // Left/Right/Home/End keyboard behavior, skipping disabled tabs.
        Assert.Contains("['ArrowLeft','ArrowRight','Home','End'].includes(event.key)", html, StringComparison.Ordinal);
        Assert.Contains("tabsOf(tablist).filter(tab=>!tab.disabled)", html, StringComparison.Ordinal);
        Assert.Contains(
            "nextIndex=(currentIndex+1)%enabledTabs.length",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "nextIndex=(currentIndex-1+enabledTabs.length)%enabledTabs.length",
            html,
            StringComparison.Ordinal);
        Assert.Contains("nextIndex=0", html, StringComparison.Ordinal);
        Assert.Contains("nextIndex=enabledTabs.length-1", html, StringComparison.Ordinal);

        // Disabled tabs are never clickable/activatable.
        Assert.Contains("if(!tab||tab.disabled||tab.parentElement!==tablist)return", html, StringComparison.Ordinal);

        // Remembered per-side tab preference, applied only when still enabled for the new session.
        Assert.Contains("const preferredTab={}", html, StringComparison.Ordinal);
        Assert.Contains("if(remembered&&enabled(remembered))return remembered", html, StringComparison.Ordinal);
        Assert.Contains(
            "if(options&&options.remember)preferredTab[tablist.dataset.side]=key",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "activateTab(tablist,tab.dataset.tab,{remember:true})",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "activateTab(tablist,enabledTabs[nextIndex].dataset.tab,{focus:true,remember:true})",
            html,
            StringComparison.Ordinal);
        Assert.Contains("if(key)activateTab(tablist,key)", html, StringComparison.Ordinal);

        // Visible-focus CSS for tabs.
        Assert.Contains(".tab-strip [role=tab]:focus-visible{outline:2px solid var(--accent)", html, StringComparison.Ordinal);
        Assert.Contains(".tab-strip [role=tab]:disabled{color:", html, StringComparison.Ordinal);

        // setupTabs runs on every selection alongside existing behaviors.
        Assert.Contains(
            "highlightSelected(detailContent);renderProtocolTrees(detailContent);setupTabs(detailContent);",
            html,
            StringComparison.Ordinal);
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

    [Fact]
    public void GeneratesLazySafeMapiProtocolTreeSelectedAsInitialTab()
    {
        const string attack = "</script><img src=x onerror=alert(1)>";
        var root = new MapiNode(
            "Execute",
            MapiNodeKind.Operation,
            0,
            8,
            null,
            [MapiNode.Leaf("PropertyValue", MapiNodeKind.Property, 4, 4, attack)]);
        var parse = new MapiMessageParse(
            MapiDirection.Request,
            root,
            ImmutableArray<string>.Empty,
            true,
            8,
            8);
        var mapi = new MapiSession(
            "1",
            0,
            MapiEndpoint.Mailbox,
            "Execute",
            "0",
            false,
            parse,
            null,
            ImmutableArray<string>.Empty);
        var session = new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/mapi",
            StatusCode = 200,
            Request = Message("POST /mapi HTTP/1.1", "application/mapi-http", "binary")
        };
        session.Mapi = mapi;
        var report = new SazReport { SourceName = "mapi.saz" };
        report.Sessions.Add(session);

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains("<option value=\"mapi\">MAPI/NSPI only</option>", html, StringComparison.Ordinal);
        Assert.Contains("data-mapi=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("<th>Protocol</th>", html, StringComparison.Ordinal);
        Assert.Contains("data-protocol=", html, StringComparison.Ordinal);
        Assert.Contains("renderProtocolTrees(detailContent)", html, StringComparison.Ordinal);
        Assert.Contains("document.createElement(hasChildren?'details':'div')", html, StringComparison.Ordinal);
        Assert.Contains("value.textContent=String(node.value)", html, StringComparison.Ordinal);
        Assert.DoesNotContain(attack, html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);

        // MAPI is the deterministic initial tab whenever a protocol tree is present for that side.
        Assert.Contains(
            "id=\"request-tab-mapi\" aria-controls=\"request-panel-mapi\" aria-selected=\"true\" data-tab=\"mapi\" tabindex=\"0\">MAPI",
            html,
            StringComparison.Ordinal);
        var mapiPanelStart = html.IndexOf("id=\"request-panel-mapi\"", StringComparison.Ordinal);
        Assert.True(mapiPanelStart >= 0);
        var mapiPanelTagEnd = html.IndexOf('>', mapiPanelStart);
        var mapiPanelOpenTag = html[html.LastIndexOf('<', mapiPanelStart)..(mapiPanelTagEnd + 1)];
        Assert.DoesNotContain("hidden", mapiPanelOpenTag, StringComparison.Ordinal);
    }

    [Fact]
    public void AllTabsDisabledAndVisiblyPlaceholderedWhenSideHasNoCapturedMessage()
    {
        var report = new SazReport { SourceName = "no-response.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "9",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/timeout",
                Request = Message("GET /timeout HTTP/1.1", "text/plain", "request only"),
                Response = null
            });

        var html = new HtmlReportGenerator().Generate(report);

        foreach (var key in new[] { "json", "xml", "mapi", "headers", "raw" })
        {
            Assert.Contains(
                $"id=\"response-tab-{key}\" aria-controls=\"response-panel-{key}\" aria-selected=\"false\" data-tab=\"{key}\" tabindex=\"-1\" disabled aria-disabled=\"true\"",
                html,
                StringComparison.Ordinal);
        }

        Assert.Contains("No response entry was captured.", html, StringComparison.Ordinal);
        // The tab strip itself remains present (always visible) even though every tab is disabled.
        Assert.Contains("role=\"tablist\" aria-label=\"Response detail views\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RawTabShowsOriginalHeadersDecodedBodyStatusAndCapturedBytesControl()
    {
        var message = new HttpMessage
        {
            StartLine = "HTTP/1.1 200 OK",
            Body = new BodyPreview
            {
                Length = 5,
                CapturedLength = 25,
                Preview = "hello",
                CapturedBytesPreview = "1F 8B 08 00 00 00 00 00",
                RemovedEncodings = ["content: gzip"],
                DecodingStatus = "Decoded in wire-removal order: content: gzip."
            }
        };
        message.Headers.Add(new HttpHeader("Content-Type", "text/plain"));
        message.Headers.Add(new HttpHeader("Content-Encoding", "gzip"));

        var report = new SazReport { SourceName = "raw.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "3",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/raw",
                StatusCode = 200,
                Response = message
            });

        var html = new HtmlReportGenerator().Generate(report);

        // Raw is the initial/selected tab: no JSON/XML/MAPI, headers exist but Raw wins per priority.
        Assert.Contains(
            "id=\"response-tab-raw\" aria-controls=\"response-panel-raw\" aria-selected=\"true\" data-tab=\"raw\" tabindex=\"0\">Raw",
            html,
            StringComparison.Ordinal);

        var rawPanelStart = html.IndexOf("id=\"response-panel-raw\"", StringComparison.Ordinal);
        var rawPanelEnd = html.IndexOf("id=\"response-tab-", rawPanelStart + 1, StringComparison.Ordinal);
        var rawSection = rawPanelEnd > rawPanelStart ? html[rawPanelStart..rawPanelEnd] : html[rawPanelStart..];

        Assert.Contains("Original headers", rawSection, StringComparison.Ordinal);
        Assert.Contains("Content-Encoding: gzip", rawSection, StringComparison.Ordinal);
        Assert.Contains("Decoded body", rawSection, StringComparison.Ordinal);
        Assert.Contains("decode-status", rawSection, StringComparison.Ordinal);
        Assert.Contains("Decoded in wire-removal order: content: gzip.", rawSection, StringComparison.Ordinal);
        Assert.Contains("hello", rawSection, StringComparison.Ordinal);
        Assert.Contains("<details class=\"captured-bytes\"><summary>Captured bytes (pre-decode)</summary>", rawSection, StringComparison.Ordinal);
        Assert.Contains("1F 8B 08 00 00 00 00 00", rawSection, StringComparison.Ordinal);
    }

    [Fact]
    public void BinaryBodyDisablesJsonAndXmlAndUsesBoundedAccuratelyLabeledRawView()
    {
        var message = new HttpMessage
        {
            StartLine = "HTTP/1.1 200 OK",
            Body = new BodyPreview
            {
                Length = 4,
                CapturedLength = 4,
                IsBinary = true,
                IsTruncated = true,
                Preview = "DE AD BE EF"
            }
        };
        message.Headers.Add(new HttpHeader("Content-Type", "application/octet-stream"));

        var report = new SazReport { SourceName = "binary.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "4",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/bin",
                StatusCode = 200,
                Response = message
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains(
            "id=\"response-tab-json\" aria-controls=\"response-panel-json\" aria-selected=\"false\" data-tab=\"json\" tabindex=\"-1\" disabled aria-disabled=\"true\">JSON",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"response-tab-xml\" aria-controls=\"response-panel-xml\" aria-selected=\"false\" data-tab=\"xml\" tabindex=\"-1\" disabled aria-disabled=\"true\">XML",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"response-tab-raw\" aria-controls=\"response-panel-raw\" aria-selected=\"true\" data-tab=\"raw\" tabindex=\"0\">Raw",
            html,
            StringComparison.Ordinal);
        Assert.Contains("Binary / hex", html, StringComparison.Ordinal);
        Assert.Contains("DE AD BE EF", html, StringComparison.Ordinal);
    }

    [Fact]
    public void IsolatesRequestAndResponseTabContentFromEachOther()
    {
        var report = new SazReport { SourceName = "isolation.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "5",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/isolate",
                StatusCode = 200,
                Request = Message("POST /isolate HTTP/1.1", "application/json", """{"marker":"REQ_ONLY_MARKER"}"""),
                Response = Message("HTTP/1.1 200 OK", "application/xml", "<marker>RESP_ONLY_MARKER</marker>")
            });

        var html = new HtmlReportGenerator().Generate(report);

        var requestStart = html.IndexOf("<section class=\"message-panel\"><h3>Request</h3>", StringComparison.Ordinal);
        var responseStart = html.IndexOf("<section class=\"message-panel\"><h3>Response</h3>", StringComparison.Ordinal);
        Assert.True(requestStart >= 0 && responseStart > requestStart);

        var requestSection = html[requestStart..responseStart];
        var responseSection = html[responseStart..];
        var responseSectionEnd = responseSection.IndexOf("</div><div class=\"session-meta\">", StringComparison.Ordinal);
        if (responseSectionEnd > 0)
        {
            responseSection = responseSection[..responseSectionEnd];
        }

        Assert.Contains("REQ_ONLY_MARKER", requestSection, StringComparison.Ordinal);
        Assert.DoesNotContain("RESP_ONLY_MARKER", requestSection, StringComparison.Ordinal);
        Assert.Contains("RESP_ONLY_MARKER", responseSection, StringComparison.Ordinal);
        Assert.DoesNotContain("REQ_ONLY_MARKER", responseSection, StringComparison.Ordinal);

        // Distinct, side-scoped stable IDs guarantee independent aria wiring per pane.
        Assert.Contains("id=\"request-tab-json\"", requestSection, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"response-tab-json\"", requestSection, StringComparison.Ordinal);
        Assert.Contains("id=\"response-tab-xml\"", responseSection, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"request-tab-xml\"", responseSection, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesInjectionAttemptsAcrossJsonXmlMapiAndHeaderTabs()
    {
        const string jsonAttack = "</script><svg onload=alert('json')>";
        const string xmlAttack = "</script><svg onload=alert('xml')>";
        const string headerAttack = "</script><svg onload=alert('header')>";
        const string mapiAttack = "</script><svg onload=alert('mapi')>";

        var root = new MapiNode(
            "Execute",
            MapiNodeKind.Operation,
            0,
            8,
            null,
            [MapiNode.Leaf("PropertyValue", MapiNodeKind.Property, 4, 4, mapiAttack)]);
        var parse = new MapiMessageParse(MapiDirection.Request, root, ImmutableArray<string>.Empty, true, 8, 8);
        var mapi = new MapiSession("6", 0, MapiEndpoint.Mailbox, "Execute", "0", false, parse, null, ImmutableArray<string>.Empty);

        var request = Message(
            "POST /mapi HTTP/1.1",
            "application/json",
            $$"""{"value":"{{jsonAttack}}"}""");
        request.Headers.Add(new HttpHeader("X-Attack", headerAttack));
        var response = Message("HTTP/1.1 200 OK", "application/xml", $"<value>{xmlAttack}</value>");

        var session = new HttpSession
        {
            Id = "6",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/mapi",
            StatusCode = 200,
            Request = request,
            Response = response
        };
        session.Mapi = mapi;

        var report = new SazReport { SourceName = "injection.saz" };
        report.Sessions.Add(session);

        var html = new HtmlReportGenerator().Generate(report);

        Assert.DoesNotContain(jsonAttack, html, StringComparison.Ordinal);
        Assert.DoesNotContain(xmlAttack, html, StringComparison.Ordinal);
        Assert.DoesNotContain(headerAttack, html, StringComparison.Ordinal);
        Assert.DoesNotContain(mapiAttack, html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        Assert.Contains("&lt;/script&gt;&lt;svg onload=alert(&#39;json&#39;)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;/script&gt;&lt;svg onload=alert(&#39;xml&#39;)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;/script&gt;&lt;svg onload=alert(&#39;header&#39;)&gt;", html, StringComparison.Ordinal);
    }

    private static HttpMessage Message(string startLine, string contentType, string body)
    {
        var message = new HttpMessage
        {
            StartLine = startLine,
            Body = new BodyPreview
            {
                Length = body.Length,
                CapturedLength = body.Length,
                Preview = body
            }
        };
        message.Headers.Add(new HttpHeader("Content-Type", contentType));
        return message;
    }
}
