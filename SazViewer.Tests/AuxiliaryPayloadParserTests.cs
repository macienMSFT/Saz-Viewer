using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

/// <summary>
/// Focused coverage for <c>AuxiliaryPayloadParser</c>, the semantic dispatcher for AUX_HEADER-framed
/// auxiliary blocks ([MS-OXCRPC] 2.2.2.2). Exercises all 40 version/type dispatch entries, variable
/// offset-based strings/bytes, malformed/truncated/unknown-version/unknown-type handling, node
/// budget and cancellation enforcement, and HTML-safe rendering of decoded field values.
/// This file is intentionally separate from MapiParserTests.cs.
/// </summary>
public sealed class AuxiliaryPayloadParserTests
{
    // ---- All 40 version/type dispatch entries -----------------------------------------------------

    [Fact]
    public void DispatchesAllFortyVersionOneAndVersionTwoAuxiliaryBlockTypes()
    {
        var cases = AllDispatchCases();
        Assert.Equal(40, cases.Count);

        var buffer = Concat(cases.Select(c => Block(c.Version, c.Type, c.Payload)).ToArray());
        var warnings = new List<string>();
        var budget = new MapiNodeBudget();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, budget, CancellationToken.None);

        Assert.Equal(40, nodes.Length);
        Assert.Empty(warnings);

