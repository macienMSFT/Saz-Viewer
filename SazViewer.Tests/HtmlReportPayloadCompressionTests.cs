using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SazViewer.Core;
using Xunit.Abstractions;

namespace SazViewer.Tests;

public sealed class HtmlReportPayloadCompressionTests(ITestOutputHelper output)
{
    [Fact]
    public void WebSocketEnvelopeKeepsBoundedPrefixWhenJsonEscapingExpandsContent()
    {
        var report = new SazReport { SourceName = "websocket-escaping.saz" };
        report.Sessions.Add(new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "GET",
            Url = "wss://example.test/socket",
            StatusCode = 101
        });
        var text = new string('\u0001', 900_000);
        var payload = Encoding.UTF8.GetBytes(text);
        for (var index = 0; index < 4; index++)
        {
            report.WebSocketMessages.Add(new WebSocketMessage
            {
                SessionId = "1",
                MessageIndex = index,
                RecordIndex = index,
                Direction = "Server",
                Type = "Text",
                PayloadLength = payload.Length,
                Preview = "control text",
                Text = text,
                IsComplete = true,
                IsDecoded = true,
                Payload = payload
            });
        }

        var html = new HtmlReportGenerator().Generate(report);
        var envelope = Assert.Single(ExtractEnvelopes(html)
            .Where(item => item.Type == "websocket-session"));
        using var decoded = JsonDocument.Parse(envelope.DecodedJson);

