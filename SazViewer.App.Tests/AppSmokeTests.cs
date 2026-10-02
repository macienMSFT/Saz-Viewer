using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Windows.Automation;
using Microsoft.Playwright;

namespace SazViewer.App.Tests;

/// <summary>
/// Launches the real SazViewer.App.exe with synthetic captures and drives the native UI through UI Automation.
/// The sandboxed WebView tab is inspected over the Chrome DevTools Protocol: the debugging port is enabled only
/// for this child process through the standard WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS variable, and the app data
/// root is redirected to a private temp directory.
/// </summary>
public sealed class AppSmokeTests
{
    private const string PreviewUrl = "https://webview-preview.sazviewer.invalid/document.html";

    [Fact]
    public async Task App_ShowsNativeGrid_AndSandboxedWebViewTab_IsLazyBlockedAndDisposed()
    {
        using var temp = new TempDirectory();
        var capturePath = TestCaptures.WritePlain(temp.File("smoke.saz"));
        var port = GetFreePort();

        using var process = Process.Start(CreateStartInfo(capturePath, Path.Combine(temp.Path, "data"), port))!;
        try
        {
            var window = await WaitForAsync(() => MainWindow(process), "main window");
            var grid = await WaitForAsync(() => Find(window, "HTTP sessions", ControlType.DataGrid), "session grid");
            var rows = await WaitForAsync(() => DataRows(grid) is { Count: 2 } found ? found : null, "two session rows");

            // The WebView tab's WebView2 is created only when the tab is shown.
            Assert.False(await DebuggerRespondsAsync(port));

            ((SelectionItemPattern)rows[1].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var responseViews = await WaitForAsync(() => Find(window, "Response detail views", ControlType.Tab), "Response views");
            var webViewTab = await WaitForAsync(() => Find(responseViews, "WebView", ControlType.TabItem), "WebView tab");
            ((SelectionItemPattern)webViewTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

            await WaitForDebuggerAsync(port, process);
            using var playwright = await Playwright.CreateAsync();
            await using (var browser = await playwright.Chromium.ConnectOverCDPAsync($"http://127.0.0.1:{port}"))
            {
                var page = await WaitForPageAsync(browser.Contexts.Single(), PreviewUrl);
                Assert.Equal(PreviewUrl, page.Url);
                // DevTools evaluation still works with page scripts disabled; requests and navigation stay blocked.
                var fetchResults = await page.EvaluateAsync<string[]>(
                    """
                    async () => {
                      const probe = async url => {
                        try { const r = await fetch(url, {cache: 'no-store'}); return url + ':' + r.status; }
                        catch { return url + ':blocked'; }
                      };
                      return [await probe('https://example.com/'), await probe('https://webview-preview.sazviewer.invalid/other')];
                    }
                    """);
                Assert.All(fetchResults, result => Assert.True(
                    result.EndsWith(":blocked", StringComparison.Ordinal) || result.EndsWith(":403", StringComparison.Ordinal), result));
                Assert.True(await page.EvaluateAsync<bool>("() => window.open('https://example.com/', '_blank') === null"));
            }

            // Leaving the tab disposes the WebView2 (and with it the browser process and its debugging endpoint).
            var headers = await WaitForAsync(() => Find(responseViews, "Headers", ControlType.TabItem), "Headers tab");
            ((SelectionItemPattern)headers.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await PreviewPageExistsAsync(port) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
            Assert.False(await PreviewPageExistsAsync(port));
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task SecondLaunch_ForwardsPathToRunningInstance_WhichOpensSecondTab()
    {
        using var temp = new TempDirectory();
        var first = TestCaptures.WritePlain(temp.File("first.saz"));
        var second = TestCaptures.WriteSimple(temp.File("second.saz"), 3);
        var dataDirectory = Path.Combine(temp.Path, "data");

        using var process = Process.Start(CreateStartInfo(first, dataDirectory, port: null))!;
        try
        {
            var window = await WaitForAsync(() => MainWindow(process), "main window");
            var strip = await WaitForAsync(() => Find(window, "Open captures", ControlType.Tab), "capture tabs");
            await WaitForAsync(() => TabItems(strip) == 1 ? strip : null, "first tab");
            var grid = await WaitForAsync(() => Find(window, "HTTP sessions", ControlType.DataGrid), "session grid");
            await WaitForAsync(() => DataRows(grid) is { Count: 2 } found ? found : null, "first capture rows");

            // The second launch shares the data root (and thus the instance name) and must hand off, not start a UI.
            using (var forwarder = Process.Start(CreateStartInfo(second, dataDirectory, port: null))!)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await forwarder.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, forwarder.ExitCode);
            }
            Assert.False(process.HasExited);
            await WaitForAsync(() => TabItems(strip) == 2 ? strip : null, "forwarded tab");
            // The forwarded tab becomes active and shows its own sessions.
            await WaitForAsync(
                () => window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "HTTP sessions"))
                    .Cast<AutomationElement>().Any(candidate => !candidate.Current.IsOffscreen && DataRows(candidate).Count == 3) ? strip : null,
                "second capture rows");

            // Forwarding the same file again focuses the existing tab instead of opening a third one.
            using (var again = Process.Start(CreateStartInfo(second, dataDirectory, port: null))!)
            {
                await again.WaitForExitAsync();
                Assert.Equal(0, again.ExitCode);
            }
            await Task.Delay(1500);
            Assert.Equal(2, TabItems(strip));
        }
        finally
        {
            await StopAsync(process);
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
        return startInfo;
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static AutomationElement? MainWindow(Process process) =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)));

    private static AutomationElement? Find(AutomationElement scope, string name, ControlType type) =>
        scope.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)));

    private static List<AutomationElement> DataRows(AutomationElement grid) =>
        grid.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem))
            .Cast<AutomationElement>().ToList();

    private static int TabItems(AutomationElement strip) =>
        strip.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Count;

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (probe() is { } found)
                {
                    return found;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static async Task<bool> DebuggerRespondsAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/version");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> PreviewPageExistsAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            return (await client.GetStringAsync($"http://127.0.0.1:{port}/json/list")).Contains(PreviewUrl, StringComparison.Ordinal);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
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
