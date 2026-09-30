using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class MapiParserTests
{
    [Fact]
    public void ParsesConnectRequestResponseAndAdditionalHeaders()
    {
        using var requestBody = new MemoryStream();
        WriteAsciiZ(requestBody, "/o=Example/ou=Users/cn=User");
        WriteUInt32(requestBody, 1);
        WriteUInt32(requestBody, 1252);
        WriteUInt32(requestBody, 0x0409);
        WriteUInt32(requestBody, 0x0409);
        WriteUInt32(requestBody, 0);

        using var responseBody = new MemoryStream();
        responseBody.Write(Encoding.ASCII.GetBytes("PROCESSING\r\nX-Server: test\r\n\r\n"));
        WriteUInt32(responseBody, 0);
        WriteUInt32(responseBody, 0);
        WriteUInt32(responseBody, 120);
        WriteUInt32(responseBody, 3);
        WriteUInt32(responseBody, 500);
        WriteAsciiZ(responseBody, "/o=Example");
        WriteUnicodeZ(responseBody, "Mailbox User");
        WriteUInt32(responseBody, 0);

        using var saz = Fixture(
            ("raw/1_c.txt", Http(
                "POST /mapi/emsmdb HTTP/1.1",
                requestBody.ToArray(),
                ("Host", "example.test"),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Connect"))),
            ("raw/1_s.txt", Http(
                "HTTP/1.1 200 OK",
                responseBody.ToArray(),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var report = new SazParser().Parse(saz);
        var session = Assert.Single(report.Sessions);
        var mapi = Assert.IsType<MapiSession>(session.Mapi);

        Assert.Equal(MapiEndpoint.Mailbox, mapi.Endpoint);
        Assert.Equal("Connect", mapi.RequestType);
        Assert.Equal("Mailbox User", Find(mapi.Response!.Root, "DisplayName").Value);
        Assert.Equal("PROCESSING", Find(mapi.Response.Root, "MetaTag").Value);
        Assert.Equal("X-Server: test", Find(mapi.Response.Root, "AdditionalHeader").Value);
        Assert.True(mapi.Request!.Complete);
        Assert.True(mapi.Response.Complete);
        Assert.Single(report.Mapi!.Sessions);
    }

    [Fact]
    public void ParsesExecuteExtendedBufferRopFramingAndHandles()
    {
        byte[] ropPayload = [5, 0, 0xFE, 0, 0, 0x44, 0x33, 0x22, 0x11];
        var extended = ExtendedBuffer(ropPayload, flags: 0x0004);
        using var requestBody = new MemoryStream();
        WriteUInt32(requestBody, 0);
        WriteUInt32(requestBody, (uint)extended.Length);
        requestBody.Write(extended);
        WriteUInt32(requestBody, 4096);
        WriteUInt32(requestBody, 0);

        using var saz = Fixture(
            ("raw/2_c.txt", Http(
                "POST /mapi/emsmdb HTTP/1.1",
                requestBody.ToArray(),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"))));

        var request = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Request!;

        Assert.Equal("RopLogon", Find(request.Root, "RopId").Value!.Split('(')[1].TrimEnd(')'));
        Assert.Equal("0x11223344", Find(request.Root, "[0]").Value);
        Assert.Contains(request.Warnings, warning => warning.Contains("individual ROP fields", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0, 0x41, 0x42, 0x43 }, 3, "ABC")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, 0x41, 0x42, 0x09, 0 }, 6, "ABABAB")]
    public void SafelyDecodesDocumentedDirect2Lz77Vectors(byte[] encoded, int length, string expected)
    {
        Assert.True(ExtendedBufferParser.TryDecompressLz77(encoded, length, out var decoded, out var error), error);
        Assert.Equal(expected, Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void RejectsMalformedLz77Transactionally()
    {
        Assert.False(
            ExtendedBufferParser.TryDecompressLz77(
                [0, 0, 0, 0x80, 0, 0],
                4,
                out var decoded,
                out var error));

        Assert.Empty(decoded);
        Assert.Contains("precedes", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesUnknownMapiMethodAsBoundedRawWithWarning()
    {
        using var saz = Fixture(
            ("raw/3_c.txt", Http(
                "POST /mapi/custom HTTP/1.1",
                [1, 2, 3, 4],
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "FutureOperation"))));

        var request = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Request!;

        Assert.False(request.Complete);
        Assert.Equal(MapiNodeKind.Raw, Find(request.Root, "Unparsed operation data").Kind);
        Assert.Contains(request.Warnings, warning => warning.Contains("not implemented", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Connect")]
    [InlineData("Execute")]
    [InlineData("Disconnect")]
    [InlineData("NotificationWait")]
    [InlineData("GetMailboxUrl")]
    [InlineData("GetAddressBookUrl")]
    [InlineData("Bind")]
    [InlineData("Unbind")]
    [InlineData("CompareMIds")]
    [InlineData("DNToMId")]
    [InlineData("GetMatches")]
    [InlineData("GetPropList")]
    [InlineData("GetProps")]
    [InlineData("GetSpecialTable")]
    [InlineData("GetTemplateInfo")]
    [InlineData("ModLinkAtt")]
    [InlineData("ModProps")]
    [InlineData("QueryRows")]
    [InlineData("QueryColumns")]
    [InlineData("ResolveNames")]
    [InlineData("ResortRestriction")]
    [InlineData("SeekEntries")]
    [InlineData("UpdateStat")]
    public void DetectsAllUpstreamMapiHttpRequestTypes(string requestType)
    {
        using var saz = Fixture(
            ("raw/4_c.txt", Http(
                "POST /mapi HTTP/1.1",
                [],
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", requestType))));

        var session = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi;

        Assert.NotNull(session);
        Assert.Equal(requestType, session.RequestType);
    }

    [Fact]
    public void ParsesMapiFromFullyDecodedHttpEntityBytes()
    {
        using var body = new MemoryStream();
        WriteAsciiZ(body, "user");
        WriteUInt32(body, 0);
        WriteUInt32(body, 1252);
        WriteUInt32(body, 0);
        WriteUInt32(body, 0);
        WriteUInt32(body, 0);
        var compressed = Gzip(body.ToArray());
        using var saz = Fixture(
            ("raw/5_c.txt", Http(
                "POST /mapi HTTP/1.1",
                compressed,
                ("Content-Type", "application/mapi-http"),
                ("Content-Encoding", "gzip"),
                ("X-RequestType", "Connect"))));

        var session = Assert.Single(new SazParser().Parse(saz).Sessions);

        Assert.True(session.Request!.Body.WasDecoded);
        Assert.Equal("user", Find(session.Mapi!.Request!.Root, "UserDn").Value);
    }

    [Fact]
    public void RejectsDeclaredLz77OutputBeyondProtocolLimit()
    {
        Assert.False(
            ExtendedBufferParser.TryDecompressLz77(
                [],
                MapiParseLimits.MaxPayloadBytes + 1,
                out var decoded,
                out var error));

        Assert.Empty(decoded);
        Assert.Contains("safe limit", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesOperationFieldsWhenMapiErrorCodeIsNonzero()
    {
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        using var response = new MemoryStream();
        response.Write(Encoding.ASCII.GetBytes("\r\n"));
        WriteUInt32(response, 0);
        WriteUInt32(response, 5);
        response.Write(guid.ToByteArray());
        WriteUInt32(response, 0);
        using var saz = Fixture(
            ("raw/6_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                [],
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Bind"))),
            ("raw/6_s.txt", Http(
                "HTTP/1.1 200 OK",
                response.ToArray(),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var parsed = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Response!;

        Assert.Equal(guid.ToString(), Find(parsed.Root, "ServerGuid").Value);
        Assert.True(parsed.Complete);
    }

    [Fact]
    public void ParsesStatDeltaAsSignedInteger()
    {
        using var request = new MemoryStream();
        WriteUInt32(request, 0);
        request.WriteByte(1);
        for (var index = 0; index < 9; index++)
        {
            WriteUInt32(request, index == 3 ? uint.MaxValue : 0);
        }
        WriteUInt32(request, 0);
        using var saz = Fixture(
            ("raw/7_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                request.ToArray(),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Bind"))));

        var parsed = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Request!;

        Assert.Equal("-1", Find(parsed.Root, "Delta").Value);
    }

    [Fact]
    public void CompletesPreviouslyMissingNspiMethodEnvelopes()
    {
        var cases = new (string Method, byte[] Request, byte[] Response)[]
        {
            ("GetMatches", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(
                    stream =>
                    {
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                    })),
            ("ModProps", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(_ => { })),
            ("SeekEntries", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(
                    stream =>
                    {
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                    })),
            ("GetProps", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(
                    stream =>
                    {
                        WriteUInt32(stream, 1252);
                        stream.WriteByte(0);
                    })),
            ("GetSpecialTable", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(
                    stream =>
                    {
                        WriteUInt32(stream, 1252);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                    })),
            ("GetTemplateInfo", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 1252);
                    WriteUInt32(stream, 0x409);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(
                    stream =>
                    {
                        WriteUInt32(stream, 1252);
                        stream.WriteByte(0);
                    })),
            ("QueryRows", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(
                    stream =>
                    {
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                    })),
            ("ResolveNames", BuildBody(
                stream =>
                {
                    WriteUInt32(stream, 0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    stream.WriteByte(0);
                    WriteUInt32(stream, 0);
                }),
                BuildResponse(
                    stream =>
                    {
                        WriteUInt32(stream, 1252);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                    }))
        };

        foreach (var (method, request, response) in cases)
        {
            using var saz = Fixture(
                ("raw/8_c.txt", Http(
                    "POST /mapi/nspi HTTP/1.1",
                    request,
                    ("Content-Type", "application/mapi-http"),
                    ("X-RequestType", method))),
                ("raw/8_s.txt", Http(
                    "HTTP/1.1 200 OK",
                    response,
                    ("Content-Type", "application/mapi-http"),
                    ("X-ResponseCode", "0"))));

            var mapi = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!;

            Assert.True(mapi.Request!.Complete, $"{method} request was partial: {string.Join("; ", mapi.Request.Warnings)}");
            Assert.True(mapi.Response!.Complete, $"{method} response was partial: {string.Join("; ", mapi.Response.Warnings)}");
        }
    }

    [Fact]
    public void ParsesTypedAddressBookValuesAndMultivalues()
    {
        using var data = new MemoryStream();
        WriteUInt32(data, 6);
        WriteUInt16(data, 0x001F);
        WriteUInt16(data, 0x3001);
        data.WriteByte(1);
        WriteUnicodeZ(data, "Display Name");
        WriteUInt16(data, 0x0102);
        WriteUInt16(data, 0x3002);
        data.WriteByte(1);
        WriteUInt32(data, 3);
        data.Write([0x01, 0x02, 0x03]);
        WriteUInt16(data, 0x1003);
        WriteUInt16(data, 0x3003);
        data.WriteByte(1);
        WriteUInt32(data, 2);
        WriteUInt32(data, 10);
        WriteUInt32(data, 20);
        WriteUInt16(data, 0x101F);
        WriteUInt16(data, 0x3004);
        data.WriteByte(1);
        WriteUInt32(data, 2);
        data.WriteByte(1);
        WriteUnicodeZ(data, "First");
        data.WriteByte(0);
        WriteUInt16(data, 0x1102);
        WriteUInt16(data, 0x3005);
        data.WriteByte(1);
        WriteUInt32(data, 2);
        data.WriteByte(1);
        WriteUInt32(data, 2);
        data.Write([0xAA, 0xBB]);
        data.WriteByte(0);
        WriteUInt16(data, 0x000B);
        WriteUInt16(data, 0x3006);
        data.WriteByte(1);
        var reader = new MapiReader(data.ToArray());

        var node = NspiPropertyParser.ParseValueList(ref reader, "Values", new MapiNodeBudget());

        Assert.True(reader.End);
        Assert.Equal("Display Name", Find(node, "PropertyValue").Value);
        Assert.Contains("3 bytes", Flatten(node).Select(item => item.Value));
        Assert.Contains("20", Flatten(node).Select(item => item.Value));
        Assert.Contains("First", Flatten(node).Select(item => item.Value));
        Assert.Contains("2 bytes", Flatten(node).Select(item => item.Value));
        Assert.Contains("true", Flatten(node).Select(item => item.Value));
    }

    [Fact]
    public void ParsesEveryRestrictionFormIncludingSubObject()
    {
        var restrictions = new[]
        {
            new byte[] { 0x00, 0, 0, 0, 0 },
            new byte[] { 0x01, 0, 0, 0, 0 },
            new byte[] { 0x02, 0x08, 0x03, 0, 0x01, 0x30 },
            new byte[] { 0x03, 0, 0, 0, 0, 0x1E, 0, 0x01, 0x30, 0x1F, 0, 0x02, 0x30, (byte)'x', 0 },
            new byte[] { 0x04, 4, 0x03, 0, 0x01, 0x30, 0x03, 0, 0x01, 0x30, 42, 0, 0, 0 },
            new byte[] { 0x05, 4, 0x03, 0, 0x01, 0x30, 0x03, 0, 0x02, 0x30 },
            new byte[] { 0x06, 1, 0x03, 0, 0x01, 0x30, 0xFF, 0, 0, 0 },
            new byte[] { 0x07, 4, 0x03, 0, 0x01, 0x30, 4, 0, 0, 0 },
            new byte[] { 0x08, 0x03, 0, 0x01, 0x30 },
            new byte[] { 0x09, 0x0D, 0, 0x05, 0x30, 0x08, 0x03, 0, 0x01, 0x30 },
            new byte[] { 0x0A, 1, 0x1E, 0, 0x01, 0x30, (byte)'x', 0, 0 },
            new byte[] { 0x0B, 1, 0, 0, 0, 0x08, 0x03, 0, 0x01, 0x30 }
        };

        foreach (var bytes in restrictions)
        {
            var reader = new MapiReader(bytes);
            var node = NspiRestrictionParser.Parse(ref reader, new MapiNodeBudget());

            Assert.True(reader.End, $"{node.Name} left {reader.Remaining} bytes.");
        }
    }

    [Fact]
    public void ParsesOptionalGetPropsValuesAndGetMatchesRestriction()
    {
        var request = BuildBody(
            stream =>
            {
                WriteUInt32(stream, 0);
                stream.WriteByte(0);
                stream.WriteByte(0);
                WriteUInt32(stream, 0);
                stream.WriteByte(1);
                stream.WriteByte(0x08);
                WriteUInt32(stream, 0x30010003);
                stream.WriteByte(0);
                WriteUInt32(stream, 10);
                stream.WriteByte(0);
                WriteUInt32(stream, 0);
            });
        var response = BuildResponse(
            stream =>
            {
                WriteUInt32(stream, 1252);
                stream.WriteByte(1);
                WriteUInt32(stream, 1);
                WriteUInt16(stream, 0x001F);
                WriteUInt16(stream, 0x3001);
                stream.WriteByte(1);
                WriteUnicodeZ(stream, "Resolved");
            });
        using var getMatchesSaz = Fixture(
            ("raw/9_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                request,
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "GetMatches"))));
        using var getPropsSaz = Fixture(
            ("raw/10_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                BuildBody(
                    stream =>
                    {
                        WriteUInt32(stream, 0);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        WriteUInt32(stream, 0);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "GetProps"))),
            ("raw/10_s.txt", Http(
                "HTTP/1.1 200 OK",
                response,
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var restriction = Assert.Single(new SazParser().Parse(getMatchesSaz).Sessions).Mapi!.Request!;
        var values = Assert.Single(new SazParser().Parse(getPropsSaz).Sessions).Mapi!.Response!;

        Assert.Equal("ExistRestriction", Find(restriction.Root, "ExistRestriction").Name);
        Assert.True(restriction.Complete);
        Assert.Equal("Resolved", Find(values.Root, "PropertyValue").Value);
        Assert.True(values.Complete);
    }

    [Fact]
    public void ParsesIndependentSeekEntriesAndUpdateStatResponseFlags()
    {
        using var seekSaz = Fixture(
            ("raw/11_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                BuildBody(
                    stream =>
                    {
                        WriteUInt32(stream, 0);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        WriteUInt32(stream, 0);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "SeekEntries"))),
            ("raw/11_s.txt", Http(
                "HTTP/1.1 200 OK",
                BuildResponse(
                    stream =>
                    {
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));
        using var updateSaz = Fixture(
            ("raw/12_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                BuildBody(
                    stream =>
                    {
                        WriteUInt32(stream, 0);
                        stream.WriteByte(0);
                        stream.WriteByte(1);
                        WriteUInt32(stream, 0);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "UpdateStat"))),
            ("raw/12_s.txt", Http(
                "HTTP/1.1 200 OK",
                BuildResponse(
                    stream =>
                    {
                        stream.WriteByte(0);
                        stream.WriteByte(1);
                        WriteUInt32(stream, unchecked((uint)-2));
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var seek = Assert.Single(new SazParser().Parse(seekSaz).Sessions).Mapi!.Response!;
        var update = Assert.Single(new SazParser().Parse(updateSaz).Sessions).Mapi!.Response!;

        Assert.True(seek.Complete);
        Assert.Equal("false", Find(seek.Root, "HasColsAndRows").Value);
        Assert.True(update.Complete);
        Assert.Equal("-2", Find(update.Root, "Delta").Value);
    }

    [Fact]
    public void EnforcesRestrictionDepthAndPropertyBudgets()
    {
        var nested = Enumerable.Repeat((byte)0x02, MapiParseLimits.MaxDepth + 1)
            .Concat(new byte[] { 0x08, 0x03, 0, 0x01, 0x30 })
            .ToArray();
        var excessiveCount = BuildBody(stream => WriteUInt32(stream, MapiParseLimits.MaxCollectionCount + 1u));
        var exhaustedBudget = new MapiNodeBudget();
        exhaustedBudget.Claim(0, MapiParseLimits.MaxNodes);
        byte[] emptyList = [0, 0, 0, 0];

        Assert.Contains(
            "depth exceeds",
            Assert.Throws<MapiParseException>(() => ParseRestriction(nested)).Message);
        Assert.Contains(
            "safe limit",
            Assert.Throws<MapiParseException>(() => ParseValueList(excessiveCount, new MapiNodeBudget())).Message);
        Assert.Contains(
            "tree exceeds",
            Assert.Throws<MapiParseException>(() => ParseValueList(emptyList, exhaustedBudget)).Message);
    }

    [Theory]
    [InlineData(0x101F)]
    [InlineData(0x1102)]
    public void RejectsTruncatedMultivalueVariableProperties(ushort type)
    {
        var bytes = BuildBody(
            stream =>
            {
                stream.WriteByte(1);
                WriteUInt32(stream, 1);
                stream.WriteByte(1);
            });
        Assert.Throws<MapiParseException>(() => ParsePropertyValue(bytes, type));
    }

    [Fact]
    public void ParsesFlaggedRowsWithUnspecifiedErrorAndUnavailableValues()
    {
        var rowBytes = BuildBody(
            stream =>
            {
                stream.WriteByte(1);
                WriteUInt16(stream, 0x0003);
                stream.WriteByte(0);
                WriteUInt32(stream, 42);
                stream.WriteByte(0x0A);
                WriteUInt32(stream, 0x8004010F);
                stream.WriteByte(0x01);
            });
        var reader = new MapiReader(rowBytes);

        var row = NspiPropertyParser.ParseRow(
            ref reader,
            [0x30010000, 0x30020003, 0x30030003],
            "Row",
            new MapiNodeBudget());

        Assert.True(reader.End);
        Assert.Contains("42", Flatten(row).Select(item => item.Value));
        Assert.Contains("0x8004010F", Flatten(row).Select(item => item.Value));
        Assert.Contains("Unavailable", Flatten(row).Select(item => item.Value));
    }

    [Fact]
    public void DecodesString8UsingTheResponseCodePage()
    {
        using var saz = Fixture(
            ("raw/14_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                BuildBody(
                    stream =>
                    {
                        WriteUInt32(stream, 0);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        WriteUInt32(stream, 0);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "GetProps"))),
            ("raw/14_s.txt", Http(
                "HTTP/1.1 200 OK",
                BuildResponse(
                    stream =>
                    {
                        WriteUInt32(stream, 1252);
                        stream.WriteByte(1);
                        WriteUInt32(stream, 1);
                        WriteUInt16(stream, 0x001E);
                        WriteUInt16(stream, 0x3001);
                        stream.WriteByte(1);
                        stream.Write([0xE9, 0]);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var response = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Response!;

        Assert.True(response.Complete);
        Assert.Equal("é", Find(response.Root, "PropertyValue").Value);
    }

    [Fact]
    public void RetainsString8BytesWhenTheCodePageIsUnsupported()
    {
        byte[] bytes = [1, 0x80, 0];
        var reader = new MapiReader(bytes);
        var warnings = new List<string>();

        var value = NspiPropertyParser.ParseValue(
            ref reader,
            0x001E,
            "Value",
            new MapiNodeBudget(),
            0,
            includePresence: true,
            codePage: uint.MaxValue,
            warnings: warnings);

        Assert.True(reader.End);
        Assert.Contains(Flatten(value), node => node.Kind == MapiNodeKind.Raw);
        Assert.Contains(warnings, warning => warning.Contains("unsupported code page", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsUnsupportedRuleActionsAsPartialWithoutConsumingRawData()
    {
        using var saz = Fixture(
            ("raw/13_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                BuildBody(
                    stream =>
                    {
                        WriteUInt32(stream, 0);
                        stream.WriteByte(0);
                        stream.WriteByte(0);
                        WriteUInt32(stream, 0);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "GetProps"))),
            ("raw/13_s.txt", Http(
                "HTTP/1.1 200 OK",
                BuildResponse(
                    stream =>
                    {
                        WriteUInt32(stream, 1252);
                        stream.WriteByte(1);
                        WriteUInt32(stream, 1);
                        WriteUInt16(stream, 0x00FE);
                        WriteUInt16(stream, 0x6680);
                        stream.Write([0xDE, 0xAD, 0xBE, 0xEF]);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var response = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Response!;

        Assert.False(response.Complete);
        Assert.Contains(response.Warnings, warning => warning.Contains("Unsupported property type 0x00FE", StringComparison.Ordinal));
        Assert.Equal(MapiNodeKind.Raw, response.Root.Kind);
    }

    private static MapiNode Find(MapiNode node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindOrDefault(child, name);
            if (found is not null)
            {
                return found;
            }
        }
        throw new Xunit.Sdk.XunitException($"Node '{name}' was not found.");
    }

    private static void ParseRestriction(byte[] bytes)
    {
        var reader = new MapiReader(bytes);
        NspiRestrictionParser.Parse(ref reader, new MapiNodeBudget());
    }

    private static void ParseValueList(byte[] bytes, MapiNodeBudget budget)
    {
        var reader = new MapiReader(bytes);
        NspiPropertyParser.ParseValueList(ref reader, "Values", budget);
    }

    private static void ParsePropertyValue(byte[] bytes, ushort type)
    {
        var reader = new MapiReader(bytes);
        NspiPropertyParser.ParseValue(
            ref reader,
            type,
            "Value",
            new MapiNodeBudget(),
            0,
            includePresence: true);
    }

    private static MapiNode? FindOrDefault(MapiNode node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }
        foreach (var child in node.Children)
        {
            var found = FindOrDefault(child, name);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static IEnumerable<MapiNode> Flatten(MapiNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static byte[] ExtendedBuffer(byte[] payload, ushort flags)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), checked((ushort)payload.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2), checked((ushort)payload.Length));
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }
        return output.ToArray();
    }

    private static byte[] BuildBody(Action<MemoryStream> write)
    {
        using var body = new MemoryStream();
        write(body);
        return body.ToArray();
    }

    private static byte[] BuildResponse(Action<MemoryStream> write) =>
        BuildBody(
            stream =>
            {
                stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                write(stream);
                WriteUInt32(stream, 0);
            });

    private static byte[] Http(
        string startLine,
        byte[] body,
        params (string Name, string Value)[] headers)
    {
        var prefix = new StringBuilder(startLine).Append("\r\n");
        foreach (var (name, value) in headers)
        {
            prefix.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        prefix.Append("\r\n");
        return Encoding.Latin1.GetBytes(prefix.ToString()).Concat(body).ToArray();
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

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteAsciiZ(Stream stream, string value)
    {
        stream.Write(Encoding.ASCII.GetBytes(value));
        stream.WriteByte(0);
    }

    private static void WriteUnicodeZ(Stream stream, string value)
    {
        stream.Write(Encoding.Unicode.GetBytes(value));
        stream.WriteByte(0);
        stream.WriteByte(0);
    }
}
