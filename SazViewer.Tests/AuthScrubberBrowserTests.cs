using System.Text;
using Microsoft.Playwright;
using SazViewer.Core;

namespace SazViewer.Tests;

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
        AuthScrubber.Scrub(report);
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
            var banner = page.Locator(".auth-scrub-banner");
            await Assertions.Expect(banner).ToBeVisibleAsync();
            await Assertions.Expect(banner).ToContainTextAsync("generated with --scrub-auth");
            await Assertions.Expect(banner).ToContainTextAsync("Bearer:");
            await Assertions.Expect(page.Locator(".http-url-value")).ToContainTextAsync("[REDACTED:Token]");
            Assert.DoesNotContain(canary, await page.ContentAsync(), StringComparison.Ordinal);

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
