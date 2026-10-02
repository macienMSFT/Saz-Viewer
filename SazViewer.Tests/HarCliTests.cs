using System.Text;
using SazViewer.Cli;

namespace SazViewer.Tests;

public sealed class HarCliTests
{
    [Fact]
    public void ConvertsHarAndShowsCaptureMetadataInReport()
    {
        using var fixture = new HarCliFixture();
        var console = new FakeConsole();

        var exitCode = new CliApplication(console).Run([fixture.InputPath, fixture.OutputPath]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(fixture.OutputPath));
        var html = File.ReadAllText(fixture.OutputPath);
        Assert.Contains("<h1>HAR capture</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Creator</dt><dd>Chrome DevTools 124</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Browser</dt><dd>Microsoft Edge 124</dd>", html, StringComparison.Ordinal);
        Assert.Contains("Created:", console.Output, StringComparison.Ordinal);
        Assert.Empty(console.Error);
    }

    [Fact]
    public void RejectsPasswordStdinForHar()
    {
        using var fixture = new HarCliFixture();
        var console = new FakeConsole { IsInputRedirected = true };

        var exitCode = new CliApplication(console).Run(
            ["--password-stdin", fixture.InputPath, fixture.OutputPath]);

        Assert.Equal(2, exitCode);
        Assert.Contains("applies only to encrypted SAZ archives", console.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void ScrubsHarCookiesQueryParametersPostDataAndWebSockets()
    {
        using var fixture = new HarCliFixture(includeSecrets: true);
        var console = new FakeConsole();

        var exitCode = new CliApplication(console).Run(
            ["--scrub-auth", fixture.InputPath, fixture.OutputPath]);

        Assert.Equal(0, exitCode);
        var html = File.ReadAllText(fixture.OutputPath);
        foreach (var secret in HarCliFixture.Secrets)
        {
            Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
        }
        Assert.Contains("Authentication scrub:", console.Output, StringComparison.Ordinal);
    }

    private sealed class FakeConsole : ICliConsole
    {
        private readonly StringBuilder output = new();
        private readonly StringBuilder error = new();

        public bool IsInputRedirected { get; init; }
        public TextReader In { get; } = new StringReader("unused");
        public string Output => output.ToString();
        public string Error => error.ToString();
        public void Write(string value) => output.Append(value);
        public void WriteLine(string value = "") => output.AppendLine(value);
        public void WriteErrorLine(string value) => error.AppendLine(value);
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }

    private sealed class HarCliFixture : IDisposable
    {
        public static readonly string[] Secrets =
        [
            "query-secret-72e9",
            "request-cookie-secret-72e9",
            "response-cookie-secret-72e9",
            "post-secret-72e9",
            "websocket-secret-72e9"
        ];

        private readonly string directory =
            Path.Combine(Path.GetTempPath(), $"sazviewer-har-cli-{Guid.NewGuid():N}");

        public HarCliFixture(bool includeSecrets = false)
        {
            Directory.CreateDirectory(directory);
            InputPath = Path.Combine(directory, "capture.har");
            OutputPath = Path.Combine(directory, "capture.html");
            var query = includeSecrets ? $"?access_token={Secrets[0]}" : "";
            var requestCookie = includeSecrets ? Secrets[1] : "request-cookie";
            var responseCookie = includeSecrets ? Secrets[2] : "response-cookie";
            var post = includeSecrets ? Secrets[3] : "post-value";
            var socket = includeSecrets ? $"password={Secrets[4]}" : "hello";
            File.WriteAllText(
                InputPath,
                $$"""
                {
                  "log": {
                    "version": "1.2",
                    "creator": {"name":"Chrome DevTools","version":"124"},
                    "browser": {"name":"Microsoft Edge","version":"124"},
                    "entries": [{
                      "startedDateTime": "2024-05-01T12:00:00Z",
                      "time": 12,
                      "request": {
                        "method": "POST",
                        "url": "https://example.test/{{query}}",
                        "httpVersion": "HTTP/2",
                        "headers": [],
                        "cookies": [{"name":"session","value":"{{requestCookie}}"}],
                        "postData": {
                          "mimeType":"application/x-www-form-urlencoded",
                          "params":[{"name":"password","value":"{{post}}"}]
                        }
                      },
                      "response": {
                        "status": 200,
                        "statusText": "OK",
                        "httpVersion": "HTTP/2",
                        "headers": [],
                        "cookies": [{"name":"server","value":"{{responseCookie}}"}],
                        "content": {"mimeType":"application/json","text":"{\"ok\":true}"}
                      },
                      "timings": {"wait":5,"receive":7},
                      "_webSocketMessages": [{"type":"send","time":1714564800,"opcode":1,"data":"{{socket}}"}]
                    }]
                  }
                }
                """,
                new UTF8Encoding(false));
        }

        public string InputPath { get; }
        public string OutputPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
