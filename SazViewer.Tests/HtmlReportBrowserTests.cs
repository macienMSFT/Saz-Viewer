using System.Collections.Immutable;
using Microsoft.Playwright;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HtmlReportBrowserTests
{
    private const string InjectionText = "<img src=x onerror=globalThis.pwned=true>";

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

            await page.Locator("#primary-tab-response").ClickAsync();
            Assert.Equal("true", await page.Locator("#primary-tab-response").GetAttributeAsync("aria-selected"));
            Assert.False(await page.Locator("#primary-panel-response").EvaluateAsync<bool>(
                "panel => panel.classList.contains('hidden')"));
            Assert.True(await page.Locator("#primary-panel-request").EvaluateAsync<bool>(
                "panel => panel.classList.contains('hidden')"));
            Assert.Contains("safe", await page.Locator("#response-panel-xml").InnerTextAsync());

            await page.Locator("#response-tab-headers").ClickAsync();
            Assert.Contains("Content-Type: application/xml", await page.Locator("#response-panel-headers").InnerTextAsync());
            await page.Locator("#response-tab-raw").ClickAsync();
            Assert.Contains("<root><value>safe</value></root>", await page.Locator("#response-panel-raw").InnerTextAsync());

            if (exerciseAllControls)
            {
                await page.Locator("#inspectorNext").ClickAsync();
                Assert.Equal("true", await page.Locator("#primary-tab-request").GetAttributeAsync("aria-selected"));
                Assert.Contains("Binary body", await page.Locator("#request-panel-raw").InnerTextAsync());
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
                """{"payload":{"enabled":true},"items":[1,2]}"""),
            Response = Message(
                "HTTP/1.1 200 OK",
                "application/xml",
                "<root><value>safe</value></root>"),
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
            },
        };
        message.Headers.Add(new HttpHeader("Content-Type", "application/octet-stream"));
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
