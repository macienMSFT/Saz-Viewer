using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HtmlReportBrowserTests
{
    private const string InjectionText = "<img src=x onerror=globalThis.pwned=true>";
    private const string RequestBody = """{"payload":{"enabled":true},"items":[1,2],"attack":"</script><svg onload=globalThis.pwned=true>"}""";
    private const string ResponseBody = "<root><value>safe</value></root>";
    private static readonly string WebSocketJson = JsonSerializer.Serialize(new
    {
        kind = "update",
        items = new[] { 1, 2 },
        safe = true,
        detail = new string('x', 6550),
        searchTail = "NeedleBeyondPreview",
        unicode = "Straße雪",
        attack = InjectionText
    });

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
            await VerifyHttpActiveViewSearchAsync(browser, reportPath);
            await VerifyWebSocketInspectorAsync(browser, reportPath, 1440);
            await VerifyWebSocketInspectorAsync(browser, reportPath, 320);
            await VerifyNewTabInspectorAsync(browser, reportPath);
            await VerifyBlockedNewTabKeepsInspectorAsync(browser, reportPath);
            await VerifyInvalidInspectorStateAsync(browser, reportPath);
            await VerifyCopyModelFailureStatesAsync(browser, tempDirectory);
            await VerifyStructuralPayloadFailureStatesAsync(browser, reportPath);
            await VerifyAbandonedTreeStopsAndRebuildsAsync(browser, tempDirectory);
            await VerifyLargeMapiTreeAsync(browser, tempDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task VerifyLargeMapiTreeAsync(IBrowser browser, string tempDirectory)
    {
            const int leafCount = 5_000;
            var leaves = Enumerable.Range(0, leafCount)
                .Select(index => MapiNode.Leaf(
                    $"Field[{index}]",
                    MapiNodeKind.Field,
                    index * 4L,
                    4,
                    index == leafCount - 1 ? "LargeTreeNeedle" : index.ToString()))
                .ToImmutableArray();
            var root = new MapiNode("LargeRoot", MapiNodeKind.Array, 0, leafCount * 4L, null, leaves);
            var protocol = new MapiMessageParse(
                MapiDirection.Request,
                root,
                ImmutableArray<string>.Empty,
                true,
                leafCount * 4L,
                leafCount * 4L);
            var session = new HttpSession
            {
                Id = "large",
                ArchiveOrder = 0,
                Method = "POST",
                Url = "https://example.test/mapi-large",
                StatusCode = 200,
                Request = Message("POST /mapi-large HTTP/1.1", "application/mapi-http", "binary")
            };
            session.Mapi = new MapiSession(
                "large",
                0,
                MapiEndpoint.Mailbox,
                "Execute",
                "0",
                false,
                protocol,
                null,
                ImmutableArray<string>.Empty);
            var report = new SazReport { SourceName = "large-mapi.saz" };
            report.Sessions.Add(session);
            var path = Path.Combine(tempDirectory, "large-mapi.html");
            await File.WriteAllTextAsync(path, new HtmlReportGenerator().Generate(report));

            var errors = new List<string>();
            var page = await browser.NewPageAsync(new()
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });
            CaptureErrors(page, errors);
            try
            {
                await page.AddInitScriptAsync(
                    "const nativeFrame=requestAnimationFrame.bind(globalThis);globalThis.requestAnimationFrame=callback=>setTimeout(()=>nativeFrame(callback),50)");
                await page.GotoAsync(new Uri(path).AbsoluteUri);
                await page.Locator("#httpTable tbody tr").ClickAsync();
                await Assertions.Expect(page.Locator(".protocol-load-status")).ToContainTextAsync("Loading");
                await page.Locator("#inspectorClose").ClickAsync();
                await page.WaitForTimeoutAsync(200);
                var abandonedCount = await page.Locator("#request-panel-mapi .tree-item").CountAsync();
                await page.WaitForTimeoutAsync(200);
                Assert.Equal(abandonedCount, await page.Locator("#request-panel-mapi .tree-item").CountAsync());

                var started = DateTime.UtcNow;
                await page.Locator("#httpTable tbody tr").ClickAsync();
                await Assertions.Expect(page.Locator(".protocol-load-status")).ToHaveTextAsync("5,001 nodes");
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
                Assert.Equal(5_001, await page.Locator("#request-panel-mapi .tree-item").CountAsync());
                Assert.Equal("true", await page.Locator("#request-panel-mapi .protocol-tree>.tree-item").GetAttributeAsync("aria-expanded"));

                var search = page.Locator(".http-view-search-input");
                await page.Locator("#request-panel-mapi")
                    .GetByRole(AriaRole.Button, new() { Name = "Collapse all" })
                    .ClickAsync();
                await search.FillAsync("LargeTreeNeedle");
                await Assertions.Expect(page.Locator(".http-view-search-status")).ToHaveTextAsync("1 of 1 matches");
                Assert.Equal("true", await page.Locator("#request-panel-mapi .protocol-tree>.tree-item").GetAttributeAsync("aria-expanded"));
                await search.FillAsync("");
                Assert.Equal("false", await page.Locator("#request-panel-mapi .protocol-tree>.tree-item").GetAttributeAsync("aria-expanded"));
                Assert.Empty(errors);
            }
            finally
            {
                await page.CloseAsync();
            }
    }

    private static async Task VerifyHttpActiveViewSearchAsync(IBrowser browser, string reportPath)
    {
        var errors = new List<string>();
        var popupErrors = new List<string>();
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        await InstallClipboardTestHookAsync(page);
        CaptureErrors(page, errors);
        await page.AddInitScriptAsync(
            """
            globalThis.__httpSearchViewListenerBalance=0;
            const nativeAddEventListener=EventTarget.prototype.addEventListener;
            const nativeRemoveEventListener=EventTarget.prototype.removeEventListener;
            EventTarget.prototype.addEventListener=function(type,listener,options){
              if(type==='saz-view-change'&&this.id==='inspectorBody')globalThis.__httpSearchViewListenerBalance++;
              return nativeAddEventListener.call(this,type,listener,options);
            };
            EventTarget.prototype.removeEventListener=function(type,listener,options){
              if(type==='saz-view-change'&&this.id==='inspectorBody')globalThis.__httpSearchViewListenerBalance--;
              return nativeRemoveEventListener.call(this,type,listener,options);
            };
            """);
        IPage? popup = null;
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.Locator("#httpTable tbody tr").First.ClickAsync();

            var search = page.Locator(".http-view-search-input");
            var status = page.Locator(".http-view-search-status");
            Assert.Equal("Search active Request or Response view", await search.GetAttributeAsync("aria-label"));
            Assert.Equal("polite", await status.GetAttributeAsync("aria-live"));

            await page.Locator("#request-panel-json .tree-collapse-all").ClickAsync();
            var jsonRoot = page.Locator("#request-panel-json .tree-view>.tree-item[aria-expanded]").First;
            Assert.Equal("false", await jsonRoot.GetAttributeAsync("aria-expanded"));
            await search.FillAsync("ENABLED");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal("true", await jsonRoot.GetAttributeAsync("aria-expanded"));
            Assert.Equal(1, await page.Locator("#request-panel-json .http-search-match").CountAsync());
            Assert.Equal(0, await page.Locator("#response-panel-xml .http-search-match").CountAsync());
            Assert.True(await search.EvaluateAsync<bool>("input=>document.activeElement===input"));

            await page.Locator("#request-panel-json [data-view=pretty]").ClickAsync();
            Assert.Equal("", await search.InputValueAsync());
            Assert.Equal("0 matches", await status.InnerTextAsync());
            Assert.Equal("false", await jsonRoot.GetAttributeAsync("aria-expanded"));

            await search.FillAsync("a");
            await Assertions.Expect(page.Locator(".http-search-match-current")).ToHaveCountAsync(1);
            var matchCount = await page.Locator(".http-search-match").CountAsync();
            Assert.True(matchCount > 1);
            await search.PressAsync("Shift+Enter");
            Assert.Equal(
                (matchCount - 1).ToString(),
                await page.Locator(".http-search-match-current").GetAttributeAsync("data-match-index"));
            Assert.True(await search.EvaluateAsync<bool>("input=>document.activeElement===input"));
            await search.PressAsync("Enter");
            Assert.Equal("0", await page.Locator(".http-search-match-current").GetAttributeAsync("data-match-index"));

            await search.FillAsync("enabled");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            await search.PressAsync("Enter");
            await search.PressAsync("Shift+Enter");
            Assert.Equal("0", await page.Locator(".http-search-match-current").GetAttributeAsync("data-match-index"));

            await search.FillAsync("not-present");
            await Assertions.Expect(status).ToHaveTextAsync("0 matches");
            await search.PressAsync("Enter");
            Assert.True(await search.EvaluateAsync<bool>("input=>document.activeElement===input"));
            Assert.True(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog=>dialog.open"));

            var ignoredKeys = await search.EvaluateAsync<bool[]>(
                """
                    input => {
                      const composing = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true, isComposing: true });
                      const modified = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true, ctrlKey: true });
                      input.dispatchEvent(composing);
                      input.dispatchEvent(modified);
                      return [composing.defaultPrevented, modified.defaultPrevented];
                    }
                    """);
            Assert.Equal([false, false], ignoredKeys);

            await search.FillAsync("   ");
            await search.PressAsync("Enter");
            Assert.Equal("0 matches", await status.InnerTextAsync());
            await search.FillAsync("globalthis");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.False(await page.EvaluateAsync<bool>("()=>Boolean(globalThis.pwned)"));
            await search.FillAsync("<img src=x onerror=globalThis.pwned=true>");
            await Assertions.Expect(status).ToHaveTextAsync("0 matches");
            Assert.Equal(0, await page.Locator("#httpInspector img").CountAsync());
            Assert.Equal(ExpectedJson(), await CopyAndReadAsync(page, "request-panel-json"));

            await page.Locator("#request-tab-headers").ClickAsync();
            Assert.Equal("", await search.InputValueAsync());
            await search.FillAsync("content-type");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal(1, await page.Locator("#request-panel-headers .http-search-match").CountAsync());

            await page.Locator("#primary-tab-response").ClickAsync();
            Assert.Equal("", await search.InputValueAsync());
            await page.Locator("#response-panel-xml .tree-collapse-all").ClickAsync();
            var xmlRoot = page.Locator("#response-panel-xml .tree-view>.tree-item[aria-expanded]").First;
            Assert.Equal("false", await xmlRoot.GetAttributeAsync("aria-expanded"));
            await search.FillAsync("SAFE");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal("true", await xmlRoot.GetAttributeAsync("aria-expanded"));
            await search.FillAsync("");
            Assert.Equal("false", await xmlRoot.GetAttributeAsync("aria-expanded"));

            await page.Locator("#response-panel-xml [data-view=pretty]").ClickAsync();
            await search.FillAsync("root");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 2 matches");
            await page.Locator(".http-view-search-prev").ClickAsync();
            Assert.Equal("1", await page.Locator(".http-search-match-current").GetAttributeAsync("data-match-index"));
            await page.Locator(".http-view-search-next").ClickAsync();
            Assert.Equal("0", await page.Locator(".http-search-match-current").GetAttributeAsync("data-match-index"));

            await page.Locator("#response-tab-raw").ClickAsync();
            Assert.Equal("", await search.InputValueAsync());
            await search.FillAsync("payload");
            await Assertions.Expect(status).ToHaveTextAsync("0 matches");
            Assert.Equal(0, await page.Locator("#primary-panel-request .http-search-match").CountAsync());
            await search.FillAsync("parsed as xml");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal(ExpectedResponseRaw(), await CopyAndReadAsync(page, "response-panel-raw"));

            await page.Locator("#inspectorNext").ClickAsync();
            search = page.Locator(".http-view-search-input");
            status = page.Locator(".http-view-search-status");
            Assert.Equal("", await search.InputValueAsync());
            var capturedBytes = page.Locator("#request-panel-raw .captured-bytes");
            Assert.False(await capturedBytes.EvaluateAsync<bool>("details=>details.open"));
            await search.FillAsync("00ff1020");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.True(await capturedBytes.EvaluateAsync<bool>("details=>details.open"));
            await search.FillAsync("");
            Assert.False(await capturedBytes.EvaluateAsync<bool>("details=>details.open"));

            await page.Locator("#inspectorNext").ClickAsync();
            search = page.Locator(".http-view-search-input");
            status = page.Locator(".http-view-search-status");
            var mapiRoot = page.Locator("#request-panel-mapi .protocol-tree>.tree-item[aria-expanded]").First;
            await Assertions.Expect(mapiRoot).ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-technical").First)
                .ToContainTextAsync("Operation @0 +8");
            Assert.Equal(7, await page.Locator("#request-panel-mapi .tree-item[aria-expanded=true]").CountAsync());
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-value").Filter(new() { HasText = "RopLogon = 0xFE" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-value").Filter(new() { HasText = "Straße雪" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-value").Filter(new() { HasText = @"line\0\x1B\r\n" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-value").Filter(new() { HasText = @"\u061C\u200E\u200F\u2028\u2029\u202A\u2066" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-value-binary"))
                .ToHaveTextAsync("Binary (6 bytes): 00 FF 1B 7F … (2 more bytes)");
            var mapiText = await page.Locator("#request-panel-mapi .protocol-tree").InnerTextAsync();
            Assert.DoesNotContain('\0', mapiText);
            Assert.DoesNotContain('\u001B', mapiText);
            Assert.DoesNotContain('\uFFFD', mapiText);
            Assert.DoesNotContain('\u061C', mapiText);
            Assert.DoesNotContain('\u200E', mapiText);
            Assert.DoesNotContain('\u200F', mapiText);
            Assert.DoesNotContain('\u2028', mapiText);
            Assert.DoesNotContain('\u2029', mapiText);
            Assert.DoesNotContain('\u202A', mapiText);
            Assert.DoesNotContain('\u2066', mapiText);
            Assert.Contains("Execute; Operation; offset 0; length 8", await mapiRoot.GetAttributeAsync("aria-label"));
            await mapiRoot.FocusAsync();
            await page.Keyboard.PressAsync("End");
            Assert.Contains("RawBytes", await page.Locator("#request-panel-mapi .tree-item:focus").GetAttributeAsync("aria-label"));
            await page.Keyboard.PressAsync("Home");
            await Assertions.Expect(mapiRoot).ToBeFocusedAsync();
            await page.Keyboard.PressAsync(" ");
            Assert.Equal("false", await mapiRoot.GetAttributeAsync("aria-expanded"));
            await page.Keyboard.PressAsync("Enter");
            Assert.Equal("true", await mapiRoot.GetAttributeAsync("aria-expanded"));
            await page.Locator("#request-panel-mapi")
                .GetByRole(AriaRole.Button, new() { Name = "Collapse all" })
                .ClickAsync();
            Assert.Equal("false", await mapiRoot.GetAttributeAsync("aria-expanded"));
            await search.FillAsync("propertyvalue");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal("true", await mapiRoot.GetAttributeAsync("aria-expanded"));
            var matchingMapiRow = page.Locator("#request-panel-mapi .protocol-row:has(.http-search-match-current)");
            var matchingMapiItem = page.Locator("#request-panel-mapi .tree-item[aria-label^=\"PropertyValue:\"]");
            await matchingMapiRow.ClickAsync();
            Assert.Equal("0", await matchingMapiItem.GetAttributeAsync("tabindex"));
            await search.FillAsync("");
            Assert.Equal("false", await mapiRoot.GetAttributeAsync("aria-expanded"));
            Assert.Equal("0", await mapiRoot.GetAttributeAsync("tabindex"));
            Assert.Equal("-1", await matchingMapiItem.GetAttributeAsync("tabindex"));
            Assert.Equal(ExpectedMapi(), await CopyAndReadAsync(page, "request-panel-mapi"));
            Assert.Equal(0, await page.Locator("#request-panel-mapi input[placeholder=\"Search protocol fields...\"]").CountAsync());
            await page.SetViewportSizeAsync(480, 800);
            Assert.True(await page.Locator("#request-panel-mapi .protocol-tree").EvaluateAsync<bool>(
                "tree=>{const box=tree.getBoundingClientRect();return box.left>=0&&box.right<=innerWidth&&tree.clientWidth>0}"));
            Assert.True(await page.Locator("#httpInspector").EvaluateAsync<bool>(
                "dialog=>dialog.scrollWidth<=dialog.clientWidth"));
            await page.SetViewportSizeAsync(1280, 800);

            await page.Locator("#inspectorPrev").ClickAsync();
            await page.Locator("#inspectorPrev").ClickAsync();
            search = page.Locator(".http-view-search-input");
            await search.FillAsync("payload");
            await page.Locator("#inspectorClose").ClickAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.Equal("", await page.Locator(".http-view-search-input").InputValueAsync());
            Assert.Equal(0, await page.Locator(".http-search-match").CountAsync());
            await page.SetViewportSizeAsync(480, 800);
            search = page.Locator(".http-view-search-input");
            await search.FillAsync("payload");
            await Assertions.Expect(page.Locator(".http-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            Assert.True(await page.Locator(".http-view-search").EvaluateAsync<bool>(
                "toolbar=>{const box=toolbar.getBoundingClientRect();return box.left>=0&&box.right<=innerWidth&&box.width>0}"));
            Assert.True(await page.Locator("#httpInspector").EvaluateAsync<bool>(
                "dialog=>dialog.scrollWidth<=dialog.clientWidth"));
            await page.Locator("#request-tab-headers").ClickAsync();
            Assert.Equal("", await search.InputValueAsync());
            await page.Locator("#request-tab-json").ClickAsync();
            await page.SetViewportSizeAsync(1280, 800);
            Assert.Equal(
                1,
                await page.EvaluateAsync<int>("()=>globalThis.__httpSearchViewListenerBalance"));
            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            popup = await popupTask;
            CaptureErrors(popup, popupErrors);
            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await popup.Locator("#httpInspector[open] .http-view-search-input").WaitForAsync();
            var popupSearch = popup.Locator(".http-view-search-input");
            await popupSearch.FillAsync("payload");
            await Assertions.Expect(popup.Locator(".http-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            await popup.Locator("#request-tab-headers").ClickAsync();
            Assert.Equal("", await popupSearch.InputValueAsync());
            Assert.Equal(0, await popup.Locator(".http-search-match").CountAsync());

            Assert.Empty(errors);
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

    [WindowsEdgeFact]
    public async Task HttpActiveViewSearchCapsLargeRawMatchesAndUsesUnicodeSafeNonOverlappingRanges()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"saz viewer http active search {Guid.NewGuid():N}");
        var reportPath = Path.Combine(tempDirectory, "large http search report.html");
        var errors = new List<string>();
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var body = $"{new string('x', 6_001)}\nİx Straße雪 ΟΣ aa aa";
            var report = new SazReport { SourceName = "large-http-search.saz" };
            report.Sessions.Add(
                new HttpSession
                {
                    Id = "1",
                    ArchiveOrder = 0,
                    Method = "POST",
                    Url = "https://example.test/large-search",
                    StatusCode = 200,
                    Request = Message("POST /large-search HTTP/1.1", "text/plain", body)
                });
            await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(report));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true });
            var page = await browser.NewPageAsync();
            await InstallClipboardTestHookAsync(page);
            CaptureErrors(page, errors);
            try
            {
                await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
                await page.Locator("#httpTable tbody tr").ClickAsync();
                var search = page.Locator(".http-view-search-input");
                var status = page.Locator(".http-view-search-status");

                await search.FillAsync("X");
                await Assertions.Expect(status).ToHaveTextAsync("1 of 5000+ matches (capped)");
                Assert.Equal(5_000, await page.Locator(".http-search-match").CountAsync());

                await search.FillAsync("xx");
                await Assertions.Expect(status).ToHaveTextAsync("1 of 3000 matches");
                Assert.Equal(3_000, await page.Locator(".http-search-match").CountAsync());

                await search.FillAsync("İX");
                await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
                Assert.Equal("İx", await page.Locator(".http-search-match").InnerTextAsync());
                await search.FillAsync("STRAßE雪");
                await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
                Assert.Equal("Straße雪", await page.Locator(".http-search-match").InnerTextAsync());
                await search.FillAsync("ΟΣ");
                await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
                Assert.Equal("ΟΣ", await page.Locator(".http-search-match").InnerTextAsync());
                await search.FillAsync("aa");
                await Assertions.Expect(status).ToHaveTextAsync("1 of 2 matches");

                var copied = await CopyAndReadAsync(page, "request-panel-raw");
                Assert.NotNull(copied);
                Assert.Contains(body, copied, StringComparison.Ordinal);
                Assert.DoesNotContain("<mark", copied, StringComparison.Ordinal);
                Assert.Empty(errors);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [WindowsEdgeFact]
    public async Task WebSocketPayloadSearchHandlesFiveThousandMessagesWithinDisplayBound()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"saz viewer ws search {Guid.NewGuid():N}");
        var reportPath = Path.Combine(tempDirectory, "large websocket report.html");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var report = new SazReport { SourceName = "large-websocket-search.saz" };
            report.Sessions.Add(
                new HttpSession
                {
                    Id = "1",
                    ArchiveOrder = 0,
                    Method = "GET",
                    Url = "wss://example.test/large",
                    StatusCode = 101
                });
            report.Sessions.Add(
                new HttpSession
                {
                    Id = "2",
                    ArchiveOrder = 1,
                    Method = "GET",
                    Url = "wss://example.test/second",
                    StatusCode = 101
                });
            for (var index = 0; index < 5_000; index++)
            {
                var text = index == 4_999 ? "unique retained payload needle" : $"common payload {index}";
                var payload = Encoding.UTF8.GetBytes(text);
                report.WebSocketMessages.Add(
                    new WebSocketMessage
                    {
                        SessionId = "1",
                        MessageIndex = index,
                        RecordIndex = index,
                        Direction = index % 2 == 0 ? "Client" : "Server",
                        Type = "Text",
                        PayloadLength = payload.Length,
                        Preview = text,
                        Text = text,
                        IsComplete = true,
                        IsDecoded = true,
                        Payload = payload
                    });
            }
            report.WebSocketMessages.Add(
                new WebSocketMessage
                {
                    SessionId = "2",
                    MessageIndex = 0,
                    RecordIndex = 0,
                    Direction = "Server",
                    Type = "Text",
                    PayloadLength = 14,
                    Preview = "second session",
                    Text = "second session",
                    IsComplete = true,
                    IsDecoded = true,
                    Payload = Encoding.UTF8.GetBytes("second session")
                });
            await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(report));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true });
            var page = await browser.NewPageAsync();
            var errors = new List<string>();
            CaptureErrors(page, errors);
            try
            {
                await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
                await page.Locator("#httpTable tbody tr[data-websocket=\"true\"]").First.ClickAsync();
                await page.Locator(".ws-payload-search").WaitForAsync(new() { Timeout = 10_000 });
                Assert.Equal(5_000, await page.Locator(".ws-message-row").CountAsync());
                Assert.Equal("5000 of 5000 messages", await page.Locator(".ws-search-status").InnerTextAsync());

                await page.Locator(".ws-payload-search").FillAsync("UNIQUE RETAINED PAYLOAD NEEDLE");
                await Assertions.Expect(page.Locator(".ws-search-status"))
                    .ToHaveTextAsync("1 of 5000 messages", new() { Timeout = 10_000 });
                Assert.Equal("\u2193 5000", await page.Locator(".ws-message-row:not(.ws-filtered) .ws-id").InnerTextAsync());

                await page.Locator(".ws-payload-search").FillAsync("  ");
                await Assertions.Expect(page.Locator(".ws-search-status"))
                    .ToHaveTextAsync("5000 of 5000 messages", new() { Timeout = 10_000 });

                var splitter = page.Locator(".ws-splitter");
                await splitter.FocusAsync();
                await page.Keyboard.PressAsync("End");
                var firstTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var firstDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(firstTraffic);
                Assert.NotNull(firstDetail);
                var rememberedRatio = firstTraffic.Width / (firstTraffic.Width + firstDetail.Width);
                await page.Locator(".ws-view-search-input").FillAsync("common payload");
                await page.Locator("#inspectorNext").ClickAsync();
                await Assertions.Expect(page.Locator(".ws-search-status")).ToHaveTextAsync("1 of 1 messages");
                Assert.Equal("", await page.Locator(".ws-view-search-input").InputValueAsync());
                var secondTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var secondDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(secondTraffic);
                Assert.NotNull(secondDetail);
                var secondRatio = secondTraffic.Width / (secondTraffic.Width + secondDetail.Width);
                Assert.InRange(Math.Abs(secondRatio - rememberedRatio), 0, .02);

                await page.Keyboard.PressAsync("Escape");
                await page.Locator("#httpTable tbody tr[data-detail=\"http-detail-0\"]").ClickAsync();
                await Assertions.Expect(page.Locator(".ws-search-status")).ToHaveTextAsync("5000 of 5000 messages");
                var reopenedTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var reopenedDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(reopenedTraffic);
                Assert.NotNull(reopenedDetail);
                var reopenedRatio = reopenedTraffic.Width / (reopenedTraffic.Width + reopenedDetail.Width);
                Assert.InRange(Math.Abs(reopenedRatio - rememberedRatio), 0, .02);
                Assert.Empty(errors);
            }
            finally
            {
                await page.CloseAsync();
            }
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
            var originalRow = page.Locator("#httpTable tbody tr:not(.hidden)").First;
            var tableScroll = page.Locator(".http-table-scroll");
            await tableScroll.EvaluateAsync(
                "element=>{element.style.height='40px';element.scrollTop=35}");
            var originalScrollTop = await tableScroll.EvaluateAsync<double>("element=>element.scrollTop");
            await originalRow.ClickAsync();

            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            popup = await popupTask;
            CaptureErrors(popup, popupErrors);
            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await popup.Locator("#httpInspector[open]").WaitForAsync();

            Assert.False(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
            Assert.True(await page.Locator("main").IsVisibleAsync());
            Assert.Equal(InjectionText, await page.Locator("#httpSearch").InputValueAsync());
            Assert.Equal("2", await page.Locator("#httpFilter").InputValueAsync());
            Assert.Equal(originalScrollTop, await tableScroll.EvaluateAsync<double>("element=>element.scrollTop"));
            Assert.Equal("http-detail-0", await page.EvaluateAsync<string>(
                "() => document.activeElement?.getAttribute('data-detail') || ''"));
            Assert.Equal("true", await originalRow.GetAttributeAsync("aria-selected"));

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
            await popup.Locator("#request-panel-mapi .protocol-tree").WaitForAsync();
            Assert.Equal("true", await popup.Locator("#request-panel-mapi .protocol-tree>.tree-item").GetAttributeAsync("aria-expanded"));
            await Assertions.Expect(popup.Locator("#request-panel-mapi .protocol-value-binary"))
                .ToContainTextAsync("Binary (6 bytes)");
            Assert.True(await popup.Locator("#inspectorNext").IsDisabledAsync());
            await popup.Locator("#inspectorPrev").ClickAsync();
            Assert.Equal("1 of 2", await popup.Locator("#inspectorPosition").InnerTextAsync());

            Assert.Equal(InjectionText, await page.Locator("#httpSearch").InputValueAsync());
            Assert.Equal("2", await page.Locator("#httpFilter").InputValueAsync());

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

    private static async Task VerifyWebSocketInspectorAsync(IBrowser browser, string reportPath, int width)
    {
        var errors = new List<string>();
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = width, Height = 900 },
        });
        await InstallClipboardTestHookAsync(page);
        CaptureErrors(page, errors);
        IPage? popup = null;
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.Locator("#httpFilter").SelectOptionAsync("websocket");
            Assert.Equal(1, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await page.Locator("#httpSearch").FillAsync("ping");
            Assert.Equal(1, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await page.Locator("#httpSearch").FillAsync("");
            await page.Locator("#httpFilter").SelectOptionAsync("");
            var row = page.Locator("#httpTable tbody tr[data-websocket=\"true\"]");
            Assert.True(await page.EvaluateAsync<bool>(
                "()=>Boolean(document.getElementById('http-detail-3').content.querySelector('[data-payload-type=\"websocket-session\"]'))"));
            await row.ClickAsync();
            await page.Locator(".ws-message-row").First.WaitForAsync();
            Assert.Null(await page.Locator("#inspectorBody .websocket-inspector")
                .GetAttributeAsync("data-compressed-payload"));

            var dialog = await page.Locator("#httpInspector").BoundingBoxAsync();
            var traffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
            var detail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
            Assert.NotNull(dialog);
            Assert.NotNull(traffic);
            Assert.NotNull(detail);
            Assert.True(dialog.Width >= width - 1 && dialog.Height >= 899);
            double? rememberedSplitRatio = null;
            if (width > 900)
            {
                Assert.True(detail.X > traffic.X + traffic.Width - 2);
                var initialPreview = await page.Locator(".ws-message-row").First.Locator(".ws-message-preview").BoundingBoxAsync();
                Assert.NotNull(initialPreview);
                var splitter = page.Locator(".ws-splitter");
                Assert.True(await splitter.IsVisibleAsync());
                Assert.Equal("separator", await splitter.GetAttributeAsync("role"));
                Assert.Equal("vertical", await splitter.GetAttributeAsync("aria-orientation"));
                Assert.NotNull(await splitter.GetAttributeAsync("aria-valuemin"));
                Assert.NotNull(await splitter.GetAttributeAsync("aria-valuemax"));
                Assert.NotNull(await splitter.GetAttributeAsync("aria-valuenow"));
                Assert.Contains("Left pane", await splitter.GetAttributeAsync("aria-valuetext"));

                await splitter.FocusAsync();
                await page.Keyboard.PressAsync("Home");
                var homeTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var homeDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(homeTraffic);
                Assert.NotNull(homeDetail);
                Assert.InRange(homeTraffic.Width, 278, 282);
                Assert.True(homeDetail.Width > homeTraffic.Width);

                await page.Keyboard.PressAsync("ArrowRight");
                var arrowTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                Assert.NotNull(arrowTraffic);
                Assert.True(arrowTraffic.Width >= homeTraffic.Width + 10);
                await page.Keyboard.PressAsync("Shift+ArrowRight");
                var shiftedTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                Assert.NotNull(shiftedTraffic);
                Assert.True(shiftedTraffic.Width >= arrowTraffic.Width + 38);

                await page.Keyboard.PressAsync("End");
                var endTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var endDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(endTraffic);
                Assert.NotNull(endDetail);
                Assert.InRange(endDetail.Width, 318, 322);
                Assert.True(endTraffic.Width > endDetail.Width);

                await page.Keyboard.PressAsync("Home");
                var splitterBox = await splitter.BoundingBoxAsync();
                var layoutBox = await page.Locator(".ws-layout").BoundingBoxAsync();
                Assert.NotNull(splitterBox);
                Assert.NotNull(layoutBox);
                await page.Mouse.MoveAsync(splitterBox.X + (splitterBox.Width / 2), splitterBox.Y + 20);
                await page.Mouse.DownAsync();
                await page.Mouse.MoveAsync(layoutBox.X + (layoutBox.Width * .34f), splitterBox.Y + 20, new() { Steps = 4 });
                await page.Mouse.MoveAsync(layoutBox.X + (layoutBox.Width * .56f), splitterBox.Y + 20, new() { Steps = 4 });
                await page.Mouse.MoveAsync(layoutBox.X + (layoutBox.Width * .44f), splitterBox.Y + 20, new() { Steps = 4 });
                await page.Mouse.MoveAsync(layoutBox.X + (layoutBox.Width * .56f), splitterBox.Y + 20, new() { Steps = 4 });
                await page.Mouse.UpAsync();
                var draggedTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var draggedDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(draggedTraffic);
                Assert.NotNull(draggedDetail);
                rememberedSplitRatio = draggedTraffic.Width / (draggedTraffic.Width + draggedDetail.Width);
                Assert.InRange(rememberedSplitRatio.Value, .53, .59);
                var draggedPreview = await page.Locator(".ws-message-row").First.Locator(".ws-message-preview").BoundingBoxAsync();
                Assert.NotNull(draggedPreview);
                Assert.True(draggedPreview.Width > initialPreview.Width);
                Assert.False(await page.Locator("body").EvaluateAsync<bool>("body=>body.classList.contains('ws-resizing')"));

                await page.SetViewportSizeAsync(700, 900);
                await page.WaitForTimeoutAsync(50);
                Assert.False(await splitter.IsVisibleAsync());
                var narrowTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var narrowDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(narrowTraffic);
                Assert.NotNull(narrowDetail);
                Assert.True(narrowDetail.Y > narrowTraffic.Y + narrowTraffic.Height - 2);
                Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));

                await page.SetViewportSizeAsync(width, 900);
                await page.WaitForTimeoutAsync(50);
                Assert.True(await splitter.IsVisibleAsync());
                var restoredTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var restoredDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(restoredTraffic);
                Assert.NotNull(restoredDetail);
                var restoredRatio = restoredTraffic.Width / (restoredTraffic.Width + restoredDetail.Width);
                Assert.InRange(Math.Abs(restoredRatio - rememberedSplitRatio.Value), 0, .02);
            }
            else
            {
                Assert.True(detail.Y > traffic.Y + traffic.Height - 2);
                Assert.False(await page.Locator(".ws-splitter").IsVisibleAsync());
                Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            }

            var messages = page.Locator(".ws-message-row");
            Assert.Equal(10, await messages.CountAsync());
            Assert.Equal(
                ["ID", "Type", "Body", "Preview"],
                await page.Locator(".ws-message-header>span").AllInnerTextsAsync());
            Assert.Equal("true", await messages.First.GetAttributeAsync("aria-selected"));
            Assert.Contains("Client to server", await messages.First.GetAttributeAsync("aria-label"));
            Assert.Equal("\u2191 1", await messages.First.Locator(".ws-id").InnerTextAsync());
            Assert.Equal("Text", await messages.First.Locator(".ws-type").InnerTextAsync());
            Assert.Equal(
                Encoding.UTF8.GetByteCount(WebSocketJson).ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                await messages.First.Locator(".ws-body").InnerTextAsync());
            var listPreview = await messages.First.Locator(".ws-message-preview").InnerTextAsync();
            Assert.DoesNotContain('\n', listPreview);
            Assert.EndsWith("\u2026", listPreview, StringComparison.Ordinal);
            Assert.DoesNotContain("2024-", await messages.First.InnerTextAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain("frame", await messages.First.InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("rgb(88, 166, 255)", await messages.First.Locator(".ws-arrow")
                .EvaluateAsync<string>("element=>getComputedStyle(element).color"));
            Assert.Equal("rgb(63, 185, 80)", await messages.Nth(1).Locator(".ws-arrow")
                .EvaluateAsync<string>("element=>getComputedStyle(element).color"));
            Assert.Equal("\u2193 2", await messages.Nth(1).Locator(".ws-id").InnerTextAsync());
            Assert.Equal("Ping", await messages.Nth(1).Locator(".ws-type").InnerTextAsync());
            Assert.Contains("Ping control", await messages.Nth(1).Locator(".ws-message-preview").InnerTextAsync());
            Assert.Contains("00 FF 10 20", await messages.Nth(2).Locator(".ws-message-preview").InnerTextAsync());
            Assert.Equal("Invalid", await messages.Nth(4).Locator(".ws-type").InnerTextAsync());
            Assert.Equal("\u2191 10", await messages.Nth(9).Locator(".ws-id").InnerTextAsync());
            Assert.Equal("Partial", await messages.Nth(7).Locator(".ws-type").InnerTextAsync());
            Assert.Equal("Text", await messages.Nth(9).Locator(".ws-type").InnerTextAsync());
            Assert.Equal("1,234,567*", await messages.Nth(9).Locator(".ws-body").InnerTextAsync());
            var widthsBeforeSearch = await page.Locator(".ws-message-header>span").EvaluateAllAsync<float[]>(
                "cells=>cells.map(cell=>cell.getBoundingClientRect().width)");
            Assert.True(await page.Locator(".ws-message-tracks").EvaluateAsync<bool>(
                @"grid=>{
                  const header=grid.querySelector('.ws-message-header');
                  const rows=[...grid.querySelectorAll('.ws-message-row')];
                  if(!header||rows.length<2) return false;
                  const contentWidth=cell=>{
                    const range=document.createRange();
                    range.selectNodeContents(cell);
                    const style=getComputedStyle(cell);
                    return range.getBoundingClientRect().width
                      +parseFloat(style.paddingLeft)+parseFloat(style.paddingRight)
                      +parseFloat(style.borderLeftWidth)+parseFloat(style.borderRightWidth);
                  };
                  for(let column=0;column<3;column++){
                    const cells=[header.children[column],...rows.map(row=>row.children[column])];
                    const widths=cells.map(cell=>cell.getBoundingClientRect().width);
                    if(Math.max(...widths)-Math.min(...widths)>0.75) return false;
                    const needed=cells.map(contentWidth);
                    const longest=Math.max(...needed);
                    if(Math.abs(widths[0]-longest)>1.5) return false;
                    if(!needed.some(width=>width<longest-1)) return false;
                  }
                  const firstRow=rows[0];
                  const firstThree=[0,1,2].reduce((total,index)=>total+firstRow.children[index].getBoundingClientRect().width,0);
                  const rowWidth=firstRow.getBoundingClientRect().width;
                  const preview=firstRow.children[3];
                  const previewWidth=preview.getBoundingClientRect().width;
                  const previewStyle=getComputedStyle(preview);
                  const previewRange=document.createRange();
                  previewRange.selectNodeContents(preview);
                  const previewIsBounded=previewStyle.minWidth==='0px'
                    &&previewStyle.overflow==='hidden'
                    &&previewStyle.whiteSpace==='nowrap'
                    &&previewStyle.textOverflow==='ellipsis'
                    &&previewRange.getBoundingClientRect().width>previewWidth;
                  if(!previewIsBounded||Math.abs(previewWidth-(rowWidth-firstThree))>1.5) return false;
                  return innerWidth>900 ? previewWidth>180 : previewWidth>=179;
                }"));
            Assert.True(await page.Locator(".ws-message-scroll").EvaluateAsync<bool>(
                @"element=>{
                  const pane=element.closest('.ws-traffic-pane');
                  const inspector=element.closest('#httpInspector');
                  if(!pane||!inspector) return false;
                  const elementBounds=element.getBoundingClientRect();
                  const paneBounds=pane.getBoundingClientRect();
                  const inspectorBounds=inspector.getBoundingClientRect();
                  const contained=elementBounds.left>=paneBounds.left-1
                    &&elementBounds.right<=paneBounds.right+1
                    &&paneBounds.left>=inspectorBounds.left-1
                    &&paneBounds.right<=inspectorBounds.right+1;
                  if(innerWidth>900) return contained;
                  const original=element.scrollLeft;
                  element.scrollLeft=element.scrollWidth;
                  const scrollable=element.scrollWidth>element.clientWidth&&element.scrollLeft>original;
                  element.scrollLeft=original;
                  return contained&&scrollable;
                }"));

            var search = page.Locator(".ws-payload-search");
            Assert.Equal("Search WebSocket payloads", await search.GetAttributeAsync("aria-label"));
            Assert.Contains("retained decoded text and JSON payload content", await search.GetAttributeAsync("title"));
            Assert.Contains("bytes omitted by safety truncation", await search.GetAttributeAsync("title"));
            Assert.Equal("10 of 10 messages", await page.Locator(".ws-search-status").InnerTextAsync());
            Assert.Equal("polite", await page.Locator(".ws-search-status").GetAttributeAsync("aria-live"));

            await search.FillAsync("NEEDLEBEYONDPREVIEW");
            Assert.Equal(1, await page.Locator(".ws-message-row:not(.ws-filtered)").CountAsync());
            Assert.Equal("1 of 10 messages", await page.Locator(".ws-search-status").InnerTextAsync());
            Assert.Equal("true", await messages.First.GetAttributeAsync("aria-selected"));
            Assert.True(await search.EvaluateAsync<bool>("input=>document.activeElement===input"));
            Assert.False(await page.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));
            var widthsAfterSearch = await page.Locator(".ws-message-header>span").EvaluateAllAsync<float[]>(
                "cells=>cells.map(cell=>cell.getBoundingClientRect().width)");
            Assert.Equal(widthsBeforeSearch.Length, widthsAfterSearch.Length);
            for (var column = 0; column < widthsBeforeSearch.Length; column++)
            {
                Assert.InRange(Math.Abs(widthsBeforeSearch[column] - widthsAfterSearch[column]), 0, 0.75);
            }
            await search.FillAsync("ONERROR=GLOBALTHIS");
            Assert.Equal("1 of 10 messages", await page.Locator(".ws-search-status").InnerTextAsync());
            Assert.False(await page.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));

            await search.FillAsync("shared");
            Assert.Equal(2, await page.Locator(".ws-message-row:not(.ws-filtered)").CountAsync());
            Assert.Equal("2 of 10 messages", await page.Locator(".ws-search-status").InnerTextAsync());
            Assert.Equal("true", await messages.Nth(5).GetAttributeAsync("aria-selected"));
            await messages.Nth(5).FocusAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            Assert.Equal("true", await messages.Nth(6).GetAttributeAsync("aria-selected"));
            await search.FillAsync("beta");
            Assert.Equal("true", await messages.Nth(6).GetAttributeAsync("aria-selected"));
            Assert.True(await search.EvaluateAsync<bool>("input=>document.activeElement===input"));
            await search.FillAsync("alpha");
            Assert.Equal("true", await messages.Nth(5).GetAttributeAsync("aria-selected"));
            Assert.True(await search.EvaluateAsync<bool>("input=>document.activeElement===input"));

            foreach (var excludedQuery in new[] { "00 ff", "1,234,567", "invalid synthetic frame", "client to server" })
            {
                await search.FillAsync(excludedQuery);
                Assert.Equal("0 of 10 messages", await page.Locator(".ws-search-status").InnerTextAsync());
                Assert.Equal(0, await page.Locator(".ws-message-row:not(.ws-filtered)").CountAsync());
                Assert.True(await page.Locator(".ws-message-empty").IsVisibleAsync());
                Assert.Contains("No WebSocket messages match", await page.Locator(".ws-detail-pane").InnerTextAsync());
                Assert.Equal(0, await page.Locator(".ws-detail-content").CountAsync());
            }

            await search.FillAsync("   ");
            Assert.Equal("10 of 10 messages", await page.Locator(".ws-search-status").InnerTextAsync());
            Assert.Equal(10, await page.Locator(".ws-message-row:not(.ws-filtered)").CountAsync());
            Assert.Equal("true", await messages.First.GetAttributeAsync("aria-selected"));

            Assert.Equal("true", await page.Locator("[role=tab][data-tab=json]").GetAttributeAsync("aria-selected"));
            await page.Locator(".ws-detail-pane .tree-item").First.WaitForAsync();
            var expectedJson = new BodyFormatter().Format(
                new BodyPreview
                {
                    Length = WebSocketJson.Length,
                    CapturedLength = WebSocketJson.Length,
                    Preview = WebSocketJson
                },
                null).Formatted;
            var viewSearch = page.Locator(".ws-view-search-input");
            var viewSearchStatus = page.Locator(".ws-view-search-status");
            Assert.Equal("Search selected WebSocket payload view", await viewSearch.GetAttributeAsync("aria-label"));
            Assert.Equal("polite", await viewSearchStatus.GetAttributeAsync("aria-live"));
            await page.Locator(".ws-detail-pane .tree-collapse-all").ClickAsync();
            var treeRoot = page.Locator(".ws-detail-pane .tree-view>.tree-item[aria-expanded]").First;
            Assert.Equal("false", await treeRoot.GetAttributeAsync("aria-expanded"));
            await viewSearch.FillAsync("needlebeyondpreview");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal(1, await page.Locator(".ws-search-match").CountAsync());
            Assert.Equal(1, await page.Locator(".ws-search-match-current").CountAsync());
            Assert.Equal("true", await treeRoot.GetAttributeAsync("aria-expanded"));
            Assert.True(await viewSearch.EvaluateAsync<bool>("input=>document.activeElement===input"));
            await page.Locator(".ws-detail-pane [data-view=pretty]").ClickAsync();
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 1 matches");
            await page.Locator(".ws-detail-pane [data-view=tree]").ClickAsync();
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 1 matches");
            await viewSearch.FillAsync("   ");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("0 matches");
            Assert.Equal(0, await page.Locator(".ws-search-match").CountAsync());
            Assert.Equal("false", await treeRoot.GetAttributeAsync("aria-expanded"));

            await page.Locator(".ws-detail-pane [data-view=pretty]").ClickAsync();
            Assert.Contains("\"kind\"", await page.Locator(".ws-detail-pane .pretty-subview").InnerTextAsync());
            await viewSearch.FillAsync("x");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 5000+ matches (capped)");
            Assert.Equal(5_000, await page.Locator(".ws-search-match").CountAsync());
            Assert.Equal(expectedJson, await CopyAndReadAsync(page, "ws-json-panel-0"));
            await viewSearch.FillAsync("ONERROR=GLOBALTHIS");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 1 matches");
            Assert.False(await page.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));

            await page.Locator("[role=tab][data-tab=text]").ClickAsync();
            await viewSearch.FillAsync("needlebeyondpreview");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal(WebSocketJson, await CopyAndReadAsync(page, "ws-text-panel-0"));
            await viewSearch.FillAsync("Copy");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("0 matches");

            await page.Locator("[role=tab][data-tab=raw]").ClickAsync();
            await viewSearch.FillAsync("ID=");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 2 matches");

            await viewSearch.PressAsync("Enter");
            Assert.Equal("2 of 2 matches", await viewSearchStatus.InnerTextAsync());
            Assert.True(await viewSearch.EvaluateAsync<bool>("input=>document.activeElement===input"));
            await viewSearch.PressAsync("Enter");
            Assert.Equal("1 of 2 matches", await viewSearchStatus.InnerTextAsync());
            await viewSearch.PressAsync("Shift+Enter");
            Assert.Equal("2 of 2 matches", await viewSearchStatus.InnerTextAsync());
            await viewSearch.PressAsync("Shift+Enter");
            Assert.Equal("1 of 2 matches", await viewSearchStatus.InnerTextAsync());
            Assert.True(await viewSearch.EvaluateAsync<bool>("input=>document.activeElement===input"));

            var ignoredKeyResults = await viewSearch.EvaluateAsync<bool[]>(
                """
                input => {
                  const options = [
                    { key: 'Enter', isComposing: true },
                    { key: 'Enter', ctrlKey: true },
                    { key: 'Enter', altKey: true },
                    { key: 'Enter', metaKey: true }
                  ];
                  const results = options.map(option =>
                    input.dispatchEvent(new KeyboardEvent('keydown', {
                      ...option, bubbles: true, cancelable: true
                    })));
                  const imeFallback = new KeyboardEvent('keydown', {
                    key: 'Enter', bubbles: true, cancelable: true
                  });
                  Object.defineProperty(imeFallback, 'keyCode', { value: 229 });
                  results.push(input.dispatchEvent(imeFallback));
                  return results;
                }
                """);
            Assert.All(ignoredKeyResults, Assert.True);
            Assert.Equal("1 of 2 matches", await viewSearchStatus.InnerTextAsync());
            Assert.True(await viewSearch.EvaluateAsync<bool>("input=>document.activeElement===input"));

            await page.Locator(".ws-view-search-prev").ClickAsync();
            Assert.Equal("2 of 2 matches", await viewSearchStatus.InnerTextAsync());
            await page.Locator(".ws-view-search-next").ClickAsync();
            Assert.Equal("1 of 2 matches", await viewSearchStatus.InnerTextAsync());
            await viewSearch.FillAsync("needle");
            Assert.Equal("1 of 1 matches", await viewSearchStatus.InnerTextAsync());
            await viewSearch.PressAsync("Enter");
            Assert.Equal("1 of 1 matches", await viewSearchStatus.InnerTextAsync());
            await viewSearch.PressAsync("Shift+Enter");
            Assert.Equal("1 of 1 matches", await viewSearchStatus.InnerTextAsync());
            Assert.True(await viewSearch.EvaluateAsync<bool>("input=>document.activeElement===input"));
            await viewSearch.FillAsync("no selected payload match");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("0 matches");
            await viewSearch.PressAsync("Enter");
            await viewSearch.PressAsync("Shift+Enter");
            Assert.Equal("0 matches", await viewSearchStatus.InnerTextAsync());
            Assert.True(await viewSearch.EvaluateAsync<bool>("input=>document.activeElement===input"));
            await viewSearch.FillAsync("needle");
            await page.Locator("[role=tab][data-tab=text]").ClickAsync();
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 1 matches");

            await page.Locator("[role=tab][data-tab=json]").ClickAsync();
            await page.Locator(".ws-detail-pane [data-view=tree]").ClickAsync();
            await page.Locator(".ws-detail-pane .tree-collapse-all").ClickAsync();
            Assert.Equal("false", await page.Locator(".ws-detail-pane .tree-item[aria-expanded]").First
                .GetAttributeAsync("aria-expanded"));
            await viewSearch.FillAsync("searchtail");
            await Assertions.Expect(viewSearchStatus).ToHaveTextAsync("1 of 1 matches");

            await viewSearch.EvaluateAsync(
                "input=>{input.value='needle';input.dispatchEvent(new Event('input',{bubbles:true}))}");
            await messages.Nth(5).ClickAsync();
            Assert.Equal(0, await page.Locator(".ws-search-match").CountAsync());
            await page.Locator(".ws-view-search-input").FillAsync("STRAßE雪");
            await Assertions.Expect(page.Locator(".ws-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            await page.Locator(".ws-view-search-input").FillAsync("X");
            await Assertions.Expect(page.Locator(".ws-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal("x", await page.Locator(".ws-search-match").InnerTextAsync());
            await page.Locator(".ws-view-search-input").FillAsync("aa");
            await Assertions.Expect(page.Locator(".ws-view-search-status")).ToHaveTextAsync("1 of 2 matches");
            await messages.Nth(1).ClickAsync();
            Assert.Equal("", await page.Locator(".ws-view-search-input").InputValueAsync());
            Assert.Equal(0, await page.Locator(".ws-search-match").CountAsync());
            Assert.Contains("Ping", await page.Locator(".ws-detail-pane").InnerTextAsync());
            Assert.True(await page.Locator("[role=tab][data-tab=json]").IsDisabledAsync());
            Assert.True(await page.Locator("[role=tab][data-tab=text]").IsDisabledAsync());
            Assert.Equal("true", await page.Locator("[role=tab][data-tab=raw]").GetAttributeAsync("aria-selected"));
            Assert.Contains("Frame 1 ID=2", await page.Locator(".ws-raw-view").InnerTextAsync());

            await messages.Nth(2).FocusAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            Assert.Equal("true", await messages.Nth(3).GetAttributeAsync("aria-selected"));
            Assert.Contains("Close", await page.Locator(".ws-detail-pane").InnerTextAsync());
            await messages.Nth(2).ClickAsync();
            var binaryRaw = await CopyAndReadAsync(page, "ws-raw-panel-2");
            Assert.NotNull(binaryRaw);
            Assert.Contains("00 FF 10 20", binaryRaw.Replace("  ", " ", StringComparison.Ordinal));

            await search.FillAsync("shared");
            await page.Locator("#inspectorPrev").ClickAsync();
            Assert.True(await page.Locator(".primary-tab-strip").IsVisibleAsync());
            Assert.Equal("true", await page.Locator("#primary-tab-request").GetAttributeAsync("aria-selected"));
            await page.Locator("#primary-tab-request").FocusAsync();
            await page.Keyboard.PressAsync("Alt+ArrowRight");
            await page.Locator(".ws-message-row").First.WaitForAsync();
            Assert.Equal("inspectorClose", await page.EvaluateAsync<string>(
                "()=>document.activeElement?.id||''"));
            Assert.Equal("", await page.Locator(".ws-payload-search").InputValueAsync());
            Assert.Equal("", await page.Locator(".ws-view-search-input").InputValueAsync());
            Assert.Equal("10 of 10 messages", await page.Locator(".ws-search-status").InnerTextAsync());
            if (rememberedSplitRatio.HasValue)
            {
                var reopenedTraffic = await page.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var reopenedDetail = await page.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(reopenedTraffic);
                Assert.NotNull(reopenedDetail);
                var reopenedRatio = reopenedTraffic.Width / (reopenedTraffic.Width + reopenedDetail.Width);
                Assert.InRange(Math.Abs(reopenedRatio - rememberedSplitRatio.Value), 0, .02);
            }

            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            popup = await popupTask;
            CaptureErrors(popup, errors);
            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await popup.Locator(".ws-message-row").First.WaitForAsync();
            Assert.False(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog=>dialog.open"));
            Assert.True(await popup.Locator("body").EvaluateAsync<bool>("body=>body.classList.contains('inspector-only')"));
            Assert.Contains("WebSocket traffic", await popup.Locator(".ws-traffic-pane").InnerTextAsync());
            Assert.Equal("4 of 4", await popup.Locator("#inspectorPosition").InnerTextAsync());
            if (rememberedSplitRatio.HasValue)
            {
                var popupTraffic = await popup.Locator(".ws-traffic-pane").BoundingBoxAsync();
                var popupDetail = await popup.Locator(".ws-detail-pane").BoundingBoxAsync();
                Assert.NotNull(popupTraffic);
                Assert.NotNull(popupDetail);
                var popupRatio = popupTraffic.Width / (popupTraffic.Width + popupDetail.Width);
                Assert.InRange(popupRatio, .35, .41);
                Assert.True(Math.Abs(popupRatio - rememberedSplitRatio.Value) > .1);
            }
            await popup.Locator(".ws-payload-search").FillAsync("searchtail");
            Assert.Equal("1 of 10 messages", await popup.Locator(".ws-search-status").InnerTextAsync());
            Assert.Equal("true", await popup.Locator(".ws-message-row").First.GetAttributeAsync("aria-selected"));
            await popup.Locator(".ws-view-search-input").FillAsync("needlebeyondpreview");
            await Assertions.Expect(popup.Locator(".ws-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            await popup.Locator("#inspectorPrev").ClickAsync();
            Assert.True(await popup.Locator(".primary-tab-strip").IsVisibleAsync());
            await popup.Locator("#inspectorNext").ClickAsync();
            await popup.Locator(".ws-message-row").First.WaitForAsync();
            await popup.Locator("#inspectorClose").ClickAsync();
            await Assertions.Expect(popup.Locator("main")).ToBeVisibleAsync();

            await page.Locator("#httpTable tbody tr[data-websocket=\"true\"]").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.Locator(".ws-message-row").First.WaitForAsync();
            Assert.Equal("", await page.Locator(".ws-payload-search").InputValueAsync());
            await page.Keyboard.PressAsync("Escape");
            Assert.Equal("http-detail-3", await page.EvaluateAsync<string>(
                "()=>document.activeElement?.getAttribute('data-detail')||''"));
            Assert.Empty(errors);
        }
        finally
        {
            if (popup is not null) await popup.CloseAsync();
            await page.CloseAsync();
        }
    }

    private static async Task VerifyBlockedNewTabKeepsInspectorAsync(IBrowser browser, string reportPath)
    {
        var page = await browser.NewPageAsync();
        var errors = new List<string>();
        CaptureErrors(page, errors);
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.Locator("#httpSearch").FillAsync("example.test");
            await page.Locator("#httpFilter").SelectOptionAsync("2");
            await page.Locator("#httpTable tbody tr:not(.hidden)").First.ClickAsync();
            await page.EvaluateAsync("window.open=()=>null");

            await page.Locator("#inspectorOpenTab").ClickAsync();

            Assert.True(await page.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
            Assert.Contains(
                "The browser blocked the new tab.",
                await page.Locator("#inspectorOpenStatus").InnerTextAsync());
            Assert.Equal("example.test", await page.Locator("#httpSearch").InputValueAsync());
            Assert.Equal("2", await page.Locator("#httpFilter").InputValueAsync());
            Assert.Equal("true", await page.Locator("#httpTable tbody tr:not(.hidden)").First
                .GetAttributeAsync("aria-selected"));
            Assert.Empty(errors);
        }
        finally
        {
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

        var webSocketPage = await browser.NewPageAsync();
        var webSocketErrors = new List<string>();
        CaptureErrors(webSocketPage, webSocketErrors);
        try
        {
            await webSocketPage.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await webSocketPage.EvaluateAsync(
                """
                () => {
                  const host=document.getElementById('http-detail-3').content.querySelector('[data-payload-type="websocket-session"]');
                  host.dataset.compressedPayload='@@@@';
                }
                """);
            await webSocketPage.Locator("#httpTable tbody tr[data-websocket=\"true\"]").ClickAsync();
            await Assertions.Expect(webSocketPage.Locator(".websocket-inspector"))
                .ToContainTextAsync("WebSocket traffic could not be loaded because its compressed report payload is corrupt, unsupported, or exceeds safety limits.");
            Assert.True(await webSocketPage.Locator(".websocket-inspector")
                .EvaluateAsync<bool>("element=>element.classList.contains('warning')"));
            await webSocketPage.WaitForTimeoutAsync(50);
            Assert.Empty(webSocketErrors);
        }
        finally
        {
            await webSocketPage.CloseAsync();
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
            await page.Locator(".http-view-search-input").FillAsync("999");
            await page.Locator("#request-panel-json [data-view=\"pretty\"]").ClickAsync();
            await page.WaitForTimeoutAsync(250);
            Assert.Equal("", await page.Locator(".http-view-search-input").InputValueAsync());
            Assert.Equal("0 matches", await page.Locator(".http-view-search-status").InnerTextAsync());
            Assert.Equal(0, await page.Locator(".http-search-match").CountAsync());
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
        "  ExecuteRequestBody [Structure] @0 +8\n" +
        "    RopBuffer [Structure] @0 +8\n" +
        "      Buffers [Array] @0 +8\n" +
        "        ExtendedBuffer [Structure] @0 +8\n" +
        "          Payload [Structure] @0 +8\n" +
        "            RopsList [Array] @0 +8\n" +
        "              PropertyValue [Property] @4 +4 = safe\n" +
        "              RopId [Field] @4 +1 = 0xFE (RopLogon)\n" +
        "              UnicodeText [Field] @5 +2 = Straße雪\n" +
        "              ControlledText [Field] @6 +2 = line\0\u001B\r\n\u061C\u200E\u200F\u2028\u2029\u202A\u2066\n" +
        "              RawBytes [Raw] @8 +6 = 00FF1B7F ... [2 more bytes]";

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
            [
                new MapiNode(
                    "ExecuteRequestBody",
                    MapiNodeKind.Structure,
                    0,
                    8,
                    null,
                    [
                        new MapiNode(
                            "RopBuffer",
                            MapiNodeKind.Structure,
                            0,
                            8,
                            null,
                            [
                                new MapiNode(
                                    "Buffers",
                                    MapiNodeKind.Array,
                                    0,
                                    8,
                                    null,
                                    [
                                        new MapiNode(
                                            "ExtendedBuffer",
                                            MapiNodeKind.Structure,
                                            0,
                                            8,
                                            null,
                                            [
                                                new MapiNode(
                                                    "Payload",
                                                    MapiNodeKind.Structure,
                                                    0,
                                                    8,
                                                    null,
                                                    [
                                                        new MapiNode(
                                                            "RopsList",
                                                            MapiNodeKind.Array,
                                                            0,
                                                            8,
                                                            null,
                                                            [
                                                                MapiNode.Leaf("PropertyValue", MapiNodeKind.Property, 4, 4, "safe"),
                                                                MapiNode.Leaf("RopId", MapiNodeKind.Field, 4, 1, "0xFE (RopLogon)"),
                                                                MapiNode.Leaf("UnicodeText", MapiNodeKind.Field, 5, 2, "Straße雪"),
                                                                MapiNode.Leaf("ControlledText", MapiNodeKind.Field, 6, 2, "line\0\u001B\r\n\u061C\u200E\u200F\u2028\u2029\u202A\u2066"),
                                                                MapiNode.Leaf("RawBytes", MapiNodeKind.Raw, 8, 6, "00FF1B7F ... [2 more bytes]")
                                                            ])
                                                    ])
                                            ])
                                    ])
                            ])
                    ])
            ]);
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
        report.Sessions.Add(
            new HttpSession
            {
                Id = "4",
                ArchiveOrder = 3,
                Method = "GET",
                Url = "wss://example.test/socket",
                StatusCode = 101
            });

        var jsonBytes = Encoding.UTF8.GetBytes(WebSocketJson);
        var json = WebSocketMessage(
            0, 0, "Client", "Text", jsonBytes, WebSocketJson, complete: true, fragmented: true);
        json.Frames.Add(WebSocketFrame(0, 1, "Client", 1, "Text", final: false, masked: true, jsonBytes[..12]));
        json.Frames.Add(WebSocketFrame(2, 3, "Client", 0, "Continuation", final: true, masked: true, jsonBytes[12..]));
        report.WebSocketMessages.Add(json);

        var pingBytes = Encoding.UTF8.GetBytes("probe");
        var ping = WebSocketMessage(1, 1, "Server", "Ping", pingBytes, null, complete: true);
        ping.Frames.Add(WebSocketFrame(1, 2, "Server", 9, "Ping", final: true, masked: false, pingBytes));
        report.WebSocketMessages.Add(ping);

        var binaryBytes = new byte[] { 0x00, 0xFF, 0x10, 0x20 };
        var binary = WebSocketMessage(2, 3, "Server", "Binary", binaryBytes, null, complete: true);
        binary.Frames.Add(WebSocketFrame(3, 4, "Server", 2, "Binary", final: true, masked: false, binaryBytes));
        report.WebSocketMessages.Add(binary);

        var closeBytes = new byte[] { 0x03, 0xE8 };
        var close = WebSocketMessage(3, 4, "Server", "Close", closeBytes, null, complete: true);
        close.Frames.Add(WebSocketFrame(4, 5, "Server", 8, "Close", final: true, masked: false, closeBytes));
        report.WebSocketMessages.Add(close);

        report.WebSocketMessages.Add(
            WebSocketMessage(
                4,
                5,
                "Client",
                "Undecoded",
                new byte[] { 0xFF },
                null,
                complete: false,
                warning: "Invalid synthetic frame."));
        report.WebSocketMessages.Add(
            WebSocketMessage(
                5,
                6,
                "Server",
                "Text",
                Encoding.UTF8.GetBytes("aaaa shared alpha payload Straße雪 İx"),
                "aaaa shared alpha payload Straße雪 İx",
                complete: true));
        report.WebSocketMessages.Add(
            WebSocketMessage(
                6,
                7,
                "Client",
                "Text",
                Encoding.UTF8.GetBytes("shared beta payload"),
                "shared beta payload",
                complete: true));
        report.WebSocketMessages.Add(
            WebSocketMessage(
                7,
                8,
                "Client",
                "Text",
                Encoding.UTF8.GetBytes("unfinished"),
                null,
                complete: false,
                fragmented: true,
                warning: "Synthetic fragmented message is incomplete."));
        report.WebSocketMessages.Add(
            WebSocketMessage(
                8,
                9,
                "Server",
                "Pong",
                new byte[] { 8 },
                null,
                complete: true));
        report.WebSocketMessages.Add(
            WebSocketMessage(
                9,
                10,
                "Client",
                "Text",
                Encoding.UTF8.GetBytes("partial retained needle"),
                "partial retained needle",
                complete: true,
                fragmented: true,
                payloadLength: 1_234_567,
                warning: "Synthetic logical payload was truncated by the retained-payload limit."));
        return report;
    }

    private static WebSocketMessage WebSocketMessage(
        int messageIndex,
        int recordIndex,
        string direction,
        string type,
        byte[] payload,
        string? text,
        bool complete,
        bool fragmented = false,
        long? payloadLength = null,
        string? warning = null) =>
        new()
        {
            SessionId = "4",
            MessageIndex = messageIndex,
            RecordIndex = recordIndex,
            Timestamp = DateTimeOffset.Parse("2024-05-01T12:00:00Z").AddSeconds(recordIndex),
            Direction = direction,
            Type = type,
            PayloadLength = payloadLength ?? payload.Length,
            Preview = text ?? Convert.ToHexString(payload),
            IsBinary = type != "Text",
            IsDecoded = complete,
            IsComplete = complete,
            IsFragmented = fragmented,
            IsPayloadTruncated = payloadLength.HasValue && payloadLength.Value > payload.Length,
            Text = text,
            Warning = warning,
            Payload = payload
        };

    private static WebSocketFrame WebSocketFrame(
        int recordIndex,
        int fiddlerId,
        string direction,
        int opcode,
        string type,
        bool final,
        bool masked,
        byte[] payload) =>
        new()
        {
            RecordIndex = recordIndex,
            FiddlerId = fiddlerId,
            BitFlags = 0,
            Timestamp = DateTimeOffset.Parse("2024-05-01T12:00:00Z").AddSeconds(recordIndex),
            Direction = direction,
            Opcode = opcode,
            Type = type,
            Final = final,
            Masked = masked,
            PayloadLength = payload.Length,
            CapturedPayloadLength = payload.Length,
            IsDecoded = true,
            Payload = payload
        };

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
