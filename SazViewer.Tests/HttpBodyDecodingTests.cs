using System.IO.Compression;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class HttpBodyDecodingTests
{
    [Fact]
    public void DecodesGzipJsonAndExposesCapturedBytes()
    {
        const string json = """{"message":"decoded","value":42}""";
        using var saz = ResponseFixture(
            Gzip(Bytes(json)),
            ("Content-Type", "application/json"),
            ("Content-Encoding", "GZip"));

        var report = new SazParser().Parse(saz);
        var body = Assert.Single(report.Sessions).Response!.Body;

        Assert.Equal(json, body.Preview);
        Assert.True(body.WasDecoded);
        Assert.Equal(["content: gzip"], body.RemovedEncodings);
        Assert.NotNull(body.CapturedBytesPreview);
        Assert.Contains("wire-removal order", body.DecodingStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("decoding failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DecodesBrotliTextAndZlibDeflate()
    {
        const string brotliText = "Brotli decoded text";
        const string deflateText = "zlib wrapped deflate";
        using var brotliSaz = ResponseFixture(
            Brotli(Bytes(brotliText)),
            ("Content-Type", "text/plain; charset=utf-8"),
            ("Content-Encoding", "br"));
        using var deflateSaz = ResponseFixture(
            Zlib(Bytes(deflateText)),
            ("Content-Type", "text/plain"),
            ("Content-Encoding", "deflate"));

        var brotli = Assert.Single(new SazParser().Parse(brotliSaz).Sessions).Response!.Body;
        var deflate = Assert.Single(new SazParser().Parse(deflateSaz).Sessions).Response!.Body;

        Assert.Equal(brotliText, brotli.Preview);
        Assert.Equal(["content: br"], brotli.RemovedEncodings);
        Assert.Equal(deflateText, deflate.Preview);
        Assert.Equal(["content: deflate"], deflate.RemovedEncodings);
    }

    [Fact]
    public void FallsBackToRawDeflateVariant()
    {
        const string text = "raw deflate interoperability";
        using var saz = ResponseFixture(
            RawDeflate(Bytes(text)),
            ("Content-Type", "text/plain"),
            ("Content-Encoding", "deflate"));

        var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

        Assert.Equal(text, body.Preview);
        Assert.Equal(["content: deflate (raw variant)"], body.RemovedEncodings);
    }

    [Fact]
    public void AcceptsRawDeflateThatResemblesZlibAndRejectsRawTrailingBytes()
    {
        byte[] resemblesZlib = [0x78, 0x01, 0x00, 0xFE, 0xFF, 0x41, 0x03, 0x00];
        using var validSaz = ResponseFixture(
            resemblesZlib,
            ("Content-Type", "text/plain"),
            ("Content-Encoding", "deflate"));
        using var trailingSaz = ResponseFixture(
            [0x73, 0x04, 0x00, 0x41],
            ("Content-Type", "text/plain"),
            ("Content-Encoding", "deflate"));

        var valid = Assert.Single(new SazParser().Parse(validSaz).Sessions).Response!.Body;
        var trailingReport = new SazParser().Parse(trailingSaz);
        var trailing = Assert.Single(trailingReport.Sessions).Response!.Body;

        Assert.Equal("A", valid.Preview);
        Assert.Equal(["content: deflate (raw variant)"], valid.RemovedEncodings);
        Assert.False(trailing.WasDecoded);
        Assert.Contains(trailingReport.Warnings, warning => warning.Contains("trailing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DechunksBeforeRemovingContentEncodingAndParsesTrailers()
    {
        const string json = """{"chunked":true,"message":"decoded"}""";
        var compressed = Gzip(Bytes(json));
        var chunked = Chunk(compressed, includeExtension: true, includeTrailer: true);
        using var saz = ResponseFixture(
            chunked,
            ("Content-Type", "application/json"),
            ("Transfer-Encoding", "chunked"),
            ("Content-Encoding", "gzip"));

        var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

        Assert.Equal(json, body.Preview);
        Assert.Equal(["transfer: chunked (1 trailers)", "content: gzip"], body.RemovedEncodings);
        Assert.Contains("transfer: chunked", body.DecodingStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void DechunksMultiChunkUtf8JsonWithFiddlerTransferLengthMetadata()
    {
        const string json = """{"items":[{"id":1},{"id":2}],"message":"synthetic"}""";
        var plain = Bytes(json);
        var chunked = ChunkPieces(plain, 7, 3, 11, 5);
        var metadata =
            $"<Session><SessionFlag N=\"x-responsebodytransferlength\" V=\"{chunked.Length}\"/></Session>";
        using var saz = Fixture(
            ("raw/1_c.txt", HttpBytes("GET https://example.test/data HTTP/1.1", [], ("Host", "example.test"))),
            ("raw/1_s.txt", HttpBytes(
                "HTTP/1.1 200 OK",
                chunked,
                ("Transfer-Encoding", "chunked"),
                ("Content-Type", "application/json; odata.metadata=minimal; odata.streaming=true; charset=utf-8"))),
            ("raw/1_m.xml", Bytes(metadata)));

        var session = Assert.Single(new SazParser().Parse(saz).Sessions);
        var body = session.Response!.Body;
        var presentation = new BodyFormatter().Format(body, session.Response.Header("Content-Type"));

        Assert.Equal(json, body.Preview);
        Assert.Equal(plain, Encoding.UTF8.GetBytes(body.Preview));
        Assert.Equal(chunked.Length, body.CapturedLength);
        Assert.Equal(plain.Length, body.Length);
        Assert.Equal(["transfer: chunked"], body.RemovedEncodings);
        Assert.Equal(chunked.Length.ToString(), session.Metadata["x-responsebodytransferlength"]);
        Assert.Equal(BodyFormat.Json, presentation.Format);
    }

    [Fact]
    public void StaleChunkedHeaderProducesExplicitRawFallback()
    {
        const string normalizedJson = """{"already":"dechunked"}""";
        var bodyBytes = Bytes(normalizedJson);
        using var saz = Fixture(
            ("raw/1_c.txt", HttpBytes("GET https://example.test/data HTTP/1.1", [], ("Host", "example.test"))),
            ("raw/1_s.txt", HttpBytes(
                "HTTP/1.1 200 OK",
                bodyBytes,
                ("Transfer-Encoding", "chunked"),
                ("Content-Type", "application/json"))),
            ("raw/1_m.xml", Bytes(
                $"<Session><SessionFlag N=\"x-responsebodytransferlength\" V=\"{bodyBytes.Length}\"/></Session>")));

        var report = new SazParser().Parse(saz);
        var body = Assert.Single(report.Sessions).Response!.Body;

        Assert.False(body.WasDecoded);
        Assert.True(body.IsBinary);
        Assert.Contains("already normalized by the SAZ producer", body.DecodingStatus, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, warning => warning.Contains("already normalized", StringComparison.Ordinal));
    }

    [Fact]
    public void DecodesMultipleContentEncodingLinesInReverseOrder()
    {
        const string json = """{"chain":["gzip","br"]}""";
        var encoded = Brotli(Gzip(Bytes(json)));
        using var saz = ResponseFixture(
            encoded,
            ("Content-Type", "application/json"),
            ("Content-Encoding", "gzip"),
            ("Content-Encoding", "br, identity"));

        var response = Assert.Single(new SazParser().Parse(saz).Sessions).Response!;

        Assert.Equal(json, response.Body.Preview);
        Assert.Equal(["content: br", "content: gzip"], response.Body.RemovedEncodings);
        Assert.Equal(["gzip", "br, identity"], response.HeaderValues("content-encoding"));
    }

    [Fact]
    public void AppliesSameDecodingPipelineToRequestBodies()
    {
        const string json = """{"request":"decoded"}""";
        using var saz = Fixture(
            ("raw/1_c.txt", HttpBytes(
                "POST https://example.test/ HTTP/1.1",
                Gzip(Bytes(json)),
                ("Host", "example.test"),
                ("Content-Type", "application/json"),
                ("Content-Encoding", "gzip"))),
            ("raw/1_s.txt", HttpBytes("HTTP/1.1 204 No Content", [])));

        var request = Assert.Single(new SazParser().Parse(saz).Sessions).Request!;

        Assert.Equal(json, request.Body.Preview);
        Assert.True(request.Body.WasDecoded);
    }

    [Fact]
    public void PreservesCapturedBytesForMalformedAndUnsupportedCodings()
    {
        using var malformed = ResponseFixture(
            Bytes("5\r\nabc\r\n0\r\n\r\n"),
            ("Transfer-Encoding", "chunked"),
            ("Content-Type", "text/plain"));
        using var unsupported = ResponseFixture(
            Bytes("encoded data"),
            ("Content-Encoding", "zstd"),
            ("Content-Type", "text/plain"));

        var malformedReport = new SazParser().Parse(malformed);
        var unsupportedReport = new SazParser().Parse(unsupported);

        Assert.True(Assert.Single(malformedReport.Sessions).Response!.Body.IsBinary);
        Assert.Contains(malformedReport.Warnings, warning => warning.Contains("chunk", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("61 62 63", Assert.Single(malformedReport.Sessions).Response!.Body.Preview, StringComparison.Ordinal);
        Assert.True(Assert.Single(unsupportedReport.Sessions).Response!.Body.IsBinary);
        Assert.Contains(unsupportedReport.Warnings, warning => warning.Contains("unsupported coding 'zstd'", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsCorruptGzipWithoutExposingPartialOutput()
    {
        var corrupt = Gzip(Bytes("do not expose partial output"));
        corrupt[^1] ^= 0xFF;
        using var saz = ResponseFixture(
            corrupt,
            ("Content-Encoding", "gzip"),
            ("Content-Type", "text/plain"));

        var report = new SazParser().Parse(saz);
        var body = Assert.Single(report.Sessions).Response!.Body;

        Assert.True(body.IsBinary);
        Assert.False(body.WasDecoded);
        Assert.DoesNotContain("do not expose partial output", body.Preview, StringComparison.Ordinal);
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("corrupt or truncated", StringComparison.OrdinalIgnoreCase)
                || warning.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsTrailingBytesForChecksummedAndSelfTerminatingCodings()
    {
        var cases = new[]
        {
            (Encoding: "gzip", Bytes: Gzip(Bytes("gzip"))),
            (Encoding: "deflate", Bytes: Zlib(Bytes("zlib"))),
            (Encoding: "br", Bytes: Brotli(Bytes("brotli")))
        };

        foreach (var item in cases)
        {
            using var saz = ResponseFixture(
                [.. item.Bytes, 0x41],
                ("Content-Encoding", item.Encoding),
                ("Content-Type", "text/plain"));

            var report = new SazParser().Parse(saz);
            var body = Assert.Single(report.Sessions).Response!.Body;

            Assert.False(body.WasDecoded);
            Assert.True(body.IsBinary);
            Assert.Contains(
                report.Warnings,
                warning => warning.Contains("trailing", StringComparison.OrdinalIgnoreCase)
                    || warning.Contains("checksum", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void RejectsDuplicatedValidCompressionFooters()
    {
        var gzip = Gzip(Bytes("gzip footer"));
        var zlib = Zlib(Bytes("zlib footer"));
        var cases = new[]
        {
            (Encoding: "gzip", Bytes: gzip.Concat(gzip[^8..]).ToArray()),
            (Encoding: "deflate", Bytes: zlib.Concat(zlib[^4..]).ToArray())
        };

        foreach (var item in cases)
        {
            using var saz = ResponseFixture(
                item.Bytes,
                ("Content-Encoding", item.Encoding),
                ("Content-Type", "text/plain"));

            var report = new SazParser().Parse(saz);

            Assert.False(Assert.Single(report.Sessions).Response!.Body.WasDecoded);
            Assert.Contains(report.Warnings, warning => warning.Contains("trailing", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void DecodesConcatenatedGzipMembersIncludingEmptyLeadingMember()
    {
        var content = Gzip(Bytes("content member"));
        var cases = new[]
        {
            (Bytes: EmptyGzipMember().Concat(content).ToArray(), Expected: "content member"),
            (Bytes: content.Concat(EmptyGzipMember()).ToArray(), Expected: "content member")
        };

        foreach (var item in cases)
        {
            using var saz = ResponseFixture(
                item.Bytes,
                ("Content-Encoding", "gzip"),
                ("Content-Type", "text/plain"));

            var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

            Assert.True(body.WasDecoded);
            Assert.Equal(item.Expected, body.Preview);
        }
    }

    [Fact]
    public void LimitsConcatenatedGzipMemberCount()
    {
        var encoded = Enumerable.Range(0, 129).SelectMany(_ => EmptyGzipMember()).ToArray();
        using var saz = ResponseFixture(
            encoded,
            ("Content-Encoding", "gzip"),
            ("Content-Type", "text/plain"));

        var report = new SazParser().Parse(saz);
        var body = Assert.Single(report.Sessions).Response!.Body;

        Assert.False(body.WasDecoded);
        Assert.Contains(report.Warnings, warning => warning.Contains("128-member safety limit", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsInvalidChunkExtensionsAndTrailerNames()
    {
        using var badExtension = ResponseFixture(
            Bytes("3;=bad\r\nabc\r\n0\r\n\r\n"),
            ("Transfer-Encoding", "chunked"));
        using var badTrailer = ResponseFixture(
            Bytes("3\r\nabc\r\n0\r\nbad name: value\r\n\r\n"),
            ("Transfer-Encoding", "chunked"));

        var extensionReport = new SazParser().Parse(badExtension);
        var trailerReport = new SazParser().Parse(badTrailer);

        Assert.False(Assert.Single(extensionReport.Sessions).Response!.Body.WasDecoded);
        Assert.Contains(extensionReport.Warnings, warning => warning.Contains("invalid chunk extension", StringComparison.Ordinal));
        Assert.False(Assert.Single(trailerReport.Sessions).Response!.Body.WasDecoded);
        Assert.Contains(trailerReport.Warnings, warning => warning.Contains("malformed trailer", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportsQuotedTransferParametersContainingCommas()
    {
        const string text = "transfer-coded content";
        var encoded = Chunk(Gzip(Bytes(text)), includeExtension: false, includeTrailer: false);
        using var saz = ResponseFixture(
            encoded,
            ("Transfer-Encoding", "gzip; note=\"one,two\", chunked"),
            ("Content-Type", "text/plain"));

        var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

        Assert.Equal(text, body.Preview);
        Assert.Equal(["transfer: chunked", "transfer: gzip"], body.RemovedEncodings);
    }

    [Fact]
    public void AcceptsBadWhitespaceAroundChunkExtensions()
    {
        using var saz = ResponseFixture(
            Bytes("3 ; name = value\r\nabc\r\n0\r\n\r\n"),
            ("Transfer-Encoding", "chunked"),
            ("Content-Type", "text/plain"));

        var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

        Assert.Equal("abc", body.Preview);
        Assert.Equal(["transfer: chunked"], body.RemovedEncodings);
    }

    [Fact]
    public void CapsFailedCapturedBytePreviewAtDocumentedLimit()
    {
        var captured = Enumerable.Repeat((byte)0xAB, 70_000).ToArray();
        using var saz = ResponseFixture(
            captured,
            ("Content-Encoding", "unsupported"));

        var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

        Assert.True(body.IsTruncated);
        Assert.True(body.Preview.Length < 100_000);
        Assert.DoesNotContain("00004000", body.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcesDecompressionOutputLimit()
    {
        var expanded = Enumerable.Repeat((byte)'a', 2 * 1024 * 1024).ToArray();
        using var saz = ResponseFixture(
            Gzip(expanded),
            ("Content-Encoding", "gzip"),
            ("Content-Type", "text/plain"));

        var report = new SazParser().Parse(saz);
        var body = Assert.Single(report.Sessions).Response!.Body;

        Assert.True(body.IsBinary);
        Assert.False(body.WasDecoded);
        Assert.Contains(report.Warnings, warning => warning.Contains("safety limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EncodesDecodedInjectionContentInReport()
    {
        const string attack = """{"value":"</script><img src=x onerror=alert(1)>"}""";
        using var saz = ResponseFixture(
            Gzip(Bytes(attack)),
            ("Content-Encoding", "gzip"),
            ("Content-Type", "application/json"));

        var html = new HtmlReportGenerator().Generate(new SazParser().Parse(saz));

        Assert.DoesNotContain("</script><img src=x onerror=alert(1)>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;/script&gt;&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Decoded text", html, StringComparison.Ordinal);
        Assert.Contains("Decoded in wire-removal order: content: gzip.", html, StringComparison.Ordinal);
        Assert.Contains("Captured bytes", html, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", html, StringComparison.Ordinal);
    }

    private static MemoryStream ResponseFixture(
        byte[] body,
        params (string Name, string Value)[] headers) =>
        Fixture(
            ("raw/1_c.txt", HttpBytes("GET https://example.test/ HTTP/1.1", [], ("Host", "example.test"))),
            ("raw/1_s.txt", HttpBytes("HTTP/1.1 200 OK", body, headers)));

    private static byte[] HttpBytes(
        string startLine,
        byte[] body,
        params (string Name, string Value)[] headers)
    {
        using var output = new MemoryStream();
        Write(output, startLine + "\r\n");
        foreach (var header in headers)
        {
            Write(output, $"{header.Name}: {header.Value}\r\n");
        }
        Write(output, "\r\n");
        output.Write(body);
        return output.ToArray();
    }

    private static byte[] Chunk(byte[] body, bool includeExtension, bool includeTrailer)
    {
        using var output = new MemoryStream();
        var split = Math.Max(1, body.Length / 2);
        Write(output, $"{split:X}{(includeExtension ? ";name=\"value\"" : string.Empty)}\r\n");
        output.Write(body, 0, split);
        Write(output, "\r\n");
        Write(output, $"{body.Length - split:X}\r\n");
        output.Write(body, split, body.Length - split);
        Write(output, "\r\n0\r\n");
        if (includeTrailer)
        {
            Write(output, "X-Checksum: complete\r\n");
        }
        Write(output, "\r\n");
        return output.ToArray();
    }

    private static byte[] ChunkPieces(byte[] body, params int[] requestedSizes)
    {
        using var output = new MemoryStream();
        var offset = 0;
        foreach (var requestedSize in requestedSizes)
        {
            if (offset >= body.Length)
            {
                break;
            }
            var size = Math.Min(requestedSize, body.Length - offset);
            Write(output, $"{size:X}\r\n");
            output.Write(body, offset, size);
            Write(output, "\r\n");
            offset += size;
        }
        if (offset < body.Length)
        {
            var size = body.Length - offset;
            Write(output, $"{size:X}\r\n");
            output.Write(body, offset, size);
            Write(output, "\r\n");
        }
        Write(output, "0\r\n\r\n");
        return output.ToArray();
    }

    private static byte[] Gzip(byte[] value) =>
        Compress(value, stream => new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true));

    private static byte[] Zlib(byte[] value) =>
        Compress(value, stream => new ZLibStream(stream, CompressionLevel.SmallestSize, leaveOpen: true));

    private static byte[] RawDeflate(byte[] value) =>
        Compress(value, stream => new DeflateStream(stream, CompressionLevel.SmallestSize, leaveOpen: true));

    private static byte[] Brotli(byte[] value) =>
        Compress(value, stream => new BrotliStream(stream, CompressionLevel.SmallestSize, leaveOpen: true));

    private static byte[] EmptyGzipMember() =>
        [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static byte[] Compress(byte[] value, Func<Stream, Stream> create)
    {
        using var output = new MemoryStream();
        using (var compressor = create(output))
        {
            compressor.Write(value);
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
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var output = entry.Open();
                output.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static void Write(Stream stream, string value) => stream.Write(Bytes(value));
}