        for (var i = 0; i < cases.Count; i++)
        {
            var block = nodes[i];
            Assert.Equal($"Auxiliary block {i}", block.Name);
            var typeField = Find(block, "Type");
            Assert.Equal(AuxiliaryPayloadParser.AuxiliaryTypeName(cases[i].Version, cases[i].Type), typeField.Value);

            var auxBlock = Find(block, "AuxiliaryBlock");
            Assert.DoesNotContain(auxBlock.Children, c => c.Name.Contains("Malformed", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(DispatchCaseNames))]
    public void EachDispatchCaseProducesItsExpectedLabelInIsolation(int caseIndex)
    {
        var cases = AllDispatchCases();
        var testCase = cases[caseIndex];
        var buffer = Block(testCase.Version, testCase.Type, testCase.Payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Equal(AuxiliaryPayloadParser.AuxiliaryTypeName(testCase.Version, testCase.Type), Find(block, "Type").Value);
        Assert.Empty(warnings);
    }

    public static IEnumerable<object[]> DispatchCaseNames() =>
        Enumerable.Range(0, AllDispatchCases().Count).Select(i => new object[] { i });

    private sealed record DispatchCase(byte Version, byte Type, byte[] Payload);

    private static List<DispatchCase> AllDispatchCases()
    {
        var requestId = new byte[4]; // SessionID, RequestID
        var defMdbSuccess = new byte[12]; // TimeSinceRequest, TimeToCompleteRequest, RequestID, Reserved
        var defGcSuccess = new byte[16]; // ServerID, SessionID, TimeSinceRequest, TimeToCompleteRequest, RequestOperation, Reserved(3)
        var mdbSuccess = new byte[16]; // ClientID, ServerID, SessionID, RequestID, TimeSinceRequest, TimeToCompleteRequest
        var mdbSuccessV2 = new byte[20];
        var gcSuccess = new byte[20];
        var gcSuccessV2 = new byte[20];
        var failure = new byte[24];
        var failureV2 = new byte[28];
        var clientControl = new byte[8];
        var processInfo = ProcessInfoPayload("proc");
        var osVersionInfo = new byte[156];
        var exOrgInfo = new byte[4];
        var perfAccountInfo = new byte[20];
        var endpointCapabilities = new byte[4];
        var exceptionTrace = ExceptionTracePayload("hello");
        var clientConnectionInfo = ClientConnectionInfoPayload("ctx");
        var serverSessionInfo = ServerSessionInfoPayload("session-ctx");
        var protocolDeviceId = ProtocolDeviceIdentificationPayload("mfg", "model", "serial", "ver", "fw");
        var clientInfo = ClientInfoPayload("machine", "user", [1, 2, 3, 4], [255, 255, 255, 0], "eth0", [1, 2, 3, 4, 5, 6]);
        var serverInfo = ServerInfoPayload("dn", "server-name");
        var sessionInfoV1 = new byte[20];
        var sessionInfoV2 = new byte[24];

        return
        [
            new DispatchCase(1, 0x01, requestId),
            new DispatchCase(1, 0x02, clientInfo),
            new DispatchCase(1, 0x03, serverInfo),
            new DispatchCase(1, 0x04, sessionInfoV1),
            new DispatchCase(1, 0x05, defMdbSuccess),
            new DispatchCase(1, 0x06, defGcSuccess),
            new DispatchCase(1, 0x07, mdbSuccess),
            new DispatchCase(1, 0x08, gcSuccess),
            new DispatchCase(1, 0x09, failure),
            new DispatchCase(1, 0x0A, clientControl),
            new DispatchCase(1, 0x0B, processInfo),
            new DispatchCase(1, 0x0C, defMdbSuccess),
            new DispatchCase(1, 0x0D, defGcSuccess),
            new DispatchCase(1, 0x0E, mdbSuccess),
            new DispatchCase(1, 0x0F, gcSuccess),
            new DispatchCase(1, 0x10, failure),
            new DispatchCase(1, 0x11, defMdbSuccess),
            new DispatchCase(1, 0x12, defGcSuccess),
            new DispatchCase(1, 0x13, mdbSuccess),
            new DispatchCase(1, 0x14, gcSuccess),
            new DispatchCase(1, 0x15, failure),
            new DispatchCase(1, 0x16, osVersionInfo),
            new DispatchCase(1, 0x17, exOrgInfo),
            new DispatchCase(1, 0x18, perfAccountInfo),
            new DispatchCase(1, 0x48, endpointCapabilities),
            new DispatchCase(1, 0x49, exceptionTrace),
            new DispatchCase(1, 0x4A, clientConnectionInfo),
            new DispatchCase(1, 0x4B, serverSessionInfo),
            new DispatchCase(1, 0x4E, protocolDeviceId),
            new DispatchCase(2, 0x04, sessionInfoV2),
            new DispatchCase(2, 0x07, mdbSuccessV2),
            new DispatchCase(2, 0x08, gcSuccessV2),
            new DispatchCase(2, 0x09, failureV2),
            new DispatchCase(2, 0x0B, processInfo),
            new DispatchCase(2, 0x0E, mdbSuccessV2),
            new DispatchCase(2, 0x0F, gcSuccessV2),
            new DispatchCase(2, 0x10, failureV2),
            new DispatchCase(2, 0x13, mdbSuccessV2),
            new DispatchCase(2, 0x14, gcSuccessV2),
            new DispatchCase(2, 0x15, failureV2),
        ];
    }

    // ---- Variable-length offset strings/bytes ------------------------------------------------------

    [Fact]
    public void ResolvesAllOffsetReferencedFieldsInPerfClientInfo()
    {
        var payload = ClientInfoPayload("HOST-1", "alice", [10, 0, 0, 1], [255, 255, 255, 0], "eth0", [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        var buffer = Block(1, 0x02, payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var block = Assert.Single(nodes);
        Assert.Equal("HOST-1", Find(block, "MachineName").Value);
        Assert.Equal("alice", Find(block, "UserName").Value);
        Assert.Equal("0A000001", Find(block, "ClientIP").Value);
        Assert.Equal("FFFFFF00", Find(block, "ClientIPMask").Value);
        Assert.Equal("eth0", Find(block, "AdapterName").Value);
        Assert.Equal("AABBCCDDEEFF", Find(block, "MacAddress").Value);
    }

    [Fact]
    public void TreatsZeroOffsetAsAbsentFieldForPerfServerInfo()
    {
        // Offsets of 0 mean "field absent" per [MS-OXCRPC]; only the fixed ServerID/ServerType fields
        // should appear, with no ServerDN/ServerName children and no warnings.
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 7); // ServerID
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), 2); // ServerType = PUBLIC
        // ServerDNOffset = 0, ServerNameOffset = 0 (left as zero)
        var buffer = Block(1, 0x03, payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var block = Assert.Single(nodes);
        Assert.Null(FindOrDefault(block, "ServerDN"));
        Assert.Null(FindOrDefault(block, "ServerName"));
        Assert.Contains("SERVERTYPE_PUBLIC", Find(block, "ServerType").Value);
    }

    [Fact]
    public void WarnsAndOmitsFieldWhenOffsetPointsOutsideTheBlockButKeepsSiblingFields()
    {
        var nameBytes = Utf16Z("server-name");
        var fixedPart = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(0, 2), 1); // ServerID
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(2, 2), 1); // ServerType
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(4, 2), 9000); // ServerDNOffset: far outside block
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(6, 2), checked((ushort)(4 + fixedPart.Length))); // ServerNameOffset: valid
        var payload = Concat(fixedPart, nameBytes);
        var buffer = Block(1, 0x03, payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Null(FindOrDefault(block, "ServerDN"));
        Assert.Equal("server-name", Find(block, "ServerName").Value);
        Assert.Contains(warnings, w => w.Contains("ServerDNOffset", StringComparison.Ordinal) && w.Contains("outside the", StringComparison.Ordinal));
    }

