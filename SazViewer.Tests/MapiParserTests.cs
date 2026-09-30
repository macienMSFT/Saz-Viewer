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
        Assert.True(mapi.Response.EnvelopeSucceeded);
        Assert.True(mapi.Response.LifecycleTransitionSucceeded);
        Assert.Single(report.Mapi!.Sessions);
    }

    [Fact]
    public void TruncatedConnectResponseCannotEstablishLifecycleTransition()
    {
        using var requestBody = new MemoryStream();
        WriteAsciiZ(requestBody, "/o=Example/ou=Users/cn=User");
        WriteUInt32(requestBody, 1);
        WriteUInt32(requestBody, 1252);
        WriteUInt32(requestBody, 0x0409);
        WriteUInt32(requestBody, 0x0409);
        WriteUInt32(requestBody, 0);
        var truncatedResponse = BuildBody(
            stream =>
            {
                stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 120);
            });

        using var saz = Fixture(
            ("raw/1_c.txt", Http(
                "POST /mapi/emsmdb HTTP/1.1",
                requestBody.ToArray(),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Connect"))),
            ("raw/1_s.txt", Http(
                "HTTP/1.1 200 OK",
                truncatedResponse,
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"),
                ("Set-Cookie", "sid=must-not-register"))));

        var response = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Response!;
        Assert.True(response.EnvelopeSucceeded);
        Assert.False(response.LifecycleTransitionSucceeded);
        Assert.False(response.Complete);
    }

    [Fact]
    public void SuccessfulBindAndUnbindResponsesCompleteLifecycleTransitions()
    {
        var bindRequest = BuildBody(
            stream =>
            {
                WriteUInt32(stream, 0);
                stream.WriteByte(0);
                WriteUInt32(stream, 0);
            });
        var unbindRequest = BuildBody(
            stream =>
            {
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
            });
        var bindResponse = BuildResponse(stream => stream.Write(Guid.Empty.ToByteArray()));
        var unbindResponse = BuildResponse(_ => { });
        using var saz = Fixture(
            ("raw/1_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                bindRequest,
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Bind"))),
            ("raw/1_s.txt", Http(
                "HTTP/1.1 200 OK",
                bindResponse,
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"),
                ("Set-Cookie", "sid=nspi-session"))),
            ("raw/2_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                unbindRequest,
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Unbind"),
                ("Cookie", "sid=nspi-session"))),
            ("raw/2_s.txt", Http(
                "HTTP/1.1 200 OK",
                unbindResponse,
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var sessions = new SazParser().Parse(saz).Sessions;
        Assert.All(
            sessions,
            session =>
            {
                Assert.True(session.Mapi!.Response!.EnvelopeSucceeded);
                Assert.True(session.Mapi.Response.LifecycleTransitionSucceeded);
            });
    }

    [Fact]
    public void BodySuccessfulBindWithoutXResponseCodeDoesNotReportLifecycleSuccess()
    {
        var bindRequest = BuildBody(
            stream =>
            {
                WriteUInt32(stream, 0);
                stream.WriteByte(0);
                WriteUInt32(stream, 0);
            });
        var bindResponse = BuildResponse(stream => stream.Write(Guid.Empty.ToByteArray()));
        using var saz = Fixture(
            ("raw/1_c.txt", Http(
                "POST /mapi/nspi HTTP/1.1",
                bindRequest,
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Bind"))),
            ("raw/1_s.txt", Http(
                "HTTP/1.1 200 OK",
                bindResponse,
                ("Content-Type", "application/mapi-http"))));

        var response = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Response!;
        Assert.False(response.EnvelopeSucceeded);
        Assert.False(response.LifecycleTransitionSucceeded);
    }

    [Fact]
    public void ParsesExecuteExtendedBufferRopFramingAndHandles()
    {
        // 0x2A (RopNotify) is a real, named RopId that has zero *request*-direction schema anywhere
        // (fixed catalog or any of the four self-contained variable-width decoder families) by
        // protocol design: [MS-OXCROPS] 2.2.14.1 defines RopNotify strictly as a server-initiated
        // response operation, never a request. That makes it a stable, permanently-true example for
        // exercising the "unimplemented-for-this-direction RopId retained as raw" framing path
        // exercised by this test (which is about extended buffer/handle table framing, not about
        // decoding RopNotify itself).
        byte[] ropPayload = [5, 0, 0x2A, 0, 0, 0x44, 0x33, 0x22, 0x11];
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

        Assert.Equal("RopNotify", Find(request.Root, "RopId").Value!.Split('(')[1].TrimEnd(')'));
        Assert.Equal("0x11223344", Find(request.Root, "[0]").Value);
        Assert.Contains(request.Warnings, warning => warning.Contains("individual ROP fields", StringComparison.Ordinal));
    }

    [Fact]
    public void CorrelatesLogonPrivacyAcrossExecuteRoundTripsOnTheSameLogicalConnection()
    {
        byte[] logon =
        [
            0xFE, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00,
        ];
        byte[] setReadFlag = [0x11, 0x00, 0x00, 0x00, 0x01];

        using var saz = Fixture(
            ("raw/20_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(logon),
                ("Host", "example.test"),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))),
            ("raw/20_s.txt", Http(
                "HTTP/1.1 200 OK",
                ExecuteResponse(PrivateLogonResponse(), 0x10203040),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))),
            ("raw/21_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(setReadFlag),
                ("Host", "example.test"),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))));

        var report = new SazParser().Parse(saz);
        Assert.Equal(2, report.Sessions.Count);
        var secondRequest = report.Sessions[1].Mapi!.Request!;
        Assert.True(secondRequest.Complete);
        Assert.Equal("0x01", Find(secondRequest.Root, "ReadFlags").Value);
        Assert.DoesNotContain(secondRequest.Warnings, warning => warning.Contains("LogonFlags.Private", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotCommitLogonPrivacyWithoutASuccessfulResponse()
    {
        byte[] logon =
        [
            0xFE, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00,
        ];
        byte[] setReadFlag = [0x11, 0x00, 0x00, 0x00, 0x01];

        using var saz = Fixture(
            ("raw/24_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(logon),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))),
            ("raw/25_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(setReadFlag),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))));

        var report = new SazParser().Parse(saz);
        var secondRequest = report.Sessions[1].Mapi!.Request!;
        Assert.False(secondRequest.Complete);
        Assert.Contains(secondRequest.Warnings, warning =>
            warning.Contains("LogonFlags.Private", StringComparison.Ordinal));
    }

    [Fact]
    public void DoesNotCorrelateLogonPrivacyAcrossDifferentClientIdentities()
    {
        byte[] logon =
        [
            0xFE, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00,
        ];
        byte[] setReadFlag = [0x11, 0x00, 0x00, 0x00, 0x01];

        using var saz = Fixture(
            ("raw/22_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(logon),
                ("Host", "example.test"),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))),
            ("raw/23_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(setReadFlag),
                ("Host", "example.test"),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-b"))));

        var report = new SazParser().Parse(saz);
        var secondRequest = report.Sessions[1].Mapi!.Request!;
        Assert.False(secondRequest.Complete);
        Assert.Contains(
            secondRequest.Warnings,
            warning => warning.Contains("LogonFlags.Private", StringComparison.Ordinal)
                && warning.Contains("could not be parsed", StringComparison.Ordinal));
    }

    [Fact]
    public void CorrelatesSessionContextsAcrossCookieSequenceRolloverWithoutCrossSessionLeakage()
    {
        var context = new MapiCaptureContext();
        Assert.Null(context.ResolveAndRegisterLogonCorrelationScope(
            "connect-a",
            "Connect",
            "same-fallback",
            []));
        Assert.Null(context.CompleteLogonCorrelationEstablishment("connect-a", success: true));
        context.RegisterResponseCookieAliases(
            "connect-a",
            ["sid=session-a; Path=/mapi", "sequence=one; Path=/mapi", "affinity=shared"]);
        context.RecordLogonPrivacy("connect-a", 1, isPrivate: true);

        Assert.Null(context.ResolveAndRegisterLogonCorrelationScope(
            "execute-a-without-cookie",
            "Execute",
            "same-fallback",
            []));
        Assert.True(context.TryGetLogonPrivacy("execute-a-without-cookie", 1, out var privateA));
        Assert.True(privateA);

        Assert.Null(context.ResolveAndRegisterLogonCorrelationScope(
            "execute-a1",
            "Execute",
            "same-fallback",
            ["sid=session-a; sequence=one; affinity=shared"]));
        Assert.True(context.TryGetLogonPrivacy("execute-a1", 1, out privateA));
        Assert.True(privateA);
        context.RegisterResponseCookieAliases("execute-a1", ["sequence=two; Path=/mapi"]);

        Assert.Null(context.ResolveAndRegisterLogonCorrelationScope(
            "execute-a2",
            "Execute",
            "same-fallback",
            ["sid=session-a; sequence=two; affinity=shared"]));
        Assert.True(context.TryGetLogonPrivacy("execute-a2", 1, out privateA));
        Assert.True(privateA);

        Assert.Null(context.ResolveAndRegisterLogonCorrelationScope(
            "connect-b",
            "Connect",
            "same-fallback",
            []));
        Assert.Null(context.CompleteLogonCorrelationEstablishment("connect-b", success: true));
        context.RegisterResponseCookieAliases(
            "connect-b",
            ["sid=session-b; Path=/mapi", "sequence=other; Path=/mapi", "affinity=shared"]);
        context.RecordLogonPrivacy("connect-b", 1, isPrivate: false);

        Assert.Null(context.ResolveAndRegisterLogonCorrelationScope(
            "execute-b",
            "Execute",
            "same-fallback",
            ["sid=session-b; sequence=other; affinity=shared"]));
        Assert.True(context.TryGetLogonPrivacy("execute-b", 1, out var privateB));
        Assert.False(privateB);

        var noCookieAmbiguity = context.ResolveAndRegisterLogonCorrelationScope(
            "execute-no-cookie-ambiguous",
            "Execute",
            "same-fallback",
            []);
        Assert.Contains("multiple active session contexts", noCookieAmbiguity);
        Assert.False(context.TryGetLogonPrivacy("execute-no-cookie-ambiguous", 1, out _));

        var ambiguous = context.ResolveAndRegisterLogonCorrelationScope(
            "execute-ambiguous",
            "Execute",
            "same-fallback",
            ["affinity=shared"]);
        Assert.Contains("multiple prior session contexts", ambiguous);
        Assert.DoesNotContain("shared", ambiguous);
        Assert.DoesNotContain("session-a", ambiguous);
        Assert.DoesNotContain("session-b", ambiguous);
        Assert.False(context.TryGetLogonPrivacy("execute-ambiguous", 1, out _));
    }

    [Fact]
    public void SuccessfulDisconnectRemovesCookieAliasesAndLogicalSessionState()
    {
        var context = new MapiCaptureContext();
        context.ResolveAndRegisterLogonCorrelationScope("connect", "Connect", "fallback", []);
        context.CompleteLogonCorrelationEstablishment("connect", success: true);
        context.RegisterResponseCookieAliases("connect", ["sid=session-a", "sequence=one"]);
        context.RecordLogonPrivacy("connect", 1, isPrivate: true);
        context.ResolveAndRegisterLogonCorrelationScope(
            "disconnect",
            "Disconnect",
            "fallback",
            ["sid=session-a; sequence=one"]);
        Assert.True(context.TryGetLogonPrivacy("disconnect", 1, out _));

        context.EndLogonCorrelationScope("disconnect");
        context.ResolveAndRegisterLogonCorrelationScope(
            "after-disconnect",
            "Execute",
            "fallback",
            ["sid=session-a; sequence=one"]);

        Assert.False(context.TryGetLogonPrivacy("after-disconnect", 1, out _));
    }

    [Fact]
    public void FailedContextEstablishmentDoesNotBecomeAnActiveFallbackGeneration()
    {
        var context = new MapiCaptureContext();
        context.ResolveAndRegisterLogonCorrelationScope("failed-connect", "Connect", "fallback", []);
        context.RecordLogonPrivacy("failed-connect", 1, isPrivate: true);
        context.CompleteLogonCorrelationEstablishment("failed-connect", success: false);
        context.CompleteHttpSession("failed-connect");

        Assert.Null(context.ResolveAndRegisterLogonCorrelationScope(
            "later-execute",
            "Execute",
            "fallback",
            []));
        Assert.False(context.TryGetLogonPrivacy("later-execute", 1, out _));
    }

    [Fact]
    public void SharedCookieCannotOverrideAConflictingFallbackAndUnknownSessionCookie()
    {
        var context = new MapiCaptureContext();
        context.ResolveAndRegisterLogonCorrelationScope("connect-a", "Connect", "fallback-a", []);
        context.CompleteLogonCorrelationEstablishment("connect-a", success: true);
        context.RegisterResponseCookieAliases("connect-a", ["sid=session-a", "affinity=shared"]);
        context.RecordLogonPrivacy("connect-a", 1, isPrivate: true);

        var warning = context.ResolveAndRegisterLogonCorrelationScope(
            "partial-session-b",
            "Execute",
            "fallback-b",
            ["sid=session-b; affinity=shared"]);

        Assert.Contains("conflicted", warning);
        Assert.DoesNotContain("session-a", warning);
        Assert.DoesNotContain("session-b", warning);
        Assert.False(context.TryGetLogonPrivacy("partial-session-b", 1, out _));
    }

    [Fact]
    public void ProtocolSuccessfulDisconnectTearsDownStateEvenWhenAuxiliaryDataIsTruncated()
    {
        byte[] logon =
        [
            0xFE, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00,
        ];
        byte[] setReadFlag = [0x11, 0x00, 0x00, 0x00, 0x01];
        var truncatedDisconnectResponse = BuildBody(
            stream =>
            {
                stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 8);
                stream.Write([0x01, 0x02]);
            });

        using var saz = Fixture(
            ("raw/30_c.txt", Http(
                "POST /mapi/emsmdb HTTP/1.1",
                ExecuteRequest(logon),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))),
            ("raw/30_s.txt", Http(
                "HTTP/1.1 200 OK",
                ExecuteResponse(PrivateLogonResponse(), 0x10203040),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"),
                ("Set-Cookie", "sid=session-a"))),
            ("raw/31_c.txt", Http(
                "POST /mapi/emsmdb HTTP/1.1",
                [],
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Disconnect"),
                ("X-ClientInfo", "client-a"),
                ("Cookie", "sid=session-a"))),
            ("raw/31_s.txt", Http(
                "HTTP/1.1 200 OK",
                truncatedDisconnectResponse,
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))),
            ("raw/32_c.txt", Http(
                "POST /mapi/emsmdb HTTP/1.1",
                ExecuteRequest(setReadFlag),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"),
                ("Cookie", "sid=session-a"))));

        var report = new SazParser().Parse(saz);
        var disconnect = report.Sessions[1].Mapi!.Response!;
        Assert.False(disconnect.Complete);
        Assert.True(disconnect.EnvelopeSucceeded);
        Assert.True(disconnect.LifecycleTransitionSucceeded);
        var request = report.Sessions[2].Mapi!.Request!;
        Assert.False(request.Complete);
        Assert.Contains(request.Warnings, warning =>
            warning.Contains("LogonFlags.Private", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedOrIndeterminateDisconnectEnvelopePreservesLogicalSessionState()
    {
        var unsuccessfulResponses = new[]
        {
            BuildBody(
                stream =>
                {
                    stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                    WriteUInt32(stream, 1);
                    WriteUInt32(stream, 0);
                }),
            BuildBody(
                stream =>
                {
                    stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                    WriteUInt32(stream, 0);
                    WriteUInt32(stream, 0x80004005);
                    WriteUInt32(stream, 0);
                }),
            BuildBody(
                stream =>
                {
                    stream.Write(Encoding.ASCII.GetBytes("\r\n"));
                    WriteUInt32(stream, 0);
                })
        };

        foreach (var disconnectResponse in unsuccessfulResponses)
        {
            byte[] logon =
            [
                0xFE, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00,
            ];
            byte[] setReadFlag = [0x11, 0x00, 0x00, 0x00, 0x01];
            using var saz = Fixture(
                ("raw/40_c.txt", Http(
                    "POST /mapi/emsmdb HTTP/1.1",
                    ExecuteRequest(logon),
                    ("Content-Type", "application/mapi-http"),
                    ("X-RequestType", "Execute"),
                    ("X-ClientInfo", "client-a"))),
                ("raw/40_s.txt", Http(
                    "HTTP/1.1 200 OK",
                    ExecuteResponse(PrivateLogonResponse(), 0x10203040),
                    ("Content-Type", "application/mapi-http"),
                    ("X-ResponseCode", "0"),
                    ("Set-Cookie", "sid=session-a"))),
                ("raw/41_c.txt", Http(
                    "POST /mapi/emsmdb HTTP/1.1",
                    [],
                    ("Content-Type", "application/mapi-http"),
                    ("X-RequestType", "Disconnect"),
                    ("X-ClientInfo", "client-a"),
                    ("Cookie", "sid=session-a"))),
                ("raw/41_s.txt", Http(
                    "HTTP/1.1 200 OK",
                    disconnectResponse,
                    ("Content-Type", "application/mapi-http"),
                    ("X-ResponseCode", "0"))),
                ("raw/42_c.txt", Http(
                    "POST /mapi/emsmdb HTTP/1.1",
                    ExecuteRequest(setReadFlag),
                    ("Content-Type", "application/mapi-http"),
                    ("X-RequestType", "Execute"),
                    ("X-ClientInfo", "client-a"),
                    ("Cookie", "sid=session-a"))));

            var report = new SazParser().Parse(saz);
            var disconnect = report.Sessions[1].Mapi!.Response!;
            Assert.False(disconnect.EnvelopeSucceeded);
            Assert.False(disconnect.LifecycleTransitionSucceeded);
            var laterRequest = report.Sessions[2].Mapi!.Request!;
            Assert.True(laterRequest.Complete);
            Assert.DoesNotContain(
                laterRequest.Warnings,
                warning => warning.Contains("LogonFlags.Private", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ReconstructsQueryRowsAcrossExecuteRoundTripsFromCommittedSetColumns()
    {
        const uint tableHandle = 0x12345678;
        var setColumnsRequest = BuildBody(
            stream =>
            {
                stream.Write([0x12, 0x00, 0x00, 0x00]);
                WriteUInt16(stream, 1);
                WriteUInt16(stream, 0x0003);
                WriteUInt16(stream, 0x3001);
            });
        byte[] setColumnsResponse = [0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] queryRowsRequest = [0x15, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00];
        var queryRowsResponse = BuildBody(
            stream =>
            {
                stream.Write([0x15, 0x00]);
                WriteUInt32(stream, 0);
                stream.WriteByte(0);
                WriteUInt16(stream, 1);
                stream.WriteByte(0);
                WriteUInt32(stream, 42);
            });

        using var saz = Fixture(
            ("raw/30_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(setColumnsRequest, tableHandle),
                ("Host", "example.test"),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))),
            ("raw/30_s.txt", Http(
                "HTTP/1.1 200 OK",
                ExecuteResponse(setColumnsResponse, tableHandle),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))),
            ("raw/31_c.txt", Http(
                "POST /mapi/emsmdb/?MailboxId=mailbox-a HTTP/1.1",
                ExecuteRequest(queryRowsRequest, tableHandle),
                ("Host", "example.test"),
                ("Content-Type", "application/mapi-http"),
                ("X-RequestType", "Execute"),
                ("X-ClientInfo", "client-a"))),
            ("raw/31_s.txt", Http(
                "HTTP/1.1 200 OK",
                ExecuteResponse(queryRowsResponse, tableHandle),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var report = new SazParser().Parse(saz);
        var queryResponse = report.Sessions[1].Mapi!.Response!;
        Assert.True(queryResponse.Complete);
        Assert.Equal("42", Find(Find(queryResponse.Root, "RowData"), "Value").Value);
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
        Assert.Contains("PidTagDisplayName", Find(response.Root, "PropertyId").Value);
    }

    [Fact]
    public void IncludesTheCompleteUpstreamPropertyNameDictionaries()
    {
        Assert.Equal(572, MapiPropertyNames.PidTagSymbolCount);
        Assert.Equal(547, MapiPropertyNames.PidTagIdCount);
        Assert.Equal(365, MapiPropertyNames.PidLidSymbolCount);
        Assert.Equal(365, MapiPropertyNames.PidLidIdCount);
        Assert.Equal(131, MapiPropertyNames.PidNameSymbolCount);
        Assert.Equal(131, MapiPropertyNames.PidNameIdCount);
        Assert.Equal("PidTagDisplayName", MapiPropertyNames.PidTag(0x3001));
        Assert.Contains("PidTagMessageSize", MapiPropertyNames.PidTag(0x0E08));
        Assert.Contains("PidTagMessageSizeExtended", MapiPropertyNames.PidTag(0x0E08));
        Assert.Equal(
            "PidLidAddressBookProviderArrayType",
            MapiPropertyNames.PidLid("PSETID_Address", 0x8029));
        Assert.Equal(
            "PidNameAcceptLanguage",
            MapiPropertyNames.PidName("PS_INTERNET_HEADERS", "Accept-Language"));
        Assert.Equal(
            "PSETID_Address",
            MapiPropertyNames.PropertySetName(Guid.Parse("00062004-0000-0000-C000-000000000046")));
        Assert.Null(MapiPropertyNames.PidTag(0xFFFF));
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
    public void ParsesAllRuleActionTypesWithBoundedPartialEntryIds()
    {
        using var data = new MemoryStream();
        WriteUInt32(data, 11);
        WriteAction(data, 0x01, payload => { WriteUInt32(payload, 0); WriteUInt32(payload, 0); });
        WriteAction(data, 0x02, payload => { WriteUInt32(payload, 0); WriteUInt32(payload, 0); });
        WriteAction(data, 0x03, payload => { WriteUInt32(payload, 0); payload.Write(new byte[16]); }, flavor: 0x02);
        WriteAction(data, 0x04, payload => { WriteUInt32(payload, 0); payload.Write(new byte[16]); });
        WriteAction(data, 0x05, payload => payload.Write([0xDE, 0xAD]));
        WriteAction(data, 0x06, payload => WriteUInt32(payload, 0x0000000D));
        WriteAction(
            data,
            0x07,
            payload =>
            {
                WriteUInt32(payload, 1);
                payload.WriteByte(0);
                WriteUInt32(payload, 1);
                WriteUInt16(payload, 0x0003);
                WriteUInt16(payload, 0x3001);
                WriteUInt32(payload, 42);
            },
            flavor: 0x03);
        WriteAction(
            data,
            0x08,
            payload =>
            {
                WriteUInt32(payload, 1);
                payload.WriteByte(0);
                WriteUInt32(payload, 1);
                WriteUInt16(payload, 0x0003);
                WriteUInt16(payload, 0x3001);
                WriteUInt32(payload, 43);
            });
        WriteAction(
            data,
            0x09,
            payload =>
            {
                WriteUInt16(payload, 0x0003);
                WriteUInt16(payload, 0x3001);
                WriteUInt32(payload, 44);
            });
        WriteAction(data, 0x0A, _ => { });
        WriteAction(data, 0x0B, _ => { });
        var reader = new MapiReader(data.ToArray());
        var warnings = new List<string>();

        var value = NspiPropertyParser.ParseValue(
            ref reader,
            0x00FE,
            "Value",
            new MapiNodeBudget(),
            0,
            includePresence: false,
            warnings: warnings);

        Assert.True(reader.End);
        foreach (var name in new[]
                 {
                     "OP_MOVE", "OP_COPY", "OP_REPLY", "OP_OOF_REPLY", "OP_DEFER_ACTION",
                     "OP_BOUNCE", "OP_FORWARD", "OP_DELEGATE", "OP_TAG", "OP_DELETE", "OP_MARK_AS_READ"
                 })
        {
            Assert.Contains(Flatten(value), node => node.Value == name);
        }
        Assert.Equal("0x0000000D RejectedMessageTooLarge", Find(value, "BounceCode").Value);
        Assert.Contains("0x00000002 (ST=1, NS=0)", Flatten(value).Select(node => node.Value));
        Assert.Contains("0x00000003 (TM=0, AT=0, NC=1, PR=1)", Flatten(value).Select(node => node.Value));
        Assert.Contains("44", Flatten(value).Select(node => node.Value));
        Assert.Equal(4, warnings.Count(warning => warning.Contains("semantic fields", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0x01, 0x00000001)]
    [InlineData(0x03, 0x00000003)]
    [InlineData(0x07, 0x00000005)]
    [InlineData(0x07, 0x0000000C)]
    [InlineData(0x07, 0x80000000)]
    public void WarnsForInvalidRuleActionFlavors(byte type, uint flavor)
    {
        using var data = new MemoryStream();
        WriteUInt32(data, 1);
        WriteAction(
            data,
            type,
            payload =>
            {
                if (type == 0x01)
                {
                    WriteUInt32(payload, 0);
                    WriteUInt32(payload, 0);
                }
                else if (type == 0x03)
                {
                    WriteUInt32(payload, 0);
                    payload.Write(new byte[16]);
                }
                else
                {
                    WriteUInt32(payload, 0);
                }
            },
            flavor);
        var reader = new MapiReader(data.ToArray());
        var warnings = new List<string>();

        _ = NspiPropertyParser.ParseValue(
            ref reader,
            0x00FE,
            "Value",
            new MapiNodeBudget(),
            0,
            includePresence: false,
            warnings: warnings);

        Assert.True(reader.End);
        Assert.Contains(warnings, warning => warning.Contains("invalid ActionFlavor", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsZeroMandatoryRuleActionCounts()
    {
        static void ParseRuleAction(byte[] bytes)
        {
            var reader = new MapiReader(bytes);
            _ = NspiPropertyParser.ParseValue(
                ref reader,
                0x00FE,
                "Value",
                new MapiNodeBudget(),
                0,
                includePresence: false);
        }

        Assert.Throws<MapiParseException>(() => ParseRuleAction(new byte[4]));

        var zeroNestedCounts = new Action<MemoryStream>[]
        {
            payload => WriteUInt32(payload, 0),
            payload =>
            {
                WriteUInt32(payload, 1);
                payload.WriteByte(0);
                WriteUInt32(payload, 0);
            }
        };
        foreach (var writePayload in zeroNestedCounts)
        {
            using var data = new MemoryStream();
            WriteUInt32(data, 1);
            WriteAction(data, 0x07, writePayload);
            var reader = new MapiReader(data.ToArray());
            var warnings = new List<string>();

            var value = NspiPropertyParser.ParseValue(
                ref reader,
                0x00FE,
                "Value",
                new MapiNodeBudget(),
                0,
                includePresence: false,
                warnings: warnings);

            Assert.True(reader.End);
            Assert.Contains(warnings, warning => warning.Contains("greater than zero", StringComparison.Ordinal));
            Assert.Contains(Flatten(value), node => node.Kind == MapiNodeKind.Raw);
        }
    }

    [Fact]
    public void WarnsForInvalidRuleActionBounceCode()
    {
        using var data = new MemoryStream();
        WriteUInt32(data, 1);
        WriteAction(data, 0x06, payload => WriteUInt32(payload, 0x12345678));
        var reader = new MapiReader(data.ToArray());
        var warnings = new List<string>();

        _ = NspiPropertyParser.ParseValue(
            ref reader,
            0x00FE,
            "Value",
            new MapiNodeBudget(),
            0,
            includePresence: false,
            warnings: warnings);

        Assert.True(reader.End);
        Assert.Contains(warnings, warning => warning.Contains("invalid BounceCode", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsTruncatedRuleActionSizeAndCountFieldsWithoutEscapingActionBounds()
    {
        var malformedActions = new (byte Type, Action<MemoryStream> Payload)[]
        {
            (0x01, payload => payload.WriteByte(1)),
            (0x01, payload => { WriteUInt32(payload, 0); payload.WriteByte(1); }),
            (0x03, payload => payload.WriteByte(1)),
            (0x07, payload => payload.WriteByte(1)),
            (0x07, payload => { WriteUInt32(payload, 1); payload.WriteByte(0); payload.WriteByte(1); })
        };

        foreach (var (type, writePayload) in malformedActions)
        {
            using var data = new MemoryStream();
            WriteUInt32(data, 1);
            WriteAction(data, type, writePayload);
            var reader = new MapiReader(data.ToArray());
            var warnings = new List<string>();

            var value = NspiPropertyParser.ParseValue(
                ref reader,
                0x00FE,
                "Value",
                new MapiNodeBudget(),
                0,
                includePresence: false,
                warnings: warnings);

            Assert.True(reader.End);
            Assert.Contains(warnings, warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(Flatten(value), node => node.Kind == MapiNodeKind.Raw);
        }
    }

    [Fact]
    public void EnforcesRuleActionEntryIdAndRecipientLimits()
    {
        var malformedActions = new (byte Type, Action<MemoryStream> Payload)[]
        {
            (0x01, payload => WriteUInt32(payload, uint.MaxValue)),
            (0x03, payload => WriteUInt32(payload, uint.MaxValue)),
            (0x07, payload => WriteUInt32(payload, uint.MaxValue)),
            (0x07, payload =>
            {
                WriteUInt32(payload, 1);
                payload.WriteByte(0);
                WriteUInt32(payload, uint.MaxValue);
            })
        };

        foreach (var (type, writePayload) in malformedActions)
        {
            using var data = new MemoryStream();
            WriteUInt32(data, 1);
            WriteAction(data, type, writePayload);
            var reader = new MapiReader(data.ToArray());
            var warnings = new List<string>();

            var value = NspiPropertyParser.ParseValue(
                ref reader,
                0x00FE,
                "Value",
                new MapiNodeBudget(),
                0,
                includePresence: false,
                warnings: warnings);

            Assert.True(reader.End);
            Assert.Contains(warnings, warning => warning.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(Flatten(value), node => node.Kind == MapiNodeKind.Raw);
        }
    }

    [Fact]
    public void ReportsMalformedRuleActionsAsPartialWithoutConsumingRawData()
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
                        stream.Write([0xFF, 0xFF, 0xFF, 0xFF]);
                    }),
                ("Content-Type", "application/mapi-http"),
                ("X-ResponseCode", "0"))));

        var response = Assert.Single(new SazParser().Parse(saz).Sessions).Mapi!.Response!;

        Assert.False(response.Complete);
        Assert.Contains(response.Warnings, warning => warning.Contains("count", StringComparison.Ordinal));
        Assert.Equal(MapiNodeKind.Raw, response.Root.Kind);
    }

    /// <summary>
    /// Regression proving OP_FORWARD/OP_DELEGATE's RecipientCount and each recipient's
    /// NoOfProperties retain the pre-existing 32-bit width in the default (extended-rule) context -
    /// resolved against current MS-OXORULE rather than the pinned upstream parser's narrower
    /// (always-16-bit) reads for this structure - and that a nested PtypBinary property inside a
    /// recipient's property list also keeps the 32-bit extended-rule length prefix.
    /// </summary>
    [Fact]
    public void ParsesForwardActionRecipientWithExtendedThirtyTwoBitCountsAndNestedPtypBinaryProperty()
    {
        var binaryValue = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        using var data = new MemoryStream();
        WriteUInt32(data, 1); // NoOfActions (32-bit, extended)
        WriteAction(
            data,
            0x07,
            payload =>
            {
                WriteUInt32(payload, 1); // RecipientCount (32-bit, extended)
                payload.WriteByte(0); // Reserved
                WriteUInt32(payload, 2); // Recipient.NoOfProperties (32-bit, extended)
                WriteUInt16(payload, 0x0003);
                WriteUInt16(payload, 0x3001);
                WriteUInt32(payload, 42);
                WriteUInt16(payload, 0x0102);
                WriteUInt16(payload, 0x6684);
                WriteUInt32(payload, checked((uint)binaryValue.Length)); // 32-bit PtypBinary length
                payload.Write(binaryValue);
            },
            flavor: 0x03);
        var reader = new MapiReader(data.ToArray());
        var warnings = new List<string>();

        var value = NspiPropertyParser.ParseValue(
            ref reader,
            0x00FE,
            "Value",
            new MapiNodeBudget(),
            0,
            includePresence: false,
            warnings: warnings);

        Assert.True(reader.End);
        Assert.Empty(warnings);
        Assert.Equal(4, Find(value, "RecipientCount").Length);
        Assert.Equal("1", Find(value, "RecipientCount").Value);
        Assert.Equal(4, Find(value, "NoOfProperties").Length);
        Assert.Equal("2", Find(value, "NoOfProperties").Value);

        var longProperty = Find(value, "PropertyValue[0]");
        Assert.Equal("42", Find(longProperty, "PropertyValue").Value);

        var binaryProperty = Find(value, "PropertyValue[1]");
        Assert.Equal($"{binaryValue.Length:N0} bytes", Find(binaryProperty, "PropertyValue").Value);
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

    private static byte[] ExecuteRequest(byte[] operation, uint serverHandle = 0xFFFFFFFF)
    {
        var ropPayload = BuildBody(
            stream =>
            {
                WriteUInt16(stream, checked((ushort)(2 + operation.Length)));
                stream.Write(operation);
                WriteUInt32(stream, serverHandle);
            });
        var extended = ExtendedBuffer(ropPayload, flags: 0x0004);
        return BuildBody(
            stream =>
            {
                WriteUInt32(stream, 0);
                WriteUInt32(stream, checked((uint)extended.Length));
                stream.Write(extended);
                WriteUInt32(stream, 4096);
                WriteUInt32(stream, 0);
            });
    }

    private static byte[] ExecuteResponse(byte[] operation, uint serverHandle)
    {
        var ropPayload = BuildBody(
            stream =>
            {
                WriteUInt16(stream, checked((ushort)(2 + operation.Length)));
                stream.Write(operation);
                WriteUInt32(stream, serverHandle);
            });
        var extended = ExtendedBuffer(ropPayload, flags: 0x0004);
        return BuildResponse(
            stream =>
            {
                WriteUInt32(stream, 0);
                WriteUInt32(stream, checked((uint)extended.Length));
                stream.Write(extended);
            });
    }

    private static byte[] PrivateLogonResponse() =>
        BuildBody(
            stream =>
            {
                stream.Write([0xFE, 0x00]);
                WriteUInt32(stream, 0);
                stream.WriteByte(0x01);
                stream.Write(new byte[13 * 8]);
                stream.WriteByte(0);
                stream.Write(new byte[16]);
                WriteUInt16(stream, 1);
                stream.Write(new byte[16]);
                stream.Write(new byte[8]);
                stream.Write(new byte[8]);
                WriteUInt32(stream, 0);
            });

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

    private static void WriteAction(
        Stream stream,
        byte type,
        Action<MemoryStream> writePayload,
        uint flavor = 0)
    {
        using var action = new MemoryStream();
        action.WriteByte(type);
        WriteUInt32(action, flavor);
        WriteUInt32(action, 0);
        writePayload(action);
        WriteUInt32(stream, checked((uint)action.Length));
        action.Position = 0;
        action.CopyTo(stream);
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
