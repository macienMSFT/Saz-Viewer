using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class SazParserTests
{
    [Fact]
    public void ParsesHttpAndSortsSparseIdsByMetadataTimestamp()
    {
        using var saz = Fixture(
            ("raw/10_c.txt", Bytes(
                "POST http://example.test/late HTTP/1.1\r\nHost: example.test\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nhello")),
            ("raw/10_s.txt", Bytes(
                "HTTP/1.1 201 Created\r\nContent-Type: application/json; charset=utf-8\r\n\r\n{\"ok\":true}")),
            ("raw/10_m.xml", Bytes(Metadata("2024-05-01T12:00:02.0000000Z", "10.0.0.2", "50002"))),
            ("raw/2_c.txt", Bytes(
                "GET /early HTTP/1.1\r\nHost: example.test\r\nX-Folded: one\r\n two\r\n\r\n")),
            ("raw/2_s.txt", Bytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n<p>safe</p>")),
            ("raw/2_m.xml", Bytes(Metadata("2024-05-01T12:00:01.0000000Z", "10.0.0.1", "50001"))));

        var report = new SazParser().Parse(saz);

        Assert.Equal(["2", "10"], report.Sessions.Select(session => session.Id));
        var first = report.Sessions[0];
        Assert.Equal("GET", first.Method);
        Assert.Equal("http://example.test/early", first.Url);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal("text/html", first.ContentType);
        Assert.Equal("10.0.0.1:50001", first.ClientEndpoint);
        Assert.Equal("one two", first.Request!.Header("X-Folded"));

        var second = report.Sessions[1];
        Assert.Equal("hello", second.Request!.Body.Preview);
        Assert.Equal("{\"ok\":true}", second.Response!.Body.Preview);
        Assert.False(second.Response.Body.IsBinary);
    }

    [Fact]
    public void KeepsMalformedAndMissingEntriesAsWarnings()
    {
        using var saz = Fixture(
            ("raw/7_c.txt", Bytes("NOT A VALID REQUEST")),
            ("raw/7_m.xml", Bytes("<Session><broken>")),
            ("raw/99_s.txt", Bytes("HTTP/1.1 bad\r\nBrokenHeader\r\n\r\nbody")),
            ("raw/other.bin", [1, 2, 3]));

        var report = new SazParser().Parse(saz);

        Assert.Equal(2, report.Sessions.Count);
        Assert.Contains(report.Warnings, warning => warning.Contains("response entry is missing", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("request entry is missing", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("metadata could not be parsed", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("malformed header", StringComparison.Ordinal));
    }

    [Fact]
    public void ProducesHexPreviewForBinaryHttpBody()
    {
        var response = Bytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n\r\n")
            .Concat(new byte[] { 0, 1, 2, 255 }).ToArray();
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET http://example.test/file HTTP/1.1\r\n\r\n")),
            ("raw/1_s.txt", response));

        var report = new SazParser().Parse(saz);
        var body = Assert.Single(report.Sessions).Response!.Body;

        Assert.True(body.IsBinary);
        Assert.Equal(4, body.Length);
        Assert.Contains("00 01 02 FF", body.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotCorruptInvalidUtf8DeclaredAsText()
    {
        var response = Bytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n")
            .Concat(new byte[] { 0xC3, 0x28 }).ToArray();
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_s.txt", response));

        var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

        Assert.True(body.IsBinary);
        Assert.Contains("C3 28", body.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", body.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotCorruptInvalidUtf8WithoutDeclaredCharset()
    {
        var response = Bytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\n")
            .Concat(new byte[] { 0xC3, 0x28 }).ToArray();
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_s.txt", response));

        var body = Assert.Single(new SazParser().Parse(saz).Sessions).Response!.Body;

        Assert.True(body.IsBinary);
        Assert.Contains("C3 28", body.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlEncodesAllCapturedValues()
    {
        const string attack = "<script>globalThis.pwned=true</script>";
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes(
                $"GET http://example.test/?q={attack} HTTP/1.1\r\nX-Attack: {attack}\r\n\r\n{attack}")),
            ("raw/1_s.txt", Bytes(
                $"HTTP/1.1 200 {attack}\r\nContent-Type: text/plain\r\n\r\n{attack}")),
            ("raw/1_m.xml", Bytes(
                $"<Session><SessionTimers ClientBeginRequest=\"2024-01-01T00:00:00Z\"/><SessionFlag N=\"x-clientip\" V=\"&lt;img src=x onerror=alert(1)&gt;\"/></Session>")));

        var report = new SazParser().Parse(saz, attack);
        var html = new HtmlReportGenerator().Generate(report);

        Assert.DoesNotContain(attack, html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;globalThis.pwned=true&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToNumericIdOrderingWhenTimestampsAreMissing()
    {
        using var saz = Fixture(
            ("raw/20_c.txt", Bytes("GET /20 HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/3_c.txt", Bytes("GET /3 HTTP/1.1\r\nHost: test\r\n\r\n")));

        var report = new SazParser().Parse(saz);

        Assert.Equal(["3", "20"], report.Sessions.Select(session => session.Id));
    }

    [Fact]
    public void CalculatesOverallElapsedMillisecondsOnlyFromValidFiddlerTimers()
    {
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /valid HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_m.xml", Bytes(
                """<Session><SessionTimers ClientBeginRequest="2024-05-01T12:00:00.0000000-04:00" ClientDoneResponse="2024-05-01T16:00:01.2509000Z"/></Session>""")),
            ("raw/2_c.txt", Bytes("GET /missing HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/2_m.xml", Bytes(
                """<Session><SessionTimers ClientBeginRequest="2024-05-01T12:00:00Z"/></Session>""")),
            ("raw/3_c.txt", Bytes("GET /invalid HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/3_m.xml", Bytes(
                """<Session><SessionTimers ClientBeginRequest="not-a-time" ClientDoneResponse="2024-05-01T12:00:01Z"/></Session>""")),
            ("raw/4_c.txt", Bytes("GET /negative HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/4_m.xml", Bytes(
                """<Session><SessionTimers ClientBeginRequest="2024-05-01T12:00:02Z" ClientDoneResponse="2024-05-01T12:00:01Z"/></Session>""")));

        var sessions = new SazParser().Parse(saz).Sessions.ToDictionary(session => session.Id);

        Assert.Equal(1_250, sessions["1"].ElapsedMilliseconds);
        Assert.Null(sessions["2"].ElapsedMilliseconds);
        Assert.Null(sessions["3"].ElapsedMilliseconds);
        Assert.Null(sessions["4"].ElapsedMilliseconds);
    }

    [Theory]
    [InlineData("0", "1970-01-01T00:00:00.0000000+00:00")]
    [InlineData("621355968000000000", "1970-01-01T00:00:00.0000000+00:00")]
    [InlineData("618199776000000000", "1960-01-01T00:00:00.0000000+00:00")]
    [InlineData("-62135596800000", "0001-01-01T00:00:00.0000000+00:00")]
    [InlineData("253402300799999", "9999-12-31T23:59:59.9990000+00:00")]
    public void ParsesUnixMillisecondsAndDotNetTicksAcrossTheUnixEpoch(
        string value,
        string expected)
    {
        Assert.True(SazParser.TryParseTimestamp(value, out var timestamp));
        Assert.Equal(DateTimeOffset.Parse(expected), timestamp);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-62135596800001")]
    public void RejectsNumericTimestampsOutsideUnixAndDotNetRanges(string value)
    {
        Assert.False(SazParser.TryParseTimestamp(value, out _));
    }

    [Fact]
    public void ParsesFiddlerWebSocketRecordsAndRfcFrames()
    {
        var clientFrame = MaskedTextFrame("hello", [0x12, 0x34, 0x56, 0x78]);
        var serverFrame = new byte[] { 0x82, 0x03, 0x00, 0x7F, 0xFF };
        var websocket = WebSocketCapture(
            ("Request-Length", "7", "2024-05-01T12:00:02.0000000Z", clientFrame),
            ("Response-Length", "8", "2024-05-01T12:00:01.0000000Z", serverFrame));
        using var saz = Fixture(
            ("raw/42_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: example.test\r\nUpgrade: websocket\r\n\r\n")),
            ("raw/42_s.txt", Bytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n")),
            ("raw/42_w.txt", websocket));

        var report = new SazParser().Parse(saz);

        Assert.Equal(2, report.WebSocketMessages.Count);
        var first = report.WebSocketMessages[0];
        Assert.Equal("Server", first.Direction);
        Assert.Equal("Binary", first.Type);
        Assert.Equal(3, first.PayloadLength);
        Assert.Contains("00 7F FF", first.Preview, StringComparison.Ordinal);
        Assert.False(Assert.Single(first.Frames).Masked);

        var second = report.WebSocketMessages[1];
        Assert.Equal("Client", second.Direction);
        Assert.Equal("Text", second.Type);
        Assert.Equal("hello", second.Preview);
        Assert.True(second.IsDecoded);
        Assert.True(Assert.Single(second.Frames).Masked);
    }

    [Fact]
    public void ParsesFiddlerLeadingBlankFileHeaderAndPreservesPseudoHeaders()
    {
        using var websocket = new MemoryStream();
        Write(websocket, "\r\n");
        var frame = Frame(1, true, Bytes("hello"));
        Write(
            websocket,
            $"Response-Length: {frame.Length}\r\nID: 17\r\nBitFlags: 4\r\nDoneRead: 2024-05-01T12:00:00.0000000Z\r\n\r\n");
        websocket.Write(frame);
        Write(websocket, "\r\n");
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", websocket.ToArray()));

        var message = Assert.Single(new SazParser().Parse(saz).WebSocketMessages);

        var parsedFrame = Assert.Single(message.Frames);
        Assert.Equal(17, parsedFrame.FiddlerId);
        Assert.Equal(4, parsedFrame.BitFlags);
        Assert.Equal("hello", message.Text);
    }

    [Fact]
    public void PreservesUndecodableWebSocketRecordAsWarning()
    {
        using var saz = Fixture(
            ("raw/5_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/5_w.txt", Bytes(
                "Fiddler-WebSocket: 1\r\n\r\nUnknown-Header: value\r\n\r\nbinary tail")));

        var report = new SazParser().Parse(saz);

        var message = Assert.Single(report.WebSocketMessages);
        Assert.False(message.IsDecoded);
        Assert.Equal("Undecoded", message.Type);
        Assert.Contains("no Request-Length or Response-Length", message.Warning, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, warning => warning.Contains("cannot be safely resynchronized", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsWebSocketSourceOrderWhenTimestampsTie()
    {
        const string timestamp = "2024-05-01T12:00:00.0000000Z";
        var frame = new byte[] { 0x81, 0x02, (byte)'o', (byte)'k' };
        using var saz = Fixture(
            ("raw/10_c.txt", Bytes("GET /first HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/10_w.txt", WebSocketCapture(("Response-Length", "1", timestamp, frame))),
            ("raw/2_c.txt", Bytes("GET /second HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/2_w.txt", WebSocketCapture(("Response-Length", "1", timestamp, frame))));

        var report = new SazParser().Parse(saz);

        Assert.Equal(["10", "2"], report.WebSocketMessages.Select(message => message.SessionId));
    }

    [Fact]
    public void TruncatedWebSocketPayloadKeepsValidUtf8Prefix()
    {
        var text = new string('a', (1024 * 1024) - 10) + "\u20AC" + new string('b', 5_000);
        var frame = UnmaskedTextFrame(text);
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(
                ("Response-Length", "1", "2024-05-01T12:00:00Z", frame))));

        var message = Assert.Single(new SazParser().Parse(saz).WebSocketMessages);

        Assert.StartsWith(new string('a', 32), message.Preview, StringComparison.Ordinal);
        Assert.NotNull(message.Text);
        Assert.Equal(1_048_574, message.Text.Length);
        Assert.Contains("\u20AC", message.Text, StringComparison.Ordinal);
        Assert.EndsWith(new string('b', 7), message.Text, StringComparison.Ordinal);
        Assert.Contains("retained payload is limited", message.Warning, StringComparison.Ordinal);
        Assert.True(message.IsPayloadTruncated);
        Assert.DoesNotContain("not valid UTF-8", message.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateTruncationKeepsSearchableValidUtf8Prefix()
    {
        var firstPayload = Bytes(new string('a', 600_000));
        var secondPayload = Bytes(new string('b', 448_574) + "\u20AC" + "omitted");
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(
                ("Response-Length", "1", "2024-05-01T12:00:00Z", ExtendedFrame(1, final: false, firstPayload)),
                ("Response-Length", "2", "2024-05-01T12:00:01Z", ExtendedFrame(0, final: true, secondPayload)))));

        var message = Assert.Single(new SazParser().Parse(saz).WebSocketMessages);

        Assert.True(message.IsPayloadTruncated);
        Assert.NotNull(message.Text);
        Assert.Equal(1_048_574, message.Text.Length);
        Assert.StartsWith(new string('a', 32), message.Text, StringComparison.Ordinal);
        Assert.EndsWith(new string('b', 32), message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u20AC", message.Text, StringComparison.Ordinal);
        Assert.Contains("retained for display/copy is limited", message.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("not valid UTF-8", message.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ReassemblesFragmentedTextWithInterleavedControlFrames()
    {
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(
                ("Response-Length", "1", "2024-05-01T12:00:00Z", Frame(1, false, Bytes("hel"))),
                ("Response-Length", "2", "2024-05-01T12:00:01Z", Frame(9, true, Bytes("probe"))),
                ("Response-Length", "3", "2024-05-01T12:00:02Z", Frame(10, true, Bytes("probe"))),
                ("Response-Length", "4", "2024-05-01T12:00:03Z", Frame(0, true, Bytes("lo"))))));

        var messages = new SazParser().Parse(saz).WebSocketMessages;

        var text = Assert.Single(messages.Where(message => message.Type == "Text"));
        Assert.Equal("hello", text.Text);
        Assert.True(text.IsComplete);
        Assert.True(text.IsFragmented);
        Assert.Equal([0, 3], text.Frames.Select(frame => frame.RecordIndex));
        var ping = Assert.Single(messages.Where(message => message.Type == "Ping"));
        Assert.Equal("probe", Encoding.UTF8.GetString(ping.Payload.Span));
        Assert.Single(messages.Where(message => message.Type == "Pong"));
    }

    [Fact]
    public void KeepsFragmentationIndependentByDirection()
    {
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(
                ("Request-Length", "1", "2024-05-01T12:00:00Z", Frame(1, false, Bytes("up-"), true)),
                ("Response-Length", "2", "2024-05-01T12:00:01Z", Frame(1, true, Bytes("down"))),
                ("Request-Length", "3", "2024-05-01T12:00:02Z", Frame(0, true, Bytes("done"), true)))));

        var messages = new SazParser().Parse(saz).WebSocketMessages;

        Assert.Equal("up-done", Assert.Single(messages.Where(message => message.Direction == "Client")).Text);
        Assert.Equal("down", Assert.Single(messages.Where(message => message.Direction == "Server")).Text);
    }

    [Fact]
    public void PreservesInvalidFragmentSequencesAndInvalidUtf8()
    {
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(
                ("Response-Length", "1", "2024-05-01T12:00:00Z", Frame(0, true, Bytes("orphan"))),
                ("Response-Length", "2", "2024-05-01T12:00:01Z", Frame(1, false, Bytes("unfinished"))),
                ("Response-Length", "3", "2024-05-01T12:00:02Z", Frame(2, true, [0x01, 0x02])),
                ("Response-Length", "4", "2024-05-01T12:00:03Z", Frame(1, true, [0xC3, 0x28])),
                ("Request-Length", "5", "2024-05-01T12:00:04Z", Frame(2, false, [0x03], true)))));

        var messages = new SazParser().Parse(saz).WebSocketMessages;

        Assert.Contains("orphan continuation", messages[0].Warning, StringComparison.Ordinal);
        Assert.False(messages[0].IsComplete);
        Assert.Contains("starts a new binary message", messages[1].Warning, StringComparison.Ordinal);
        Assert.False(messages[1].IsComplete);
        Assert.Equal("Binary", messages[2].Type);
        Assert.Contains("not valid UTF-8", messages[3].Warning, StringComparison.Ordinal);
        Assert.Null(messages[3].Text);
        Assert.Contains("capture ended", messages[4].Warning, StringComparison.Ordinal);
        Assert.False(messages[4].IsComplete);
    }

    [Fact]
    public void DoesNotDecodePayloadWhenRsvExtensionBitsAreSet()
    {
        var frame = Frame(1, true, Bytes("""{"looks":"json"}"""));
        frame[0] |= 0x40;
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(
                ("Response-Length", "1", "2024-05-01T12:00:00Z", frame))));

        var message = Assert.Single(new SazParser().Parse(saz).WebSocketMessages);

        Assert.False(message.IsDecoded);
        Assert.Null(message.Text);
        Assert.Contains("RSV bits are set", message.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsAggregateRetainedWebSocketPayloadPerEntry()
    {
        var records = Enumerable.Range(0, 17)
            .Select(index => (
                "Response-Length",
                (index + 1).ToString(),
                $"2024-05-01T12:00:{index:00}Z",
                UnmaskedBinaryFrame(1024 * 1024)))
            .ToArray();
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(records)));

        var messages = new SazParser().Parse(saz).WebSocketMessages;

        Assert.Equal(17, messages.Count);
        Assert.True(messages.Sum(message => message.Payload.Length) <= 16 * 1024 * 1024);
        Assert.All(messages.SelectMany(message => message.Frames), frame => Assert.True(frame.Payload.IsEmpty));
        Assert.True(messages[^1].IsPayloadTruncated);
        Assert.Contains("retained payload is limited to 0 bytes", messages[^1].Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatesTwoByteCloseCodeAndKeepsKnownDirection()
    {
        var invalidClose = new byte[] { 0x88, 0x02, 0x03, 0xED };
        var truncated = new byte[] { 0x81 };
        using var saz = Fixture(
            ("raw/1_c.txt", Bytes("GET /socket HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_w.txt", WebSocketCapture(
                ("Response-Length", "1", "2024-05-01T12:00:00Z", invalidClose),
                ("Response-Length", "2", "2024-05-01T12:00:01Z", truncated))));

        var messages = new SazParser().Parse(saz).WebSocketMessages;

        Assert.Contains("invalid or reserved status code 1005", messages[0].Warning, StringComparison.Ordinal);
        Assert.Equal("Server", messages[1].Direction);
        Assert.Equal("Undecoded", messages[1].Type);
    }

    private static string Metadata(string timestamp, string clientIp, string clientPort) =>
        $"""
         <Session>
           <SessionTimers ClientBeginRequest="{timestamp}" ServerGotRequest="{timestamp}" />
           <SessionFlag N="x-clientip" V="{clientIp}" />
           <SessionFlag N="x-clientport" V="{clientPort}" />
           <SessionFlag N="x-hostip" V="192.0.2.10" />
           <SessionFlag N="x-serverport" V="443" />
         </Session>
         """;

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

    private static byte[] WebSocketCapture(
        params (string LengthHeader, string Id, string DoneRead, byte[] Frame)[] records)
    {
        using var stream = new MemoryStream();
        Write(stream, "Fiddler-WebSocket: 1\r\n\r\n");
        foreach (var record in records)
        {
            Write(
                stream,
                $"{record.LengthHeader}: {record.Frame.Length}\r\nID: {record.Id}\r\nBitFlags: 0\r\nDoneRead: {record.DoneRead}\r\n\r\n");
            stream.Write(record.Frame);
            Write(stream, "\r\n");
        }
        return stream.ToArray();
    }

    private static byte[] MaskedTextFrame(string text, byte[] key)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[2 + key.Length + payload.Length];
        frame[0] = 0x81;
        frame[1] = (byte)(0x80 | payload.Length);
        key.CopyTo(frame, 2);
        for (var i = 0; i < payload.Length; i++)
        {
            frame[6 + i] = (byte)(payload[i] ^ key[i % 4]);
        }
        return frame;
    }

    private static byte[] UnmaskedTextFrame(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = new byte[10 + payload.Length];
        frame[0] = 0x81;
        frame[1] = 127;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)payload.Length);
        payload.CopyTo(frame, 10);
        return frame;
    }

    private static byte[] UnmaskedBinaryFrame(int length)
    {
        var frame = new byte[10 + length];
        frame[0] = 0x82;
        frame[1] = 127;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)length);
        return frame;
    }

    private static byte[] Frame(int opcode, bool final, byte[] payload, bool masked = false)
    {
        Assert.True(payload.Length < 126);
        var key = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var frame = new byte[2 + (masked ? 4 : 0) + payload.Length];
        frame[0] = (byte)((final ? 0x80 : 0) | opcode);
        frame[1] = (byte)((masked ? 0x80 : 0) | payload.Length);
        var offset = 2;
        if (masked)
        {
            key.CopyTo(frame, offset);
            offset += key.Length;
        }
        for (var index = 0; index < payload.Length; index++)
        {
            frame[offset + index] = masked ? (byte)(payload[index] ^ key[index % key.Length]) : payload[index];
        }
        return frame;
    }

    private static byte[] ExtendedFrame(int opcode, bool final, byte[] payload)
    {
        var frame = new byte[10 + payload.Length];
        frame[0] = (byte)((final ? 0x80 : 0) | opcode);
        frame[1] = 127;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)payload.Length);
        payload.CopyTo(frame, 10);
        return frame;
    }

    private static void Write(Stream stream, string text) => stream.Write(Bytes(text));
}
