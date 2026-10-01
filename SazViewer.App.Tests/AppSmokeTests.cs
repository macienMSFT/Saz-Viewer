using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace SazViewer.App.Tests;

/// <summary>
/// Launches the real SazViewer.App.exe with a synthetic capture and drives the hosted
/// WebView2 over the Chrome DevTools Protocol. The debugging port is enabled only for
/// this child process through the standard WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
/// variable, and the app data root is redirected to a private temp directory.
/// </summary>
public sealed class AppSmokeTests
{
    private const string ReportUrl = "https://saz-viewer.invalid/report.html";

    [Fact]
    public async Task App_LoadsReport_BlocksNetworkAndNavigation_AndHostsPopup()
    {
        using var temp = new TempDirectory();
        var capturePath = Path.Combine(temp.Path, "smoke.saz");
        TestCaptures.WritePlain(capturePath);
        var port = GetFreePort();

        var appPath = Path.Combine(AppContext.BaseDirectory, "SazViewer.App.exe");
        var startInfo = new ProcessStartInfo(appPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add(capturePath);
        startInfo.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-port={port}";
        startInfo.Environment[AppPaths.DataDirectoryOverrideVariable] = Path.Combine(temp.Path, "data");
        // These smoke tests drive the report through CDP, so they use the opt-in WebView2 report view.
        startInfo.Environment["SAZVIEWER_LEGACY_HTML_REPORT"] = "1";

        using var process = Process.Start(startInfo)!;
        try
        {
            await WaitForDebuggerAsync(port, process);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
            var context = browser.Contexts.Single();
            var page = await WaitForPageAsync(context, ReportUrl);

            await page.Locator("#httpTable tbody tr").First.WaitForAsync();
            Assert.Equal(2, await page.Locator("#httpTable tbody tr").CountAsync());

            // Every request outside the in-memory report is refused, including same-origin paths.
            var fetchResults = await page.EvaluateAsync<string[]>(
                """
                async () => {
                  const probe = async url => {
                    try { const r = await fetch(url, {cache: 'no-store'}); return url + ':' + r.status; }
                    catch { return url + ':blocked'; }
                  };
                  return [
                    await probe('https://example.com/'),
                    await probe('http://127.0.0.1:1/'),
                    await probe('https://saz-viewer.invalid/other')
                  ];
                }
                """);
            Assert.All(fetchResults, result => Assert.True(
                result.EndsWith(":blocked", StringComparison.Ordinal) || result.EndsWith(":403", StringComparison.Ordinal),
                result));

            // Top-level navigation away from the report is cancelled.
            await page.EvaluateAsync("() => { window.location.href = 'https://example.com/'; }");
            await Task.Delay(1000);
            Assert.StartsWith(ReportUrl, page.Url, StringComparison.Ordinal);
            Assert.Equal(2, await page.Locator("#httpTable tbody tr").CountAsync());

            // localStorage persists on the stable synthetic origin.
            await page.EvaluateAsync("() => localStorage.setItem('saz-smoke', 'ok')");
            Assert.Equal("ok", await page.EvaluateAsync<string>("() => localStorage.getItem('saz-smoke')"));

            // Arbitrary pop-ups are refused.
            Assert.True(await page.EvaluateAsync<bool>("() => window.open('https://example.com/', '_blank') === null"));

            // "Open in new tab" lands in an app-controlled window that shows the same report.
            await page.Locator("#httpTable tbody tr").First.ClickAsync();
            await page.Locator("#inspectorOpenTab").WaitForAsync(new() { State = WaitForSelectorState.Visible });
            await page.Locator("#inspectorOpenTab").ClickAsync();
            Assert.Equal(string.Empty, await page.EvaluateAsync<string>("() => inspectorOpenStatus.textContent"));

            // The app hands WebView2 a new controller for the popup, which Playwright does not attach to
            // an existing CDP session; a fresh connection enumerates it.
            var popupBrowser = await ConnectWhenPageExistsAsync(playwright, port, ReportUrl + "#saz-inspector?");
            await using (popupBrowser)
            {
                var popup = popupBrowser.Contexts.SelectMany(c => c.Pages)
                    .Single(p => p.Url.StartsWith(ReportUrl + "#saz-inspector?", StringComparison.Ordinal));
                await popup.WaitForFunctionAsync("() => document.body.classList.contains('inspector-only')");
                Assert.Equal("ok", await popup.EvaluateAsync<string>("() => localStorage.getItem('saz-smoke')"));
                await popup.EvaluateAsync("() => { window.location.href = 'https://example.com/'; }");
                await Task.Delay(1000);
                Assert.StartsWith(ReportUrl + "#", popup.Url, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task SecondLaunch_ForwardsPathToRunningInstance_WhichOpensSecondTab()
    {
        using var temp = new TempDirectory();
        var first = TestCaptures.WritePlain(temp.File("first.saz"));
        var second = TestCaptures.WriteSimple(temp.File("second.saz"), 3);
        var port = GetFreePort();
        var dataDirectory = Path.Combine(temp.Path, "data");

        using var process = Process.Start(CreateStartInfo(first, dataDirectory, port))!;
        try
        {
            await WaitForDebuggerAsync(port, process);
            using var playwright = await Playwright.CreateAsync();
            await using (var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}"))
            {
                var page = await WaitForPageAsync(browser.Contexts.Single(), ReportUrl);
                await page.Locator("#httpTable tbody tr").First.WaitForAsync();
                Assert.Equal(2, await page.Locator("#httpTable tbody tr").CountAsync());
            }

            // The second launch shares the data root (and thus the instance name) and must hand off, not start a UI.
            using (var forwarder = Process.Start(CreateStartInfo(second, dataDirectory, port: null))!)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await forwarder.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, forwarder.ExitCode);
            }
            Assert.False(process.HasExited);

            // The forwarded tab becomes active, so its lazily created WebView2 loads its own report.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var counts = new List<int>();
            while (DateTime.UtcNow < deadline)
            {
                await using var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
                var reports = browser.Contexts.SelectMany(c => c.Pages)
                    .Where(p => p.Url.StartsWith(ReportUrl, StringComparison.Ordinal) && !p.Url.Contains('#', StringComparison.Ordinal))
                    .ToList();
                if (reports.Count == 2)
                {
                    counts.Clear();
                    foreach (var report in reports)
                    {
                        await report.Locator("#httpTable tbody tr").First.WaitForAsync();
                        counts.Add(await report.Locator("#httpTable tbody tr").CountAsync());
                    }
                    break;
                }
                await Task.Delay(500);
            }
            counts.Sort();
            Assert.Equal([2, 3], counts);

            // Forwarding the same file again focuses the existing tab instead of opening a third one.
            using (var again = Process.Start(CreateStartInfo(second, dataDirectory, port: null))!)
            {
                await again.WaitForExitAsync();
                Assert.Equal(0, again.ExitCode);
            }
            await Task.Delay(1500);
            await using (var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}"))
            {
                Assert.Equal(2, browser.Contexts.SelectMany(c => c.Pages).Count(p => p.Url == ReportUrl));
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string capturePath, string dataDirectory, int? port)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SazViewer.App.exe")) { UseShellExecute = false };
        startInfo.ArgumentList.Add(capturePath);
        if (port is not null)
        {
            startInfo.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-port={port}";
        }
        startInfo.Environment[AppPaths.DataDirectoryOverrideVariable] = dataDirectory;
        startInfo.Environment["SAZVIEWER_LEGACY_HTML_REPORT"] = "1";
        return startInfo;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForDebuggerAsync(int port, Process process)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(process.HasExited, "SazViewer.App exited before WebView2 started.");
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/version");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("WebView2 remote debugging endpoint did not start.");
    }

    private static async Task<IBrowser> ConnectWhenPageExistsAsync(IPlaywright playwright, int port, string urlPrefix)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}");
            if (browser.Contexts.SelectMany(c => c.Pages).Any(p => p.Url.StartsWith(urlPrefix, StringComparison.Ordinal)))
            {
                return browser;
            }

            await browser.DisposeAsync();
            await Task.Delay(500);
        }

        throw new TimeoutException("The popup report window did not open.");
    }

    private static async Task<IPage> WaitForPageAsync(IBrowserContext context, string urlPrefix)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var page = context.Pages.FirstOrDefault(p => !p.IsClosed && p.Url.StartsWith(urlPrefix, StringComparison.Ordinal));
            if (page is not null)
            {
                return page;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("The report page did not load. Pages: "
            + string.Join(", ", context.Pages.Select(p => (p.IsClosed ? "closed:" : "") + p.Url)));
    }
}