        Assert.InRange(envelope.DecodedBytes, 1, 32 * 1024 * 1024);
        Assert.InRange(decoded.RootElement.GetProperty("messages").GetArrayLength(), 1, 3);
        Assert.True(decoded.RootElement.GetProperty("omittedMessages").GetInt32() > 0);
    }

    [Fact]
    public void SharedEnvelopeRoundTripsUnicodeInjectionAndKeepsRepeatedPayloadsCompact()
    {
        const string hostile = "</script><svg onload=globalThis.pwned=true>雪";
        var jsonBody = JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(0, 600)
                .Select(index => new { index, name = hostile, repeated = "alpha-alpha-alpha-alpha" })
        });
        var xmlBody = "<root>"
            + string.Concat(Enumerable.Range(0, 600)
                .Select(index => $"<item index=\"{index}\">{hostile.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)}</item>"))
            + "</root>";
        var protocolRoot = new MapiNode(
            "Execute",
            MapiNodeKind.Operation,
            0,
            16000,
            null,
            Enumerable.Range(0, 2000)
                .Select(index => MapiNode.Leaf(
                    $"RepeatedField{index % 10}",
                    MapiNodeKind.Property,
                    index * 8,
                    8,
                    $"{hostile}:alpha-alpha-alpha-alpha"))
                .ToImmutableArray());
        var protocol = new MapiMessageParse(
            MapiDirection.Request,
            protocolRoot,
            ImmutableArray<string>.Empty,
            true,
            16000,
            16000);
        var session = new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/compressed",
            StatusCode = 200,
            Request = Message("POST /compressed HTTP/1.1", "application/json", jsonBody),
            Response = Message("HTTP/1.1 200 OK", "application/xml", xmlBody),
        };
        session.Mapi = new MapiSession(
            "1",
            0,
            MapiEndpoint.Mailbox,
            "Execute",
            "0",
            false,
            protocol,
            null,
            ImmutableArray<string>.Empty);
        var report = new SazReport { SourceName = "payload-accounting.saz" };
        report.Sessions.Add(session);
        report.Sessions.Add(new HttpSession
        {
            Id = "2",
            ArchiveOrder = 1,
            Method = "GET",
            Url = "wss://example.test/socket",
            StatusCode = 101
        });
        var webSocketText = JsonSerializer.Serialize(new
        {
            kind = "repeat",
            values = Enumerable.Repeat("alpha-alpha-alpha-alpha", 200),
            attack = hostile
        });
        var webSocketBytes = Encoding.UTF8.GetBytes(webSocketText);
        for (var index = 0; index < 120; index++)
        {
            var webSocket = new WebSocketMessage
            {
                SessionId = "2",
                MessageIndex = index,
                RecordIndex = index,
                Direction = index % 2 == 0 ? "Client" : "Server",
                Type = "Text",
                PayloadLength = webSocketBytes.Length,
                Preview = webSocketText[..Math.Min(200, webSocketText.Length)],
                Text = webSocketText,
                IsComplete = true,
                IsDecoded = true,
                Payload = webSocketBytes
            };
            webSocket.Frames.Add(new WebSocketFrame
            {
                RecordIndex = index,
                FiddlerId = index + 1,
                BitFlags = 0,
                Direction = webSocket.Direction,
                Opcode = 1,
                Type = "Text",
                Final = true,
                Masked = webSocket.Direction == "Client",
                PayloadLength = webSocketBytes.Length,
                CapturedPayloadLength = webSocketBytes.Length,
                IsDecoded = true,
                Payload = webSocketBytes
            });
            report.WebSocketMessages.Add(webSocket);
        }

        var html = new HtmlReportGenerator().Generate(report);
        var envelopes = ExtractEnvelopes(html);

        Assert.DoesNotContain("data-protocol=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-json-tree=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-xml-tree=", html, StringComparison.Ordinal);
        Assert.All(envelopes, envelope => Assert.Equal("1", envelope.Version));
        Assert.Contains(envelopes, envelope => envelope.Type == "copy-model");
        Assert.Contains(envelopes, envelope => envelope.Type == "mapi-protocol");
        Assert.Contains(envelopes, envelope => envelope.Type == "json-tree");
        Assert.Contains(envelopes, envelope => envelope.Type == "xml-tree");
        Assert.Contains(envelopes, envelope => envelope.Type == "websocket-session");

        var protocolEnvelope = Assert.Single(envelopes.Where(envelope => envelope.Type == "mapi-protocol"));
        using (var protocolJson = JsonDocument.Parse(protocolEnvelope.DecodedJson))
        {
            var first = protocolJson.RootElement.GetProperty("root").GetProperty("children")[0];
            Assert.Equal($"{hostile}:alpha-alpha-alpha-alpha", first.GetProperty("value").GetString());
        }
        var jsonEnvelope = Assert.Single(envelopes.Where(envelope => envelope.Type == "json-tree"));
        using (var jsonTree = JsonDocument.Parse(jsonEnvelope.DecodedJson))
        {
            var firstName = jsonTree.RootElement
                .GetProperty("children")[0]
                .GetProperty("children")[0]
                .GetProperty("children")[1]
                .GetProperty("value")
                .GetString();
            Assert.Equal(hostile, firstName);
        }
        var xmlEnvelope = Assert.Single(envelopes.Where(envelope => envelope.Type == "xml-tree"));
        using (var xmlTree = JsonDocument.Parse(xmlEnvelope.DecodedJson))
        {
            var firstText = xmlTree.RootElement
                .GetProperty("children")[0]
                .GetProperty("children")[0]
                .GetProperty("children")[0]
                .GetProperty("value")
                .GetString();
            Assert.Equal(hostile, firstText);
        }

        var structural = envelopes
            .Where(envelope => envelope.Type is "mapi-protocol" or "json-tree" or "xml-tree")
            .ToArray();
        var encodedCharacters = structural.Sum(envelope => envelope.EncodedCharacters);
        var decodedBytes = structural.Sum(envelope => envelope.DecodedBytes);
        Assert.True(
            encodedCharacters < decodedBytes / 2,
            $"Expected repeated structural payloads to compress materially: {encodedCharacters:N0} encoded chars vs {decodedBytes:N0} decoded bytes.");
        Assert.All(structural, envelope => Assert.Equal(envelope.DecodedBytes, Encoding.UTF8.GetByteCount(envelope.DecodedJson)));
        var webSocketEnvelope = Assert.Single(envelopes.Where(envelope => envelope.Type == "websocket-session"));
        Assert.True(
            webSocketEnvelope.EncodedCharacters < webSocketEnvelope.DecodedBytes / 4,
            $"Expected repeated WebSocket payloads to remain compressed: {webSocketEnvelope.EncodedCharacters:N0} encoded chars vs {webSocketEnvelope.DecodedBytes:N0} decoded bytes.");

        output.WriteLine($"Total HTML: {Encoding.UTF8.GetByteCount(html):N0} bytes");
        foreach (var category in envelopes.GroupBy(envelope => envelope.Type).OrderBy(group => group.Key))
        {
            output.WriteLine(
                $"{category.Key}: count={category.Count():N0}, encoded={category.Sum(item => item.EncodedCharacters):N0} chars, decoded={category.Sum(item => item.DecodedBytes):N0} bytes");
        }
    }

    private static IReadOnlyList<Envelope> ExtractEnvelopes(string html)
    {
        const string payloadMarker = "data-compressed-payload=\"";
        var envelopes = new List<Envelope>();
        var search = 0;
        while (true)
        {
            var payloadStart = html.IndexOf(payloadMarker, search, StringComparison.Ordinal);
            if (payloadStart < 0)
            {
                break;
            }
            payloadStart += payloadMarker.Length;
            var payloadEnd = html.IndexOf('"', payloadStart);
            Assert.True(payloadEnd > payloadStart);
            var tagEnd = html.IndexOf('>', payloadEnd);
            Assert.True(tagEnd > payloadEnd);
            var attributes = html[payloadEnd..tagEnd];
            var type = Attribute(attributes, "data-payload-type");
            var version = Attribute(attributes, "data-payload-version");
            var declared = int.Parse(Attribute(attributes, "data-payload-decoded-bytes"));
            var encoded = html[payloadStart..payloadEnd];
            var compressed = Convert.FromBase64String(encoded);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            gzip.CopyTo(decoded);
            var bytes = decoded.ToArray();
            Assert.Equal(declared, bytes.Length);
            envelopes.Add(new Envelope(
                type,
                version,
                encoded.Length,
                declared,
                new UTF8Encoding(false, true).GetString(bytes)));
            search = tagEnd + 1;
        }
        return envelopes;
    }

    private static string Attribute(string attributes, string name)
    {
        var marker = $"{name}=\"";
        var start = attributes.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Attribute {name} was not found.");
        start += marker.Length;
        var end = attributes.IndexOf('"', start);
        Assert.True(end > start, $"Attribute {name} was malformed.");
        return attributes[start..end];
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

    private sealed record Envelope(
        string Type,
        string Version,
        int EncodedCharacters,
        int DecodedBytes,
        string DecodedJson);
}
