using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HarParserTests
{
    [Fact]
    public void ParsesChromeHarIntoSharedModelWithoutDoubleDecoding()
    {
        using var stream = Har(
            Entry(
                "2024-05-01T12:00:02Z",
                "https://example.test/late?access_token=secret",
                "POST",
                requestVersion: "h2",
                responseStatus: 302,
                responseText: """{"decoded":true}""",
                responseMime: "application/json",
                responseHeaders:
                [
                    Header("content-encoding", "gzip"),
                    Header(":status", "302")
                ],
                extra: """
                "pageref":"page-1",
                "_resourceType":"fetch",
                "_priority":"High",
                "_initiator":{"type":"script","url":"https://example.test/app.js"},
                "serverIPAddress":"203.0.113.10",
                "connection":"42",
                "_webSocketMessages":[
                  {"type":"receive","time":1714564801.5,"opcode":2,"data":"AH//"},
                  {"type":"send","time":1714564801.0,"opcode":1,"data":"hello"},
                  {"type":"receive","time":1714564802.0,"opcode":9,"data":"ping"}
                ],
                """),
            Entry(
                "2024-05-01T12:00:01Z",
                "https://example.test/early",
                "GET",
                responseText: "AAEC/w==",
                responseMime: "application/octet-stream",
                responseEncoding: "base64",
                extra: """
                "responseOverride":{"redirectURL":"https://example.test/next"}
                """));

        var report = new HarParser().Parse(stream, "chrome.har");

        Assert.Equal(CaptureFormat.Har, report.Format);
        Assert.Equal("Chrome DevTools 124", report.Creator);
        Assert.Equal("Microsoft Edge 124", report.Browser);
        Assert.Equal(["2", "1"], report.Sessions.Select(session => session.Id));

        var early = report.Sessions[0];
        Assert.Equal("GET", early.Method);
        Assert.Equal(200, early.StatusCode);
        Assert.Equal([0, 1, 2, 255], early.Response!.Body.DecodedBytes.ToArray());
        Assert.True(early.Response.Body.IsBinary);
        Assert.Empty(early.Response.Body.CapturedBytes.ToArray());
        Assert.False(early.Response.Body.WasDecoded);
        Assert.Contains("original wire bytes", early.Response.Body.DecodingStatus, StringComparison.Ordinal);
        Assert.Equal("https://example.test/next", early.Response.Header("Location"));

        var late = report.Sessions[1];
        Assert.Equal("HTTP/2", late.Request!.StartLine.Split(' ').Last());
        Assert.Equal("""{"decoded":true}""", late.Response!.Body.Preview);
        Assert.False(late.Response.Body.WasDecoded);
        Assert.Equal(2_500, late.ElapsedMilliseconds);
        Assert.Equal("25", late.Timers["HAR.Wait"]);
        Assert.Equal("4", late.Timers["DNSTime"]);
        Assert.Equal("8", late.Timers["TCPConnectTime"]);
        Assert.Equal("3", late.Timers["HTTPSHandshakeTime"]);
        Assert.Equal("fetch", late.Metadata["_resourceType"]);
        Assert.Equal("High", late.Metadata["_priority"]);
        Assert.Equal("Page title", late.Metadata["har.page.title"]);
        Assert.Equal("203.0.113.10", late.Metadata["x-hostip"]);
        Assert.Equal("42", late.Metadata["connection"]);
        Assert.Contains(report.Warnings, warning => warning.Contains("pseudo-headers", StringComparison.Ordinal));

        Assert.Equal(["Text", "Binary", "Ping"], report.WebSocketMessages.Select(message => message.Type));
        Assert.Equal(["Client", "Server", "Server"], report.WebSocketMessages.Select(message => message.Direction));
        Assert.Equal("hello", report.WebSocketMessages[0].Text);
        Assert.Equal([0, 0x7F, 0xFF], report.WebSocketMessages[1].Payload.ToArray());
    }

    [Fact]
    public void PreservesMalformedEntriesAndEmptyLogsAsWarnings()
    {
        using var malformed = Json(
            """
            {
              "log": {
                "version": "1.2",
                "entries": [
                  17,
                  {"startedDateTime":"not-a-time","request":{"headers":[{"value":"missing name"}]},"response":"bad"}
                ]
              }
            }
            """);

        var report = new HarParser().Parse(malformed, "malformed.har");

        Assert.Equal(2, report.Sessions.Count);
        Assert.Contains(report.Warnings, warning => warning.Contains("not an object", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("invalid startedDateTime", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("response is missing", StringComparison.Ordinal));

        using var empty = Json("""{"log":{"version":"1.2","entries":[]}}""");
        var emptyReport = new HarParser().Parse(empty, "empty.har");
        Assert.Empty(emptyReport.Sessions);
        Assert.Contains("HAR log.entries is empty.", emptyReport.Warnings);
    }

    [Fact]
    public void CapsHugeDecodedBodies()
    {
        var huge = new string('x', HarParser.MaxBodyBytes + 1024);
        var payload = JsonSerializer.Serialize(new
        {
            log = new
            {
                version = "1.2",
                entries = new[]
                {
                    new
                    {
                        startedDateTime = "2024-05-01T12:00:00Z",
                        time = 1,
                        request = new { method = "GET", url = "https://example.test/", httpVersion = "HTTP/1.1", headers = Array.Empty<object>() },
                        response = new
                        {
                            status = 200,
                            statusText = "OK",
                            httpVersion = "HTTP/1.1",
                            headers = Array.Empty<object>(),
                            content = new { size = huge.Length, mimeType = "text/plain", text = huge }
                        }
                    }
                }
            }
        });
        using var stream = Json(payload);

        var body = Assert.Single(new HarParser().Parse(stream, "huge.har").Sessions).Response!.Body;

        Assert.Equal(huge.Length, body.Length);
        Assert.Equal(HarParser.MaxBodyBytes, body.DecodedBytes.Length);
        Assert.True(body.IsTruncated);
    }

    [Fact]
    public void DetectsSazAndHarByContentAndRejectsUnknownInput()
    {
        using var temp = new TempDirectory();
        var harPath = temp.File("capture.data");
        File.WriteAllText(harPath, """{"log":{"version":"1.2","entries":[]}}""");
        Assert.Equal(CaptureFormat.Har, CaptureParser.DetectFormat(harPath));
        Assert.Equal(CaptureFormat.Har, new CaptureParser().Parse(harPath).Format);

        var sazPath = temp.File("capture.bin");
        using (var archive = ZipFile.Open(sazPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("raw/1_c.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.ASCII);
            writer.Write("GET / HTTP/1.1\r\nHost: example.test\r\n\r\n");
        }
        Assert.Equal(CaptureFormat.Saz, CaptureParser.DetectFormat(sazPath));
        Assert.Equal(CaptureFormat.Saz, new CaptureParser().Parse(sazPath).Format);

        var unknown = temp.File("unknown.txt");
        File.WriteAllText(unknown, "not a capture");
        var exception = Assert.Throws<InvalidDataException>(() => CaptureParser.DetectFormat(unknown));
        Assert.Contains("not a supported capture", exception.Message, StringComparison.Ordinal);
    }

    private static MemoryStream Har(params string[] entries)
    {
        var json =
            $$"""
            {
              "log": {
                "version": "1.2",
                "creator": {"name":"Chrome DevTools","version":"124"},
                "browser": {"name":"Microsoft Edge","version":"124"},
                "pages": [{"id":"page-1","title":"Page title"}],
                "entries": [{{string.Join(",", entries)}}]
              }
            }
            """;
        return Json(json);
    }

    private static string Entry(
        string started,
        string url,
        string method,
        string requestVersion = "HTTP/1.1",
        int responseStatus = 200,
        string responseText = "ok",
        string responseMime = "text/plain",
        string? responseEncoding = null,
        IReadOnlyList<string>? responseHeaders = null,
        string? extra = null)
    {
        var headers = responseHeaders is null ? "" : string.Join(",", responseHeaders);
        var redirect = extra?.Contains("responseOverride", StringComparison.Ordinal) == true
            ? "\"redirectURL\":\"https://example.test/next\","
            : "\"redirectURL\":\"\",";
        var safeExtra = extra?.Replace(
            "\"responseOverride\":{\"redirectURL\":\"https://example.test/next\"}",
            "",
            StringComparison.Ordinal) ?? string.Empty;
        return
            $$"""
            {
              "startedDateTime":{{JsonSerializer.Serialize(started)}},
              "time":2500,
              "request":{
                "method":{{JsonSerializer.Serialize(method)}},
                "url":{{JsonSerializer.Serialize(url)}},
                "httpVersion":{{JsonSerializer.Serialize(requestVersion)}},
                "headers":[{"name":":authority","value":"example.test"}],
                "cookies":[{"name":"session","value":"cookie-secret"}],
                "queryString":[{"name":"access_token","value":"secret"}],
                "postData":{"mimeType":"application/x-www-form-urlencoded","params":[{"name":"password","value":"post-secret"}]},
                "headersSize":100,
                "bodySize":20
              },
              "response":{
                "status":{{responseStatus}},
                "statusText":"OK",
                "httpVersion":{{JsonSerializer.Serialize(requestVersion)}},
                "headers":[{{headers}}],
                "cookies":[{"name":"server","value":"cookie-secret"}],
                {{redirect}}
                "headersSize":80,
                "bodySize":12,
                "content":{
                  "size":{{(responseEncoding == "base64" ? 4 : Encoding.UTF8.GetByteCount(responseText))}},
                  "compression":2,
                  "mimeType":{{JsonSerializer.Serialize(responseMime)}},
                  "text":{{JsonSerializer.Serialize(responseText)}}
                  {{(responseEncoding is null ? "" : "," + JsonSerializer.Serialize("encoding") + ":" + JsonSerializer.Serialize(responseEncoding))}}
                }
              },
              "timings":{"blocked":-1,"dns":4,"connect":8,"ssl":3,"send":2,"wait":25,"receive":7}
              {{(string.IsNullOrWhiteSpace(safeExtra) ? "" : "," + safeExtra)}}
            }
            """;
    }

    private static string Header(string name, string value) =>
        $$"""{"name":{{JsonSerializer.Serialize(name)}},"value":{{JsonSerializer.Serialize(value)}}}""";

    private static MemoryStream Json(string value) =>
        new(Encoding.UTF8.GetBytes(value));

    private sealed class TempDirectory : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"sazviewer-har-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(path);

        public string File(string name) => Path.Combine(path, name);

        public void Dispose()
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
