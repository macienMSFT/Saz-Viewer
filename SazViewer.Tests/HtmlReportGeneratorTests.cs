using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HtmlReportGeneratorTests
{
    [Fact]
    public void OpensFullScreenAccessibleDialogInspectorWithSafeContent()
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

        // Full-screen dialog inspector with correct dialog aria-modal/name semantics.
        Assert.Contains("<dialog id=\"httpInspector\" aria-labelledby=\"inspectorTitle\" aria-modal=\"true\">", html, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"inspectorTitle\" class=\"inspector-heading\"></h2>", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"inspectorBody\" class=\"inspector-body\"></div>", html, StringComparison.Ordinal);
        Assert.Contains("dialog#httpInspector{position:fixed;inset:0;width:100vw;height:100vh", html, StringComparison.Ordinal);
        Assert.Contains(".session-details{flex:0 0 auto;max-height:30vh;overflow:auto", html, StringComparison.Ordinal);
        Assert.Contains(".tab-panels.primary-panels{flex:1;min-height:0;display:flex;flex-direction:column;overflow:hidden", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".session-details{flex-basis:100%", html, StringComparison.Ordinal);
        Assert.Contains("<template id=\"http-detail-0\">", html, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"0\" aria-selected=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("template.content.cloneNode(true)", html, StringComparison.Ordinal);
        Assert.Contains("event.key==='Enter'||event.key===' '", html, StringComparison.Ordinal);
        Assert.Contains("inspector.showModal()", html, StringComparison.Ordinal);

        // Accessible Close/X, Escape close (native <dialog> cancel/close), and focus restore.
        Assert.Contains(
            "<button type=\"button\" id=\"inspectorClose\" class=\"inspector-close\" aria-label=\"Close session inspector\">",
            html,
            StringComparison.Ordinal);
        Assert.Contains("inspectorClose.addEventListener('click',closeInspector)", html, StringComparison.Ordinal);
        Assert.Contains("focusRow?.focus()", html, StringComparison.Ordinal);

        // Both sides render the always-visible five-tab secondary strip with correct roles.
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

        // Response body is XML: XML tab enabled/selected, JSON/MAPI disabled.
        Assert.Contains(
            "id=\"response-tab-xml\" aria-controls=\"response-panel-xml\" aria-selected=\"true\" data-tab=\"xml\" tabindex=\"0\">XML",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"response-tab-json\" aria-controls=\"response-panel-json\" aria-selected=\"false\" data-tab=\"json\" tabindex=\"-1\" disabled aria-disabled=\"true\">JSON",
            html,
            StringComparison.Ordinal);

        // Syntax highlighting hooks exist for Pretty Text only; safe DOM construction throughout.
        Assert.Contains("syn-key", html, StringComparison.Ordinal);
        Assert.Contains("syn-tag", html, StringComparison.Ordinal);
        Assert.Contains("document.createElement('span')", html, StringComparison.Ordinal);
        Assert.Contains("span.textContent=text", html, StringComparison.Ordinal);
        Assert.Contains("hydrateCopyModel(panel,model);", html, StringComparison.Ordinal);
        Assert.Contains("highlightSelected(panel);", html, StringComparison.Ordinal);

        // Global Formatted/Decoded/Captured toggle controls remain removed in favor of tabs.
        Assert.DoesNotContain("data-body-view", html, StringComparison.Ordinal);
        Assert.DoesNotContain("body-toggle", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"body-toolbar\"", html, StringComparison.Ordinal);

        Assert.DoesNotContain("<span class=\"syn-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
        Assert.DoesNotContain(attack, html, StringComparison.Ordinal);
        Assert.Equal(
            new BodyFormatter().Format(report.Sessions[0].Request!.Body, "application/json").Formatted,
            CopyTextForPanel(html, "request-panel-json"));
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimaryTabsAlwaysResetToRequestAndAreNeverDisabled()
    {
        var report = new SazReport { SourceName = "primary.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/",
                StatusCode = 200,
                Request = Message("GET / HTTP/1.1", "text/plain", "hello"),
                Response = Message("HTTP/1.1 200 OK", "text/plain", "world")
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains(
            "<div class=\"primary-tab-strip tab-strip\" role=\"tablist\" aria-label=\"Request or response\" data-side=\"primary\" data-priority=\"request,response\">",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"primary-tab-request\" aria-controls=\"primary-panel-request\" aria-selected=\"true\" data-tab=\"request\" tabindex=\"0\">Request",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"primary-tab-response\" aria-controls=\"primary-panel-response\" aria-selected=\"false\" data-tab=\"response\" tabindex=\"-1\">Response",
            html,
            StringComparison.Ordinal);
        // Primary tabs are never disabled, even though secondary content availability varies per side.
        Assert.DoesNotContain("id=\"primary-tab-request\" aria-controls=\"primary-panel-request\" aria-selected=\"true\" data-tab=\"request\" tabindex=\"0\" disabled", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"primary-panels tab-panels\">", html, StringComparison.Ordinal);
        Assert.Contains("id=\"primary-panel-request\" aria-labelledby=\"primary-tab-request\" tabindex=\"0\" class=\"tab-panel primary-panel\">", html, StringComparison.Ordinal);
        Assert.Contains("id=\"primary-panel-response\" aria-labelledby=\"primary-tab-response\" tabindex=\"0\" class=\"tab-panel primary-panel hidden\">", html, StringComparison.Ordinal);

        // Clearing preferredTab on every row load forces primary back to "request" (first in its
        // priority list) whenever a row is freshly opened or navigated to via Previous/Next.
        Assert.Contains("Object.keys(preferredTab).forEach(key=>delete preferredTab[key]);", html, StringComparison.Ordinal);
        Assert.Contains("for(const key of tablist.dataset.priority.split(',')){if(enabled(key))return key}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NewTabInspectorUsesBoundedEncodedStateAndSafeRestoration()
    {
        var report = new SazReport { SourceName = "new tab.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/path?q=<script>globalThis.pwned=true</script>",
                StatusCode = 200,
                Request = Message("GET / HTTP/1.1", "text/plain", "safe"),
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains(
            "id=\"inspectorOpenTab\" aria-label=\"Open in new tab: this session inspector\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("id=\"inspectorOpenStatus\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"reportStatus\"", html, StringComparison.Ordinal);
        Assert.Contains("const INSPECTOR_HASH_PREFIX='#saz-inspector?'", html, StringComparison.Ordinal);
        Assert.Contains("const MAX_INSPECTOR_HASH_LENGTH=4096", html, StringComparison.Ordinal);
        Assert.Contains("const MAX_INSPECTOR_QUERY_LENGTH=512", html, StringComparison.Ordinal);
        Assert.Contains("new URLSearchParams()", html, StringComparison.Ordinal);
        Assert.Contains("new URLSearchParams(hash.slice(INSPECTOR_HASH_PREFIX.length))", html, StringComparison.Ordinal);
        Assert.Contains("entries.length>8", html, StringComparison.Ordinal);
        Assert.Contains("!/^http-detail-\\d{1,9}$/.test(session)", html, StringComparison.Ordinal);
        Assert.Contains("ALLOWED_INSPECTOR_FILTERS.has(filter)", html, StringComparison.Ordinal);
        Assert.Contains("if(httpSearch.value.length>MAX_INSPECTOR_QUERY_LENGTH)return null", html, StringComparison.Ordinal);
        Assert.Contains("return hash.length<=MAX_INSPECTOR_HASH_LENGTH?hash:null", html, StringComparison.Ordinal);
        Assert.Contains("url.hash=state.slice(1)", html, StringComparison.Ordinal);
        Assert.Contains("popup.opener=null", html, StringComparison.Ordinal);
        Assert.Contains("try{popup=window.open(url,'_blank')}catch{}", html, StringComparison.Ordinal);
        Assert.Contains("try{popup.opener=null}catch{}", html, StringComparison.Ordinal);
        Assert.Contains("retainSelectionOnClose=true", html, StringComparison.Ordinal);
        Assert.Contains("const focusRow=retainSelectionOnClose?currentRow:originRow", html, StringComparison.Ordinal);
        Assert.Contains("  closeInspector();", html, StringComparison.Ordinal);
        Assert.Contains("The browser blocked the new tab.", html, StringComparison.Ordinal);
        Assert.Contains("httpSearch.value=state.query", html, StringComparison.Ordinal);
        Assert.Contains("httpFilter.value=state.filter", html, StringComparison.Ordinal);
        Assert.Contains("rows.find(candidate=>candidate.dataset.detail===state.session)", html, StringComparison.Ordinal);
        Assert.Contains("document.body.classList.add('inspector-only')", html, StringComparison.Ordinal);
        Assert.Contains("inspectorClose.textContent='Back to sessions'", html, StringComparison.Ordinal);
        Assert.Contains("No HTTP sessions match the restored inspector filters.", html, StringComparison.Ordinal);
        Assert.Contains("warning.textContent=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("document.write", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>globalThis.pwned=true</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyControlsUseCompleteBoundedModelTextForEverySecondaryView()
    {
        const string attack = "</textarea><script>globalThis.pwned=true</script>";
        const string requestBody = """{"payload":"</textarea><script>globalThis.pwned=true</script>","value":1}""";
        const string responseBody = "<root><value>safe</value></root>";
        var protocolRoot = new MapiNode(
            "Execute",
            MapiNodeKind.Operation,
            0,
            8,
            "root",
            [MapiNode.Leaf("PropertyValue", MapiNodeKind.Property, 4, 4, attack)]);
        var protocol = new MapiMessageParse(
            MapiDirection.Request,
            protocolRoot,
            ["synthetic warning"],
            true,
            8,
            8);
        var session = new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/copy",
            StatusCode = 200,
            Request = Message("POST /copy HTTP/1.1", "application/json", requestBody),
            Response = Message("HTTP/1.1 200 OK", "application/xml", responseBody),
        };
        session.Mapi = new MapiSession(
            "1",
            0,
            MapiEndpoint.Mailbox,
            "Execute",
            "0",
            false,
            protocol,
            null,
            ImmutableArray<string>.Empty);
        var report = new SazReport { SourceName = "copy.saz" };
        report.Sessions.Add(session);

        var html = new HtmlReportGenerator().Generate(report);
        var expectedJson = new BodyFormatter().Format(session.Request.Body, "application/json").Formatted;
        var expectedXml = new BodyFormatter().Format(session.Response.Body, "application/xml").Formatted;

        Assert.Equal(10, html.Split("class=\"copy-button\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(expectedJson, CopyTextForPanel(html, "request-panel-json"));
        Assert.Equal(expectedXml, CopyTextForPanel(html, "response-panel-xml"));
        Assert.Equal(
            "POST /copy HTTP/1.1\nContent-Type: application/json\n",
            CopyTextForPanel(html, "request-panel-headers"));
        Assert.Equal(
            $"Original headers\nPOST /copy HTTP/1.1\nContent-Type: application/json\n\n" +
            $"Body ({requestBody.Length} B)\nFormat: JSON\n" +
            "Status: Parsed as JSON from Content-Type and body content.\n" +
            requestBody,
            CopyTextForPanel(html, "request-panel-raw"));
        Assert.Contains("aria-label=\"Copy request JSON pretty text\"", PanelCopyButton(html, "request-panel-json"));
        Assert.Contains("data-copy-kind=\"mapi\"", PanelCopyButton(html, "request-panel-mapi"));
        Assert.Contains("disabled aria-disabled=\"true\"", PanelCopyButton(html, "response-panel-mapi"));
        Assert.Contains("synthetic warning", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.DoesNotContain(attack, html, StringComparison.Ordinal);
        Assert.Contains("setupCopyControls(inspectorBody)", html, StringComparison.Ordinal);
        Assert.Contains("navigator.clipboard.writeText(text)", html, StringComparison.Ordinal);
        Assert.Contains("document.execCommand('copy')", html, StringComparison.Ordinal);
        Assert.Contains("textarea.remove()", html, StringComparison.Ordinal);
        Assert.Contains("button.textContent='Copied'", html, StringComparison.Ordinal);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RawCopyLabelsBinaryTruncationAndRejectsSourcesOverTheCopyCap()
    {
        var binary = new HttpMessage
        {
            StartLine = "HTTP/1.1 200 OK",
            Body = new BodyPreview
            {
                Length = 5000,
                CapturedLength = 32,
                IsBinary = true,
                IsTruncated = true,
                Preview = "Binary body (5,000 bytes)\n00 FF 10 20",
                CapturedBytesPreview = "00FF1020",
                CapturedBytesPreviewTruncated = true,
            },
        };
        binary.Headers.Add(new HttpHeader("Content-Type", "application/octet-stream"));
        var report = new SazReport { SourceName = "binary.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Response = binary,
            });
        var html = new HtmlReportGenerator().Generate(report);
        var raw = CopyTextForPanel(html, "response-panel-raw");

        Assert.Contains("Format: Binary / hex", raw, StringComparison.Ordinal);
        Assert.Contains("Binary body; showing a bounded, truncated hex preview.", raw, StringComparison.Ordinal);
        Assert.Contains("[Body preview truncated; the complete body is not retained in this report.]", raw, StringComparison.Ordinal);
        Assert.Contains("Captured bytes (pre-decode)\n00FF1020\n[Captured byte preview truncated]", raw, StringComparison.Ordinal);
        Assert.Contains("disabled aria-disabled=\"true\"", PanelCopyButton(html, "response-panel-json"));

        var oversized = Message(
            "POST /large HTTP/1.1",
            "text/plain",
            new string('x', (1024 * 1024) + 1));
        var oversizedReport = new SazReport { SourceName = "oversized.saz" };
        oversizedReport.Sessions.Add(
            new HttpSession
            {
                Id = "2",
                ArchiveOrder = 0,
                Request = oversized,
            });
        var oversizedHtml = new HtmlReportGenerator().Generate(oversizedReport);
        var oversizedButton = PanelCopyButton(oversizedHtml, "request-panel-raw");
        Assert.Contains("data-copy-error=\"Copy source exceeds the 1 MiB safety limit.\"", oversizedButton);
        Assert.DoesNotContain("data-copy-key=", oversizedButton, StringComparison.Ordinal);
        Assert.Contains("data-copy-field=\"displayRawBody\"", oversizedHtml, StringComparison.Ordinal);
        Assert.Contains(
            "[Body display truncated at the 256 KiB rendering limit.]",
            CopyModelTextForPanel(oversizedHtml, "request-panel-raw", "displayRawBody"),
            StringComparison.Ordinal);

        var oversizedHeaders = Message("GET /headers HTTP/1.1", "text/plain", "body");
        oversizedHeaders.Headers.Add(new HttpHeader("X-Large", new string('h', (1024 * 1024) + 1)));
        var oversizedHeadersReport = new SazReport { SourceName = "oversized-headers.saz" };
        oversizedHeadersReport.Sessions.Add(new HttpSession
        {
            Id = "3",
            ArchiveOrder = 0,
            Request = oversizedHeaders,
        });
        var oversizedHeadersHtml = new HtmlReportGenerator().Generate(oversizedHeadersReport);
        Assert.Contains(
            "data-copy-error=\"Copy source exceeds the 1 MiB safety limit.\"",
            PanelCopyButton(oversizedHeadersHtml, "request-panel-headers"),
            StringComparison.Ordinal);
        Assert.Contains("data-copy-field=\"displayHeaders\"", oversizedHeadersHtml, StringComparison.Ordinal);
        Assert.Contains(
            "[Header display truncated at the 256 KiB rendering limit.]",
            CopyModelTextForPanel(oversizedHeadersHtml, "request-panel-headers", "displayHeaders"),
            StringComparison.Ordinal);
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

        // Deterministic priority order (data-driven, reused by both primary and secondary tabs).
        Assert.Contains("data-priority=\"mapi,json,xml,raw,headers\"", html, StringComparison.Ordinal);
        Assert.Contains("data-priority=\"request,response\"", html, StringComparison.Ordinal);

        // Roving tabindex: only the active tab is focusable, updated on activation.
        Assert.Contains("tab.tabIndex=active?0:-1", html, StringComparison.Ordinal);

        // Left/Right/Home/End keyboard behavior, skipping disabled tabs.
        Assert.Contains("['ArrowLeft','ArrowRight','Home','End'].includes(event.key)", html, StringComparison.Ordinal);
        Assert.Contains("tabsOf(tablist).filter(tab=>!tab.disabled)", html, StringComparison.Ordinal);
        Assert.Contains("nextIndex=(currentIndex+1)%enabledTabs.length", html, StringComparison.Ordinal);
        Assert.Contains("nextIndex=(currentIndex-1+enabledTabs.length)%enabledTabs.length", html, StringComparison.Ordinal);
        Assert.Contains("nextIndex=0", html, StringComparison.Ordinal);
        Assert.Contains("nextIndex=enabledTabs.length-1", html, StringComparison.Ordinal);

        // Disabled tabs are never clickable/activatable.
        Assert.Contains("if(!tab||tab.disabled||tab.parentElement!==tablist)return", html, StringComparison.Ordinal);

        // Remembered per-side tab preference, applied only when still enabled for the new session.
        Assert.Contains("const preferredTab={}", html, StringComparison.Ordinal);
        Assert.Contains("if(remembered&&enabled(remembered))return remembered", html, StringComparison.Ordinal);
        Assert.Contains("if(options&&options.remember)preferredTab[tablist.dataset.side]=key", html, StringComparison.Ordinal);
        Assert.Contains("activateTab(tablist,tab.dataset.tab,{remember:true})", html, StringComparison.Ordinal);
        Assert.Contains("activateTab(tablist,enabledTabs[nextIndex].dataset.tab,{focus:true,remember:true})", html, StringComparison.Ordinal);
        Assert.Contains("if(key)activateTab(tablist,key)", html, StringComparison.Ordinal);

        // Visible-focus CSS for tabs.
        Assert.Contains(".tab-strip [role=tab]:focus-visible{outline:2px solid var(--accent)", html, StringComparison.Ordinal);
        Assert.Contains(".tab-strip [role=tab]:disabled{color:", html, StringComparison.Ordinal);

        // setupTabs/highlighting/tree/protocol rendering runs on every row load.
        Assert.Contains("highlightSelected(panel);", html, StringComparison.Ordinal);
        Assert.Contains("prepareLazyPayloads(inspectorBody);", html, StringComparison.Ordinal);
        Assert.Contains("hydrateViewPayloads(targetPanel,renderGeneration);", html, StringComparison.Ordinal);
        Assert.Contains("setupTabs(inspectorBody);", html, StringComparison.Ordinal);
        Assert.Contains("setupTreeToggles(inspectorBody);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TablistKeydownIgnoresModifiedArrowKeysToAvoidConflictingWithInspectorNavigation()
    {
        // Regression test: Alt+Arrow is the inspector's Previous/Next shortcut (see
        // NavigationControlsExposeAccessibleLabelsAndOperateOnVisibleFilteredRows). The tablist's
        // own Left/Right/Home/End roving-tabindex handler must ignore Alt/Ctrl/Meta-modified
        // arrow/home/end key presses so it never also switches tabs (or otherwise intercepts the
        // event) at the same time the dialog is navigating rows.
        var report = new SazReport { SourceName = "modifiers.saz" };
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

        var keydownStart = html.IndexOf("tablist.addEventListener('keydown',event=>{", StringComparison.Ordinal);
        Assert.True(keydownStart >= 0, "Expected to find the tablist keydown handler.");
        var keydownEnd = html.IndexOf("});", keydownStart, StringComparison.Ordinal);
        Assert.True(keydownEnd > keydownStart, "Expected the tablist keydown handler to be closed.");
        var handlerBody = html[keydownStart..keydownEnd];

        Assert.Contains("if(event.altKey||event.ctrlKey||event.metaKey)return;", handlerBody, StringComparison.Ordinal);

        // The modifier guard must run before the arrow/home/end key check, not after.
        var modifierGuardIndex = handlerBody.IndexOf("if(event.altKey||event.ctrlKey||event.metaKey)return;", StringComparison.Ordinal);
        var arrowCheckIndex = handlerBody.IndexOf("['ArrowLeft','ArrowRight','Home','End'].includes(event.key)", StringComparison.Ordinal);
        Assert.True(modifierGuardIndex >= 0 && arrowCheckIndex >= 0 && modifierGuardIndex < arrowCheckIndex,
            "Expected the modifier guard to run before the tablist's own arrow/home/end handling.");
    }

    [Fact]
    public void NavigationControlsExposeAccessibleLabelsAndOperateOnVisibleFilteredRows()
    {
        var report = new SazReport { SourceName = "nav.saz" };
        for (var index = 0; index < 3; index++)
        {
            report.Sessions.Add(
                new HttpSession
                {
                    Id = index.ToString(),
                    ArchiveOrder = index,
                    Method = "GET",
                    Url = $"https://example.test/{index}",
                    StatusCode = 200
                });
        }

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains("id=\"inspectorPrev\" aria-label=\"Previous session\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"inspectorNext\" aria-label=\"Next session\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"inspectorPosition\" class=\"inspector-position\" aria-live=\"polite\"", html, StringComparison.Ordinal);

        // Previous/Next navigate the currently visible/filtered rows (not all rows), in table order.
        Assert.Contains("function visibleRows(){return httpRows.filter(row=>!row.classList.contains('hidden'))}", html, StringComparison.Ordinal);
        Assert.Contains("const rows=visibleRows();", html, StringComparison.Ordinal);
        Assert.Contains("const prevDisabled=index<=0;", html, StringComparison.Ordinal);
        Assert.Contains("const nextDisabled=index<0||index>=rows.length-1;", html, StringComparison.Ordinal);
        Assert.Contains("inspectorPrev.disabled=prevDisabled;", html, StringComparison.Ordinal);
        Assert.Contains("inspectorNext.disabled=nextDisabled;", html, StringComparison.Ordinal);
        Assert.Contains("row.scrollIntoView({block:'nearest'});", html, StringComparison.Ordinal);
        Assert.Contains("inspectorPrev.addEventListener('click',()=>navigate(-1));", html, StringComparison.Ordinal);
        Assert.Contains("inspectorNext.addEventListener('click',()=>navigate(1));", html, StringComparison.Ordinal);

        // Non-conflicting Alt+Arrow shortcuts (plain arrows are used by the tablist roving tabindex).
        Assert.Contains("if(!event.altKey)return;", html, StringComparison.Ordinal);
        Assert.Contains("if(event.key==='ArrowLeft'){event.preventDefault();navigate(-1)}", html, StringComparison.Ordinal);
        Assert.Contains("else if(event.key==='ArrowRight'){event.preventDefault();navigate(1)}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterAndScrollStateArePreservedOutsideDialog()
    {
        var report = new SazReport { SourceName = "filter.saz" };
        report.Sessions.Add(
            new HttpSession { Id = "1", ArchiveOrder = 0, Method = "GET", Url = "https://example.test/", StatusCode = 200 });

        var html = new HtmlReportGenerator().Generate(report);

        // The dialog is a fully separate overlay: opening/closing it never touches the underlying
        // table's filter inputs or scroll position, so only one, simple filter binding remains.
        Assert.Contains("bindFilter('httpSearch','httpFilter','httpTable');", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wsSearch", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wsFilter", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wsTable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("clearSelection", html, StringComparison.Ordinal);
        Assert.DoesNotContain("selected session is hidden by the active filter", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoWarningsOrWebSocketSectionsAreRendered()
    {
        var report = new SazReport { SourceName = "capture.saz" };
        report.Warnings.Add("Synthetic top-level warning");
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

        Assert.DoesNotContain("<h2>Warnings</h2>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>WebSocket messages</h2>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ws-table-scroll", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic top-level warning", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"HTTP sessions\"", html, StringComparison.Ordinal);

        var workspace = html.IndexOf("<section class=\"http-workspace\"", StringComparison.Ordinal);
        var search = html.IndexOf("id=\"httpSearch\"", StringComparison.Ordinal);
        Assert.True(workspace >= 0 && search > workspace);
    }

    [Fact]
    public void WebSocketSessionsUseCompressedAccessibleInspectorPayload()
    {
        const string hostile = """{"attack":"</script><svg onload=globalThis.pwned=true>","ok":true}""";
        var report = new SazReport { SourceName = "websocket.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "7",
            ArchiveOrder = 0,
            Method = "GET",
            Url = "wss://example.test/socket",
            StatusCode = 101
        });
        var message = new WebSocketMessage
        {
            SessionId = "7",
            MessageIndex = 0,
            RecordIndex = 0,
            Timestamp = DateTimeOffset.Parse("2024-05-01T12:00:00Z"),
            Direction = "Client",
            Type = "Text",
            PayloadLength = Encoding.UTF8.GetByteCount(hostile),
            Preview = hostile,
            Text = hostile,
            IsDecoded = true,
            IsComplete = true,
            IsFragmented = true,
            Payload = Encoding.UTF8.GetBytes(hostile)
        };
        message.Frames.Add(new WebSocketFrame
        {
            RecordIndex = 0,
            FiddlerId = 11,
            BitFlags = 0,
            Timestamp = message.Timestamp,
            Direction = "Client",
            Opcode = 1,
            Type = "Text",
            Masked = true,
            PayloadLength = 10,
            CapturedPayloadLength = 10,
            IsDecoded = true,
            Payload = Encoding.UTF8.GetBytes(hostile[..10])
        });
        message.Frames.Add(new WebSocketFrame
        {
            RecordIndex = 2,
            FiddlerId = 13,
            BitFlags = 4,
            Timestamp = message.Timestamp.Value.AddSeconds(2),
            Direction = "Client",
            Opcode = 0,
            Type = "Continuation",
            Final = true,
            Masked = true,
            PayloadLength = message.PayloadLength - 10,
            CapturedPayloadLength = hostile.Length - 10,
            IsDecoded = true,
            Payload = Encoding.UTF8.GetBytes(hostile[10..])
        });
        report.WebSocketMessages.Add(message);

        var html = new HtmlReportGenerator().Generate(report);
        using var payload = ExtractCompressedPayload(html, "websocket-session", 0, html.Length);
        var first = payload.RootElement.GetProperty("messages")[0];

        Assert.Contains("data-websocket=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"websocket\">WebSocket only</option>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"websocket-inspector\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payload-type=\"websocket-session\"", html, StringComparison.Ordinal);
        Assert.Contains(".ws-client .ws-arrow{color:#58a6ff}", html, StringComparison.Ordinal);
        Assert.Contains(".ws-server .ws-arrow{color:#3fb950}", html, StringComparison.Ordinal);
        Assert.Contains("['ID','Type','Body','Preview'].forEach", html, StringComparison.Ordinal);
        Assert.Contains(".ws-message-tracks{display:grid;grid-template-columns:max-content max-content max-content minmax(180px,1fr);min-width:100%}", html, StringComparison.Ordinal);
        Assert.Contains(".ws-message-header,.ws-message-list,.ws-message-row{display:grid;grid-template-columns:subgrid;grid-column:1/-1}", html, StringComparison.Ordinal);
        Assert.Contains(".ws-message-row.ws-filtered{height:0;min-height:0;border:0;visibility:hidden;overflow:hidden}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns:62px 74px 86px", html, StringComparison.Ordinal);
        Assert.Contains("grid.append(header,list);scroll.append(grid)", html, StringComparison.Ordinal);
        Assert.Contains("message.type==='Text'&&typeof message.text==='string'?message.text.toLowerCase():''", html, StringComparison.Ordinal);
        Assert.Contains("search.placeholder='Search WebSocket payloads...'", html, StringComparison.Ordinal);
        Assert.Contains("search.setAttribute('aria-label','Search WebSocket payloads')", html, StringComparison.Ordinal);
        Assert.Contains("searchStatus.setAttribute('aria-live','polite')", html, StringComparison.Ordinal);
        Assert.Contains("const query=search.value.trim().toLowerCase()", html, StringComparison.Ordinal);
        Assert.Contains("if(!visibleIndices.includes(selectedIndex))select(visibleIndices[0],false)", html, StringComparison.Ordinal);
        Assert.Contains("No WebSocket messages match this payload search.", html, StringComparison.Ordinal);
        Assert.Contains("role','separator'", html, StringComparison.Ordinal);
        Assert.Contains("Resize WebSocket traffic and payload panes", html, StringComparison.Ordinal);
        Assert.Contains("let webSocketSplitRatio=.38", html, StringComparison.Ordinal);
        Assert.Contains("splitter.setPointerCapture(event.pointerId)", html, StringComparison.Ordinal);
        Assert.Contains("aria-valuetext',`Left pane ${now} percent; right pane ${100-now} percent`", html, StringComparison.Ordinal);
        Assert.Contains("Search selected payload view...", html, StringComparison.Ordinal);
        Assert.Contains("highlightWebSocketSearchRoots", html, StringComparison.Ordinal);
        Assert.Contains("MAX_MATCHES=5000", html, StringComparison.Ordinal);
        Assert.Contains("document.createElement('mark')", html, StringComparison.Ordinal);
        Assert.Contains("function foldWebSocketSearchText(text)", html, StringComparison.Ordinal);
        Assert.Contains("for(let scan=nodeIndex;scan<nodes.length&&nodes[scan].start<end;scan++)", html, StringComparison.Ordinal);
        Assert.Contains("function move(delta)", html, StringComparison.Ordinal);
        Assert.Contains("event.key!=='Enter'||event.isComposing||event.keyCode===229||event.ctrlKey||event.altKey||event.metaKey", html, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault();move(event.shiftKey?-1:1);input.focus({preventScroll:true})", html, StringComparison.Ordinal);
        Assert.Contains("previous.addEventListener('click',()=>move(-1))", html, StringComparison.Ordinal);
        Assert.Contains("next.addEventListener('click',()=>move(1))", html, StringComparison.Ordinal);
        Assert.Contains("content.addEventListener('saz-view-change',run)", html, StringComparison.Ordinal);
        Assert.Contains("removeWebSocketSearchMarks(content)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", html, StringComparison.Ordinal);
        Assert.Contains("const logicalId=index+1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("const meta=wsElement('span','ws-message-meta'", html, StringComparison.Ordinal);
        Assert.Contains("`${message.payloadLengthText}${limited?'*':''}`", html, StringComparison.Ordinal);
        Assert.Contains("tablist.setAttribute('aria-label','WebSocket message views')", html, StringComparison.Ordinal);
        Assert.Contains("wsTab('json','JSON'", html, StringComparison.Ordinal);
        Assert.Contains("wsTab('text','Text'", html, StringComparison.Ordinal);
        Assert.Contains("wsTab('raw','Raw'", html, StringComparison.Ordinal);
        Assert.Contains("renderWebSocketInspector(host,generation)", html, StringComparison.Ordinal);
        Assert.DoesNotContain(hostile, html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h2>WebSocket messages</h2>", html, StringComparison.Ordinal);
        Assert.Equal(hostile, first.GetProperty("text").GetString());
        Assert.Equal("Text", first.GetProperty("listType").GetString());
        Assert.Equal(message.PayloadLength.ToString("N0", CultureInfo.InvariantCulture), first.GetProperty("payloadLengthText").GetString());
        Assert.DoesNotContain('\n', first.GetProperty("listPreview").GetString()!);
        Assert.Contains("\n  \"attack\":", first.GetProperty("jsonPretty").GetString(), StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"object\"", first.GetProperty("jsonTree").GetString(), StringComparison.Ordinal);
        Assert.Contains("Frame 0 ID=11 BitFlags=0", first.GetProperty("raw").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, first.GetProperty("frames").GetArrayLength());
    }

    [Fact]
    public void WebSocketTrafficColumnsUseLogicalLengthsTypesAndSingleLinePreviews()
    {
        var report = new SazReport { SourceName = "websocket-list.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "GET",
            Url = "wss://example.test/socket",
            StatusCode = 101
        });
        report.WebSocketMessages.Add(new WebSocketMessage
        {
            SessionId = "1",
            MessageIndex = 0,
            RecordIndex = 0,
            Direction = "Client",
            Type = "Text",
            PayloadLength = 6603,
            Preview = "unused",
            Text = " first\r\n\tsecond   third " + new string('x', 220),
            IsComplete = true,
            IsDecoded = true
        });
        report.WebSocketMessages.Add(new WebSocketMessage
        {
            SessionId = "1",
            MessageIndex = 1,
            RecordIndex = 1,
            Direction = "Server",
            Type = "Binary",
            PayloadLength = 20,
            Preview = "unused",
            IsBinary = true,
            IsComplete = true,
            IsDecoded = true,
            Payload = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray()
        });
        report.WebSocketMessages.Add(new WebSocketMessage
        {
            SessionId = "1",
            MessageIndex = 2,
            RecordIndex = 2,
            Direction = "Server",
            Type = "Ping",
            PayloadLength = 1,
            Preview = "unused",
            IsBinary = true,
            IsComplete = true,
            IsDecoded = true,
            Payload = new byte[] { 0x7E }
        });
        report.WebSocketMessages.Add(new WebSocketMessage
        {
            SessionId = "1",
            MessageIndex = 3,
            RecordIndex = 3,
            Direction = "Unknown",
            Type = "Undecoded",
            PayloadLength = 22908,
            Preview = "unused",
            IsBinary = true,
            Warning = "bad\r\n\tframe   data"
        });
        report.WebSocketMessages.Add(new WebSocketMessage
        {
            SessionId = "1",
            MessageIndex = 4,
            RecordIndex = 4,
            Direction = "Client",
            Type = "Text",
            PayloadLength = 387,
            Preview = "unused",
            Warning = "capture ended",
            IsFragmented = true
        });

        var html = new HtmlReportGenerator().Generate(report);
        using var payload = ExtractCompressedPayload(html, "websocket-session", 0, html.Length);
        var messages = payload.RootElement.GetProperty("messages");

        Assert.Equal("6,603", messages[0].GetProperty("payloadLengthText").GetString());
        Assert.Equal("Text", messages[0].GetProperty("listType").GetString());
        Assert.Equal("first second third " + new string('x', 160) + "\u2026", messages[0].GetProperty("listPreview").GetString());
        Assert.DoesNotContain('\n', messages[0].GetProperty("listPreview").GetString()!);
        Assert.Equal(
            "Binary (20 bytes): 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F \u2026",
            messages[1].GetProperty("listPreview").GetString());
        Assert.Equal("Ping control (1 byte): 7E", messages[2].GetProperty("listPreview").GetString());
        Assert.Equal("Invalid", messages[3].GetProperty("listType").GetString());
        Assert.Equal("Invalid/partial: bad frame data", messages[3].GetProperty("listPreview").GetString());
        Assert.Equal("Partial", messages[4].GetProperty("listType").GetString());
        Assert.Equal("22,908", messages[3].GetProperty("payloadLengthText").GetString());
    }

    [Fact]
    public void NoObsoleteSplitterOrResizerCodeRemains()
    {
        var report = new SazReport { SourceName = "no-resizer.saz" };
        report.Sessions.Add(
            new HttpSession { Id = "1", ArchiveOrder = 0, Method = "GET", Url = "https://example.test/", StatusCode = 200 });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.DoesNotContain("httpDetails", html, StringComparison.Ordinal);
        Assert.DoesNotContain("detailResizer", html, StringComparison.Ordinal);
        Assert.DoesNotContain("detail-resizer", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".detail-pane{", html, StringComparison.Ordinal);
        Assert.DoesNotContain("--detail-height", html, StringComparison.Ordinal);
        Assert.DoesNotContain("resizeTo(", html, StringComparison.Ordinal);
        Assert.DoesNotContain("updateResizeAria", html, StringComparison.Ordinal);
        Assert.DoesNotContain("message-grid", html, StringComparison.Ordinal);
        Assert.DoesNotContain("session-heading", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveLayoutIsSingleFullScreenSideAtAllWidths()
    {
        var report = new SazReport { SourceName = "responsive.saz" };
        report.Sessions.Add(
            new HttpSession { Id = "1", ArchiveOrder = 0, Method = "GET", Url = "https://example.test/", StatusCode = 200 });

        var html = new HtmlReportGenerator().Generate(report);

        // HTTP remains a single visible side. WebSocket traffic intentionally uses a bounded
        // two-column inspector that stacks at the existing narrow breakpoint.
        Assert.Contains(".ws-layout{flex:1;min-height:0;display:flex;gap:6px;overflow:hidden}", html, StringComparison.Ordinal);
        Assert.Contains("dialog#httpInspector{position:fixed;inset:0;width:100vw;height:100vh;max-width:100vw;max-height:100vh", html, StringComparison.Ordinal);
        Assert.Contains("@media(max-width:900px){main{padding:2px}.ws-layout{display:grid;grid-template-columns:1fr;grid-template-rows:", html, StringComparison.Ordinal);
        Assert.Contains(".primary-panel{flex:1;min-height:0;display:flex;flex-direction:column", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TreeAndPrettyTextToggleMarkupAndAriaSemanticsArePresent()
    {
        var report = new SazReport { SourceName = "toggle.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/api",
                StatusCode = 200,
                Request = Message("POST /api HTTP/1.1", "application/json", """{"a":1}""")
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains("<div class=\"structured-body\" data-format=\"json\">", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"tree-toolbar\"><div class=\"view-toggle\" role=\"group\" aria-label=\"JSON view mode\">", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" data-view=\"tree\" aria-pressed=\"true\">Tree</button>", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" data-view=\"pretty\" aria-pressed=\"false\">Pretty Text</button>", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"tree-expand-all\">Expand all</button>", html, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"tree-collapse-all\">Collapse all</button>", html, StringComparison.Ordinal);
        Assert.Contains("class=\"tree-subview\" data-compressed-payload=\"", html, StringComparison.Ordinal);
        Assert.Contains("data-payload-type=\"json-tree\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"pretty-subview hidden\"", html, StringComparison.Ordinal);

        // Toggle behavior and tree accessibility semantics (tree/treeitem/group, focus-visible).
        Assert.Contains("function setupTreeToggles(root){", html, StringComparison.Ordinal);
        Assert.Contains("treeView.classList.toggle('hidden',!showTree);", html, StringComparison.Ordinal);
        Assert.Contains("prettyView.classList.toggle('hidden',showTree);", html, StringComparison.Ordinal);
        Assert.Contains("tree.setAttribute('role','tree');", html, StringComparison.Ordinal);
        Assert.Contains("item.setAttribute('role','treeitem');", html, StringComparison.Ordinal);
        Assert.Contains("group.setAttribute('role','group');", html, StringComparison.Ordinal);
        Assert.Contains(".tree-item[role=treeitem]:focus-visible>.tree-row", html, StringComparison.Ordinal);

        // Initially expanded by default (desiredExpanded starts true; Expand/Collapse all can
        // change it live, and that state is what still-queued nodes consult as they stream in).
        Assert.Contains("if(hasKids)item.setAttribute('aria-expanded',String(desiredExpanded));", html, StringComparison.Ordinal);
        Assert.Contains("let desiredExpanded=true;", html, StringComparison.Ordinal);

        // Individual mouse + Enter/Space toggles, plus roving-tabindex arrow-key traversal.
        Assert.Contains("row.addEventListener('click',event=>{event.stopPropagation();setActive(item,true);toggle()});", html, StringComparison.Ordinal);
        Assert.Contains("if(key==='Enter'||key===' '){", html, StringComparison.Ordinal);
        Assert.Contains("if(item._toggle){event.preventDefault();item._toggle()}", html, StringComparison.Ordinal);
        Assert.Contains("key==='ArrowDown'", html, StringComparison.Ordinal);
        Assert.Contains("key==='ArrowUp'", html, StringComparison.Ordinal);
        Assert.Contains("key==='ArrowRight'", html, StringComparison.Ordinal);
        Assert.Contains("key==='ArrowLeft'", html, StringComparison.Ordinal);
        Assert.Contains("key==='Home'", html, StringComparison.Ordinal);
        Assert.Contains("key==='End'", html, StringComparison.Ordinal);

        // Expand all / Collapse all bulk controls.
        Assert.Contains("function setAllExpanded(container,expanded){", html, StringComparison.Ordinal);
        Assert.Contains("tree-expand-all", html, StringComparison.Ordinal);
        Assert.Contains("tree-collapse-all", html, StringComparison.Ordinal);

        // Bounded/batched rendering so large expanded documents cannot freeze the page, with a
        // generation token so a superseded (e.g. row-navigated-away-from) build stops scheduling
        // further batches instead of continuing to render into detached DOM.
        Assert.Contains("if(queue.length&&generation===renderGeneration)requestAnimationFrame(step);", html, StringComparison.Ordinal);
        Assert.Contains("if(generation!==renderGeneration||host._treeBuildToken!==buildToken)return;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TreeUsesTrueRovingTabindexWithVisibleArrowTraversalAndStatusEntriesNeverDefaultFocusable()
    {
        // Regression test: previously every JSON/XML treeitem (and every truncation/depth-limit
        // status entry) got tabIndex=0 unconditionally, so a large tree created thousands of page
        // Tab stops and there was no ArrowUp/Down/Left/Right/Home/End traversal at all. There must
        // now be exactly one roving tab stop per tree, managed by setActive(), plus a full
        // keyboard traversal contract, and status entries must default to -1 (never spawning their
        // own uncontrolled Tab stop).
        var report = new SazReport { SourceName = "roving.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/api",
                StatusCode = 200,
                Request = Message("POST /api HTTP/1.1", "application/json", """{"a":1}""")
            });

        var html = new HtmlReportGenerator().Generate(report);

        // Items and status entries are created with tabIndex=-1; only setActive() promotes one to 0.
        Assert.Contains("item.className='tree-item';item.setAttribute('role','treeitem');item.tabIndex=-1;", html, StringComparison.Ordinal);
        Assert.Contains("status.setAttribute('role','treeitem');status.tabIndex=-1;", html, StringComparison.Ordinal);
        Assert.Contains("function setActive(item,focus){", html, StringComparison.Ordinal);
        Assert.Contains("if(activeItem&&activeItem!==item)activeItem.tabIndex=-1;", html, StringComparison.Ordinal);
        Assert.Contains("item.tabIndex=0;activeItem=item;", html, StringComparison.Ordinal);
        Assert.Contains("if(focus)item.focus();", html, StringComparison.Ordinal);
        Assert.Contains("if(!activeItem)setActive(item,false);", html, StringComparison.Ordinal);
        Assert.Contains("if(!activeItem)setActive(status,false);", html, StringComparison.Ordinal);

        // Visible-order traversal helpers (skip collapsed/hidden subtrees).
        Assert.Contains("function isVisible(el){", html, StringComparison.Ordinal);
        Assert.Contains("if(node.classList.contains('tree-group')&&node.hidden)return false;", html, StringComparison.Ordinal);
        Assert.Contains("function visibleStops(){return stops().filter(isVisible)}", html, StringComparison.Ordinal);
        Assert.Contains("function restoreVisibleActive(focus){", html, StringComparison.Ordinal);
        Assert.Contains("while(candidate&&!isVisible(candidate))candidate=ownerItem(candidate);", html, StringComparison.Ordinal);
        Assert.Contains("if(!value)restoreVisibleActive(focusWasInTree);", html, StringComparison.Ordinal);
        Assert.Contains("if(current&&current.closest('.tree-group[hidden]')){", html, StringComparison.Ordinal);
        Assert.Contains("candidate??=container.querySelector('.tree-view>.tree-item,.tree-view>.tree-status');", html, StringComparison.Ordinal);

        // ArrowUp/Down move across the visible flattened order.
        Assert.Contains("if(key==='ArrowDown'){", html, StringComparison.Ordinal);
        Assert.Contains("if(key==='ArrowUp'){", html, StringComparison.Ordinal);
        Assert.Contains("const list=visibleStops(),index=list.indexOf(item);", html, StringComparison.Ordinal);
        Assert.Contains("if(index>=0&&index<list.length-1){event.preventDefault();setActive(list[index+1],true)}", html, StringComparison.Ordinal);
        Assert.Contains("if(index>0){event.preventDefault();setActive(list[index-1],true)}", html, StringComparison.Ordinal);

        // ArrowRight expands a collapsed node or moves into its first child when already expanded.
        Assert.Contains("if(key==='ArrowRight'){", html, StringComparison.Ordinal);
        Assert.Contains("if(!expanded){item._toggle();return}", html, StringComparison.Ordinal);
        Assert.Contains(
            "const first=group&&[...group.children].find(child=>child.classList.contains('tree-item')||child.classList.contains('tree-status'));",
            html,
            StringComparison.Ordinal);
        Assert.Contains("if(first)setActive(first,true);", html, StringComparison.Ordinal);

        // ArrowLeft collapses an expanded node or moves focus to its parent (ownerItem).
        Assert.Contains("if(key==='ArrowLeft'){", html, StringComparison.Ordinal);
        Assert.Contains("if(item._toggle&&item.getAttribute('aria-expanded')==='true'){item._toggle();return}", html, StringComparison.Ordinal);
        Assert.Contains("function ownerItem(el){", html, StringComparison.Ordinal);
        Assert.Contains("return container&&container.classList.contains('tree-group')?container.parentElement:null;", html, StringComparison.Ordinal);
        Assert.Contains("const owner=ownerItem(item);", html, StringComparison.Ordinal);

        // Home/End jump to the first/last visible stop in the whole tree.
        Assert.Contains("if(key==='Home'){", html, StringComparison.Ordinal);
        Assert.Contains("if(key==='End'){", html, StringComparison.Ordinal);
        Assert.Contains("if(list.length){event.preventDefault();setActive(list[0],true)}", html, StringComparison.Ordinal);
        Assert.Contains("if(list.length){event.preventDefault();setActive(list[list.length-1],true)}", html, StringComparison.Ordinal);

        // Modifier keys (other than the Enter/Space toggle path) are ignored inside the tree too,
        // so Alt+Arrow inspector navigation and Shift-selection gestures are not hijacked.
        Assert.Contains("if(event.altKey||event.ctrlKey||event.metaKey||event.shiftKey)return;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationPreservesFocusOnAStableControlAtBoundariesAndAfterRowReplacement()
    {
        // Regression test: Previous/Next used to just set inspectorPrev/Next.disabled at the
        // boundary, which silently blurs a focused-but-now-disabled button (losing keyboard
        // context), and loadRow() replaced #inspectorBody's content outright even if focus was
        // currently inside it (e.g. a secondary tab or tree item reached via the Alt+Arrow
        // shortcut), stranding focus nowhere. Both must now land focus on a stable, guaranteed
        // enabled control instead.
        var report = new SazReport { SourceName = "focus.saz" };
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

        // updateNavState() moves focus off a soon-to-be-disabled Prev/Next button before disabling
        // it, preferring the sibling that remains enabled, falling back to a stable control.
        Assert.Contains("const activeWasPrev=document.activeElement===inspectorPrev;", html, StringComparison.Ordinal);
        Assert.Contains("const activeWasNext=document.activeElement===inspectorNext;", html, StringComparison.Ordinal);
        Assert.Contains("if((activeWasPrev&&prevDisabled)||(activeWasNext&&nextDisabled)){", html, StringComparison.Ordinal);
        Assert.Contains("if(activeWasPrev&&!nextDisabled)inspectorNext.focus();", html, StringComparison.Ordinal);
        Assert.Contains("else if(activeWasNext&&!prevDisabled)inspectorPrev.focus();", html, StringComparison.Ordinal);
        Assert.Contains("else focusStableInspectorControl();", html, StringComparison.Ordinal);
        Assert.Contains("function focusStableInspectorControl(){", html, StringComparison.Ordinal);
        Assert.Contains("const primaryRequestTab=inspectorBody.querySelector('.primary-tab-strip [role=\"tab\"][aria-selected=\"true\"]');", html, StringComparison.Ordinal);

        // loadRow() captures whether focus was inside the body before replacing it. HTTP restores
        // the Request tab; an asynchronously rendered WebSocket destination uses the stable Close
        // control rather than leaving focus on detached content.
        Assert.Contains("const focusWasInBody=inspectorBody.contains(document.activeElement);", html, StringComparison.Ordinal);
        Assert.Contains("if(focusWasInBody){", html, StringComparison.Ordinal);
        Assert.Contains("if(primaryRequestTab)primaryRequestTab.focus();", html, StringComparison.Ordinal);
        Assert.Contains("else inspectorClose.focus();", html, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonTreePreservesSourceOrderDuplicateKeysTypesAndCounts()
    {
        var report = new SazReport { SourceName = "json-tree.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/api",
                StatusCode = 200,
                Request = Message(
                    "POST /api HTTP/1.1",
                    "application/json",
                    """{"dup":1,"dup":2,"arr":[10,"two",null,true,false],"obj":{"inner":"v"}}""")
            });

        var html = new HtmlReportGenerator().Generate(report);
        using var payload = ExtractTreePayload(html, "data-json-tree");
        var root = payload.RootElement;

        Assert.Equal("object", root.GetProperty("kind").GetString());
        Assert.Equal(4, root.GetProperty("count").GetInt32());
        var children = root.GetProperty("children");
        Assert.Equal(4, children.GetArrayLength());

        // Duplicate keys and original source order are preserved (not deduplicated/reordered).
        Assert.Equal("dup", children[0].GetProperty("name").GetString());
        Assert.Equal("1", children[0].GetProperty("value").GetString());
        Assert.Equal("dup", children[1].GetProperty("name").GetString());
        Assert.Equal("2", children[1].GetProperty("value").GetString());

        var arr = children[2];
        Assert.Equal("arr", arr.GetProperty("name").GetString());
        Assert.Equal("array", arr.GetProperty("kind").GetString());
        Assert.Equal(5, arr.GetProperty("count").GetInt32());
        var arrChildren = arr.GetProperty("children");
        Assert.Equal(5, arrChildren.GetArrayLength());
        Assert.Equal("number", arrChildren[0].GetProperty("kind").GetString());
        Assert.True(arrChildren[0].GetProperty("isIndex").GetBoolean());
        Assert.Equal("0", arrChildren[0].GetProperty("name").GetString());
        Assert.Equal("string", arrChildren[1].GetProperty("kind").GetString());
        Assert.Equal("two", arrChildren[1].GetProperty("value").GetString());
        Assert.Equal("null", arrChildren[2].GetProperty("kind").GetString());
        Assert.Equal("boolean", arrChildren[3].GetProperty("kind").GetString());
        Assert.Equal("true", arrChildren[3].GetProperty("value").GetString());
        Assert.Equal("boolean", arrChildren[4].GetProperty("kind").GetString());
        Assert.Equal("false", arrChildren[4].GetProperty("value").GetString());

        var obj = children[3];
        Assert.Equal("obj", obj.GetProperty("name").GetString());
        Assert.Equal("object", obj.GetProperty("kind").GetString());
        Assert.Equal(1, obj.GetProperty("count").GetInt32());
        var inner = obj.GetProperty("children")[0];
        Assert.Equal("inner", inner.GetProperty("name").GetString());
        Assert.Equal("string", inner.GetProperty("kind").GetString());
        Assert.Equal("v", inner.GetProperty("value").GetString());
    }

    [Fact]
    public void XmlTreePreservesOrderAttributesNamespacesTextCdataAndComments()
    {
        var report = new SazReport { SourceName = "xml-tree.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/api",
                StatusCode = 200,
                Request = Message(
                    "POST /api HTTP/1.1",
                    "application/xml",
                    "<root xmlns:ns=\"urn:test\"><ns:child ns:attr=\"v\">Hello</ns:child><!--note--><![CDATA[cdata-content]]></root>")
            });

        var html = new HtmlReportGenerator().Generate(report);
        using var payload = ExtractTreePayload(html, "data-xml-tree");
        var root = payload.RootElement;

        Assert.Equal("document", root.GetProperty("kind").GetString());
        var docChildren = root.GetProperty("children");
        Assert.Equal(1, docChildren.GetArrayLength());

        var rootElement = docChildren[0];
        Assert.Equal("element", rootElement.GetProperty("kind").GetString());
        Assert.Equal("root", rootElement.GetProperty("name").GetString());
        var rootAttrs = rootElement.GetProperty("attrs");
        Assert.Equal(1, rootAttrs.GetArrayLength());
        Assert.Equal("xmlns:ns", rootAttrs[0].GetProperty("name").GetString());
        Assert.Equal("urn:test", rootAttrs[0].GetProperty("value").GetString());

        var rootKids = rootElement.GetProperty("children");
        Assert.Equal(3, rootKids.GetArrayLength());

        // Document order preserved: element, comment, CDATA (in source order).
        var childElement = rootKids[0];
        Assert.Equal("element", childElement.GetProperty("kind").GetString());
        Assert.Equal("ns:child", childElement.GetProperty("name").GetString());
        var childAttrs = childElement.GetProperty("attrs");
        Assert.Equal(1, childAttrs.GetArrayLength());
        Assert.Equal("ns:attr", childAttrs[0].GetProperty("name").GetString());
        Assert.Equal("v", childAttrs[0].GetProperty("value").GetString());
        var text = childElement.GetProperty("children")[0];
        Assert.Equal("text", text.GetProperty("kind").GetString());
        Assert.Equal("Hello", text.GetProperty("value").GetString());

        var comment = rootKids[1];
        Assert.Equal("comment", comment.GetProperty("kind").GetString());
        Assert.Equal("note", comment.GetProperty("value").GetString());

        var cdata = rootKids[2];
        Assert.Equal("cdata", cdata.GetProperty("kind").GetString());
        Assert.Equal("cdata-content", cdata.GetProperty("value").GetString());
    }

    [Fact]
    public void DeeplyNestedJsonHitsDepthLimitAndStillProducesAValidSerializableTreePayload()
    {
        // Regression test: a TreeNode chain alternates through two JSON nesting levels per tree
        // level (object, then its "children" array, then the next object), so serializing a
        // depth-limited tree can require roughly double the original document's nesting depth.
        // This must stay comfortably under JsonSerializerOptions.MaxDepth or the payload silently
        // fails to serialize, disabling the Tree toggle even though the document is exactly the
        // kind of over-deep-but-otherwise-valid input the depth budget exists to still show,
        // truncated, in the tree (falling back to Pretty Text only is the wrong outcome here).
        string DeepJson(int depth) => depth <= 0 ? $"\"{new string('x', 4000)}\"" : $$"""{"n":{{DeepJson(depth - 1)}}}""";
        var deepBody = DeepJson(50); // Above TreeMaxDepth(40), below BodyFormatter's safe depth(64).

        var report = new SazReport { SourceName = "deep.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/deep",
                StatusCode = 200,
                Response = Message("HTTP/1.1 200 OK", "application/json", deepBody)
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains(
            "<button type=\"button\" data-view=\"tree\" aria-pressed=\"true\">Tree</button>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Tree view is not available for this body", html, StringComparison.Ordinal);

        using var payload = ExtractTreePayload(html, "data-json-tree");
        var node = payload.RootElement;
        var depth = 0;
        while (node.TryGetProperty("children", out var children) && children.GetArrayLength() > 0)
        {
            node = children[0];
            depth++;
            if (depth > 45)
            {
                break;
            }
        }

        Assert.True(node.GetProperty("depthLimited").GetBoolean(), "Expected the deepest retained node to be marked depthLimited.");
        // Regression test (issue 4): the depth-limited node must report its real immediate
        // property count (1, for the single "n" key at every level of this fixture) instead of
        // omitting Count entirely, which the client then defaulted to a misleading "0 properties".
        Assert.Equal(1, node.GetProperty("count").GetInt32());
        Assert.Contains("Maximum nesting depth reached", html, StringComparison.Ordinal);
        Assert.DoesNotContain("(0 properties)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DeeplyNestedXmlHitsDepthLimitAndReportsAccurateImmediateChildCount()
    {
        // Regression test (issue 4), XML side: BuildXmlElement's depth-limit branch used to skip
        // the subtree without ever counting it, leaving Count unset and rendering a false
        // "(0 children)" label for a node that actually has real (just not descended-into)
        // children. The immediate child count (here: 2 - one child element plus a comment) must
        // now be accurate.
        string DeepXml(int depth) => depth <= 0
            ? $"<leaf>{new string('x', 3000)}</leaf>"
            : $"<n>{DeepXml(depth - 1)}<!--c--></n>";
        var deepBody = $"<root>{DeepXml(45)}</root>"; // Above TreeMaxDepth(40), below MaxStructuredDepth(64),
                                                        // and padded so pretty-print expansion stays within
                                                        // BodyFormatter's safe expansion-ratio limit.

        var report = new SazReport { SourceName = "deep-xml.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "GET",
                Url = "https://example.test/deep-xml",
                StatusCode = 200,
                Response = Message("HTTP/1.1 200 OK", "application/xml", deepBody)
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains(
            "<button type=\"button\" data-view=\"tree\" aria-pressed=\"true\">Tree</button>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Tree view is not available for this body", html, StringComparison.Ordinal);

        using var payload = ExtractTreePayload(html, "data-xml-tree");
        var node = payload.RootElement.GetProperty("children")[0]; // <root>
        var depth = 0;
        while (node.TryGetProperty("children", out var children)
               && children.GetArrayLength() > 0
               && children[0].GetProperty("kind").GetString() == "element")
        {
            node = children[0];
            depth++;
            if (depth > 45)
            {
                break;
            }
        }

        Assert.True(node.GetProperty("depthLimited").GetBoolean(), "Expected the deepest retained element to be marked depthLimited.");
        Assert.Equal(2, node.GetProperty("count").GetInt32());
        Assert.Contains("Maximum nesting depth reached", html, StringComparison.Ordinal);
        Assert.DoesNotContain("(0 children)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TreeBudgetsProduceAccurateOmittedCountsForOverBudgetCollections()
    {
        var numbers = string.Join(',', Enumerable.Range(0, 500));
        var report = new SazReport { SourceName = "budget.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/api",
                StatusCode = 200,
                Request = Message("POST /api HTTP/1.1", "application/json", $"[{numbers}]")
            });

        var html = new HtmlReportGenerator().Generate(report);
        using var payload = ExtractTreePayload(html, "data-json-tree");
        var root = payload.RootElement;

        Assert.Equal("array", root.GetProperty("kind").GetString());
        Assert.Equal(500, root.GetProperty("count").GetInt32());
        var children = root.GetProperty("children");
        Assert.Equal(300, children.GetArrayLength()); // TreeMaxChildrenPerNode
        Assert.Equal(200, root.GetProperty("omitted").GetInt32());

        // Client renderer shows an accurate, discoverable truncation status node.
        Assert.Contains("more not shown here", html, StringComparison.Ordinal);
        Assert.Contains("Maximum nesting depth reached", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchedRendererQueuesStatusEntriesAfterSiblingsHonorsLiveExpandStateAndCancelsOnRowChange()
    {
        // Regression test (issue 5): the batched tree renderer previously (a) appended
        // truncation/depth-limit status nodes to a group synchronously, before its real
        // (still-queued, not-yet-appended) children, producing the wrong DOM order; (b) baked
        // "expanded" into each node at creation time with no way for Expand/Collapse all to affect
        // nodes appended in a later batch; and (c) had no way to stop a still-running batched
        // build for a row the user has since navigated away from. All three must now be fixed:
        // status entries are queued (not appended immediately) so they land after their real
        // siblings once the queue drains; a per-tree "desired expanded" state is consulted by
        // appendItem for every node (including ones not yet created); and a renderGeneration
        // token is checked before each batch/step so a superseded build stops cleanly.
        var report = new SazReport { SourceName = "batching.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/api",
                StatusCode = 200,
                Request = Message("POST /api HTTP/1.1", "application/json", """{"a":1}""")
            });

        var html = new HtmlReportGenerator().Generate(report);

        // (a) Status entries for omitted/depth-limited content are queued, not appended eagerly,
        // so they always land after their real (possibly still-queued) sibling nodes.
        Assert.Contains(
            "if(node.omitted>0)queue.push({status:true,parent:group,text:`+${node.omitted} more not shown here",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "if(node.depthLimited)queue.push({status:true,parent:group,text:'Maximum nesting depth reached",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "if(rootNode.omitted>0)queue.push({status:true,parent:tree,text:`+${rootNode.omitted} more not shown here",
            html,
            StringComparison.Ordinal);
        Assert.Contains("if(entry.status)appendStatusEntry(entry.parent,entry.text);", html, StringComparison.Ordinal);
        Assert.Contains("else appendItem(entry.node,entry.parent,entry.depth);", html, StringComparison.Ordinal);
        // The old eager "appendStatus(group,...)"/"appendStatus(tree,...)" call sites (which
        // appended out-of-order, ahead of queued children) must be gone.
        Assert.DoesNotContain("appendStatus(group,", html, StringComparison.Ordinal);
        Assert.DoesNotContain("appendStatus(tree,", html, StringComparison.Ordinal);

        // (b) A live "desired expanded" flag, consulted by appendItem for every node it creates
        // (including ones still sitting in the queue when Expand/Collapse all is clicked), rather
        // than a value baked in once at buildTree() call time.
        Assert.Contains("host._setDesiredExpanded=value=>{", html, StringComparison.Ordinal);
        Assert.Contains("desiredExpanded=value;", html, StringComparison.Ordinal);
        Assert.Contains("if(typeof container._setDesiredExpanded==='function'){container._setDesiredExpanded(expanded);return}", html, StringComparison.Ordinal);
        Assert.Contains("group.hidden=!desiredExpanded;", html, StringComparison.Ordinal);

        // (c) Generation/cancellation token: loadRow() bumps renderGeneration before rendering,
        // and step() bails out (without scheduling a further requestAnimationFrame) once a newer
        // generation has superseded it, so a build for a row the user has navigated away from
        // stops instead of continuing to build into detached DOM.
        Assert.Contains("renderGeneration++;", html, StringComparison.Ordinal);
        Assert.Contains("const generation=renderGeneration;", html, StringComparison.Ordinal);
        Assert.Contains("function buildTree(host,rootNode,kind,generation){", html, StringComparison.Ordinal);
        Assert.Contains("async function renderValueTree(host,generation){", html, StringComparison.Ordinal);
        Assert.Contains("buildTree(host,payload,kind,generation);", html, StringComparison.Ordinal);
        Assert.Contains("if(generation!==renderGeneration||host.classList.contains('hidden')||host.closest('.tab-panel.hidden'))return;", html, StringComparison.Ordinal);
        Assert.Contains("if(queue.length&&generation===renderGeneration)requestAnimationFrame(step);", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedRecognizedBodyDisablesStructuredTabsAndKeepsRawIntact()
    {
        var badJson = Message("POST /bad HTTP/1.1", "application/json", "{not valid json");
        var badXml = Message("HTTP/1.1 200 OK", "application/xml", "<root><unterminated>");
        var report = new SazReport { SourceName = "malformed.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "1",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/bad",
                StatusCode = 200,
                Request = badJson,
                Response = badXml
            });

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains(
            "id=\"request-tab-json\" aria-controls=\"request-panel-json\" aria-selected=\"false\" data-tab=\"json\" tabindex=\"-1\" disabled aria-disabled=\"true\">JSON",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"response-tab-xml\" aria-controls=\"response-panel-xml\" aria-selected=\"false\" data-tab=\"xml\" tabindex=\"-1\" disabled aria-disabled=\"true\">XML",
            html,
            StringComparison.Ordinal);
        Assert.Contains("{not valid json", CopyTextForPanel(html, "request-panel-raw"), StringComparison.Ordinal);
        Assert.Contains("<root><unterminated>", CopyTextForPanel(html, "response-panel-raw"), StringComparison.Ordinal);
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

        // Display/copy strings share one compressed per-side model while the capped tree payload
        // remains separately available for lazy rendering. Server-side token markup must not
        // multiply report size by the token count.
        Assert.True(
            html.Length < capturedCharacters * 16,
            $"Expected compact bounded rendering, but {capturedCharacters:N0} captured characters produced {html.Length:N0} HTML characters.");
        Assert.DoesNotContain("<span class=\"syn-number\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public void UsesCompactWideHttpColumnsWithoutTypeAndKeepsFullTimestampMetadata()
    {
        var firstTimestamp = new DateTimeOffset(2026, 9, 29, 21, 7, 8, 123, TimeSpan.FromHours(-4));
        var secondTimestamp = firstTimestamp.AddMilliseconds(877);
        var report = new SazReport { SourceName = "table-layout.saz" };
        report.Sessions.Add(
            new HttpSession
            {
                Id = "early",
                ArchiveOrder = 0,
                Timestamp = firstTimestamp,
                Method = "GET",
                Url = "https://example.test/a/very/long/path?first=1&second=2",
                StatusCode = 200,
                ContentType = "application/json"
            });
        report.Sessions.Add(
            new HttpSession
            {
                Id = "later",
                ArchiveOrder = 1,
                Timestamp = secondTimestamp,
                Method = "POST",
                Url = "https://example.test/later",
                StatusCode = 201,
                ContentType = "text/plain"
            });

        var html = new HtmlReportGenerator().Generate(report);
        var httpHeaderStart = html.IndexOf("<table id=\"httpTable\">", StringComparison.Ordinal);
        var httpHeaderEnd = html.IndexOf("</thead>", httpHeaderStart, StringComparison.Ordinal);
        var httpHeader = html[httpHeaderStart..httpHeaderEnd];

        Assert.Contains(
            "<th class=\"http-time\">Time</th><th class=\"http-id\">ID</th><th class=\"http-method\">Method</th><th class=\"http-protocol\">Protocol</th><th class=\"http-url\">URL</th><th class=\"http-status\">Status</th><th class=\"http-bytes num\">Req</th><th class=\"http-bytes num\">Resp</th>",
            httpHeader,
            StringComparison.Ordinal);
        Assert.DoesNotContain(">Type</th>", httpHeader, StringComparison.Ordinal);
        Assert.Contains("#httpTable{min-width:1140px;table-layout:auto}", html, StringComparison.Ordinal);
        Assert.Contains("#httpTable .http-time{width:184px;min-width:184px;white-space:nowrap}", html, StringComparison.Ordinal);
        Assert.Contains("#httpTable .http-url{width:52%;min-width:420px;word-break:normal;overflow-wrap:anywhere}", html, StringComparison.Ordinal);
        Assert.Contains("#httpTable th,#httpTable td{padding:5px 7px;line-height:1.3}", html, StringComparison.Ordinal);
        Assert.Contains(
            "<time datetime=\"2026-09-29T21:07:08.1230000-04:00\" title=\"Captured timestamp: 2026-09-29 21:07:08.123 -04:00\" aria-label=\"Captured timestamp 2026-09-29 21:07:08.123 -04:00\">2026-09-29 21:07:08.123</time>",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain(">2026-09-29 21:07:08.123 -04:00</time>", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf(">early</td>", StringComparison.Ordinal)
            < html.IndexOf(">later</td>", StringComparison.Ordinal),
            "Display-only timestamp formatting must not reorder sessions.");
        Assert.Contains("https://example.test/a/very/long/path?first=1&amp;second=2", html, StringComparison.Ordinal);
        Assert.Contains("application/json", html, StringComparison.Ordinal);
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
        Assert.Contains("<th class=\"http-protocol\">Protocol</th>", html, StringComparison.Ordinal);
        Assert.Contains("data-payload-type=\"mapi-protocol\"", html, StringComparison.Ordinal);
        Assert.Contains("renderProtocolTree(host,generation)", html, StringComparison.Ordinal);
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
        Assert.Contains("Decoded body", rawSection, StringComparison.Ordinal);
        Assert.Contains("decode-status", rawSection, StringComparison.Ordinal);
        Assert.Contains("Decoded in wire-removal order: content: gzip.", rawSection, StringComparison.Ordinal);
        Assert.Contains("<details class=\"captured-bytes\"><summary>Captured bytes (pre-decode)</summary>", rawSection, StringComparison.Ordinal);
        var rawCopy = CopyTextForPanel(html, "response-panel-raw");
        Assert.Contains("Content-Encoding: gzip", rawCopy, StringComparison.Ordinal);
        Assert.Contains("hello", rawCopy, StringComparison.Ordinal);
        Assert.Contains("1F 8B 08 00 00 00 00 00", rawCopy, StringComparison.Ordinal);
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
        Assert.Contains("DE AD BE EF", CopyTextForPanel(html, "response-panel-raw"), StringComparison.Ordinal);
    }

    [Fact]
    public void IsolatesRequestAndResponsePrimaryPanelsFromEachOther()
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

        var requestStart = html.IndexOf("id=\"primary-panel-request\"", StringComparison.Ordinal);
        var responseStart = html.IndexOf("id=\"primary-panel-response\"", StringComparison.Ordinal);
        Assert.True(requestStart >= 0 && responseStart > requestStart);

        var templateEnd = html.IndexOf("</template>", responseStart, StringComparison.Ordinal);
        var requestSection = html[requestStart..responseStart];
        var responseSection = html[responseStart..templateEnd];

        Assert.Contains("REQ_ONLY_MARKER", CopyTextForPanel(html, "request-panel-json"), StringComparison.Ordinal);
        Assert.DoesNotContain("RESP_ONLY_MARKER", CopyTextForPanel(html, "request-panel-json"), StringComparison.Ordinal);
        Assert.Contains("RESP_ONLY_MARKER", CopyTextForPanel(html, "response-panel-xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("REQ_ONLY_MARKER", CopyTextForPanel(html, "response-panel-xml"), StringComparison.Ordinal);

        // Distinct, side-scoped stable IDs guarantee independent aria wiring per pane.
        Assert.Contains("id=\"request-tab-json\"", requestSection, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"response-tab-json\"", requestSection, StringComparison.Ordinal);
        Assert.Contains("id=\"response-tab-xml\"", responseSection, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"request-tab-xml\"", responseSection, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesInjectionAttemptsAcrossJsonXmlMapiHeaderAndTreeTabs()
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
        Assert.Equal(
            new BodyFormatter().Format(request.Body, "application/json").Formatted,
            CopyTextForPanel(html, "request-panel-json"));
        Assert.Contains(xmlAttack, CopyTextForPanel(html, "response-panel-raw"), StringComparison.Ordinal);
        Assert.Contains(headerAttack, CopyTextForPanel(html, "request-panel-headers"), StringComparison.Ordinal);

        // The JSON tree payload itself (client-parsed via JSON.parse, never innerHTML) must also
        // never carry the raw, unescaped attack string.
        using var payload = ExtractTreePayload(html, "data-json-tree");
        var value = payload.RootElement.GetProperty("children")[0].GetProperty("value").GetString();
        Assert.Equal(jsonAttack, value);
    }

    private static string PanelCopyButton(string html, string panelId)
    {
        var panelStart = html.IndexOf($"id=\"{panelId}\"", StringComparison.Ordinal);
        Assert.True(panelStart >= 0, $"Panel {panelId} was not found.");
        var buttonStart = html.IndexOf("<button", panelStart, StringComparison.Ordinal);
        var buttonEnd = html.IndexOf("</button>", buttonStart, StringComparison.Ordinal);
        Assert.True(buttonStart >= 0 && buttonEnd > buttonStart, $"Copy button for {panelId} was not found.");
        return html[buttonStart..(buttonEnd + "</button>".Length)];
    }

    private static string CopyTextForPanel(string html, string panelId)
    {
        var key = panelId[(panelId.LastIndexOf('-') + 1)..];
        return CopyModelTextForPanel(html, panelId, key);
    }

    private static string CopyModelTextForPanel(string html, string panelId, string key)
    {
        var panelStart = html.IndexOf($"id=\"{panelId}\"", StringComparison.Ordinal);
        Assert.True(panelStart >= 0, $"Panel {panelId} was not found.");
        var messagePanelStart = html.LastIndexOf("<section class=\"message-panel\"", panelStart, StringComparison.Ordinal);
        Assert.True(messagePanelStart >= 0, $"Message panel for {panelId} was not found.");
        using var document = ExtractCompressedPayload(
            html,
            "copy-model",
            messagePanelStart,
            panelStart);
        return document.RootElement.GetProperty(key).GetString()!;
    }

    private static JsonDocument ExtractTreePayload(string html, string attributeName)
    {
        var type = attributeName.Contains("json", StringComparison.Ordinal) ? "json-tree" : "xml-tree";
        return ExtractCompressedPayload(html, type, 0, html.Length);
    }

    private static JsonDocument ExtractCompressedPayload(
        string html,
        string type,
        int searchStart,
        int searchEnd)
    {
        var typeMarker = $"data-payload-type=\"{type}\"";
        var typeIndex = html.IndexOf(typeMarker, searchStart, StringComparison.Ordinal);
        Assert.True(typeIndex >= searchStart && typeIndex < searchEnd, $"Compressed payload {type} was not found.");
        const string payloadMarker = "data-compressed-payload=\"";
        var payloadStart = html.LastIndexOf(payloadMarker, typeIndex, StringComparison.Ordinal);
        Assert.True(payloadStart >= searchStart, $"Compressed payload data for {type} was not found.");
        payloadStart += payloadMarker.Length;
        var payloadEnd = html.IndexOf('"', payloadStart);
        Assert.True(payloadEnd > payloadStart && payloadEnd < searchEnd, $"Compressed payload data for {type} was malformed.");
        var compressed = Convert.FromBase64String(html[payloadStart..payloadEnd]);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip, new JsonDocumentOptions { MaxDepth = 256 });
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
