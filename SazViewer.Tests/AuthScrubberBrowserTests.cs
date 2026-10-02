using System.Text;
using Microsoft.Playwright;
using SazViewer.Core;

namespace SazViewer.Tests;

[Collection(EdgeBrowserCollection.Name)]
public sealed class AuthScrubberBrowserTests
{
    [WindowsEdgeFact]
    public async Task ScrubbedReportRendersBannerMarkersAndSafeInspectorViews()
    {
        const string canary = "edge-scrub-canary-4f155d7d";
        var report = new SazReport { SourceName = "scrubbed.saz" };
        var request = Message(
            "POST /login?access_token=" + canary + " HTTP/1.1",
            "application/json",
            $$"""{"client_secret":"{{canary}}","safe":true}""");
        request.Headers.Add(new HttpHeader("Authorization", "Bearer " + canary));
        request.Headers.Add(new HttpHeader("Cookie", "auth=" + canary));
        var response = Message(
            "HTTP/1.1 200 OK",
            "text/html",
            "<!doctype html><html><body><p>pass" + "word=" + canary + "</p></body></html>");
        response.Headers.Add(new HttpHeader("Set-Cookie", "session=" + canary + "; Path=/"));
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/login?access_token=" + canary,
            StatusCode = 200,
            Request = request,
            Response = response
        });
        var summary = AuthScrubber.Scrub(report);
        Assert.Contains("[REDACTED:", report.Sessions[0].Response!.Body.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, report.Sessions[0].Response!.Body.Preview, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), $"saz-scrub-edge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.html");
        await File.WriteAllTextAsync(path, new HtmlReportGenerator().Generate(report));
        var errors = new List<string>();
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = "msedge",
                Headless = true
            });
            var page = await browser.NewPageAsync();
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    errors.Add(message.Text);
                }
            };
            page.PageError += (_, exception) => errors.Add(exception);

            await page.GotoAsync(new Uri(path).AbsoluteUri);
            await page.EvaluateAsync("()=>localStorage.setItem('saz-viewer.http-layout.wide.v2','single')");
            await page.ReloadAsync();
            var banner = page.Locator(".auth-scrub-banner");
            var bannerToggle = page.Locator("#authScrubToggle");
            var bannerDetails = page.Locator("#authScrubDetails");
            await Assertions.Expect(banner).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#authScrubSummary"))
                .ToHaveTextAsync(
                    $"Credentials scrubbed: {summary.Total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} replacements");
            await Assertions.Expect(bannerToggle).ToHaveAttributeAsync("aria-expanded", "false");
            await Assertions.Expect(bannerToggle).ToHaveAttributeAsync("aria-controls", "authScrubDetails");
            await Assertions.Expect(bannerToggle).ToHaveAttributeAsync("aria-label", "Show credential scrub details");
            await Assertions.Expect(bannerDetails).ToBeHiddenAsync();
            await bannerToggle.FocusAsync();
            await bannerToggle.PressAsync("Enter");
            await Assertions.Expect(bannerToggle).ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(bannerToggle).ToHaveAttributeAsync("aria-label", "Hide credential scrub details");
            await Assertions.Expect(bannerDetails).ToBeVisibleAsync();
            await Assertions.Expect(bannerDetails).ToContainTextAsync("Bearer:");
            Assert.Equal(
                "expanded",
                await page.EvaluateAsync<string>("()=>localStorage.getItem('saz-viewer.auth-scrub-banner.v1')"));
            await page.ReloadAsync();
            await Assertions.Expect(bannerToggle).ToHaveAttributeAsync("aria-expanded", "true");
            await Assertions.Expect(bannerDetails).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".http-url-value")).ToContainTextAsync("[REDACTED:Token]");
            Assert.DoesNotContain(canary, await page.ContentAsync(), StringComparison.Ordinal);

            await page.Locator("#httpTable tbody tr").ClickAsync();
            var popupTask = page.WaitForPopupAsync();
            await page.Locator("#inspectorOpenTab").ClickAsync();
            var popup = await popupTask;
            popup.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    errors.Add(message.Text);
                }
            };
            popup.PageError += (_, exception) => errors.Add(exception);
            await popup.Locator("#inspectorClose").ClickAsync();
            await Assertions.Expect(popup.Locator("#authScrubToggle")).ToHaveAttributeAsync("aria-expanded", "true");
            await popup.Locator("#authScrubToggle").FocusAsync();
            await popup.Locator("#authScrubToggle").PressAsync("Space");
            await Assertions.Expect(bannerToggle).ToHaveAttributeAsync("aria-expanded", "false");
            await popup.CloseAsync();

            await page.Locator("#httpTable tbody tr").ClickAsync();
            await page.Locator("#request-tab-json").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-json .formatted-view"))
                .ToContainTextAsync("[REDACTED:Secret]");

            await page.Locator("#request-tab-auth").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-auth .auth-headers"))
                .ToContainTextAsync("Bearer [redacted]");
            await page.Locator("#request-panel-auth .auth-reveal").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-auth .auth-headers"))
                .ToContainTextAsync("Bearer [REDACTED:Bearer]");

            await page.Locator("#request-tab-raw").ClickAsync();
            await Assertions.Expect(page.Locator("#request-panel-raw"))
                .ToContainTextAsync("[REDACTED:Cookie]");

            await page.Locator("#primary-tab-response").ClickAsync();
            await page.Locator("#response-tab-webview").ClickAsync();
            await Assertions.Expect(page.Locator("#response-panel-webview iframe")).ToHaveCountAsync(1);
            await page.Locator("#response-panel-webview [data-webview-mode='source']").ClickAsync();
            Assert.Contains(
                "[REDACTED:",
                await page.Locator("#response-panel-webview .webview-source").InnerTextAsync(),
                StringComparison.Ordinal);

            Assert.DoesNotContain(canary, await page.Locator("#httpInspector").InnerTextAsync(), StringComparison.Ordinal);
            Assert.Empty(errors);

            await using var blockedContext = await browser.NewContextAsync();
            var blockedPage = await blockedContext.NewPageAsync();
            var blockedErrors = new List<string>();
            blockedPage.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    blockedErrors.Add(message.Text);
                }
            };
            blockedPage.PageError += (_, exception) => blockedErrors.Add(exception);
            await blockedPage.AddInitScriptAsync(
                "Storage.prototype.getItem=()=>{throw new DOMException('blocked')};Storage.prototype.setItem=()=>{throw new DOMException('blocked')}");
            await blockedPage.GotoAsync(new Uri(path).AbsoluteUri);
            await Assertions.Expect(blockedPage.Locator("#authScrubToggle"))
                .ToHaveAttributeAsync("aria-expanded", "false");
            await blockedPage.Locator("#authScrubToggle").ClickAsync();
            await Assertions.Expect(blockedPage.Locator("#authScrubToggle"))
                .ToHaveAttributeAsync("aria-expanded", "true");
            await blockedPage.ReloadAsync();
            await Assertions.Expect(blockedPage.Locator("#authScrubToggle"))
                .ToHaveAttributeAsync("aria-expanded", "false");
            Assert.Empty(blockedErrors);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpMessage Message(string startLine, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var message = new HttpMessage
        {
            StartLine = startLine,
            Body = new BodyPreview
            {
                Length = bytes.Length,
                CapturedLength = bytes.Length,
                Preview = body,
                CapturedBytes = bytes,
                DecodedBytes = bytes
            }
        };
        message.Headers.Add(new HttpHeader("Content-Type", contentType));
        return message;
    }

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
