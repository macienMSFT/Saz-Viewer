using System.Collections.Immutable;
using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HtmlReportBrowserTests
{
    private const string InjectionText = "<img src=x onerror=globalThis.pwned=true>";
    private const string RequestBody = """{"payload":{"enabled":true},"items":[1,2],"emptyObject":{},"emptyArray":[],"escapedQuote":"a\"b","trailingBackslash":"x\\","unicodeEscape":"\u263A","emoji":"😀","attack":"</script><svg onload=globalThis.pwned=true>"}""";
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
            var report = CreateReport();
            await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(report));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = "msedge",
                Headless = true,
            });

            await VerifyHttpSessionTableLayoutAsync(browser, tempDirectory);
            await VerifyInspectorAsync(browser, reportPath, 1440, exerciseAllControls: true);
            await VerifyInspectorAsync(browser, reportPath, 480, exerciseAllControls: false);
            await VerifyUnpairedSurrogatesAsync(browser, reportPath);
            await VerifyThemePersistenceAsync(browser, reportPath);
            await VerifyHttpActiveViewSearchAsync(browser, reportPath);
            await VerifyHttpLayoutDefaultsAndMigrationAsync(browser, reportPath);
            await VerifyHttpSplitViewAsync(browser, reportPath);
            await VerifyStructuredTreeToolbarsAsync(browser, tempDirectory);
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

    private static async Task VerifyUnpairedSurrogatesAsync(IBrowser browser, string reportPath)
    {
        var errors = new List<string>();
        var page = await browser.NewPageAsync();
        CaptureErrors(page, errors);
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.EvaluateAsync(
                """
                async()=>{
                  const host=document.getElementById('http-detail-0').content.querySelector('[data-payload-type="http-session"]');
                  const compressed=Uint8Array.from(atob(host.dataset.compressedPayload),character=>character.charCodeAt(0));
                  const sourceStream=new Blob([compressed]).stream().pipeThrough(new DecompressionStream('gzip'));
                  const model=JSON.parse(await new Response(sourceStream).text());
                  const body=String.raw`{"emoji":"😀","loneHigh":"\uD800","loneLow":"\uDC00"}`;
                  model.request.body.fallbackText=body;
                  model.request.body.length=new TextEncoder().encode(body).length;
                  model.request.body.capturedLength=model.request.body.length;
                  const encoded=new TextEncoder().encode(JSON.stringify(model));
                  const reader=new Blob([encoded]).stream().pipeThrough(new CompressionStream('gzip')).getReader();
                  const chunks=[];let length=0;
                  while(true){
                    const result=await reader.read();
                    if(result.done)break;
                    chunks.push(result.value);length+=result.value.length;
                  }
                  const replacement=new Uint8Array(length);let offset=0;
                  for(const chunk of chunks){replacement.set(chunk,offset);offset+=chunk.length}
                  let binary='';for(const value of replacement)binary+=String.fromCharCode(value);
                  host.dataset.compressedPayload=btoa(binary);
                  host.dataset.payloadDecodedBytes=String(encoded.length);
                }
                """);
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            var labels = page.Locator("#request-panel-json .tree-label");
            await Assertions.Expect(labels.Filter(new() { HasText = "emoji: 😀" })).ToHaveCountAsync(1);
            await Assertions.Expect(labels.Filter(new() { HasText = @"loneHigh: \uD800" })).ToHaveCountAsync(1);
            await Assertions.Expect(labels.Filter(new() { HasText = @"loneLow: \uDC00" })).ToHaveCountAsync(1);
            Assert.DoesNotContain('\uFFFD', await page.Locator("#request-panel-json .json-tree").InnerTextAsync());
            Assert.Empty(errors);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [WindowsEdgeFact]
    public async Task OptionalCorpusReportMeetsLazyInspectorPerformanceBudget()
    {
        var reportPath = Environment.GetEnvironmentVariable("SAZ_VIEWER_PERF_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
        {
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true,
        });
        var page = await browser.NewPageAsync();
        var timer = Stopwatch.StartNew();
        await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
        await page.Locator("#httpTable tbody tr").First.WaitForAsync();
        timer.Stop();

        var timings = await page.EvaluateAsync<JsonElement>(
            """
                async()=>{
                  const inflate=async host=>{
                    const binary=atob(host.dataset.compressedPayload);
                    const bytes=Uint8Array.from(binary,character=>character.charCodeAt(0));
                    const stream=new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
                    return JSON.parse(await new Response(stream).text());
                  };
                  let json=null,hex=null;
                  for(const template of document.querySelectorAll('template[id^="http-detail-"]')){
                    const host=template.content.querySelector('[data-payload-type="http-session"]');
                    if(!host)continue;
                    const store=await inflate(host);
                    for(const side of ['request','response']){
                      const model=store[side];if(!model)continue;
                      const retained=(model.body.decoded||model.body.captured||'').length;
                      const candidate={detail:template.id,side,size:retained};
                      if(model.format==='json'&&model.canToggle&&(!json||retained>json.size))json=candidate;
                      if(model.body.captured&&(!hex||model.body.captured.length>hex.size))hex=candidate;
                    }
                  }
                  const waitFor=predicate=>new Promise((resolve,reject)=>{
                    const started=performance.now();
                    const poll=()=>{
                      if(predicate())return resolve();
                      if(performance.now()-started>5000)return reject(new Error('performance view did not become ready'));
                      requestAnimationFrame(poll);
                    };
                    poll();
                  });
                  const measure=async(candidate,key,ready)=>{
                    if(!candidate)return null;
                    const row=document.querySelector(`#httpTable tbody tr[data-detail="${candidate.detail}"]`);
                    const started=performance.now();row.click();
                    await waitFor(()=>document.querySelector('.primary-tab-strip'));
                    const shellMs=performance.now()-started;
                    if(candidate.side==='response')document.getElementById('primary-tab-response').click();
                    document.getElementById(`${candidate.side}-tab-${key}`).click();
                    await waitFor(()=>ready(document.getElementById(`${candidate.side}-panel-${key}`)));
                    const totalMs=performance.now()-started;
                    document.getElementById('inspectorClose').click();
                    return{totalMs,viewMs:totalMs-shellMs,shellMs};
                  };
                  await new Promise(resolve=>setTimeout(resolve,1000));
                  const jsonMs=await measure(json,'json',panel=>(panel?.querySelector('.formatted-view')?.textContent||'').length>0);
                  await new Promise(resolve=>setTimeout(resolve,250));
                  const hexMs=await measure(hex,'hex',panel=>(panel?.querySelector('.hex-dump')?.textContent||'').length>0);
                  return{jsonMs,hexMs,jsonBytes:json?.size||0,hexBytes:hex?.size||0};
                }
                """);

        var jsonTiming = timings.GetProperty("jsonMs");
        var hexTiming = timings.GetProperty("hexMs");
        var jsonMs = jsonTiming.ValueKind == JsonValueKind.Null
            ? (double?)null
            : jsonTiming.GetProperty("viewMs").GetDouble();
        var hexMs = hexTiming.ValueKind == JsonValueKind.Null
            ? (double?)null
            : hexTiming.GetProperty("viewMs").GetDouble();
        var jsonDescription = jsonMs is null
            ? "largest JSON view: unavailable"
            : $"largest JSON view: {jsonMs:F1} ms after a {jsonTiming.GetProperty("shellMs").GetDouble():F1} ms inspector shell " +
              $"({timings.GetProperty("jsonBytes").GetInt32():N0} base64 chars)";
        var hexDescription = hexMs is null
            ? "largest HexView: unavailable"
            : $"largest HexView: {hexMs:F1} ms after a {hexTiming.GetProperty("shellMs").GetDouble():F1} ms inspector shell " +
              $"({timings.GetProperty("hexBytes").GetInt32():N0} base64 chars)";
        Console.WriteLine(
            $"Corpus initial load: {timer.Elapsed.TotalMilliseconds:F1} ms; " +
            $"{jsonDescription}; {hexDescription}.");
        if (jsonMs is not null)
        {
            Assert.True(jsonMs < 50, $"Largest retained JSON first open took {jsonMs:F1} ms.");
        }
        if (hexMs is not null)
        {
            Assert.True(hexMs < 50, $"Largest retained HexView first open took {hexMs:F1} ms.");
        }
    }

    private static async Task VerifyHttpSessionTableLayoutAsync(IBrowser browser, string tempDirectory)
    {
        var reportPath = Path.Combine(tempDirectory, "http-table-layout.html");
        await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(CreateHttpSessionTableReport()));
        var errors = new List<string>();
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        CaptureErrors(page, errors);
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            Assert.Equal(
                ["Time", "ID", "Result", "Method", "URL", "Elapsed Time", "Req", "Resp"],
                await page.Locator("#httpTable thead th").AllInnerTextsAsync());
            Assert.Equal("200", await page.Locator("#httpTable tbody tr").Nth(0).Locator(".http-result").InnerTextAsync());
            Assert.Equal("HTTP 200 OK", await page.Locator("#httpTable tbody tr").Nth(0).Locator(".http-result").GetAttributeAsync("title"));
            Assert.Equal("1,234 ms", await page.Locator("#httpTable tbody tr").Nth(0).Locator(".http-elapsed").InnerTextAsync());
            Assert.Equal("\u2014", await page.Locator("#httpTable tbody tr").Nth(3).Locator(".http-result").InnerTextAsync());
            Assert.Equal("Aborted by client", await page.Locator("#httpTable tbody tr").Nth(3).Locator(".http-result").GetAttributeAsync("title"));
            Assert.Equal("\u2014", await page.Locator("#httpTable tbody tr").Nth(3).Locator(".http-elapsed").InnerTextAsync());
            Assert.Equal(0, await page.Locator("#httpTable .http-protocol").CountAsync());

            var layoutError = await page.Locator("#httpTable").EvaluateAsync<string>(
                """
                    table=>{
                      const header=[...table.tHead.rows[0].cells],rows=[...table.tBodies[0].rows];
                      if(header.length!==8||rows.length!==4)return'wrong table shape';
                      const contentWidth=cell=>{
                        const range=document.createRange();range.selectNodeContents(cell);
                        const style=getComputedStyle(cell);
                        return range.getBoundingClientRect().width
                          +parseFloat(style.paddingLeft)+parseFloat(style.paddingRight)
                          +parseFloat(style.borderLeftWidth)+parseFloat(style.borderRightWidth);
                      };
                      for(const column of [0,1,2,3,5,6,7]){
                        const cells=[header[column],...rows.map(row=>row.cells[column])];
                        const widths=cells.map(cell=>cell.getBoundingClientRect().width);
                        if(Math.max(...widths)-Math.min(...widths)>.75)return`column ${column} is not aligned`;
                        const longest=Math.max(...cells.map(contentWidth));
                        if(Math.abs(widths[0]-longest)>1.5)return`column ${column} is not content-fit`;
                        if(cells.some(cell=>cell.scrollWidth>cell.clientWidth+1))return`column ${column} clips its header or value`;
                      }
                      const first=rows[0],url=first.cells[4],urlText=url.querySelector('.http-url-value');
                      const otherWidth=[0,1,2,3,5,6,7].reduce((total,index)=>total+first.cells[index].getBoundingClientRect().width,0);
                      if(Math.abs(url.getBoundingClientRect().width-(first.getBoundingClientRect().width-otherWidth))>1.5)return'URL does not receive remaining width';
                      const style=getComputedStyle(urlText);
                      if(style.overflow!=='hidden'||style.textOverflow!=='ellipsis'||style.whiteSpace!=='nowrap')return'URL ellipsis styling is missing';
                      if(urlText.scrollWidth<=urlText.clientWidth)return'long URL did not overflow';
                      for(const column of [2,5,6,7]){
                        if(getComputedStyle(first.cells[column]).textAlign!=='right')return`numeric column ${column} is not right aligned`;
                      }
                      return'';
                    }
                    """);
            Assert.Equal("", layoutError);

            var widthsBeforeFilter = await page.Locator("#httpTable thead th").EvaluateAllAsync<float[]>(
                "cells=>cells.map(cell=>cell.getBoundingClientRect().width)");
            var search = page.Locator("#httpSearch");
            var hideConnect = page.Locator("#hideConnect");
            await hideConnect.CheckAsync();
            Assert.Equal(3, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await search.FillAsync("connect-only");
            Assert.Equal(0, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await search.FillAsync("");
            await page.Locator("#httpTable tbody tr:not(.hidden)").First.ClickAsync();
            Assert.Equal("1 of 3", await page.Locator("#inspectorPosition").InnerTextAsync());
            await page.Locator("#inspectorNext").ClickAsync();
            Assert.Equal("2 of 3", await page.Locator("#inspectorPosition").InnerTextAsync());
            Assert.Contains("long-session-id", await page.Locator("#inspectorTitle").InnerTextAsync());
            await page.Locator("#inspectorClose").ClickAsync();
            await search.FillAsync("1,234 ms");
            Assert.Equal(1, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await search.FillAsync("aborted by client");
            Assert.Equal(1, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            var widthsAfterFilter = await page.Locator("#httpTable thead th").EvaluateAllAsync<float[]>(
                "cells=>cells.map(cell=>cell.getBoundingClientRect().width)");
            Assert.Equal(widthsBeforeFilter.Length, widthsAfterFilter.Length);
            for (var index = 0; index < widthsBeforeFilter.Length; index++)
            {
                Assert.InRange(Math.Abs(widthsBeforeFilter[index] - widthsAfterFilter[index]), 0, .75);
            }
            await search.FillAsync("");
            await page.Locator("#httpFilter").SelectOptionAsync("2");
            Assert.Equal(1, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await page.Locator("#httpFilter").SelectOptionAsync("0");
            Assert.Equal(1, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await page.Locator("#httpFilter").SelectOptionAsync("");
            await search.FillAsync("connect-only");
            Assert.Equal(0, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            await hideConnect.UncheckAsync();
            Assert.Equal(1, await page.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            Assert.Empty(errors);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [WindowsEdgeFact]
    public async Task ImageHexAndAuthViewsAreSafeExactAndResetAcrossNavigation()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"saz viewer content tabs {Guid.NewGuid():N}");
        var reportPath = Path.Combine(tempDirectory, "content-tabs.html");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(CreateContentTabReport()));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = "msedge",
                Headless = true
            });
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new ViewportSize { Width = 1100, Height = 760 }
            });
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            CaptureErrors(page, errors);
            await InstallClipboardTestHookAsync(page);
            await page.AddInitScriptAsync("""
                    globalThis.__createdObjectUrls=[];
                    globalThis.__revokedObjectUrls=[];
                    const nativeCreateObjectURL=URL.createObjectURL.bind(URL);
                    const nativeRevokeObjectURL=URL.revokeObjectURL.bind(URL);
                    URL.createObjectURL=value=>{const url=nativeCreateObjectURL(value);globalThis.__createdObjectUrls.push(url);return url};
                    URL.revokeObjectURL=url=>{globalThis.__revokedObjectUrls.push(url);nativeRevokeObjectURL(url)};
                    """);
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.ReloadAsync();

            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.Equal(
                ["JSON", "XML", "MAPI", "Image", "WebView", "HexView", "Auth", "Headers", "Raw"],
                await page.Locator("#primary-panel-request>.message-panel>.tab-strip>[role=tab]").AllTextContentsAsync());
            await page.Locator("#request-tab-hex").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-hex .hex-dump")).ToContainTextAsync("1F 8B 08 00");
            await Assertions.Expect(page.Locator("#request-panel-hex .hex-status")).ToHaveTextAsync("Showing all 8 bytes.");
            await page.Locator("#request-panel-hex .hex-source").SelectOptionAsync("decoded");
            await Assertions.Expect(page.Locator("#request-panel-hex .hex-dump")).ToContainTextAsync("00 01 02 03 04 05 06 07");
            await Assertions.Expect(page.Locator("#request-panel-hex .hex-dump")).ToContainTextAsync("F8 F9 FA FB FC FD FE FF");
            await Assertions.Expect(page.Locator("#request-panel-hex .hex-dump")).ToContainTextAsync("| !\"#$%&'()*+,-./|");
            var decodedHex = await page.Locator("#request-panel-hex .hex-dump").InnerTextAsync();
            Assert.Equal(decodedHex, await CopyAndReadAsync(page, "request-panel-hex"));
            var search = page.Locator(".http-view-search-input");
            await search.FillAsync("00 01 02");
            await Assertions.Expect(page.Locator(".http-view-search-status")).ToHaveTextAsync("1 of 2 matches");

            await page.Locator("#request-tab-auth").ClickAsync();
            Assert.Equal("", await search.InputValueAsync());
            var authPanel = page.Locator("#request-panel-auth");
            await Assertions.Expect(authPanel).ToContainTextAsync("Authorization: Bearer [redacted]");
            await Assertions.Expect(authPanel).ToContainTextAsync("Proxy-Authorization: [redacted]");
            Assert.DoesNotContain("request-secret-token", await authPanel.InnerTextAsync(), StringComparison.Ordinal);
            Assert.Equal(
                "Authorization: Bearer [redacted]\nProxy-Authorization: [redacted]\n",
                await CopyAndReadAsync(page, "request-panel-auth"));
            await search.FillAsync("request-secret-token");
            await Assertions.Expect(page.Locator(".http-view-search-status")).ToHaveTextAsync("0 matches");

            await page.EvaluateAsync("""
                    () => {
                      document.querySelector('#request-panel-auth .auth-reveal').click();
                      document.querySelector('#request-tab-hex').click();
                    }
                    """);
            await page.Locator("#request-tab-auth").ClickAsync();
            await page.WaitForTimeoutAsync(100);
            Assert.DoesNotContain("request-secret-token", await authPanel.InnerTextAsync(), StringComparison.Ordinal);
            await Assertions.Expect(authPanel.Locator(".auth-reveal")).ToHaveAttributeAsync("aria-pressed", "false");

            await authPanel.Locator(".auth-reveal").ClickAsync();
            await Assertions.Expect(search).ToHaveValueAsync("");
            await Assertions.Expect(authPanel).ToContainTextAsync("request-secret-token");
            await search.FillAsync("request-secret-token");
            await Assertions.Expect(page.Locator(".http-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            var revealed = await CopyAndReadAsync(page, "request-panel-auth");
            Assert.Contains("request-secret-token", revealed, StringComparison.Ordinal);
            await page.Locator("#request-tab-hex").ClickAsync();
            await page.Locator("#request-tab-auth").ClickAsync();
            Assert.DoesNotContain("request-secret-token", await authPanel.InnerTextAsync(), StringComparison.Ordinal);
            await Assertions.Expect(authPanel.Locator(".auth-reveal")).ToHaveAttributeAsync("aria-pressed", "false");

            await page.Locator("#primary-tab-response").ClickAsync();
            await page.Locator("#response-tab-auth").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-auth")).ToContainTextAsync("WWW-Authenticate: Digest realm=[redacted], nonce=[redacted]");
            await page.Locator("#primary-tab-request").ClickAsync();
            await page.Locator("#request-tab-auth").ClickAsync();
            Assert.DoesNotContain("request-secret-token", await authPanel.InnerTextAsync(), StringComparison.Ordinal);

            await page.Locator("#inspectorClose").ClickAsync();
            await page.Locator("#httpTable tbody tr").Nth(1).ClickAsync();
            await page.Locator("#request-tab-image").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-image .image-load-status"))
                .ToContainTextAsync("decoded locally");
            Assert.True(await page.Locator("#request-panel-image img").EvaluateAsync<bool>("image=>image.naturalWidth===1&&image.naturalHeight===1"));
            Assert.Equal(1, await page.EvaluateAsync<int>("globalThis.__createdObjectUrls.length"));
            Assert.Equal(1, await page.EvaluateAsync<int>("globalThis.__revokedObjectUrls.length"));
            Assert.NotEqual(
                "none",
                await page.Locator("#request-panel-image .image-stage").EvaluateAsync<string>("element=>getComputedStyle(element).backgroundImage"));
            await page.Locator("#primary-tab-response").ClickAsync();
            await page.Locator("#response-tab-image").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-image .warning"))
                .ToContainTextAsync("does not match the retained bytes");
            await Assertions.Expect(page.Locator("#response-panel-image .image-load-status"))
                .ToContainTextAsync("decoded locally");

            await page.SetViewportSizeAsync(420, 760);
            Assert.True(await page.Locator("#httpInspector").EvaluateAsync<bool>(
                "dialog=>dialog.scrollWidth<=dialog.clientWidth"));

            await page.Locator("#inspectorClose").ClickAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            await page.Locator("#request-tab-auth").ClickAsync();
            await page.Locator("#request-panel-auth .auth-reveal").ClickAsync();
            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            var popup = await popupTask;
            try
            {
                await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await popup.Locator("#request-tab-auth").ClickAsync();
                Assert.DoesNotContain("request-secret-token", await popup.Locator("#request-panel-auth").InnerTextAsync(), StringComparison.Ordinal);
                Assert.Null(await popup.EvaluateAsync<object?>("window.opener"));
            }
            finally
            {
                await popup.CloseAsync();
            }
            Assert.Empty(errors);
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
    public async Task WebViewIsInertOfflineLazySearchableAndTornDownAcrossNavigation()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"saz viewer webview {Guid.NewGuid():N}");
        var reportPath = Path.Combine(tempDirectory, "webview.html");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var report = CreateWebViewReport();
            await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(report));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = "msedge",
                Headless = true
            });
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new ViewportSize { Width = 1100, Height = 760 }
            });
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            var externalRequests = new List<string>();
            var unexpectedPopups = 0;
            var unexpectedDownloads = 0;
            CaptureErrors(page, errors);
            await InstallClipboardTestHookAsync(page);
            page.Request += (_, request) =>
            {
                if (request.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || request.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || request.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                    || request.Url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                {
                    externalRequests.Add(request.Url);
                }
            };
            page.Popup += (_, _) => unexpectedPopups++;
            page.Download += (_, _) => unexpectedDownloads++;

            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.Equal(
                ["JSON", "XML", "MAPI", "Image", "WebView", "HexView", "Auth", "Headers", "Raw"],
                await page.Locator("#primary-panel-request>.message-panel>.tab-strip>[role=tab]").AllTextContentsAsync());
            await Assertions.Expect(page.Locator("#request-panel-webview iframe")).ToHaveCountAsync(0);

            await page.Locator("#request-tab-webview").ClickAsync();
            var frameElement = page.Locator("#request-panel-webview iframe");
            await page.WaitForTimeoutAsync(250);
            Assert.True(
                await frameElement.CountAsync() == 1,
                $"WebView frame was not created. Status: {await page.Locator("#request-panel-webview .webview-status").InnerTextAsync()} Errors: {string.Join(" | ", errors)}");
            Assert.Equal("", await frameElement.GetAttributeAsync("sandbox"));
            Assert.Equal("no-referrer", await frameElement.GetAttributeAsync("referrerpolicy"));
            Assert.Null(await frameElement.GetAttributeAsync("allow"));
            await Assertions.Expect(page.Locator("#request-panel-webview .webview-status"))
                .ToContainTextAsync("Scripts, forms, navigation, storage, and subresources are blocked");
            var child = page.Frames.Single(frame => frame != page.MainFrame);
            await Assertions.Expect(child.Locator("h1")).ToHaveTextAsync("Safe captured layout");
            await Assertions.Expect(child.Locator("a")).ToHaveTextAsync("Inert captured link");
            await Assertions.Expect(child.Locator("img,script,form,iframe,object,svg,math,video,a[href]"))
                .ToHaveCountAsync(0);
            await Assertions.Expect(child.Locator("style")).ToHaveCountAsync(1);
            Assert.DoesNotContain("invalid", await child.Locator("style").InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.True(child.Url is "" or "about:srcdoc", $"Unexpected child-frame URL: {child.Url}");
            Assert.Null(await page.EvaluateAsync<object?>("globalThis.__webviewPwned"));
            Assert.Empty(externalRequests);
            Assert.Equal(0, unexpectedPopups);
            Assert.Equal(0, unexpectedDownloads);
            Assert.Equal(new Uri(reportPath).AbsoluteUri, page.Url);

            var search = page.Locator(".http-view-search-input");
            await search.FillAsync("script.invalid");
            await Assertions.Expect(page.Locator("#request-panel-webview .webview-source")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".http-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            Assert.Contains("https://script.invalid/", await page.Locator("#request-panel-webview .webview-source").InnerTextAsync(), StringComparison.Ordinal);
            Assert.Equal(WebViewSource(), await CopyAndReadAsync(page, "request-panel-webview"));

            await page.Locator("#request-panel-webview [data-webview-mode=rendered]").ClickAsync();
            await Assertions.Expect(frameElement).ToHaveCountAsync(1);
            var explicitTheme = await page.EvaluateAsync<string>(
                "()=>{const value=document.documentElement.dataset.theme;const current=value==='light'||value==='dark'?value:(matchMedia('(prefers-color-scheme:dark)').matches?'dark':'light');return current==='dark'?'light':'dark'}");
            await page.Locator("dialog .theme-toggle").ClickAsync();
            await Assertions.Expect(frameElement).ToHaveCountAsync(1);
            child = page.Frames.Single(frame => frame != page.MainFrame);
            Assert.Equal(explicitTheme, await child.Locator("html").GetAttributeAsync("data-theme"));

            await page.Locator("#request-tab-raw").ClickAsync();
            await Assertions.Expect(frameElement).ToHaveCountAsync(0);
            await page.Locator("#request-tab-webview").ClickAsync();
            await Assertions.Expect(frameElement).ToHaveCountAsync(1);
            await page.Locator("#primary-tab-response").ClickAsync();
            await Assertions.Expect(frameElement).ToHaveCountAsync(0);
            await page.Locator("#response-tab-webview").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-webview iframe")).ToHaveCountAsync(1);

            await page.SetViewportSizeAsync(420, 760);
            Assert.True(await page.Locator("#httpInspector").EvaluateAsync<bool>(
                "dialog=>dialog.scrollWidth<=dialog.clientWidth"));

            await page.Locator("#inspectorClose").ClickAsync();
            await Assertions.Expect(page.Locator("#httpInspector iframe")).ToHaveCountAsync(0);
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            await page.Locator("#request-tab-webview").WaitForAsync();
            await page.EvaluateAsync("""
                        () => {
                          document.querySelector('#request-tab-webview').click();
                          document.querySelector('#request-tab-raw').click();
                        }
                        """);
            await page.WaitForTimeoutAsync(100);
            await Assertions.Expect(page.Locator("#request-panel-webview iframe")).ToHaveCountAsync(0);

            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            var popup = await popupTask;
            try
            {
                await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await popup.Locator("#request-tab-webview").ClickAsync();
                await Assertions.Expect(popup.Locator("#request-panel-webview iframe")).ToHaveCountAsync(1);
                Assert.Empty(externalRequests);
            }
            finally
            {
                await popup.CloseAsync();
            }

            Assert.Empty(errors);
            Assert.Empty(externalRequests);
            Assert.Equal(0, unexpectedDownloads);
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
        const int leafCount = MapiParseLimits.MaxNodes - 1;
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
        var html = new HtmlReportGenerator().Generate(report);
        Assert.Contains("data-payload-type=\"mapi-protocol\"", html, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(html), 1, 8 * 1024 * 1024);
        await File.WriteAllTextAsync(path, html);

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
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").ClickAsync();
            await Assertions.Expect(page.Locator(".protocol-load-status")).ToContainTextAsync("Loading");
            await page.Locator("#inspectorClose").ClickAsync();
            await page.WaitForTimeoutAsync(200);
            var abandonedCount = await page.Locator("#request-panel-mapi .tree-item").CountAsync();
            await page.WaitForTimeoutAsync(200);
            Assert.Equal(abandonedCount, await page.Locator("#request-panel-mapi .tree-item").CountAsync());

            var started = DateTime.UtcNow;
            await page.Locator("#httpTable tbody tr").ClickAsync();
            await Assertions.Expect(page.Locator(".protocol-load-status")).ToHaveTextAsync(
                "25,000 nodes",
                new() { Timeout = 25_000 });
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(25));
            Assert.Equal(MapiParseLimits.MaxNodes, await page.Locator("#request-panel-mapi .tree-item").CountAsync());
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

    private static async Task VerifyThemePersistenceAsync(IBrowser browser, string reportPath)
    {
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        CaptureErrors(page, errors);
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
        await page.EvaluateAsync("()=>{try{localStorage.removeItem('saz-viewer-theme')}catch{}}");
        await page.ReloadAsync();

        var mainTheme = page.Locator(".controls .theme-toggle");
        var inspectorTheme = page.Locator(".inspector-header .theme-toggle");
        Assert.Equal("system", await page.Locator("html").GetAttributeAsync("data-theme"));
        await Assertions.Expect(mainTheme).ToHaveAttributeAsync("aria-label", "Switch to light theme");
        await Assertions.Expect(inspectorTheme).ToHaveAttributeAsync("aria-label", "Switch to light theme");
        await Assertions.Expect(mainTheme).ToHaveAttributeAsync("aria-pressed", "true");
        var buttonBox = await mainTheme.BoundingBoxAsync();
        Assert.NotNull(buttonBox);
        Assert.InRange(buttonBox.Width, 35, 40);
        Assert.InRange(buttonBox.Height, 35, 40);
        Assert.Equal(
            "rgb(13, 17, 23)",
            await page.Locator("body").EvaluateAsync<string>("element=>getComputedStyle(element).backgroundColor"));

        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await Assertions.Expect(mainTheme).ToHaveAttributeAsync("aria-label", "Switch to dark theme");
        Assert.Equal("system", await page.Locator("html").GetAttributeAsync("data-theme"));
        Assert.Equal(
            "rgb(255, 255, 255)",
            await page.Locator("body").EvaluateAsync<string>("element=>getComputedStyle(element).backgroundColor"));

        await mainTheme.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        Assert.Equal("dark", await page.Locator("html").GetAttributeAsync("data-theme"));
        Assert.Equal("dark", await page.EvaluateAsync<string>("()=>localStorage.getItem('saz-viewer-theme')"));
        await Assertions.Expect(mainTheme).ToBeFocusedAsync();
        await Assertions.Expect(mainTheme).ToHaveAttributeAsync("aria-label", "Switch to light theme");
        await Assertions.Expect(mainTheme).ToHaveAttributeAsync("title", "Switch to light theme");
        await Assertions.Expect(inspectorTheme).ToHaveAttributeAsync("aria-label", "Switch to light theme");
        await page.ReloadAsync();
        Assert.Equal("dark", await page.Locator("html").GetAttributeAsync("data-theme"));

        var secondPage = await context.NewPageAsync();
        var secondErrors = new List<string>();
        CaptureErrors(secondPage, secondErrors);
        await secondPage.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await secondPage.GotoAsync(new Uri(reportPath).AbsoluteUri);
        Assert.Equal("dark", await secondPage.Locator("html").GetAttributeAsync("data-theme"));

        await page.Locator("#httpTable tbody tr").First.ClickAsync();
        inspectorTheme = page.Locator(".inspector-header .theme-toggle");
        await inspectorTheme.ClickAsync();
        Assert.Equal("light", await page.Locator("html").GetAttributeAsync("data-theme"));
        await Assertions.Expect(page.Locator(".controls .theme-toggle")).ToHaveAttributeAsync("aria-label", "Switch to dark theme");
        await Assertions.Expect(secondPage.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        await Assertions.Expect(secondPage.Locator(".controls .theme-toggle")).ToHaveAttributeAsync("aria-label", "Switch to dark theme");
        await page.EvaluateAsync("()=>localStorage.clear()");
        await Assertions.Expect(secondPage.Locator("html")).ToHaveAttributeAsync("data-theme", "system");
        await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer-theme','light')");
        await Assertions.Expect(secondPage.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        await secondPage.CloseAsync();
        Assert.Equal(
            "rgb(255, 255, 255)",
            await page.Locator("body").EvaluateAsync<string>("element=>getComputedStyle(element).backgroundColor"));
        await page.ReloadAsync();
        Assert.Equal("light", await page.Locator("html").GetAttributeAsync("data-theme"));
        await page.Locator("#httpTable tbody tr").First.ClickAsync();
        var popupTask = page.WaitForPopupAsync();
        await page.Locator("#inspectorOpenTab").ClickAsync();
        var popup = await popupTask;
        await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        Assert.Equal("light", await popup.Locator("html").GetAttributeAsync("data-theme"));
        await Assertions.Expect(popup.Locator(".inspector-header .theme-toggle"))
            .ToHaveAttributeAsync("aria-label", "Switch to dark theme");
        await popup.CloseAsync();

        await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer-theme','system')");
        await page.ReloadAsync();
        Assert.Equal("system", await page.Locator("html").GetAttributeAsync("data-theme"));
        await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer-theme','invalid')");
        await page.ReloadAsync();
        Assert.Equal("system", await page.Locator("html").GetAttributeAsync("data-theme"));
        await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer-theme','dark')");
        await page.ReloadAsync();
        Assert.Equal("dark", await page.Locator("html").GetAttributeAsync("data-theme"));
        await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer-theme','light')");
        await page.ReloadAsync();
        Assert.Equal("light", await page.Locator("html").GetAttributeAsync("data-theme"));
        Assert.Empty(errors);
        Assert.Empty(secondErrors);

        await using var blockedContext = await browser.NewContextAsync();
        var blockedPage = await blockedContext.NewPageAsync();
        var blockedErrors = new List<string>();
        CaptureErrors(blockedPage, blockedErrors);
        await blockedPage.AddInitScriptAsync(
            "Storage.prototype.getItem=()=>{throw new DOMException('blocked')};Storage.prototype.setItem=()=>{throw new DOMException('blocked')}");
        await blockedPage.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await blockedPage.GotoAsync(new Uri(reportPath).AbsoluteUri);
        Assert.Equal("system", await blockedPage.Locator("html").GetAttributeAsync("data-theme"));
        await blockedPage.Locator(".controls .theme-toggle").ClickAsync();
        Assert.Equal("light", await blockedPage.Locator("html").GetAttributeAsync("data-theme"));
        await blockedPage.ReloadAsync();
        Assert.Equal("system", await blockedPage.Locator("html").GetAttributeAsync("data-theme"));
        await Assertions.Expect(blockedPage.Locator(".controls .theme-toggle"))
            .ToHaveAttributeAsync("aria-label", "Switch to light theme");
        await blockedPage.Locator("#httpTable tbody tr").First.ClickAsync();
        await blockedPage.Locator(".http-layout-toggle").ClickAsync();
        Assert.False(await blockedPage.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
        await blockedPage.ReloadAsync();
        await blockedPage.Locator("#httpTable tbody tr").First.ClickAsync();
        Assert.True(await blockedPage.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
        Assert.Empty(blockedErrors);
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
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();

            var search = page.Locator(".http-view-search-input");
            var status = page.Locator(".http-view-search-status");
            Assert.Equal("Search active Request or Response view", await search.GetAttributeAsync("aria-label"));
            Assert.Equal("polite", await status.GetAttributeAsync("aria-live"));

            var jsonTree = page.Locator("#request-panel-json .json-tree");
            await jsonTree.Locator(".tree-item").First.WaitForAsync();
            Assert.True(await jsonTree.EvaluateAsync<bool>("tree=>tree.classList.contains('protocol-tree')"));
            Assert.Equal(
                [
                    "Root",
                    "payload",
                    "enabled: true",
                    "items",
                    "items[0]: 1",
                    "items[1]: 2",
                    "emptyObject",
                    "emptyArray",
                    "escapedQuote: a\"b",
                    "trailingBackslash: x\\",
                    "unicodeEscape: ☺",
                    "emoji: 😀",
                    "attack: </script><svg onload=globalThis.pwned=true>"
                ],
                await jsonTree.Locator(".tree-label").AllTextContentsAsync());
            var emptyObject = jsonTree.Locator(".tree-item[aria-label='emptyObject']");
            var emptyArray = jsonTree.Locator(".tree-item[aria-label='emptyArray']");
            Assert.Null(await emptyObject.GetAttributeAsync("aria-expanded"));
            Assert.Null(await emptyArray.GetAttributeAsync("aria-expanded"));
            await Assertions.Expect(emptyObject.Locator(":scope>.tree-row>.tree-caret")).ToHaveClassAsync(new Regex(@"\btree-caret-leaf\b"));
            await Assertions.Expect(emptyArray.Locator(":scope>.tree-row>.tree-caret")).ToHaveClassAsync(new Regex(@"\btree-caret-leaf\b"));
            var jsonCaret = jsonTree.Locator(".tree-item[aria-expanded]>.tree-row>.tree-caret").First;
            Assert.Equal("\u2212", await jsonCaret.InnerTextAsync());
            var jsonCaretStyle = await jsonCaret.EvaluateAsync<string>(
                "caret=>{const style=getComputedStyle(caret);return [style.width,style.height,style.borderTopWidth,style.borderTopStyle,style.borderRadius,style.backgroundColor,style.fontSize,style.lineHeight,style.textAlign].join('|')}");

            await page.Locator("#request-panel-json .tree-collapse-all").ClickAsync();
            var jsonRoot = page.Locator("#request-panel-json .tree-view>.tree-item[aria-expanded]").First;
            Assert.Equal("false", await jsonRoot.GetAttributeAsync("aria-expanded"));
            Assert.Equal("+", await jsonRoot.Locator(":scope>.tree-row>.tree-caret").InnerTextAsync());
            await search.FillAsync("ENABLED");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal("true", await jsonRoot.GetAttributeAsync("aria-expanded"));
            Assert.Equal("\u2212", await jsonRoot.Locator(":scope>.tree-row>.tree-caret").InnerTextAsync());
            Assert.Equal(1, await page.Locator("#request-panel-json .http-search-match").CountAsync());
            Assert.Equal(0, await page.Locator("#response-panel-xml .http-search-match").CountAsync());
            Assert.True(await search.EvaluateAsync<bool>("input=>document.activeElement===input"));

            await page.Locator("#request-panel-json [data-view=pretty]").ClickAsync();
            var formattedStrings = await page.Locator("#request-panel-json .formatted-view .syn-string").AllTextContentsAsync();
            Assert.Contains(@"""a\u0022b""", formattedStrings);
            Assert.Contains(@"""x\\""", formattedStrings);
            Assert.Contains(@"""\u263A""", formattedStrings);
            Assert.Contains(@"""\uD83D\uDE00""", formattedStrings);
            Assert.Equal("", await search.InputValueAsync());
            Assert.Equal("0 matches", await status.InnerTextAsync());
            Assert.Equal("false", await jsonRoot.GetAttributeAsync("aria-expanded"));
            Assert.Equal("+", await jsonRoot.Locator(":scope>.tree-row>.tree-caret").InnerTextAsync());

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
            await AssertTreeActionToolbarAsync(page, "#response-panel-xml");
            await page.Locator("#response-panel-xml .tree-collapse-all").ClickAsync();
            var xmlRoot = page.Locator("#response-panel-xml .tree-view>.tree-item[aria-expanded]").First;
            Assert.Equal("false", await xmlRoot.GetAttributeAsync("aria-expanded"));
            await search.FillAsync("SAFE");
            await Assertions.Expect(status).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal("true", await xmlRoot.GetAttributeAsync("aria-expanded"));
            await search.FillAsync("");
            Assert.Equal("false", await xmlRoot.GetAttributeAsync("aria-expanded"));

            await page.Locator("#response-panel-xml [data-view=pretty]").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-xml .tree-expand-all")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#response-panel-xml .tree-collapse-all")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#response-panel-xml .copy-button")).ToBeVisibleAsync();
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
            var mapiCaret = mapiRoot.Locator(":scope>.tree-row>.tree-caret");
            Assert.Equal("\u2212", await mapiCaret.InnerTextAsync());
            Assert.Equal(
                jsonCaretStyle,
                await mapiCaret.EvaluateAsync<string>(
                    "caret=>{const style=getComputedStyle(caret);return [style.width,style.height,style.borderTopWidth,style.borderTopStyle,style.borderRadius,style.backgroundColor,style.fontSize,style.lineHeight,style.textAlign].join('|')}"));
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-technical").First)
                .ToContainTextAsync("Operation @0 +8");
            Assert.Equal(7, await page.Locator("#request-panel-mapi .tree-item[aria-expanded=true]").CountAsync());
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-value").Filter(new() { HasText = "RopLogon = 0xFE" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#request-panel-mapi .protocol-value").Filter(new() { HasText = "Straße雪😀" }))
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

    private static async Task VerifyHttpLayoutDefaultsAndMigrationAsync(IBrowser browser, string reportPath)
    {
        const string legacyKey = "saz-viewer.http-layout.v1";
        const string wideKey = "saz-viewer.http-layout.wide.v2";
        const string narrowKey = "saz-viewer.http-layout.narrow.v2";
        var errors = new List<string>();
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 900, Height = 800 },
        });
        IPage? peer = null;
        CaptureErrors(page, errors);
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.EvaluateAsync(
                $"()=>{{localStorage.removeItem('{legacyKey}');localStorage.removeItem('{wideKey}');localStorage.removeItem('{narrowKey}')}}");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            await Assertions.Expect(page.Locator(".http-layout-toggle"))
                .ToHaveAttributeAsync("aria-pressed", "true");
            Assert.Null(await page.EvaluateAsync<string?>($"()=>localStorage.getItem('{wideKey}')"));

            await page.Locator(".http-layout-toggle").ClickAsync();
            Assert.Equal("single", await page.EvaluateAsync<string>($"()=>localStorage.getItem('{wideKey}')"));
            await page.SetViewportSizeAsync(899, 800);
            await Assertions.Expect(page.Locator(".http-layout-toggle"))
                .ToHaveAttributeAsync("aria-pressed", "false");
            Assert.Null(await page.EvaluateAsync<string?>($"()=>localStorage.getItem('{narrowKey}')"));

            await page.Locator(".http-layout-toggle").ClickAsync();
            Assert.Equal("split", await page.EvaluateAsync<string>($"()=>localStorage.getItem('{narrowKey}')"));
            await page.SetViewportSizeAsync(900, 800);
            await Assertions.Expect(page.Locator(".http-layout-toggle"))
                .ToHaveAttributeAsync("aria-pressed", "false");
            await page.SetViewportSizeAsync(899, 800);
            await Assertions.Expect(page.Locator(".http-layout-toggle"))
                .ToHaveAttributeAsync("aria-pressed", "true");

            var peerTask = page.WaitForPopupAsync();
            await page.EvaluateAsync("(url)=>window.open(url,'_blank')", new Uri(reportPath).AbsoluteUri);
            peer = await peerTask;
            CaptureErrors(peer, errors);
            await peer.SetViewportSizeAsync(899, 800);
            await peer.Locator("#httpTable").WaitForAsync();
            await peer.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.True(await peer.Locator("#inspectorBody")
                .EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            await peer.Locator(".http-layout-toggle").ClickAsync();
            await Assertions.Expect(page.Locator(".http-layout-toggle"))
                .ToHaveAttributeAsync("aria-pressed", "false");
            await peer.CloseAsync();
            peer = null;

            await page.EvaluateAsync(
                $"()=>{{localStorage.removeItem('{legacyKey}');localStorage.removeItem('{wideKey}');localStorage.removeItem('{narrowKey}')}}");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.False(await page.Locator("#inspectorBody")
                .EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));

            await page.SetViewportSizeAsync(900, 800);
            await page.EvaluateAsync(
                $"()=>{{localStorage.removeItem('{wideKey}');localStorage.removeItem('{narrowKey}');localStorage.setItem('{legacyKey}','split')}}");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.True(await page.Locator("#inspectorBody")
                .EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            Assert.Equal("split", await page.EvaluateAsync<string>($"()=>localStorage.getItem('{wideKey}')"));
            Assert.Null(await page.EvaluateAsync<string?>($"()=>localStorage.getItem('{legacyKey}')"));

            await page.EvaluateAsync(
                $"()=>{{localStorage.removeItem('{wideKey}');localStorage.setItem('{legacyKey}','single')}}");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.True(await page.Locator("#inspectorBody")
                .EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            Assert.Null(await page.EvaluateAsync<string?>($"()=>localStorage.getItem('{wideKey}')"));
            Assert.Null(await page.EvaluateAsync<string?>($"()=>localStorage.getItem('{legacyKey}')"));
            Assert.Empty(errors);
        }
        finally
        {
            if (peer is not null)
            {
                await peer.CloseAsync();
            }
            await page.CloseAsync();
        }
    }

    private static async Task VerifyStructuredTreeToolbarsAsync(IBrowser browser, string tempDirectory)
    {
        var report = CreateReport();
        report.Sessions[0].Response = Message(
            "HTTP/1.1 200 OK",
            "application/json",
            """{"result":{"ok":true},"values":[3,4]}""");
        var reportPath = Path.Combine(tempDirectory, "structured tree toolbars.html");
        await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(report));
        var errors = new List<string>();
        var popupErrors = new List<string>();
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        CaptureErrors(page, errors);
        IPage? popup = null;
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','split')");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.True(await page.Locator("#inspectorBody")
                .EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));

            await AssertTreeActionToolbarAsync(page, "#request-panel-json");
            await AssertTreeActionToolbarAsync(page, "#response-panel-json");

            var requestExpand = page.Locator("#request-panel-json .tree-expand-all");
            var requestPretty = page.Locator("#request-panel-json [data-view='pretty']");
            await requestExpand.FocusAsync();
            await page.EvaluateAsync(
                "()=>document.querySelector(\"#request-panel-json [data-view='pretty']\").click()");
            await Assertions.Expect(requestPretty).ToBeFocusedAsync();
            await Assertions.Expect(requestExpand).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#request-panel-json .tree-collapse-all")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#request-panel-json .copy-button")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#response-panel-json .tree-expand-all")).ToBeVisibleAsync();
            await page.Locator("#request-panel-json [data-view='tree']").ClickAsync();
            await Assertions.Expect(requestExpand).ToBeVisibleAsync();

            await page.Locator("#response-panel-json [data-view='pretty']").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-json .tree-expand-all")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#response-panel-json .copy-button")).ToBeVisibleAsync();
            await page.Locator("#response-panel-json [data-view='tree']").ClickAsync();

            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            popup = await popupTask;
            CaptureErrors(popup, popupErrors);
            await popup.Locator("#httpInspector[open]").WaitForAsync();
            await AssertTreeActionToolbarAsync(popup, "#request-panel-json");
            await AssertTreeActionToolbarAsync(popup, "#response-panel-json");
            await popup.Locator("#request-panel-json [data-view='pretty']").ClickAsync();
            await Assertions.Expect(popup.Locator("#request-panel-json .tree-expand-all")).ToBeHiddenAsync();
            await popup.Locator("#request-panel-json [data-view='tree']").ClickAsync();
            await popup.CloseAsync();
            popup = null;

            await page.Locator("#httpFilter").SelectOptionAsync("websocket");
            await page.Locator("#httpTable tbody tr:not(.hidden)").ClickAsync();
            await AssertTreeActionToolbarAsync(page, "#ws-json-panel-0");
            await page.Locator("#ws-json-panel-0 [data-view='pretty']").ClickAsync();
            await Assertions.Expect(page.Locator("#ws-json-panel-0 .tree-expand-all")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#ws-json-panel-0 .copy-button")).ToBeVisibleAsync();
            await page.Locator("#ws-json-panel-0 [data-view='tree']").ClickAsync();
            await Assertions.Expect(page.Locator("#ws-json-panel-0 .tree-expand-all")).ToBeVisibleAsync();

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

    private static async Task AssertTreeActionToolbarAsync(IPage page, string panelSelector)
    {
        var toolbar = page.Locator($"{panelSelector}>.copy-toolbar");
        await toolbar.WaitForAsync();
        await Assertions.Expect(page.Locator($"{panelSelector} [data-view='pretty']"))
            .ToHaveTextAsync("Formatted Text");
        Assert.Equal(
            ["Expand all", "Collapse all", "Copy"],
            await toolbar.Locator("button").AllTextContentsAsync());
        Assert.Contains(
            "formatted text",
            await toolbar.Locator(".copy-button").GetAttributeAsync("aria-label") ?? string.Empty,
            StringComparison.Ordinal);
        var treeId = await page.Locator($"{panelSelector} .tree-subview").GetAttributeAsync("id");
        Assert.False(string.IsNullOrEmpty(treeId));
        Assert.Equal(treeId, await toolbar.Locator(".tree-expand-all").GetAttributeAsync("aria-controls"));
        Assert.Equal(treeId, await toolbar.Locator(".tree-collapse-all").GetAttributeAsync("aria-controls"));
        await Assertions.Expect(toolbar.Locator(".tree-expand-all")).ToBeVisibleAsync();
        await Assertions.Expect(toolbar.Locator(".tree-collapse-all")).ToBeVisibleAsync();
        await Assertions.Expect(toolbar.Locator(".copy-button")).ToBeVisibleAsync();
    }

    private static async Task VerifyHttpSplitViewAsync(IBrowser browser, string reportPath)
    {
        var errors = new List<string>();
        var popupErrors = new List<string>();
        var lifecyclePath = Path.Combine(Path.GetDirectoryName(reportPath)!, "split lifecycle report.html");
        await File.WriteAllTextAsync(lifecyclePath, new HtmlReportGenerator().Generate(CreateSplitLifecycleReport()));
        var page = await browser.NewPageAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        });
        await InstallClipboardTestHookAsync(page);
        CaptureErrors(page, errors);
        IPage? popup = null;
        try
        {
            await page.GotoAsync(new Uri(reportPath).AbsoluteUri);
            await page.EvaluateAsync(
                "()=>{localStorage.removeItem('saz-viewer.http-layout.v1');localStorage.removeItem('saz-viewer.http-layout.narrow.v2');localStorage.setItem('saz-viewer.http-layout.wide.v2','single')}");
            await page.ReloadAsync();
            Assert.Null(await page.EvaluateAsync<string?>("()=>localStorage.getItem('saz-viewer.http-layout.v1')"));
            Assert.Equal("single", await page.EvaluateAsync<string>("()=>localStorage.getItem('saz-viewer.http-layout.wide.v2')"));
            await page.Locator("#httpTable tbody tr").First.ClickAsync();

            var toggle = page.Locator(".http-layout-toggle");
            Assert.Equal("false", await toggle.GetAttributeAsync("aria-pressed"));
            Assert.Equal("Split view", await toggle.GetAttributeAsync("aria-label"));
            Assert.Equal("Split view", await toggle.GetAttributeAsync("title"));
            Assert.True(await toggle.Locator(".layout-icon-split")
                .EvaluateAsync<bool>("icon=>getComputedStyle(icon).display!=='none'"));
            Assert.False(await toggle.Locator(".layout-icon-single")
                .EvaluateAsync<bool>("icon=>getComputedStyle(icon).display!=='none'"));
            Assert.Equal("inspector-nav", await toggle.Locator("xpath=..").GetAttributeAsync("class"));
            var toggleBox = await toggle.BoundingBoxAsync();
            Assert.NotNull(toggleBox);
            Assert.InRange(toggleBox.Width, 35, 37);
            Assert.InRange(toggleBox.Height, 35, 37);
            Assert.True(await page.Locator(".primary-view-bar").IsVisibleAsync());
            Assert.Equal(1, await page.Locator(".http-view-search-input").CountAsync());
            Assert.True(await page.Locator("#primary-panel-response").EvaluateAsync<bool>("panel=>panel.classList.contains('hidden')"));

            await toggle.ClickAsync();
            Assert.True(await page.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            Assert.Equal("true", await toggle.GetAttributeAsync("aria-pressed"));
            Assert.Equal("Single view", await toggle.GetAttributeAsync("aria-label"));
            Assert.Equal("Single view", await toggle.GetAttributeAsync("title"));
            Assert.False(await toggle.Locator(".layout-icon-split")
                .EvaluateAsync<bool>("icon=>getComputedStyle(icon).display!=='none'"));
            Assert.True(await toggle.Locator(".layout-icon-single")
                .EvaluateAsync<bool>("icon=>getComputedStyle(icon).display!=='none'"));
            Assert.False(await page.Locator(".primary-view-bar").IsVisibleAsync());
            Assert.False(await page.Locator(".primary-tab-strip").IsVisibleAsync());
            Assert.Equal("request-pane-heading", await page.Locator("#primary-panel-request").GetAttributeAsync("aria-labelledby"));
            Assert.Equal("response-pane-heading", await page.Locator("#primary-panel-response").GetAttributeAsync("aria-labelledby"));
            Assert.False(await page.Locator("#primary-panel-request").EvaluateAsync<bool>("panel=>panel.classList.contains('hidden')"));
            Assert.False(await page.Locator("#primary-panel-response").EvaluateAsync<bool>("panel=>panel.classList.contains('hidden')"));
            Assert.Equal(2, await page.Locator(".http-view-search-input").CountAsync());
            Assert.Equal("split", await page.EvaluateAsync<string>("()=>localStorage.getItem('saz-viewer.http-layout.wide.v2')"));

            var requestSearch = page.Locator("#primary-panel-request .http-view-search-input");
            var responseSearch = page.Locator("#primary-panel-response .http-view-search-input");
            var requestStatus = page.Locator("#primary-panel-request .http-view-search-status");
            var responseStatus = page.Locator("#primary-panel-response .http-view-search-status");
            Assert.Equal("Search active Request view", await requestSearch.GetAttributeAsync("aria-label"));
            Assert.Equal("Search active Response view", await responseSearch.GetAttributeAsync("aria-label"));
            await requestSearch.FillAsync("enabled");
            await responseSearch.FillAsync("safe");
            await Assertions.Expect(requestStatus).ToHaveTextAsync("1 of 1 matches");
            await Assertions.Expect(responseStatus).ToHaveTextAsync("1 of 1 matches");
            await requestSearch.PressAsync("Enter");
            await responseSearch.PressAsync("Shift+Enter");
            await page.Locator("#request-tab-headers").ClickAsync();
            Assert.Equal("", await requestSearch.InputValueAsync());
            Assert.Equal("safe", await responseSearch.InputValueAsync());
            await Assertions.Expect(responseStatus).ToHaveTextAsync("1 of 1 matches");
            Assert.Equal(ExpectedXml(), await CopyAndReadAsync(page, "response-panel-xml"));

            var splitter = page.Locator(".http-splitter");
            await Assertions.Expect(splitter).ToBeVisibleAsync();
            Assert.Equal("vertical", await splitter.GetAttributeAsync("aria-orientation"));
            var initialRatio = int.Parse((await splitter.GetAttributeAsync("aria-valuenow"))!);
            await splitter.FocusAsync();
            await page.Keyboard.PressAsync("ArrowRight");
            var keyboardRatio = int.Parse((await splitter.GetAttributeAsync("aria-valuenow"))!);
            Assert.True(keyboardRatio > initialRatio);
            await page.Keyboard.PressAsync("Home");
            Assert.Equal(await splitter.GetAttributeAsync("aria-valuemin"), await splitter.GetAttributeAsync("aria-valuenow"));
            await page.Keyboard.PressAsync("End");
            Assert.Equal(await splitter.GetAttributeAsync("aria-valuemax"), await splitter.GetAttributeAsync("aria-valuenow"));
            var maximumRatio = int.Parse((await splitter.GetAttributeAsync("aria-valuenow"))!);
            var splitterBox = await splitter.BoundingBoxAsync();
            Assert.NotNull(splitterBox);
            await page.Mouse.MoveAsync((float)(splitterBox.X + splitterBox.Width / 2), (float)(splitterBox.Y + splitterBox.Height / 2));
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync((float)(splitterBox.X - 80), (float)(splitterBox.Y + splitterBox.Height / 2));
            await page.Mouse.UpAsync();
            Assert.True(int.Parse((await splitter.GetAttributeAsync("aria-valuenow"))!) < maximumRatio);

            await page.Locator("#inspectorNext").ClickAsync();
            Assert.True(await page.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            Assert.Equal(2, await page.Locator(".http-view-search-input").CountAsync());
            await page.Locator("#inspectorPrev").ClickAsync();

            await toggle.ClickAsync();
            Assert.True(await page.Locator(".primary-view-bar").IsVisibleAsync());
            await page.Locator("#primary-tab-response").ClickAsync();
            await toggle.ClickAsync();
            Assert.False(await page.Locator(".primary-view-bar").IsVisibleAsync());
            await toggle.ClickAsync();
            Assert.Equal(1, await page.Locator(".http-view-search-input").CountAsync());
            Assert.True(await page.Locator("#primary-panel-request").EvaluateAsync<bool>("panel=>panel.classList.contains('hidden')"));
            Assert.False(await page.Locator("#primary-panel-response").EvaluateAsync<bool>("panel=>panel.classList.contains('hidden')"));
            await toggle.ClickAsync();

            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.True(await page.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            Assert.Equal(2, await page.Locator(".http-view-search-input").CountAsync());

            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            popup = await popupTask;
            CaptureErrors(popup, popupErrors);
            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await popup.Locator("#httpInspector[open]").WaitForAsync();
            Assert.True(await popup.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            Assert.Equal(2, await popup.Locator(".http-view-search-input").CountAsync());
            await popup.Locator("#primary-panel-request .http-view-search-input").FillAsync("enabled");
            await popup.Locator("#primary-panel-response .http-view-search-input").FillAsync("safe");
            await Assertions.Expect(popup.Locator("#primary-panel-request .http-view-search-status")).ToHaveTextAsync("1 of 1 matches");
            await Assertions.Expect(popup.Locator("#primary-panel-response .http-view-search-status")).ToHaveTextAsync("1 of 1 matches");

            await popup.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.narrow.v2','split')");
            await popup.SetViewportSizeAsync(600, 800);
            var narrowRequest = await popup.Locator("#primary-panel-request").BoundingBoxAsync();
            var narrowResponse = await popup.Locator("#primary-panel-response").BoundingBoxAsync();
            Assert.NotNull(narrowRequest);
            Assert.NotNull(narrowResponse);
            Assert.True(Math.Abs(narrowRequest.X - narrowResponse.X) < 2);
            Assert.True(narrowResponse.Y > narrowRequest.Y + narrowRequest.Height - 2);
            Assert.False(await popup.Locator(".http-splitter").IsVisibleAsync());
            await Assertions.Expect(popup.Locator(".http-splitter")).ToHaveAttributeAsync("aria-hidden", "true");
            Assert.True(await popup.Locator("#httpInspector").EvaluateAsync<bool>("dialog=>dialog.scrollWidth<=dialog.clientWidth"));

            await popup.Locator(".http-layout-toggle").ClickAsync();
            Assert.Equal("single", await popup.EvaluateAsync<string>("()=>localStorage.getItem('saz-viewer.http-layout.narrow.v2')"));
            Assert.Equal(1, await popup.Locator(".http-view-search-input").CountAsync());
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.True(await page.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            await page.Locator("#inspectorClose").ClickAsync();
            await popup.CloseAsync();
            popup = null;
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','invalid')");
            await page.ReloadAsync();
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            Assert.True(await page.Locator("#inspectorBody").EvaluateAsync<bool>("body=>body.classList.contains('http-split')"));
            await page.Locator("#inspectorClose").ClickAsync();

            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.GotoAsync(new Uri(lifecyclePath).AbsoluteUri);
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            await page.Locator(".http-layout-toggle").ClickAsync();
            await page.Locator("#request-tab-image").ClickAsync();
            await page.Locator("#response-tab-webview").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-image img")).ToHaveAttributeAsync("src", new Regex("^blob:"));
            await Assertions.Expect(page.Locator("#response-panel-webview iframe")).ToHaveCountAsync(1);
            Assert.Equal(1, await page.Locator("#request-panel-image img[src^='blob:']").CountAsync());
            Assert.Equal(1, await page.Locator("#response-panel-webview iframe").CountAsync());

            await page.Locator(".http-layout-toggle").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-webview iframe")).ToHaveCountAsync(0);
            Assert.Equal(1, await page.Locator("#request-panel-image img[src^='blob:']").CountAsync());
            await page.Locator("#primary-tab-response").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-webview iframe")).ToHaveCountAsync(1);
            Assert.Null(await page.Locator("#request-panel-image img").GetAttributeAsync("src"));
            await page.Locator(".http-layout-toggle").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-webview iframe")).ToHaveCountAsync(1);
            Assert.Equal(1, await page.Locator("#request-panel-image img[src^='blob:']").CountAsync());

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
                await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
                await page.ReloadAsync();
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
            await page.EvaluateAsync(
                "()=>localStorage.setItem(innerWidth>=900?'saz-viewer.http-layout.wide.v2':'saz-viewer.http-layout.narrow.v2','single')");
            await page.ReloadAsync();
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
            await page.Locator("#request-panel-json .tree-item").First.WaitForAsync();
            Assert.Contains("payload", await page.Locator("#primary-panel-request").InnerTextAsync());
            Assert.Null(await page.Locator("#request-panel-json .tree-subview")
                .GetAttributeAsync("data-compressed-payload"));
            Assert.Null(await page.Locator("#response-panel-xml .tree-subview")
                .GetAttributeAsync("data-compressed-payload"));
            Assert.Equal(0, await page.Locator("#response-panel-xml .tree-item").CountAsync());

            await page.Locator("#request-tab-json").ClickAsync();
            Assert.False(await page.Locator("#request-panel-json .tree-subview").EvaluateAsync<bool>(
                "tree => tree.classList.contains('hidden')"));
            Assert.Equal(ExpectedJson(), await CopyAndReadAsync(page, "request-panel-json"));
            var jsonCopyButton = page.Locator("#request-panel-json .copy-button");
            Assert.Equal("Copied", await jsonCopyButton.InnerTextAsync());
            Assert.Contains("Copied request JSON formatted text", await page.Locator("#request-panel-json .copy-status").InnerTextAsync());
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
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.ReloadAsync();
            await page.Locator("#httpSearch").FillAsync(InjectionText);
            await page.Locator("#httpFilter").SelectOptionAsync("2");
            await page.Locator("#hideConnect").CheckAsync();
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
            Assert.True(await page.Locator("#hideConnect").IsCheckedAsync());
            Assert.Equal(originalScrollTop, await tableScroll.EvaluateAsync<double>("element=>element.scrollTop"));
            Assert.Equal("http-detail-0", await page.EvaluateAsync<string>(
                "() => document.activeElement?.getAttribute('data-detail') || ''"));
            Assert.Equal("true", await originalRow.GetAttributeAsync("aria-selected"));

            Assert.StartsWith(reportUrl, popup.Url, StringComparison.Ordinal);
            Assert.Contains("#saz-inspector?", popup.Url, StringComparison.Ordinal);
            Assert.Contains("q=%3Cimg", popup.Url, StringComparison.Ordinal);
            Assert.Contains("hideConnect=1", popup.Url, StringComparison.Ordinal);
            Assert.True(await popup.Locator("body").EvaluateAsync<bool>(
                "body => body.classList.contains('inspector-only')"));
            Assert.True(await popup.Locator("main").EvaluateAsync<bool>(
                "main => getComputedStyle(main).display === 'none'"));
            Assert.Equal("Back to sessions", await popup.Locator("#inspectorClose").InnerTextAsync());
            Assert.Equal("1 of 2", await popup.Locator("#inspectorPosition").InnerTextAsync());
            Assert.Equal(2, await popup.Locator("#httpTable tbody tr:not(.hidden)").CountAsync());
            Assert.True(await popup.Locator("#hideConnect").IsCheckedAsync());
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
            Assert.True(await page.Locator("#hideConnect").IsCheckedAsync());

            Assert.False(await popup.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));
            Assert.False(await page.EvaluateAsync<bool>("() => Boolean(globalThis.pwned)"));
            await popup.Locator("#inspectorClose").ClickAsync();
            Assert.False(await popup.Locator("#httpInspector").EvaluateAsync<bool>("dialog => dialog.open"));
            Assert.False(await popup.Locator("body").EvaluateAsync<bool>(
                "body => body.classList.contains('inspector-only')"));
            Assert.True(await popup.Locator("main").IsVisibleAsync());
            Assert.Equal(InjectionText, await popup.Locator("#httpSearch").InputValueAsync());
            Assert.Equal("2", await popup.Locator("#httpFilter").InputValueAsync());
            Assert.True(await popup.Locator("#hideConnect").IsCheckedAsync());
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
            await page.EvaluateAsync(
                "()=>localStorage.setItem(innerWidth>=900?'saz-viewer.http-layout.wide.v2':'saz-viewer.http-layout.narrow.v2','single')");
            await page.ReloadAsync();
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
            Assert.False(await page.Locator("#inspectorLayoutToggle").IsVisibleAsync());
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
            Assert.Equal(
                await page.Locator("html").EvaluateAsync<string>(
                    "element=>{const probe=document.createElement('span');probe.style.color=getComputedStyle(element).getPropertyValue('--direction-client');document.body.append(probe);const color=getComputedStyle(probe).color;probe.remove();return color}"),
                await messages.First.Locator(".ws-arrow").EvaluateAsync<string>("element=>getComputedStyle(element).color"));
            Assert.Equal(
                await page.Locator("html").EvaluateAsync<string>(
                    "element=>{const probe=document.createElement('span');probe.style.color=getComputedStyle(element).getPropertyValue('--direction-server');document.body.append(probe);const color=getComputedStyle(probe).color;probe.remove();return color}"),
                await messages.Nth(1).Locator(".ws-arrow").EvaluateAsync<string>("element=>getComputedStyle(element).color"));
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
            Assert.Contains(
                "NeedleBeyondPreview",
                await page.Locator(".ws-detail-pane .tree-subview").TextContentAsync());
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
            await page.Locator(".primary-tab-strip").WaitForAsync();
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
            await popup.Locator(".primary-tab-strip").WaitForAsync();
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
            await page.Locator("#httpTable tbody tr:not(.hidden)").First.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
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
                .ToContainTextAsync("[Body preview truncated; the complete body is not retained in this report.]");
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
            await Assertions.Expect(unsupportedPage.Locator(".http-session-source .warning"))
                .ToContainTextAsync("Open the report in a current Microsoft Edge or Google Chrome release.");
            Assert.Equal(0, await unsupportedPage.Locator(".message-panel").CountAsync());
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
        const string invalidSessionSchema = """{"schema":2}""";
        var invalidSessionPayload = GzipBase64(invalidSessionSchema);
        var expansionPayload = GzipBase64(new string('x', 1024));
        var truncatedPayload = expansionPayload[..^4];
        var cases = new[]
        {
            new PayloadMutation("invalid base64", "base64", null, null),
            new PayloadMutation("corrupt gzip", "gzip", null, null),
            new PayloadMutation("truncated gzip", "truncated", truncatedPayload, 1024),
            new PayloadMutation("invalid JSON", "json", invalidJsonPayload, 8),
            new PayloadMutation("invalid session schema", "schema", invalidSessionPayload, Encoding.UTF8.GetByteCount(invalidSessionSchema)),
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
                      const host=document.getElementById('http-detail-0').content.querySelector('[data-payload-type="http-session"]');
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
                      else if(args.kind==='encoded-limit')host.dataset.compressedPayload='A'.repeat((48*1024*1024)+4);
                      else if(args.kind==='decoded-limit')host.dataset.payloadDecodedBytes=String((32*1024*1024)+1);
                    }
                    """,
                    new { kind = testCase.Kind, payload = testCase.Payload, decodedBytes = testCase.DecodedBytes });
                await page.Locator("#httpTable tbody tr").First.ClickAsync();
                var warning = page.Locator(".http-session-source .warning");
                await Assertions.Expect(warning).ToContainTextAsync(
                    "HTTP session could not be loaded because its compressed report payload is corrupt, unsupported, or exceeds safety limits.");
                Assert.True(
                    await warning.EvaluateAsync<bool>("element => element.classList.contains('warning')"),
                    testCase.Name);
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
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.ReloadAsync();
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
            Assert.Equal("Root", await page.Locator("#request-panel-json .tree-label").First.InnerTextAsync());
            Assert.Equal("[0]: 0", await page.Locator("#request-panel-json .tree-label").Nth(1).InnerTextAsync());

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
                Length = Encoding.UTF8.GetByteCount(RequestBody),
                CapturedLength = Encoding.UTF8.GetByteCount(RequestBody),
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
        "              UnicodeText [Field] @5 +2 = Straße雪😀\n" +
        "              ControlledText [Field] @6 +2 = line\0\u001B\r\n\u061C\u200E\u200F\u2028\u2029\u202A\u2066\n" +
        "              RawBytes [Raw] @8 +6 = 00FF1B7F ... [2 more bytes]";

    private static string ExpectedRequestRaw() =>
        "Original headers\n" +
        "POST /formatted HTTP/1.1\n" +
        "Content-Type: application/json\n\n" +
        $"Body ({Encoding.UTF8.GetByteCount(RequestBody)} B)\n" +
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

    private static string WebViewSource() =>
        """
        <!doctype html><html><head>
        <base href="https://base.invalid/">
        <meta http-equiv="refresh" content="0;url=https://refresh.invalid/">
        <link rel="stylesheet" href="https://css.invalid/site.css">
        <style>@import "https://import.invalid/site.css";body{background:url(https://style.invalid/bg.png)}</style>
        <script>globalThis.__webviewPwned=true;parent.__webviewPwned=true;fetch("https://script.invalid/");localStorage.setItem("pwned","1")</script>
        </head><body onload="globalThis.__webviewPwned=true">
        <h1>Safe captured layout</h1>
        <a href="https://link.invalid/" ping="https://ping.invalid/" target="_top">Inert captured link</a>
        <img src="https://image.invalid/x.png" srcset="https://image2.invalid/x.png 2x" onerror="parent.__webviewPwned=true">
        <form action="https://form.invalid/" target="_top"><button formaction="https://button.invalid/">Submit</button></form>
        <iframe src="https://frame.invalid/" srcdoc="<script>parent.__webviewPwned=true</script>"></iframe>
        <object data="https://object.invalid/"></object><embed src="https://embed.invalid/">
        <video poster="https://poster.invalid/"><source src="https://media.invalid/"></video>
        <svg><script>parent.__webviewPwned=true</script><a href="https://svg.invalid/">SVG</a></svg>
        <math><a href="https://math.invalid/">Math</a></math>
        <p>Searchable safe text</p>
        </body></html>
        """;

    private static SazReport CreateWebViewReport()
    {
        var source = WebViewSource();
        var bytes = Encoding.UTF8.GetBytes(source);
        var report = new SazReport { SourceName = "webview.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/webview",
            StatusCode = 200,
            Request = ByteMessage("POST /webview HTTP/1.1", "text/html; charset=utf-8", bytes, bytes),
            Response = ByteMessage("HTTP/1.1 200 OK", "text/html; charset=utf-8", bytes, bytes)
        });
        return report;
    }

    private static SazReport CreateSplitLifecycleReport()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var html = Encoding.UTF8.GetBytes(WebViewSource());
        var report = new SazReport { SourceName = "split-lifecycle.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/split-lifecycle",
            StatusCode = 200,
            Request = ByteMessage("POST /split-lifecycle HTTP/1.1", "image/png", png, png),
            Response = ByteMessage("HTTP/1.1 200 OK", "text/html; charset=utf-8", html, html)
        });
        return report;
    }

    private static SazReport CreateContentTabReport()
    {
        var report = new SazReport { SourceName = "content-tabs.saz" };
        var captured = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xAA, 0xBB, 0xCC, 0xDD };
        var decoded = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var request = ByteMessage(
            "POST /auth HTTP/1.1",
            "application/octet-stream",
            captured,
            decoded,
            ["content: gzip"]);
        request.Headers.Add(new HttpHeader("Authorization", "Bearer request-secret-token"));
        request.Headers.Add(new HttpHeader("Proxy-Authorization", "opaqueSingleToken"));
        var responseBytes = Encoding.UTF8.GetBytes("response body");
        var response = ByteMessage(
            "HTTP/1.1 401 Unauthorized",
            "text/plain; charset=utf-8",
            responseBytes,
            responseBytes);
        response.Headers.Add(new HttpHeader("WWW-Authenticate", "Digest realm=\"private\", nonce=\"response-secret\""));
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/auth",
            StatusCode = 401,
            Request = request,
            Response = response
        });

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        report.Sessions.Add(new HttpSession
        {
            Id = "2",
            ArchiveOrder = 1,
            Method = "POST",
            Url = "https://example.test/image",
            StatusCode = 200,
            Request = ByteMessage("POST /image HTTP/1.1", "image/png", png, png),
            Response = ByteMessage("HTTP/1.1 200 OK", "text/plain", png, png)
        });
        return report;
    }

    private static SazReport CreateHttpSessionTableReport()
    {
        var report = new SazReport { SourceName = "http-table-layout.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Timestamp = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
            Method = "GET",
            Url = $"https://example.test/{new string('x', 900)}",
            StatusCode = 200,
            StatusText = "OK",
            ElapsedMilliseconds = 1_234,
            RequestBytes = 1_024,
            ResponseBytes = 2_048,
            Response = Message("HTTP/1.1 200 OK", "text/plain", "ok")
        });
        report.Sessions.Add(new HttpSession
        {
            Id = "connect",
            ArchiveOrder = 1,
            Timestamp = new DateTimeOffset(2026, 9, 30, 12, 0, 1, TimeSpan.Zero),
            Method = "cOnNeCt",
            Url = "https://connect-only.example.test:443",
            StatusCode = 200,
            StatusText = "Connection Established",
            ElapsedMilliseconds = 11,
            RequestBytes = 100,
            ResponseBytes = 200,
            Response = Message("HTTP/1.1 200 Connection Established", "text/plain", "")
        });
        report.Sessions.Add(new HttpSession
        {
            Id = "long-session-id",
            ArchiveOrder = 2,
            Timestamp = new DateTimeOffset(2026, 9, 30, 12, 0, 2, TimeSpan.Zero),
            Method = "OPTIONS",
            Url = "https://example.test/short",
            StatusCode = 418,
            StatusText = "I'm a teapot",
            ElapsedMilliseconds = 7,
            RequestBytes = 9_876_543,
            ResponseBytes = 42,
            Response = Message("HTTP/1.1 418 I'm a teapot", "text/plain", "short")
        });
        report.Sessions.Add(new HttpSession
        {
            Id = "3",
            ArchiveOrder = 3,
            Timestamp = new DateTimeOffset(2026, 9, 30, 12, 0, 3, TimeSpan.Zero),
            Method = "PATCH",
            Url = "https://example.test/aborted",
            StatusText = "Aborted by client",
            RequestBytes = 5
        });
        return report;
    }

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
                                                                MapiNode.Leaf("UnicodeText", MapiNodeKind.Field, 5, 2, "Straße雪😀"),
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
            Response = Message("HTTP/1.1 200 OK", "application/mapi-http", "binary"),
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
        var byteLength = Encoding.UTF8.GetByteCount(body);
        var message = new HttpMessage
        {
            StartLine = startLine,
            Body = new BodyPreview
            {
                Length = byteLength,
                CapturedLength = byteLength,
                Preview = body,
            },
        };
        message.Headers.Add(new HttpHeader("Content-Type", contentType));
        return message;
    }

    private static HttpMessage ByteMessage(
        string startLine,
        string contentType,
        byte[] captured,
        byte[] decoded,
        IReadOnlyList<string>? removedEncodings = null)
    {
        var isText = contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
        var message = new HttpMessage
        {
            StartLine = startLine,
            Body = new BodyPreview
            {
                Length = decoded.Length,
                CapturedLength = captured.Length,
                IsBinary = !isText,
                Charset = isText ? "utf-8" : null,
                Preview = isText ? Encoding.UTF8.GetString(decoded) : HttpMessageParser.HexPreview(decoded),
                CapturedBytes = captured,
                DecodedBytes = decoded,
                NormalizedBytes = decoded,
                RemovedEncodings = removedEncodings ?? []
            }
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