    [Fact]
    public void FallsBackToRawWhenOffsetStringHasNoNullTerminatorWithinTheBlock()
    {
        var fixedPart = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(4, 2), checked((ushort)(4 + fixedPart.Length))); // ServerDNOffset
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(6, 2), 0); // ServerNameOffset absent
        var unterminated = new byte[] { 0x41, 0x00, 0x42, 0x00 }; // "AB" with no UTF-16 null terminator
        var payload = Concat(fixedPart, unterminated);
        var buffer = Block(1, 0x03, payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Null(FindOrDefault(block, "ServerDN"));
        Assert.NotNull(FindOrDefault(block, "ServerDN (malformed)"));
        Assert.Contains(warnings, w => w.Contains("ServerDN at offset", StringComparison.Ordinal) && w.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvesAllFiveOffsetStringsInProtocolDeviceIdentification()
    {
        var payload = ProtocolDeviceIdentificationPayload("Contoso", "Model X", "SN-12345", "1.2.3", "9.9");
        var buffer = Block(1, 0x4E, payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var block = Assert.Single(nodes);
        Assert.Equal("Contoso", Find(block, "DeviceManufacturer").Value);
        Assert.Equal("Model X", Find(block, "DeviceModel").Value);
        Assert.Equal("SN-12345", Find(block, "DeviceSerialNumber").Value);
        Assert.Equal("1.2.3", Find(block, "DeviceVersion").Value);
        Assert.Equal("9.9", Find(block, "DeviceFirmwareVersion").Value);
    }

    // ---- Malformed / truncated / unknown / limits --------------------------------------------------

    [Fact]
    public void FallsBackToRawWhenDeclaredBlockSizeExceedsRemainingBytes()
    {
        var buffer = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0, 2), 100); // declares far more than we actually have
        buffer[2] = 1;
        buffer[3] = 0x01;
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var node = Assert.Single(nodes);
        Assert.Equal("Malformed auxiliary block", node.Name);
        Assert.Equal(MapiNodeKind.Raw, node.Kind);
        Assert.Equal(0, node.Offset);
        Assert.Equal(buffer.Length, node.Length);
        Assert.Equal(Convert.ToHexString(buffer), node.Value);
        Assert.Contains(warnings, w => w.Contains("invalid size", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsTrailingUnframedBytesAtEndOfStream()
    {
        var validBlock = Block(1, 0x01, new byte[4]);
        var buffer = Concat(validBlock, [0x01, 0x02]); // 2 trailing bytes, not enough for another header
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Equal(2, nodes.Length);
        Assert.Equal("Trailing auxiliary bytes", nodes[1].Name);
        Assert.Equal(MapiNodeKind.Raw, nodes[1].Kind);
        Assert.Contains(warnings, w => w.Contains("unframed bytes", StringComparison.Ordinal));
    }

    [Fact]
    public void FallsBackToRawPayloadWhenFixedStructFieldsAreTruncated()
    {
        // AUX_TYPE_PERF_REQUESTID needs 4 payload bytes (SessionID + RequestID); declare a block with
        // only 2 payload bytes so the semantic dispatch fails and the raw fallback engages.
        var buffer = Block(1, 0x01, new byte[2]);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        var auxBlock = Find(block, "AuxiliaryBlock");
        var raw = Assert.Single(auxBlock.Children);
        Assert.Equal("Malformed auxiliary block payload", raw.Name);
        Assert.Equal(MapiNodeKind.Raw, raw.Kind);
        Assert.Contains(warnings, w => w.Contains("could not be decoded", StringComparison.Ordinal));
    }

    [Fact]
    public void FallsBackToRawWithWarningForUnrecognizedVersionOneType()
    {
        var buffer = Block(1, 0x99, new byte[4]);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Equal("0x99 (unrecognized version-1 type)", Find(block, "Type").Value);
        var payload = Find(block, "Payload");
        Assert.Equal(4, payload.Offset);
        Assert.Equal(4, payload.Length);
        Assert.Contains(warnings, w => w.Contains("unrecognized version-1 type", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesMsOxcrpcServerCapabilitiesOmittedUpstream()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x0B);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(
            Block(1, 0x46, payload),
            0,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Empty(warnings);
        Assert.Equal("0x46 AUX_TYPE_SERVER_CAPABILITIES", Find(block, "Type").Value);
        var flags = Find(block, "ServerCapabilityFlags").Value;
        Assert.Contains("PACKED_FAST_TRANSFER_UPLOAD_BUFFERS", flags);
        Assert.Contains("PACKED_WRITE_STREAM_UPLOAD_BUFFERS", flags);
        Assert.Contains("ULTRA_LARGE_PACKED_DOWNLOAD_BUFFERS", flags);
    }

    [Fact]
    public void FallsBackToRawWithWarningForUnrecognizedVersionTwoType()
    {
        var buffer = Block(2, 0x99, new byte[4]);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Equal("0x99 (unrecognized version-2 type)", Find(block, "Type").Value);
        Assert.Contains(warnings, w => w.Contains("unrecognized version-2 type", StringComparison.Ordinal));
    }

    [Fact]
    public void FallsBackToRawWithWarningForUnsupportedVersion()
    {
        var buffer = Block(3, 0x01, new byte[4]);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Equal("0x01 (unsupported version 3)", Find(block, "Type").Value);
        Assert.Contains(warnings, w => w.Contains("unsupported version 3", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesMultiLineExceptionTraceIntoIndividualLineFields()
    {
        var payload = ExceptionTracePayload("First failure\r\nSecond failure\nThird failure");
        var buffer = Block(1, 0x49, payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var block = Assert.Single(nodes);
        var messageArray = Find(block, "ExceptionMessage");
        Assert.Equal(MapiNodeKind.Array, messageArray.Kind);
        Assert.Equal(3, messageArray.Children.Length);
        Assert.Equal("First failure", messageArray.Children[0].Value);
        Assert.Equal("Second failure", messageArray.Children[1].Value);
        Assert.Equal("Third failure", messageArray.Children[2].Value);
        Assert.DoesNotContain("\r", messageArray.Children[0].Value);
    }

    [Fact]
    public void OmitsExceptionMessageArrayWhenTraceHasNoTextAfterRopIndex()
    {
        var payload = new byte[4]; // RopIndex only, no trailing message bytes
        var buffer = Block(1, 0x49, payload);
        var warnings = new List<string>();

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, warnings, new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        Assert.Null(FindOrDefault(block, "ExceptionMessage"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void ThrowsOperationCanceledWhenTokenIsAlreadyCancelled()
    {
        var buffer = Block(1, 0x01, new byte[4]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => AuxiliaryPayloadParser.Parse(buffer, 0, new List<string>(), new MapiNodeBudget(), cts.Token));
    }

    [Fact]
    public void ThrowsWhenNodeBudgetIsExhausted()
    {
        var buffer = Concat(
            Block(1, 0x01, new byte[4]),
            Block(1, 0x01, new byte[4]),
            Block(1, 0x01, new byte[4]));
        var budget = new MapiNodeBudget();
        budget.Claim(0, 24_999); // Leave headroom below MaxNodes (25,000) so the next block overflows it.

        Assert.Throws<MapiParseException>(
            () => AuxiliaryPayloadParser.Parse(buffer, 0, new List<string>(), budget, CancellationToken.None));
    }

    // ---- HTML-safe tree behavior --------------------------------------------------------------------

    [Fact]
    public void PreservesHtmlSpecialCharactersAsPlainNodeValuesWithoutEncodingInTheParser()
    {
        const string attack = "</script><img src=x onerror=alert(1)>";
        var payload = ClientInfoPayload(attack, "user", [1, 2, 3, 4], [255, 255, 255, 0], "eth0", [1, 2, 3, 4, 5, 6]);
        var buffer = Block(1, 0x02, payload);

        var nodes = AuxiliaryPayloadParser.Parse(buffer, 0, new List<string>(), new MapiNodeBudget(), CancellationToken.None);

        var block = Assert.Single(nodes);
        // The parser must not escape or reject special characters; escaping is the report renderer's job.
        Assert.Equal(attack, Find(block, "MachineName").Value);
    }

    [Fact]
    public void HtmlReportGeneratorEscapesDecodedAuxiliaryFieldValuesInTheProtocolTree()
    {
        const string attack = "</script><img src=x onerror=globalThis.pwned=true>";
        var payload = ClientInfoPayload(attack, "user", [1, 2, 3, 4], [255, 255, 255, 0], "eth0", [1, 2, 3, 4, 5, 6]);
        var buffer = Block(1, 0x02, payload);
        var root = new MapiNode(
            "Auxiliary",
            MapiNodeKind.Structure,
            0,
            buffer.Length,
            null,
            AuxiliaryPayloadParser.Parse(buffer, 0, new List<string>(), new MapiNodeBudget(), CancellationToken.None));
        var parse = new MapiMessageParse(MapiDirection.Request, root, ImmutableArray<string>.Empty, true, buffer.Length, buffer.Length);
        var mapi = new MapiSession(
            "1",
            0,
            MapiEndpoint.Mailbox,
            "Connect",
            "0",
            false,
            parse,
            null,
            ImmutableArray<string>.Empty);
        var session = new HttpSession
        {
            Id = "1",
            ArchiveOrder = 0,
            Method = "POST",
            Url = "https://example.test/mapi",
            StatusCode = 200,
            Request = Message("POST /mapi HTTP/1.1", "application/mapi-http", "binary")
        };
        session.Mapi = mapi;
        var report = new SazReport { SourceName = "aux.saz" };
        report.Sessions.Add(session);

        var html = new HtmlReportGenerator().Generate(report);

        Assert.Contains("data-protocol=", html, StringComparison.Ordinal);
        // The report renders the protocol tree as JSON embedded in an HTML attribute. The JSON
        // encoder itself escapes '<'/'>' as \u003C/\u003E (defense in depth against browsers that
        // might sniff HTML out of a script/style context), and the attribute is additionally
        // HTML-encoded, so the raw attack string must never appear verbatim in the output and the
        // dangerous substrings must never appear unescaped.
        Assert.DoesNotContain(attack, html, StringComparison.Ordinal);
        Assert.DoesNotContain("</script><img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x onerror", html, StringComparison.Ordinal);
        Assert.Contains("\\u003C/script\\u003E\\u003Cimg src=x onerror=globalThis.pwned=true\\u003E", html, StringComparison.Ordinal);
    }

    // ---- Payload builders -----------------------------------------------------------------------------

    private static byte[] ClientInfoPayload(
        string machineName,
        string userName,
        byte[] clientIp,
        byte[] clientIpMask,
        string adapterName,
        byte[] macAddress)
    {
        var machineNameBytes = Utf16Z(machineName);
        var userNameBytes = Utf16Z(userName);
        var adapterNameBytes = Utf16Z(adapterName);

        const int fixedLength = 28;
        var machineNameOffset = 4 + fixedLength;
        var userNameOffset = machineNameOffset + machineNameBytes.Length;
        var clientIpOffset = userNameOffset + userNameBytes.Length;
        var clientIpMaskOffset = clientIpOffset + clientIp.Length;
        var adapterNameOffset = clientIpMaskOffset + clientIpMask.Length;
        var macAddressOffset = adapterNameOffset + adapterNameBytes.Length;

        var fixedPart = new byte[fixedLength];
        var span = fixedPart.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], 100); // AdapterSpeed
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], 1); // ClientID
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], checked((ushort)machineNameOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], checked((ushort)userNameOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..12], checked((ushort)clientIp.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..14], checked((ushort)clientIpOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..16], checked((ushort)clientIpMask.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..18], checked((ushort)clientIpMaskOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..20], checked((ushort)adapterNameOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], checked((ushort)macAddress.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], checked((ushort)macAddressOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(span[24..26], 2); // ClientMode = CACHED
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..28], 0); // Reserved

        return Concat(fixedPart, machineNameBytes, userNameBytes, clientIp, clientIpMask, adapterNameBytes, macAddress);
    }

    private static byte[] ServerInfoPayload(string serverDn, string serverName)
    {
        var dnBytes = Utf16Z(serverDn);
        var nameBytes = Utf16Z(serverName);
        const int fixedLength = 8;
        var dnOffset = 4 + fixedLength;
        var nameOffset = dnOffset + dnBytes.Length;

        var fixedPart = new byte[fixedLength];
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(0, 2), 3); // ServerID
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(2, 2), 1); // ServerType = PRIVATE
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(4, 2), checked((ushort)dnOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(6, 2), checked((ushort)nameOffset));

        return Concat(fixedPart, dnBytes, nameBytes);
    }

    private static byte[] ProcessInfoPayload(string processName)
    {
        var nameBytes = Utf16Z(processName);
        const int fixedLength = 24;
        var nameOffset = 4 + fixedLength;

        var fixedPart = new byte[fixedLength];
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(0, 2), 42); // ProcessID
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(2, 2), 0); // Reserved1
        // ProcessGuid occupies bytes [4..20)
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(20, 2), checked((ushort)nameOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(22, 2), 0); // Reserved2

        return Concat(fixedPart, nameBytes);
    }

    private static byte[] ClientConnectionInfoPayload(string contextInfo)
    {
        var contextBytes = Utf16Z(contextInfo);
        const int fixedLength = 28;
        var contextOffset = 4 + fixedLength;

        var fixedPart = new byte[fixedLength];
        // ConnectionGUID occupies bytes [0..16)
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(16, 2), checked((ushort)contextOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(18, 2), 0); // Reserved
        BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(20, 4), 1); // ConnectionAttempts
        BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(24, 4), 1); // ConnectionFlags = cached mode

        return Concat(fixedPart, contextBytes);
    }

    private static byte[] ServerSessionInfoPayload(string contextInfo)
    {
        var contextBytes = Utf16Z(contextInfo);
        const int fixedLength = 2;
        var contextOffset = 4 + fixedLength;

        var fixedPart = new byte[fixedLength];
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(0, 2), checked((ushort)contextOffset));

        return Concat(fixedPart, contextBytes);
    }

    private static byte[] ProtocolDeviceIdentificationPayload(
        string manufacturer,
        string model,
        string serial,
        string version,
        string firmware)
    {
        var manufacturerBytes = Utf16Z(manufacturer);
        var modelBytes = Utf16Z(model);
        var serialBytes = Utf16Z(serial);
        var versionBytes = Utf16Z(version);
        var firmwareBytes = Utf16Z(firmware);

        const int fixedLength = 10;
        var manufacturerOffset = 4 + fixedLength;
        var modelOffset = manufacturerOffset + manufacturerBytes.Length;
        var serialOffset = modelOffset + modelBytes.Length;
        var versionOffset = serialOffset + serialBytes.Length;
        var firmwareOffset = versionOffset + versionBytes.Length;

        var fixedPart = new byte[fixedLength];
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(0, 2), checked((ushort)manufacturerOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(2, 2), checked((ushort)modelOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(4, 2), checked((ushort)serialOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(6, 2), checked((ushort)versionOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(fixedPart.AsSpan(8, 2), checked((ushort)firmwareOffset));

        return Concat(fixedPart, manufacturerBytes, modelBytes, serialBytes, versionBytes, firmwareBytes);
    }

    private static byte[] ExceptionTracePayload(string message)
    {
        var ropIndex = new byte[4];
        return Concat(ropIndex, Encoding.ASCII.GetBytes(message));
    }

    // ---- Generic byte-buffer helpers -------------------------------------------------------------

    private static byte[] Block(byte version, byte type, byte[] payload)
    {
        var size = checked((ushort)(4 + payload.Length));
        var block = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), size);
        block[2] = version;
        block[3] = type;
        payload.CopyTo(block, 4);
        return block;
    }

    private static byte[] Utf16Z(string value) => Encoding.Unicode.GetBytes(value + "\0");

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static HttpMessage Message(string startLine, string contentType, string body)
    {
        var message = new HttpMessage
        {
            StartLine = startLine,
            Body = new BodyPreview
            {
                Length = body.Length,
                Preview = body
            }
        };
        message.Headers.Add(new HttpHeader("Content-Type", contentType));
        return message;
    }

    private static MapiNode Find(MapiNode node, string name) =>
        FindOrDefault(node, name) ?? throw new Xunit.Sdk.XunitException($"Node '{name}' was not found.");

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
}
