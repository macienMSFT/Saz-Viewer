using System.IO.Compression;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class DeferredBodyDecodingTests
{
    [Fact]
    public void DeferredParseDoesNotDecodeBodiesUntilAccessed()
    {
        using var saz = Capture();

        var report = new SazParser { DeferBodyDecoding = true }.Parse(saz);

        Assert.True(report.HasDeferredWork);
        var plain = report.Sessions.Single(session => session.Id == "1");
        Assert.False(plain.Response!.IsBodyDecoded);
        Assert.Equal("""{"ok":true}""", plain.Response.Body.Preview);
        Assert.True(plain.Response.IsBodyDecoded);
    }

    [Fact]
    public void CompletedDeferredParseMatchesEagerParse()
    {
        using var eagerInput = Capture();
        using var deferredInput = Capture();
        var eager = new SazParser().Parse(eagerInput);
        var deferred = new SazParser { DeferBodyDecoding = true }.Parse(deferredInput);

        // Touch one later body first so the per-session warning order is exercised out of order.
        _ = deferred.Sessions.Single(session => session.Id == "3").Response!.Body;
        SazParser.CompleteDeferred(deferred);

        Assert.False(deferred.HasDeferredWork);
        Assert.Equal(eager.Warnings, deferred.Warnings);
        Assert.NotEmpty(eager.Warnings);
        Assert.Equal(eager.Sessions.Count, deferred.Sessions.Count);
        for (var index = 0; index < eager.Sessions.Count; index++)
        {
            Assert.Equal(eager.Sessions[index].Warnings, deferred.Sessions[index].Warnings);
            Assert.Equal(eager.Sessions[index].Response?.Body.Preview, deferred.Sessions[index].Response?.Body.Preview);
            Assert.Equal(eager.Sessions[index].Response?.Body.DecodingStatus, deferred.Sessions[index].Response?.Body.DecodingStatus);
        }
        Assert.Equal(new HtmlReportGenerator().Generate(eager), new HtmlReportGenerator().Generate(deferred));
    }

    [Fact]
    public void CompleteDeferredIsIdempotentAndIgnoresEagerReports()
    {
        using var deferredInput = Capture();
        using var eagerInput = Capture();
        var deferred = new SazParser { DeferBodyDecoding = true }.Parse(deferredInput);
        var eager = new SazParser().Parse(eagerInput);
        var eagerWarnings = eager.Warnings.ToArray();

        SazParser.CompleteDeferred(deferred);
        var first = deferred.Warnings.ToArray();
        SazParser.CompleteDeferred(deferred);
        SazParser.CompleteDeferred(eager);

        Assert.Equal(first, deferred.Warnings);
        Assert.Equal(eagerWarnings, eager.Warnings);
        Assert.False(eager.HasDeferredWork);
    }

    [Fact]
    public void DeferredSessionWarningsAppearOnceBodyIsDecoded()
    {
        using var saz = Capture();
        var report = new SazParser { DeferBodyDecoding = true }.Parse(saz);
        var broken = report.Sessions.Single(session => session.Id == "2");

        _ = broken.Request?.Body;
        _ = broken.Response!.Body;

        Assert.Contains(broken.Warnings, warning => warning.Contains("gzip", StringComparison.OrdinalIgnoreCase));
    }

    private static MemoryStream Capture() => Fixture(
        ("raw/1_c.txt", Http("GET https://example.test/a HTTP/1.1", [], ("Host", "example.test"))),
        ("raw/1_s.txt", Http("HTTP/1.1 200 OK", Gzip(Bytes("""{"ok":true}""")), ("Content-Type", "application/json"), ("Content-Encoding", "gzip"))),
        ("raw/2_c.txt", Http("GET https://example.test/b HTTP/1.1", [], ("Host", "example.test"))),
        ("raw/2_s.txt", Http("HTTP/1.1 200 OK", Bytes("not gzip at all"), ("Content-Type", "text/plain"), ("Content-Encoding", "gzip"))),
        ("raw/3_c.txt", Http("POST https://example.test/c HTTP/1.1", Bytes("x=1"), ("Host", "example.test"), ("Content-Length", "3"))),
        ("raw/3_s.txt", Http("HTTP/1.1 200 OK", Bytes("tiny"), ("Content-Type", "text/plain"), ("Content-Encoding", "br"))),
        ("raw/4_c.txt", Http(
            "POST https://outlook.example.test/mapi/emsmdb/?MailboxId=x HTTP/1.1",
            [1, 2, 3],
            ("Host", "outlook.example.test"),
            ("Content-Type", "application/mapi-http"),
            ("X-RequestType", "Execute"),
            ("Content-Length", "3"))),
        ("raw/4_s.txt", Http(
            "HTTP/1.1 200 OK",
            Bytes("PROCESSING\r\nDONE\r\n\r\n"),
            ("Content-Type", "application/mapi-http"),
            ("X-ResponseCode", "0"))),
        ("raw/5_c.txt", Http("GET https://example.test/missing HTTP/1.1", [], ("Host", "example.test"))));

    private static byte[] Http(string startLine, byte[] body, params (string Name, string Value)[] headers)
    {
        using var output = new MemoryStream();
        Write(output, startLine + "\r\n");
        foreach (var (name, value) in headers)
        {
            Write(output, $"{name}: {value}\r\n");
        }
        Write(output, "\r\n");
        output.Write(body);
        return output.ToArray();
    }

    private static byte[] Gzip(byte[] value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(value);
        }
        return output.ToArray();
    }

    private static MemoryStream Fixture(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                output.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static void Write(Stream stream, string value) => stream.Write(Bytes(value));
}
