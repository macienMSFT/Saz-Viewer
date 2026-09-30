using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Microsoft.Playwright;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HtmlReportBrowserTests
{
    private const string InjectionText = "<img src=x onerror=globalThis.pwned=true>";
    private const string RequestBody = """{"payload":{"enabled":true},"items":[1,2],"attack":"</script><svg onload=globalThis.pwned=true>"}""";
    private const string ResponseBody = "<root><value>safe</value></root>";

    [WindowsEdgeFact]
    public async Task GeneratedReportInspectorIsVisibleAndInteractiveAtDesktopAndNarrowWidths()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"saz viewer ünicode {Guid.NewGuid():N}");
        var reportPath = Path.Combine(tempDirectory, "capture report.html");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(CreateReport()));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = "msedge",
                Headless = true,
            });

            await VerifyInspectorAsync(browser, reportPath, 1440, exerciseAllControls: true);
            await VerifyInspectorAsync(browser, reportPath, 480, exerciseAllControls: false);
            await VerifyNewTabInspectorAsync(browser, reportPath);
            await VerifyInvalidInspectorStateAsync(browser, reportPath);
            await VerifyCopyModelFailureStatesAsync(browser, tempDirectory);
            await VerifyStructuralPayloadFailureStatesAsync(browser, reportPath);
            await VerifyAbandonedTreeStopsAndRebuildsAsync(browser, tempDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task VerifyInspectorAsync(
        IBrowser browser,
        string reportPath,
        int width,
        bool exerciseAllControls)
    {
        var errors = new List<string>();
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
        });
        await InstallClipboardTestHookAsync(page);
        page.PageError += (_, error) => errors.Add($"page error: {error}");
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add($"console error: {message.Text}");
            }
        };

        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            var firstRow = page.Locator("#httpTable tbody tr").First;
            await firstRow.ClickAsync();
            await page.Locator("#httpInspector[open]").WaitForAsync();

            var dialog = await page.Locator("#httpInspector").BoundingBoxAsync();
            var tabs = await page.Locator(".primary-tab-strip").BoundingBoxAsync();
            var request = await page.Locator("#primary-panel-request").BoundingBoxAsync();
            Assert.NotNull(dialog);
            Assert.NotNull(tabs);
            Assert.NotNull(request);
            Assert.True(dialog.Width >= width - 1 && dialog.Height >= 899);
            Assert.True(
                tabs.Y < 250,
                $"Primary tabs were pushed out of the usable viewport at y={tabs.Y}.");
            Assert.True(
                request.Y < 300 && request.Height > 500 && request.Y + request.Height <= 901,
                $"Request content did not fill the visible inspector: y={request.Y}, height={request.Height}.");
            Assert.Equal("true", await page.Locator("#primary-tab-request").GetAttributeAsync("aria-selected"));
            Assert.Contains("payload", await page.Locator("#primary-panel-request").InnerTextAsync());
            await page.Locator("#request-panel-json .tree-item").First.WaitForAsync();
            Assert.Null(await page.Locator("#request-panel-json .tree-subview")
                .GetAttributeAsync("data-compressed-payload"));
            Assert.NotNull(await page.Locator("#response-panel-xml .tree-subview")
                .GetAttributeAsync("data-compressed-payload"));
            Assert.Equal(0, await page.Locator("#response-panel-xml .tree-item").CountAsync());

            await page.Locator("#request-tab-json").ClickAsync();
            Assert.False(await page.Locator("#request-panel-json .tree-subview").EvaluateAsync<bool>(
                "tree => tree.classList.contains('hidden')"));
            Assert.Equal(ExpectedJson(), await CopyAndReadAsync(page, "request-panel-json"));
            var jsonCopyButton = page.Locator("#request-panel-json .copy-button");
            Assert.Equal("Copied", await jsonCopyButton.InnerTextAsync());
            Assert.Contains("Copied request JSON pretty text", await page.Locator("#request-panel-json .copy-status").InnerTextAsync());
            await page.WaitForTimeoutAsync(1600);
            Assert.Equal("Copy", await jsonCopyButton.InnerTextAsync());

            await page.Locator("#request-tab-headers").ClickAsync();
            Assert.Equal(
                "POST /formatted HTTP/1.1\nContent-Type: application/json\n",
                await CopyAndReadAsync(page, "request-panel-headers", keyboard: true));
            await page.Locator("#request-tab-raw").ClickAsync();
            Assert.Equal(ExpectedRequestRaw(), await CopyAndReadAsync(page, "request-panel-raw"));

            await page.Locator("#primary-tab-response").ClickAsync();
            Assert.Equal("true", await page.Locator("#primary-tab-response").GetAttributeAsync("aria-selected"));
            Assert.False(await page.Locator("#primary-panel-response").EvaluateAsync<bool>(
                "panel => panel.classList.contains('hidden')"));
            Assert.True(await page.Locator("#primary-panel-request").EvaluateAsync<bool>(
                "panel => panel.classList.contains('hidden')"));
            Assert.Contains("safe", await page.Locator("#response-panel-xml").InnerTextAsync());
            await page.Locator("#response-panel-xml .tree-item").First.WaitForAsync();
            Assert.Null(await page.Locator("#response-panel-xml .tree-subview")
                .GetAttributeAsync("data-compressed-payload"));
            Assert.Equal(ExpectedXml(), await CopyAndReadAsync(page, "response-panel-xml"));
            Assert.True(await page.Locator("#response-panel-mapi .copy-button").IsDisabledAsync());

            await page.Locator("#response-tab-headers").ClickAsync();
            Assert.Contains("Content-Type: application/xml", await page.Locator("#response-panel-headers").InnerTextAsync());
            Assert.Equal(
                "HTTP/1.1 200 OK\nContent-Type: application/xml\n",
                await CopyAndReadAsync(page, "response-panel-headers"));
            await page.Locator("#response-tab-raw").ClickAsync();
            Assert.Contains("<root><value>safe</value></root>", await page.Locator("#response-panel-raw").InnerTextAsync());
            Assert.Equal(ExpectedResponseRaw(), await CopyAndReadAsync(page, "response-panel-raw", mode: "fallback"));
            Assert.True(await page.EvaluateAsync<bool>("globalThis.__fallbackFocusedInInspector"));

            await page.EvaluateAsync("globalThis.__clipboardMode='failure';globalThis.__copiedText=null");
            await page.Locator("#response-panel-raw .copy-button").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-raw .copy-status"))
                .ToContainTextAsync("Copy failed");
            Assert.Equal("Copy", await page.Locator("#response-panel-raw .copy-button").InnerTextAsync());

            if (exerciseAllControls)
            {
                await page.Locator("#inspectorNext").ClickAsync();
                Assert.Equal("true", await page.Locator("#primary-tab-request").GetAttributeAsync("aria-selected"));
                await Assertions.Expect(page.Locator("#request-panel-raw .copy-button")).ToBeEnabledAsync();
                Assert.Contains("Binary body", await page.Locator("#request-panel-raw").InnerTextAsync());
                await page.Locator("#request-panel-raw .captured-bytes summary").ClickAsync();
                Assert.Contains(
                    "00FF1020\n[Captured byte preview truncated]",
                    await page.Locator("#request-panel-raw .captured-bytes pre").InnerTextAsync());
                await page.Locator("#primary-tab-response").ClickAsync();
                Assert.Contains("No response entry was captured", await page.Locator("#primary-panel-response").InnerTextAsync());

                await page.Locator("#inspectorClose").ClickAsync();
                Assert.False(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
                Assert.Equal("http-detail-0", await page.EvaluateAsync<string>(
                    "document.activeElement?.getAttribute('data-detail')"));

                await page.Locator("#httpFilter").SelectOptionAsync("mapi");
                await page.Locator("#httpTable tbody tr:not(.hidden)").ClickAsync();
                Assert.Equal("true", await page.Locator("#request-tab-mapi").GetAttributeAsync("aria-selected"));
                await page.Locator("#request-panel-mapi")
                    .GetByRole(AriaRole.Button, new() { Name = "Expand all" })
                    .ClickAsync();
                Assert.Contains("PropertyValue", await page.Locator("#request-panel-mapi").InnerTextAsync());
                await page.Locator("#request-panel-mapi")
                    .GetByRole(AriaRole.Button, new() { Name = "Collapse all" })
                    .ClickAsync();
                Assert.Equal(ExpectedMapi(), await CopyAndReadAsync(page, "request-panel-mapi"));
                await page.Keyboard.PressAsync("Escape");
                Assert.False(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
            }

            Assert.Empty(errors);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task VerifyNewTabInspectorAsync(IBrowser browser, string reportPath)
    {
        var originalErrors = new List<string>();
        var popupErrors = new List<string>();
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        await InstallClipboardTestHookAsync(page);
        CaptureErrors(page, originalErrors);
        IPage? popup = null;
        try
        {
            var reportUrl = new Uri(reportPath).AbsoluteUri;
            await page.GotoAsync(reportUrl);
            await page.Locator("#httpSearch").FillAsync(InjectionText);
            await page.Locator("#httpFilter").SelectOptionAsync("2");
            Assert.Equal(2, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await page.Locator("#httpTable tbody tr:not(.hidden)").First.ClickAsync();

            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            popup = await popupTask;
            CaptureErrors(popup, popupErrors);
            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await popup.Locator("#httpInspector[open]").WaitForAsync();

            Assert.StartsWith(reportUrl, popup.Url, StringComparison.Ordinal);
            Assert.Contains("#saz-inspector?", popup.Url, StringComparison.Ordinal);
            Assert.Contains("q=%3Cimg", popup.Url, StringComparison.Ordinal);
            Assert.True(await popup.Locator("body").EvaluateAsync<bool>(
                "body => body.classList.contains('inspector-only')"));
            Assert.True(await popup.Locator("main").EvaluateAsync<bool>(
                "main => getComputedStyle(main).display === 'none'"));
            Assert.Equal("Back to sessions", await popup.Locator("#inspectorClose").InnerTextAsync());
            Assert.Equal("1 of 2", await popup.Locator("#inspectorPosition").InnerTextAsync());
            Assert.Equal(2, await popup.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            Assert.Contains("payload", await popup.Locator("#primary-panel-request").InnerTextAsync());
            var popupRequest = await popup.Locator("#primary-panel-request").BoundingBoxAsync();
            Assert.NotNull(popupRequest);
            Assert.True(popupRequest.Y < 300 && popupRequest.Height > 400);
            await popup.Locator("#request-tab-json").ClickAsync();
            Assert.Equal(
                ExpectedJson(),
                await CopyAndReadAsync(popup, "request-panel-json", mode: "fallback"));
            Assert.True(await popup.EvaluateAsync<bool>("globalThis.__fallbackFocusedInInspector"));

            await popup.Locator("#primary-tab-response").ClickAsync();
            await popup.Locator("#response-tab-raw").ClickAsync();
            Assert.Contains("<root><value>safe</value></root>", await popup.Locator("#response-panel-raw").InnerTextAsync());
            await popup.Locator("#inspectorNext").ClickAsync();
            Assert.Equal("2 of 2", await popup.Locator("#inspectorPosition").InnerTextAsync());
            Assert.Equal("true", await popup.Locator("#primary-tab-request").GetAttributeAsync("aria-selected"));
            Assert.Equal("true", await popup.Locator("#request-tab-mapi").GetAttributeAsync("aria-selected"));
            Assert.True(await popup.Locator("#inspectorNext").IsDisabledAsync());
            await popup.Locator("#inspectorPrev").ClickAsync();
            Assert.Equal("1 of 2", await popup.Locator("#inspectorPosition").InnerTextAsync());

            Assert.True(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
            Assert.Equal(InjectionText, await page.Locator("#httpSearch").InputValueAsync());
            Assert.Equal("2", await page.Locator("#httpFilter").InputValueAsync());
            await page.Locator("#primary-tab-response").ClickAsync();
            Assert.Equal("true", await page.Locator("#primary-tab-response").GetAttributeAsync("aria-selected"));

            Assert.False(await popup.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));
            Assert.False(await page.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));
            await popup.Locator("#inspectorClose").ClickAsync();
            Assert.False(await popup.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
            Assert.False(await popup.Locator("body").EvaluateAsync<bool>(
                "body => body.classList.contains('inspector-only')"));
            Assert.True(await popup.Locator("main").IsVisibleAsync());
            Assert.Equal(InjectionText, await popup.Locator("#httpSearch").InputValueAsync());
            Assert.Equal("2", await popup.Locator("#httpFilter").InputValueAsync());
            Assert.DoesNotContain("#saz-inspector?", popup.Url, StringComparison.Ordinal);

            await popup.WaitForTimeoutAsync(100);
            Assert.Empty(originalErrors);
            Assert.Empty(popupErrors);
        }
        finally
        {
            if (popup is not null)
            {
                await popup.CloseAsync();
            }
            await page.CloseAsync();
        }
    }

    private static async Task VerifyInvalidInspectorStateAsync(IBrowser browser, string reportPath)
    {
        var reportUrl = new Uri(reportPath).AbsoluteUri;
        var page = await browser.NewPageAsync();
        var errors = new List<string>();
        CaptureErrors(page, errors);
        try
        {
            var maliciousSession = Uri.EscapeDataString("<svg onload=globalThis.pwned=true>");
            await page.GotoAsync($"{reportUrl}#saz-inspector?v=1&session={maliciousSession}");
            Assert.True(await page.Locator("#reportStatus").IsVisibleAsync());
            Assert.Contains("does not identify a valid session", await page.Locator("#reportStatus").InnerTextAsync());
            Assert.False(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
            Assert.False(await page.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));

            await page.GotoAsync("about:blank");
            await page.GotoAsync($"{reportUrl}#saz-inspector?{new string('a', 4100)}");
            Assert.Contains("too large", await page.Locator("#reportStatus").InnerTextAsync());
            Assert.True(await page.Locator("main").IsVisibleAsync());

            await page.GotoAsync("about:blank");
            await page.GotoAsync(
                $"{reportUrl}#saz-inspector?v=1&session=http-detail-1&q=example.test&filter=2");
            await page.Locator("#httpInspector[open]").WaitForAsync();
            Assert.Contains(
                "not visible under the restored filters",
                await page.Locator("#inspectorOpenStatus").InnerTextAsync());
            Assert.Equal("Session 1: POST https://example.test/formatted",
                await page.Locator("#inspectorTitle").InnerTextAsync());
            await page.Locator("#inspectorClose").ClickAsync();

            await page.GotoAsync("about:blank");
            await page.GotoAsync(
                $"{reportUrl}#saz-inspector?v=1&session=http-detail-0&q=no-such-session&filter=2");
            await page.Locator("#httpInspector[open]").WaitForAsync();
            Assert.Equal("Session unavailable", await page.Locator("#inspectorTitle").InnerTextAsync());
            Assert.Contains("No HTTP sessions match", await page.Locator("#inspectorBody").InnerTextAsync());

            await page.WaitForTimeoutAsync(100);
            Assert.Empty(errors);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static void CaptureErrors(IPage page, List<string> errors)
    {
        page.PageError += (_, error) => errors.Add($"page error: {error}");
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                errors.Add($"console error: {message.Text}");
            }
        };
    }

    private static async Task VerifyCopyModelFailureStatesAsync(IBrowser browser, string tempDirectory)
    {
        var oversizedMessage = Message(
            "POST /oversized HTTP/1.1",
            "text/plain",
            new string('x', (1024 * 1024) + 1));
        oversizedMessage.Headers.Add(new HttpHeader("X-Large", new string('h', (1024 * 1024) + 1)));
        var oversizedReport = new SazReport { SourceName = "oversized-browser.saz" };
        oversizedReport.Sessions.Add(new HttpSession
        {
            Id = "oversized",
            ArchiveOrder = 0,
            Request = oversizedMessage,
        });
        var oversizedPath = Path.Combine(tempDirectory, "oversized report.html");
        await File.WriteAllTextAsync(oversizedPath, new HtmlReportGenerator().Generate(oversizedReport));

        var oversizedPage = await browser.NewPageAsync();
        try
        {
            await oversizedPage.GotoAsync(new Uri(oversizedPath).AbsoluteUri);
            await oversizedPage.Locator("#httpTable tbody tr").ClickAsync();
            await Assertions.Expect(oversizedPage.Locator("#request-panel-raw .body-view"))
                .ToContainTextAsync("[Body display truncated at the 256 KiB rendering limit.]");
            await Assertions.Expect(oversizedPage.Locator("#request-panel-raw .headers"))
                .ToContainTextAsync("[Header display truncated at the 256 KiB rendering limit.]");
            var copy = oversizedPage.Locator("#request-panel-raw .copy-button");
            await copy.ClickAsync();
            await Assertions.Expect(oversizedPage.Locator("#request-panel-raw .copy-status"))
                .ToContainTextAsync("Copy source exceeds the 1 MiB safety limit.");
        }

        finally
        {
            await oversizedPage.CloseAsync();
        }

        await using var unsupportedContext = await browser.NewContextAsync();
        await unsupportedContext.AddInitScriptAsync(
            "Object.defineProperty(globalThis,'DecompressionStream',{configurable:true,value:undefined})");
        var unsupportedPage = await unsupportedContext.NewPageAsync();
        var errors = new List<string>();
        CaptureErrors(unsupportedPage, errors);
        try
        {
            var standardPath = Path.Combine(tempDirectory, "capture report.html");
            await unsupportedPage.GotoAsync(new Uri(standardPath).AbsoluteUri);
            await unsupportedPage.Locator("#httpTable tbody tr").First.ClickAsync();
            await Assertions.Expect(unsupportedPage.Locator("#request-panel-json .tree-subview"))
                .ToContainTextAsync("Open the report in a current Microsoft Edge or Google Chrome release.");
            await unsupportedPage.Locator("#request-tab-raw").ClickAsync();
            await Assertions.Expect(unsupportedPage.Locator("#request-panel-raw .body-view"))
                .ToContainTextAsync("Content could not be displayed because this browser could not read the compressed local report data.");
            var copy = unsupportedPage.Locator("#request-panel-raw .copy-button");
            await Assertions.Expect(copy).ToBeEnabledAsync();
            await copy.ClickAsync();
            await Assertions.Expect(unsupportedPage.Locator("#request-panel-raw .copy-status"))
                .ToContainTextAsync("Copy data could not be prepared.");
            Assert.Empty(errors);
        }
        finally
        {
            await unsupportedPage.CloseAsync();
        }
    }

    private static async Task VerifyStructuralPayloadFailureStatesAsync(IBrowser browser, string reportPath)
    {
        var invalidJsonPayload = GzipBase64("not-json");
        const string invalidTreeSchema = """{"kind":"object","children":[null]}""";
        var invalidTreePayload = GzipBase64(invalidTreeSchema);
        var expansionPayload = GzipBase64(new string('x', 1024));
        var truncatedPayload = expansionPayload[..^4];
        var cases = new[]
        {
            new PayloadMutation("invalid base64", "base64", null, null),
            new PayloadMutation("corrupt gzip", "gzip", null, null),
            new PayloadMutation("truncated gzip", "truncated", truncatedPayload, 1024),
            new PayloadMutation("invalid JSON", "json", invalidJsonPayload, 8),
            new PayloadMutation("invalid tree schema", "schema", invalidTreePayload, Encoding.UTF8.GetByteCount(invalidTreeSchema)),
            new PayloadMutation("declared expansion limit", "expansion", expansionPayload, 8),
            new PayloadMutation("unsupported version", "version", null, null),
            new PayloadMutation("unsupported type", "type", null, null),
            new PayloadMutation("encoded limit", "encoded-limit", null, null),
            new PayloadMutation("decoded limit", "decoded-limit", null, null),
        };
        foreach (var testCase in cases)
        {
            var page = await browser.NewPageAsync();
            var errors = new List<string>();
            CaptureErrors(page, errors);
            try
            {
                await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
                await page.EvaluateAsync(
                    """
                    args=>{
                      const host=document.getElementById('http-detail-0').content.querySelector('[data-payload-type="json-tree"]');
                      if(args.kind==='base64')host.dataset.compressedPayload='@@@@';
                      else if(args.kind==='gzip')host.dataset.compressedPayload='AAAA';
                      else if(args.kind==='truncated'){
                        host.dataset.compressedPayload=args.payload;
                        host.dataset.payloadDecodedBytes=String(args.decodedBytes);
                      }
                      else if(args.kind==='json'){
                        host.dataset.compressedPayload=args.payload;
                        host.dataset.payloadDecodedBytes=String(args.decodedBytes);
                      }else if(args.kind==='schema'){
                        host.dataset.compressedPayload=args.payload;
                        host.dataset.payloadDecodedBytes=String(args.decodedBytes);
                      }else if(args.kind==='expansion'){
                        host.dataset.compressedPayload=args.payload;
                        host.dataset.payloadDecodedBytes=String(args.decodedBytes);
                      }else if(args.kind==='version')host.dataset.payloadVersion='2';
                      else if(args.kind==='type')host.dataset.payloadType='copy-model';
                      else if(args.kind==='encoded-limit')host.dataset.compressedPayload='A'.repeat((12*1024*1024)+4);
                      else if(args.kind==='decoded-limit')host.dataset.payloadDecodedBytes=String((8*1024*1024)+1);
                    }
                    """,
                    new { kind = testCase.Kind, payload = testCase.Payload, decodedBytes = testCase.DecodedBytes });
                await page.Locator("#httpTable tbody tr").First.ClickAsync();
                var tree = page.Locator("#request-panel-json .tree-subview");
                var expected = testCase.Kind == "schema"
                    ? "Tree view could not be rendered because its decoded structure is invalid."
                    : "Tree view could not be loaded because its compressed report payload is corrupt, unsupported, or exceeds safety limits.";
                await Assertions.Expect(tree).ToContainTextAsync(expected);
                Assert.True(
                    await tree.EvaluateAsync<bool>("element => element.classList.contains('warning')"),
                    testCase.Name);
                Assert.Contains("use pretty text", (await tree.InnerTextAsync()).ToLowerInvariant());
                await page.WaitForTimeoutAsync(50);
                Assert.Empty(errors);
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        var mapiPage = await browser.NewPageAsync();
        var mapiErrors = new List<string>();
        CaptureErrors(mapiPage, mapiErrors);
        try
        {
            await mapiPage.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await mapiPage.EvaluateAsync(
                """
                () => {
                  const host=document.getElementById('http-detail-2').content.querySelector('[data-payload-type="mapi-protocol"]');
                  host.dataset.compressedPayload='@@@@';
                }
                """);
            await mapiPage.Locator("#httpTable tbody tr[data-detail=\"http-detail-2\"]").ClickAsync();
            await Assertions.Expect(mapiPage.Locator("#request-panel-mapi .protocol-block"))
                .ToContainTextAsync("Protocol tree could not be loaded because its compressed report payload is corrupt, unsupported, or exceeds safety limits.");
            var copy = mapiPage.Locator("#request-panel-mapi .copy-button");
            Assert.True(await copy.IsDisabledAsync());
            Assert.Null(await copy.GetAttributeAsync("aria-busy"));
            await mapiPage.WaitForTimeoutAsync(50);
            Assert.Empty(mapiErrors);
        }
        finally
        {
            await mapiPage.CloseAsync();
        }
    }

    private static string GzipBase64(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(value));
        }
        return Convert.ToBase64String(output.ToArray());
    }

    private static async Task VerifyAbandonedTreeStopsAndRebuildsAsync(IBrowser browser, string tempDirectory)
    {
        var body = $"[{string.Join(',', Enumerable.Range(0, 1000))}]";
        var report = new SazReport { SourceName = "lazy-tree.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "lazy",
            ArchiveOrder = 0,
            Request = Message("POST /lazy HTTP/1.1", "application/json", body),
            Response = Message("HTTP/1.1 200 OK", "application/json", body),
        });
        var reportPath = Path.Combine(tempDirectory, "lazy tree report.html");
        await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(report));
        var page = await browser.NewPageAsync();
        var errors = new List<string>();
        CaptureErrors(page, errors);
        try
        {
            await page.AddInitScriptAsync(
                "const nativeFrame=requestAnimationFrame.bind(globalThis);globalThis.requestAnimationFrame=callback=>setTimeout(()=>nativeFrame(callback),100)");
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.Locator("#httpTable tbody tr").ClickAsync();
            await page.Locator("#request-panel-json [data-view=\"pretty\"]").ClickAsync();
            await page.WaitForTimeoutAsync(250);
            Assert.True(await page.Locator("#request-panel-json .tree-item").CountAsync() < 301);

            await page.Locator("#request-panel-json [data-view=\"tree\"]").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-json .tree-item")).ToHaveCountAsync(301);
            Assert.Contains(
                "[] (1000 items)",
                await page.Locator("#request-panel-json .tree-label").First.InnerTextAsync());

            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").ClickAsync();
            await page.Locator("#request-tab-raw").ClickAsync();
            await page.WaitForTimeoutAsync(250);
            await page.Locator("#request-tab-json").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-json .tree-item")).ToHaveCountAsync(301);

            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").ClickAsync();
            await page.Locator("#primary-tab-response").ClickAsync();
            await page.WaitForTimeoutAsync(250);
            await page.Locator("#primary-tab-request").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-json .tree-item")).ToHaveCountAsync(301);
            Assert.Empty(errors);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static async Task InstallClipboardTestHookAsync(IPage page)
    {
        await page.Context.AddInitScriptAsync(
            """
            globalThis.__clipboardMode='modern';
            globalThis.__copiedText=null;
            globalThis.__fallbackFocusedInInspector=false;
            Object.defineProperty(navigator,'clipboard',{
              configurable:true,
              value:{
                writeText(text){
                  if(globalThis.__clipboardMode==='modern'){
                    globalThis.__copiedText=text;
                    return Promise.resolve();
                  }
                  return Promise.reject(new Error('clipboard denied for test'));
                }
              }
            });
            const nativeExecCommand=Document.prototype.execCommand;
            window.addEventListener('copy',event=>{
              if(globalThis.__clipboardMode!=='fallback')return;
              globalThis.__fallbackFocusedInInspector=
                document.activeElement?.matches('textarea[aria-hidden="true"]')===true&&
                document.activeElement?.closest('dialog')?.id==='httpInspector';
              globalThis.__copiedText=event.clipboardData?.getData('text/plain')??null;
            });
            Document.prototype.execCommand=function(command,...args){
              if(command==='copy'&&globalThis.__clipboardMode==='failure')return false;
              return nativeExecCommand.call(this,command,...args);
            };
            """);
    }

    private static async Task<string?> CopyAndReadAsync(
        IPage page,
        string panelId,
        string mode = "modern",
        bool keyboard = false)
    {
        await page.EvaluateAsync(
            "mode=>{globalThis.__clipboardMode=mode;globalThis.__copiedText=null;globalThis.__fallbackFocusedInInspector=false}",
            mode);
        var button = page.Locator($"#{panelId} .copy-button");
        await Assertions.Expect(button).ToBeEnabledAsync();
        if (keyboard)
        {
            await button.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
        }
        else
        {
            await button.ClickAsync();
        }
        await Assertions.Expect(button).ToHaveTextAsync("Copied");
        return await page.EvaluateAsync<string?>("globalThis.__copiedText");
    }

    private static string ExpectedJson() =>
        new BodyFormatter().Format(
            new BodyPreview
            {
                Length = RequestBody.Length,
                CapturedLength = RequestBody.Length,
                Preview = RequestBody,
            },
            "application/json").Formatted;

    private static string ExpectedXml() =>
        new BodyFormatter().Format(
            new BodyPreview
            {
                Length = ResponseBody.Length,
                CapturedLength = ResponseBody.Length,
                Preview = ResponseBody,
            },
            "application/xml").Formatted;

    private static string ExpectedMapi() =>
        "MAPI protocol (complete; 8 of 8 bytes)\n" +
        "Execute [Operation] @0 +8\n" +
        "  PropertyValue [Property] @4 +4 = safe";

    private static string ExpectedRequestRaw() =>
        "Original headers\n" +
        "POST /formatted HTTP/1.1\n" +
        "Content-Type: application/json\n\n" +
        $"Body ({RequestBody.Length} B)\n" +
        "Format: JSON\n" +
        "Status: Parsed as JSON from Content-Type and body content.\n" +
        RequestBody;

    private static string ExpectedResponseRaw() =>
        "Original headers\n" +
        "HTTP/1.1 200 OK\n" +
        "Content-Type: application/xml\n\n" +
        $"Body ({ResponseBody.Length} B)\n" +
        "Format: XML\n" +
        "Status: Parsed as XML from Content-Type and body content.\n" +
        ResponseBody;

    private static SazReport CreateReport()
    {
        var report = new SazReport { SourceName = "browser-test.saz" };
        var formatted = new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/formatted",
            StatusCode = 200,
            ClientEndpoint = InjectionText,
            Request = Message(
                "POST /formatted HTTP/1.1",
                "application/json",
                RequestBody),
            Response = Message(
                "HTTP/1.1 200 OK",
                "application/xml",
                ResponseBody),
        };
        formatted.Warnings.Add("Synthetic warning that reproduces the collapsed session-details layout.");
        report.Sessions.Add(formatted);

        report.Sessions.Add(
            new HttpSession
            {
                Id = "2",
                ArchiveOrder = 1,
                Method = "POST",
                Url = "https://example.test/binary",
                Request = BinaryMessage(),
            });

        var protocolRoot = new MapiNode(
            "Execute",
            MapiNodeKind.Operation,
            0,
            8,
            null,
            [MapiNode.Leaf("PropertyValue", MapiNodeKind.Property, 4, 4, "safe")]);
        var protocol = new MapiMessageParse(
            MapiDirection.Request,
            protocolRoot,
            ImmutableArray<string>.Empty,
            true,
            8,
            8);
        var mapiSession = new HttpSession
        {
            Id = "3",
            ArchiveOrder = 2,
            Method = "POST",
            Url = "https://example.test/mapi",
            StatusCode = 200,
            ClientEndpoint = InjectionText,
            Request = Message("POST /mapi HTTP/1.1", "application/mapi-http", "binary"),
        };
        mapiSession.Mapi = new MapiSession(
            "3",
            2,
            MapiEndpoint.Mailbox,
            "Execute",
            "0",
            false,
            protocol,
            null,
            ImmutableArray<string>.Empty);
        report.Sessions.Add(mapiSession);
        return report;
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
                Preview = body,
            },
        };
        message.Headers.Add(new HttpHeader("Content-Type", contentType));
        return message;
    }

    private static HttpMessage BinaryMessage()
    {
        var message = new HttpMessage
        {
            StartLine = "POST /binary HTTP/1.1",
            Body = new BodyPreview
            {
                Length = 4,
                CapturedLength = 4,
                IsBinary = true,
                Preview = "Binary body (4 bytes)\n00 FF 10 20",
                CapturedBytesPreview = "00FF1020",
                CapturedBytesPreviewTruncated = true,
            },
        };
        message.Headers.Add(new HttpHeader("Content-Type", "application/octet-stream"));
        return message;
    }

    private sealed record PayloadMutation(string Name, string Kind, string? Payload, int? DecodedBytes);

    private sealed class WindowsEdgeFactAttribute : FactAttribute
    {
        public WindowsEdgeFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "The generated-report browser smoke test requires Windows and Microsoft Edge.";
                return;
            }

            var edgeInstalled = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            }.Any(programFiles => File.Exists(Path.Combine(
                programFiles,
                "Microsoft",
                "Edge",
                "Application",
                "msedge.exe")));
            if (!edgeInstalled)
            {
                Skip = "The generated-report browser smoke test requires Microsoft Edge.";
            }
        }
    }
}
