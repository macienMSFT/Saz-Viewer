using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class AuthScrubberTests
{
    private static readonly string[] Canaries =
    [
        "bearer-canary-4f31d75d",
        "basic-canary-7d551faf",
        "cookie-canary-653f31ad",
        "sas-canary-d138ea29",
        "api-canary-71395b45",
        "json-canary-2d204d7d",
        "form-canary-7a57452a",
        "xml-canary-3c9ec88e",
        "multipart-canary-4b10fe7c",
        "plain-canary-7e5ba7e5",
        "websocket-canary-e43fb262",
        "mapi-canary-cf8e57bc"
    ];

    [Fact]
    public void ScrubsEveryReportSurfaceAndCompressedEnvelope()
    {
        var report = CreateReport();

        var summary = AuthScrubber.Scrub(report);
        var html = new HtmlReportGenerator().Generate(report);

        Assert.True(summary.Total >= Canaries.Length);
        Assert.Contains("Bearer", summary.Counts.Keys);
        Assert.Contains("Cookie", summary.Counts.Keys);
        Assert.Contains("AzureSAS", summary.Counts.Keys);
        Assert.Contains("This report was generated with --scrub-auth.", html, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:Bearer]", DecompressedPayloadText(html), StringComparison.Ordinal);
        Assert.Contains("[REDACTED:Cookie]", DecompressedPayloadText(html), StringComparison.Ordinal);
        Assert.Contains("[REDACTED:AzureSAS]", DecompressedPayloadText(html), StringComparison.Ordinal);
        Assert.Contains("Captured pre-decode bytes were removed by --scrub-auth", DecompressedPayloadText(html), StringComparison.Ordinal);
        Assert.DoesNotContain("data-payload-type=\"copy-model\"", html, StringComparison.Ordinal);

        AssertNoCanaries(html);
        foreach (var payload in DecompressPayloads(html))
        {
            AssertNoCanaries(payload);
        }
    }

    [Fact]
    public void ScrubbingIsOptInAndDeterministic()
    {
        var report = CreateReport();
        var before = new HtmlReportGenerator().Generate(report);

        Assert.Contains(Canaries[0], DecompressedPayloadText(before), StringComparison.Ordinal);
        Assert.Null(report.AuthScrub);

        var first = AuthScrubber.Scrub(report);
        var firstHtml = new HtmlReportGenerator().Generate(report);
        var secondReport = CreateReport();
        var second = AuthScrubber.Scrub(secondReport);
        var secondHtml = new HtmlReportGenerator().Generate(secondReport);

        Assert.Equal(first.Counts, second.Counts);
        Assert.Equal(firstHtml, secondHtml);
    }

    [Fact]
    public void SyntheticSazScrubRemovesParsedCanariesAndRetainedByteLeaks()
    {
        const string authorization = "saz-auth-canary-923d1a";
        const string cookie = "saz-cookie-canary-75cf02";
        const string query = "saz-query-canary-7b9e8a";
        const string json = "saz-json-canary-b8b302";
        const string form = "saz-form-canary-aac019";
        const string xml = "saz-xml-canary-e188fb";
        const string multipart = "saz-multipart-canary-69810b";
        const string plain = "saz-plain-canary-abd7ee";
        const string compressed = "saz-gzip-canary-042bc0";
        const string websocket = "saz-websocket-canary-cca925";
        const string mapi = "saz-mapi-canary-6df810";
        var canaries = new[]
        {
            authorization, cookie, query, json, form, xml, multipart, plain, compressed, websocket, mapi
        };
        var compressedBody = Gzip(Encoding.UTF8.GetBytes($$"""{"client_secret":"{{compressed}}"}"""));

        using var saz = Fixture(
            ("raw/1_c.txt", HttpMessageBytes(
                $"POST https://example.test/login?access_token={query} HTTP/1.1\r\n"
                + "Host: example.test\r\n"
                + $"Authorization: Bearer {authorization}\r\n"
                + $"Cookie: session={cookie}\r\n"
                + "Content-Type: application/json\r\n",
                Encoding.UTF8.GetBytes($$"""{"password":"{{json}}"}"""))),
            ("raw/1_s.txt", HttpMessageBytes(
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Encoding: gzip\r\n"
                + "Transfer-Encoding: chunked\r\n",
                Chunked(compressedBody),
                includeContentLength: false)),
            ("raw/2_c.txt", HttpMessageBytes(
                "POST https://example.test/form HTTP/1.1\r\n"
                + "Host: example.test\r\n"
                + "Content-Type: application/x-www-form-urlencoded\r\n",
                Encoding.UTF8.GetBytes($"client_secret={form}&safe=yes"))),
            ("raw/3_c.txt", HttpMessageBytes(
                "POST https://example.test/xml HTTP/1.1\r\n"
                + "Host: example.test\r\n"
                + "Content-Type: application/xml\r\n",
                Encoding.UTF8.GetBytes($"<root><password>{xml}</password><safe>yes</safe></root>"))),
            ("raw/4_c.txt", HttpMessageBytes(
                "POST https://example.test/multipart HTTP/1.1\r\n"
                + "Host: example.test\r\n"
                + "Content-Type: multipart/form-data; boundary=saz-boundary\r\n",
                Encoding.UTF8.GetBytes(
                    "--saz-boundary\r\n"
                    + "Content-Disposition: form-data; name=\"api_key\"\r\n\r\n"
                    + multipart
                    + "\r\n--saz-boundary--\r\n"))),
            ("raw/5_c.txt", HttpMessageBytes(
                "POST https://example.test/plain HTTP/1.1\r\n"
                + "Host: example.test\r\n"
                + "Content-Type: text/plain\r\n",
                Encoding.UTF8.GetBytes($"password={plain}"))),
            ("raw/6_c.txt", Bytes(
                "GET https://example.test/socket HTTP/1.1\r\n"
                + "Host: example.test\r\n"
                + "Upgrade: websocket\r\n\r\n")),
            ("raw/6_s.txt", Bytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n")),
            ("raw/6_w.txt", WebSocketCapture(
                UnmaskedTextFrame($$"""{"access_token":"{{websocket}}"}"""))));

        var report = new SazParser().Parse(saz);
        AttachSyntheticMapi(report, mapi);
        var summary = AuthScrubber.Scrub(report);
        var html = new HtmlReportGenerator().Generate(report);

        Assert.True(summary.Total >= canaries.Length);
        AssertNoCanaries(html, canaries);
        foreach (var payload in DecompressPayloads(html))
        {
            AssertNoCanaries(payload, canaries);
            AssertPayloadByteFieldsContainNoCanaries(payload, canaries);
        }
        foreach (var message in report.Sessions
                     .SelectMany(session => new[] { session.Request, session.Response })
                     .OfType<HttpMessage>())
        {
            AssertNoCanaries(message.Body.Preview, canaries);
            AssertNoCanaries(Encoding.UTF8.GetString(message.Body.CapturedBytes.Span), canaries);
            AssertNoCanaries(Encoding.UTF8.GetString(message.Body.DecodedBytes.Span), canaries);
        }
        Assert.Equal(0, report.Sessions[0].Response!.Body.CapturedBytes.Length);
        Assert.Contains("removed by --scrub-auth", report.Sessions[0].Response!.Body.DecodingStatus);
        Assert.Equal("[REDACTED:Password]", report.Mapi!.Sessions[0].Request!.Root.Children[0].Value);
    }

    [Fact]
    public void ScrubsRequiredAuthenticationSchemesHeadersAndStandaloneTokenFamilies()
    {
        var secrets = new[]
        {
            "basic-scheme-canary-5f3a07",
            "bearer-scheme-canary-14b1dc",
            "ntlm-scheme-canary-42292e",
            "negotiate-scheme-canary-0c914f",
            "kerberos-scheme-canary-626cde",
            "digest-response-canary-0d60b0",
            "digest-nonce-canary-bc5a19",
            "aws-signature-canary-b2598e",
            "azure-shared-key-canary-9fd107",
            "challenge-nonce-canary-b9a220",
            "api-header-canary-239aa3",
            "subscription-header-canary-fc8e44",
            "amazon-header-canary-cd0448",
            "google-header-canary-785ce3",
            "microsoft-header-canary-99a371",
            "functions-header-canary-d26d3b",
            "csrf-header-canary-21bc9d",
            "github_pat_11AA22BB33CC44DD55EE66FF",
            "xoxb-1234567890-abcdefghijklmnop",
            "AKIA1234567890ABCDEF",
            "AIza1234567890abcdefghijklmnop",
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJjYW5hcnkifQ.abcdef1234567890",
            "eyJhbGciOiJBMjU2S1cifQ.abcdefghij.klmnopqrst.uvwxyzABCD.efghijklmn",
            "aws-secret-access-canary-7c0a5b",
            "oauth-code-canary-95eaf0",
            "private-key-canary-7683fd",
            "saml-canary-f9d4c1",
            "webhook-canary-2f01aa"
        };
        var report = new SazReport { SourceName = "auth-families.saz" };
        var body = string.Join(
            '\n',
            secrets[17],
            secrets[18],
            secrets[19],
            secrets[20],
            secrets[21],
            secrets[22],
            $"aws_secret_access_key={secrets[23]}",
            $"code={secrets[24]}",
            $"-----BEGIN PRIVATE KEY-----\n{secrets[25]}\n-----END PRIVATE KEY-----",
            $"<Assertion>{secrets[26]}</Assertion>",
            $"https://hooks.slack.com/services/T000/B000/{secrets[27]}");
        var message = Message(
            "POST /tokens HTTP/1.1",
            "text/plain",
            body,
            [
                new("Authorization", "Basic " + secrets[0]),
                new("Authorization", "Bearer " + secrets[1]),
                new("Authorization", "NTLM " + secrets[2]),
                new("Authorization", "Negotiate " + secrets[3]),
                new("Authorization", "Kerberos " + secrets[4]),
                new("Authorization", $"Digest response=\"{secrets[5]}\", nonce=\"{secrets[6]}\""),
                new("Authorization", $"AWS4-HMAC-SHA256 Credential=test/20250101/region/service/aws4_request, Signature={secrets[7]}"),
                new("Authorization", "SharedKey account:" + secrets[8]),
                new("WWW-Authenticate", $"Digest realm=\"safe\", nonce=\"{secrets[9]}\""),
                new("X-API-Key", secrets[10]),
                new("Ocp-Apim-Subscription-Key", secrets[11]),
                new("X-Amz-Security-Token", secrets[12]),
                new("X-Goog-Api-Key", secrets[13]),
                new("X-MS-Identity-Token", secrets[14]),
                new("X-Functions-Key", secrets[15]),
                new("X-CSRF-Token", secrets[16])
            ]);
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/tokens",
            Request = message
        });

        var summary = AuthScrubber.Scrub(report);
        var serialized = new HtmlReportGenerator().Generate(report)
            + "\n"
            + DecompressedPayloadText(new HtmlReportGenerator().Generate(report));

        AssertNoCanaries(serialized, secrets);
        Assert.Contains("Bearer", summary.Counts.Keys);
        Assert.Contains("Basic", summary.Counts.Keys);
        Assert.Contains("NTLM", summary.Counts.Keys);
        Assert.Contains("Negotiate", summary.Counts.Keys);
        Assert.Contains("Kerberos", summary.Counts.Keys);
        Assert.Contains("GitHubToken", summary.Counts.Keys);
        Assert.Contains("SlackToken", summary.Counts.Keys);
        Assert.Contains("AWS", summary.Counts.Keys);
        Assert.Contains("APIKey", summary.Counts.Keys);
        Assert.Contains("JWT", summary.Counts.Keys);
        Assert.Contains("PrivateKey", summary.Counts.Keys);
        Assert.Contains("SAML", summary.Counts.Keys);
        Assert.Contains("Webhook", summary.Counts.Keys);
    }

    [Fact]
    public void FailsClosedForMalformedStructuredMultipartRawMapiUserInfoAndBinaryWebSocket()
    {
        const string malformedJson = "malformed-json-canary-a91f0c";
        const string malformedXml = "malformed-xml-canary-8fc202";
        const string multipart = "unquoted-multipart-canary-2804ef";
        const string rawMapi = "raw-mapi-canary-d45b0a";
        const string userInfo = "userinfo-canary-93e4c1";
        const string binaryWebSocket = "binary-websocket-canary-399abd";
        var canaries = new[]
        {
            malformedJson, malformedXml, multipart, rawMapi, userInfo, binaryWebSocket
        };
        var request = Message(
            $"POST https://user:{userInfo}@example.test/data HTTP/1.1",
            "application/json",
            $$"""{"client_secret":"{{malformedJson}}",}""",
            []);
        var response = Message(
            "HTTP/1.1 200 OK",
            "application/xml",
            $"<root><password>{malformedXml}</password><broken></root>",
            []);
        var report = new SazReport { SourceName = "fail-closed.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = $"https://user:{userInfo}@example.test/data",
            Request = request,
            Response = response
        });
        report.Sessions.Add(Session(
            "2",
            "POST /multipart HTTP/1.1",
            "https://example.test/multipart",
            "multipart/form-data; boundary=boundary",
            "--boundary\r\n"
            + "Content-Disposition: form-data; name=client_secret\r\n\r\n"
            + multipart
            + "\r\n--boundary--\r\n",
            []));
        report.WebSocketMessages.Add(new WebSocketMessage
        {
            SessionId = "1",
            MessageIndex = 0,
            RecordIndex = 0,
            Direction = "Server",
            Type = "Binary",
            PayloadLength = binaryWebSocket.Length,
            Preview = binaryWebSocket,
            IsBinary = true,
            IsDecoded = true,
            IsComplete = true,
            Payload = Encoding.UTF8.GetBytes(binaryWebSocket)
        });
        AttachSyntheticMapi(report, rawMapi, MapiNodeKind.Raw, "Unparsed operation data");

        AuthScrubber.Scrub(report);
        var html = new HtmlReportGenerator().Generate(report);

        AssertNoCanaries(html, canaries);
        foreach (var payload in DecompressPayloads(html))
        {
            AssertNoCanaries(payload, canaries);
            AssertPayloadByteFieldsContainNoCanaries(payload, canaries);
        }
        Assert.Contains("[REDACTED:UserInfo]@", report.Sessions[0].Url);
        Assert.Contains("[REDACTED:UserInfo]@", report.Sessions[0].Request!.StartLine);
        Assert.Equal(0, report.Sessions[0].Request!.Body.CapturedBytes.Length);
        Assert.Equal(0, report.Sessions[0].Response!.Body.DecodedBytes.Length);
        Assert.Contains("removed by --scrub-auth", report.Sessions[0].Request!.Body.Preview);
        Assert.Contains("[REDACTED:Multipart]", report.Sessions[1].Request!.Body.Preview);
        Assert.Equal("[REDACTED:BinaryPayload]", report.Mapi!.Sessions[0].Request!.Root.Children[0].Value);
        Assert.Equal(0, report.WebSocketMessages[0].Payload.Length);
        Assert.Contains("removed by --scrub-auth", report.WebSocketMessages[0].Preview);
    }

    [Fact]
    public void PropagatesKnownHeaderSecretsIntoOtherwiseUnsuspiciousValues()
    {
        const string canary = "propagated-cookie-canary-b370e2";
        var report = new SazReport { SourceName = "propagation.saz" };
        report.Sessions.Add(Session(
            "1",
            "POST /data HTTP/1.1",
            "https://example.test/data",
            "application/json",
            $$"""{"note":"{{canary}}"}""",
            [new("Cookie", "session=" + canary)]));

        AuthScrubber.Scrub(report);

        Assert.DoesNotContain(canary, report.Sessions[0].Request!.Body.Preview, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:Cookie]", report.Sessions[0].Request!.Body.Preview);
    }

    private static SazReport CreateReport()
    {
        var report = new SazReport { SourceName = "synthetic.saz" };
        report.Sessions.Add(Session(
            "1",
            "POST /resource?sig=sas-canary-d138ea29&sv=2024-01-01&code=api-canary-71395b45 HTTP/1.1",
            "https://example.test/resource?sig=sas-canary-d138ea29&sv=2024-01-01&code=api-canary-71395b45",
            "application/json",
            """{"client_secret":"json-canary-2d204d7d","access_token":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJjYW5hcnkifQ.signature123456"}""",
            [
                new("Authorization", "Bearer bearer-canary-4f31d75d"),
                new("Authorization", "Basic basic-canary-7d551faf"),
                new("Cookie", "session=cookie-canary-653f31ad; harmless=also-secret"),
                new("X-API-Key", "api-canary-71395b45")
            ]));
        report.Sessions.Add(Session(
            "2",
            "POST /form HTTP/1.1",
            "https://example.test/form",
            "application/x-www-form-urlencoded",
            "username=safe&password=form-canary-7a57452a",
            [new("Set-Cookie", "auth=cookie-canary-653f31ad; Path=/; HttpOnly")]));
        report.Sessions.Add(Session(
            "3",
            "POST /xml HTTP/1.1",
            "https://example.test/xml",
            "application/xml",
            "<root><password>xml-canary-3c9ec88e</password></root>",
            []));
        report.Sessions.Add(Session(
            "4",
            "POST /multipart HTTP/1.1",
            "https://example.test/multipart",
            "multipart/form-data; boundary=boundary",
            "--boundary\r\nContent-Disposition: form-data; name=\"client_secret\"\r\n\r\nmultipart-canary-4b10fe7c\r\n--boundary--\r\n",
            []));
        report.Sessions.Add(Session(
            "5",
            "POST /plain HTTP/1.1",
            "https://example.test/plain",
            "text/plain",
            "password=plain-canary-7e5ba7e5",
            []));
        report.Sessions.Add(EncodedSession());

        var webSocketSession = Session(
            "7",
            "GET /hub?access_token=websocket-canary-e43fb262 HTTP/1.1",
            "wss://example.test/hub?access_token=websocket-canary-e43fb262",
            null,
            string.Empty,
            []);
        report.Sessions.Add(webSocketSession);
        report.WebSocketMessages.Add(new WebSocketMessage
        {
            SessionId = "7",
            MessageIndex = 0,
            RecordIndex = 0,
            Direction = "Client",
            Type = "Text",
            PayloadLength = 57,
            Preview = """{"accessToken":"websocket-canary-e43fb262"}""",
            Text = """{"accessToken":"websocket-canary-e43fb262"}""",
            IsComplete = true,
            IsDecoded = true,
            Payload = Encoding.UTF8.GetBytes("""{"accessToken":"websocket-canary-e43fb262"}""")
        });

        var mapiRoot = new MapiNode(
            "Execute",
            MapiNodeKind.Structure,
            0,
            10,
            null,
            [MapiNode.Leaf("AuthenticationToken", MapiNodeKind.Field, 1, 9, "mapi-canary-cf8e57bc")]);
        var parse = new MapiMessageParse(
            MapiDirection.Request,
            mapiRoot,
            ImmutableArray<string>.Empty,
            true,
            10,
            10);
        var mapiSession = new MapiSession(
            "1",
            0,
            MapiEndpoint.Mailbox,
            "Execute",
            null,
            false,
            parse,
            null,
            ImmutableArray<string>.Empty);
        var sessions = ImmutableArray.Create(mapiSession);
        report.Mapi = new MapiCapture(
            sessions,
            sessions.ToImmutableDictionary(session => session.HttpSessionId, StringComparer.Ordinal),
            new MapiCoverage(1, 1, 0, ["Execute"], [], [], []));
        report.Sessions[0].Mapi = mapiSession;
        return report;
    }

    private static HttpSession Session(
        string id,
        string startLine,
        string url,
        string? contentType,
        string body,
        IReadOnlyList<HttpHeader> headers)
    {
        var message = Message(startLine, contentType, body, headers);
        return new HttpSession
        {
            Id = id,
            ArchiveOrder = int.Parse(id),
            Method = startLine.Split(' ')[0],
            Url = url,
            Request = message,
            RequestBytes = body.Length
        };
    }

    private static HttpSession EncodedSession()
    {
        const string text = """{"password":"plain-canary-7e5ba7e5"}""";
        var decoded = Encoding.UTF8.GetBytes(text);
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(decoded);
            }
            compressed = output.ToArray();
        }
        var message = new HttpMessage
        {
            StartLine = "POST /compressed HTTP/1.1",
            Body = new BodyPreview
            {
                Length = decoded.Length,
                CapturedLength = compressed.Length,
                Preview = text,
                RemovedEncodings = ["content: gzip"],
                CapturedBytes = compressed,
                DecodedBytes = decoded,
                DecodingStatus = "Decoded in wire-removal order: content: gzip."
            }
        };
        message.Headers.Add(new HttpHeader("Content-Type", "application/json"));
        message.Headers.Add(new HttpHeader("Content-Encoding", "gzip"));
        return new HttpSession
        {
            Id = "6",
            ArchiveOrder = 6,
            Method = "POST",
            Url = "https://example.test/compressed",
            Request = message,
            RequestBytes = compressed.Length
        };
    }

    private static HttpMessage Message(
        string startLine,
        string? contentType,
        string body,
        IReadOnlyList<HttpHeader> headers)
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
        if (contentType is not null)
        {
            message.Headers.Add(new HttpHeader("Content-Type", contentType));
        }
        message.Headers.AddRange(headers);
        return message;
    }

    private static void AttachSyntheticMapi(
        SazReport report,
        string canary,
        MapiNodeKind kind = MapiNodeKind.Field,
        string name = "Password")
    {
        var session = report.Sessions[0];
        var root = new MapiNode(
            "Execute",
            MapiNodeKind.Structure,
            0,
            10,
            null,
            [MapiNode.Leaf(name, kind, 1, 9, canary)]);
        var parse = new MapiMessageParse(
            MapiDirection.Request,
            root,
            ImmutableArray<string>.Empty,
            true,
            10,
            10);
        var mapiSession = new MapiSession(
            session.Id,
            session.ArchiveOrder,
            MapiEndpoint.Mailbox,
            "Execute",
            null,
            false,
            parse,
            null,
            ImmutableArray<string>.Empty);
        var sessions = ImmutableArray.Create(mapiSession);
        report.Mapi = new MapiCapture(
            sessions,
            sessions.ToImmutableDictionary(item => item.HttpSessionId, StringComparer.Ordinal),
            new MapiCoverage(1, 1, 0, ["Execute"], [], [], []));
        session.Mapi = mapiSession;
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

    private static byte[] HttpMessageBytes(
        string headers,
        byte[] body,
        bool includeContentLength = true)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(headers));
        if (includeContentLength)
        {
            stream.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n"));
        }
        stream.Write(Encoding.ASCII.GetBytes("\r\n"));
        stream.Write(body);
        return stream.ToArray();
    }

    private static byte[] Gzip(byte[] value)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(value);
        }
        return stream.ToArray();
    }

    private static byte[] Chunked(byte[] value)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes($"{value.Length:X}\r\n"));
        stream.Write(value);
        stream.Write(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"));
        return stream.ToArray();
    }

    private static byte[] WebSocketCapture(byte[] frame)
    {
        using var stream = new MemoryStream();
        stream.Write(Bytes("Fiddler-WebSocket: 1\r\n\r\n"));
        stream.Write(Bytes(
            $"Response-Length: {frame.Length}\r\n"
            + "ID: 1\r\n"
            + "BitFlags: 0\r\n"
            + "DoneRead: 2025-01-02T03:04:05.0000000Z\r\n\r\n"));
        stream.Write(frame);
        stream.Write(Bytes("\r\n"));
        return stream.ToArray();
    }

    private static byte[] UnmaskedTextFrame(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        Assert.InRange(payload.Length, 0, 125);
        return [(byte)0x81, (byte)payload.Length, .. payload];
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static IEnumerable<string> DecompressPayloads(string html)
    {
        foreach (Match match in Regex.Matches(html, "data-compressed-payload=\"([A-Za-z0-9+/=]+)\""))
        {
            using var input = new MemoryStream(Convert.FromBase64String(match.Groups[1].Value));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            yield return reader.ReadToEnd();
        }
    }

    private static string DecompressedPayloadText(string html) =>
        string.Join('\n', DecompressPayloads(html));

    private static void AssertNoCanaries(string value)
    {
        foreach (var canary in Canaries)
        {
            Assert.DoesNotContain(canary, value, StringComparison.Ordinal);
        }
    }

    private static void AssertNoCanaries(string value, IEnumerable<string> canaries)
    {
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, value, StringComparison.Ordinal);
        }
    }

    private static void AssertPayloadByteFieldsContainNoCanaries(
        string payload,
        IEnumerable<string> canaries)
    {
        using var document = JsonDocument.Parse(payload);
        Inspect(document.RootElement);

        void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.NameEquals("captured") || property.NameEquals("decoded"))
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is { Length: > 0 } base64)
                    {
                        AssertNoCanaries(
                            Encoding.UTF8.GetString(Convert.FromBase64String(base64)),
                            canaries);
                    }
                    else
                    {
                        Inspect(property.Value);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Inspect(item);
                }
            }
        }
    }
}
