using System.Collections.Immutable;
using Microsoft.Playwright;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HtmlReportBrowserTests
{
    [WindowsEdgeFact]
    public async Task GeneratedReportInspectorIsVisibleAndInteractiveAtDesktopAndNarrowWidths()
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"saz-viewer-browser-{Guid.NewGuid():N}.html");
        try
        {
            await File.WriteAllTextAsync(reportPath, new HtmlReportGenerator().Generate(CreateReport()));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new()
            {
                Channel = "msedge",
                Headless = true,
            });

            await VerifyInspectorAsync(browser, reportPath, 1440, exerciseAllControls: true);
            await VerifyInspectorAsync(browser, reportPath, 480, exerciseAllControls: false);
        }
        finally
        {
            File.Delete(reportPath);
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
