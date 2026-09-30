using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

/// <summary>
/// Integration coverage for <see cref="RopVariableDispatcher"/> - the single routing point that picks
/// among the four independently authored, self-contained family decoder modules
/// (<see cref="RopFolderTableDecoders"/>, <see cref="RopPropertyStoreDecoders"/>,
/// <see cref="RopMessageRulesDecoders"/>, <see cref="RopFastTransferDecoders"/>) for every RopId that
/// <see cref="RopSemanticParser"/> has no fixed-width schema for.
/// <para>
/// These tests exercise the dispatcher at three depths: (1) its own <c>Supports</c> contract against
/// every RopId/direction pair, cross-checked for overlap against both the other families and the
/// fixed-schema catalog; (2) the real public entry point a production caller reaches
/// (<see cref="RopBufferParser.Parse"/>), including multi-operation lists that mix operations from
/// several families and malformed/truncated input; and (3) the full MAPI/HTTP message pipeline
/// (<see cref="MapiHttpMessageParser.Parse"/> with a real <see cref="MapiCaptureContext"/>), proving
/// the capture-local FastTransfer stream assembler and capture scope are threaded correctly end to
/// end.
/// </para>
/// </summary>
public sealed class RopVariableDispatcherTests
{
    // ---------------------------------------------------------------------------------------------
    // Coverage / overlap invariants - the permanent regression guard for the four-family wiring.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SupportsIsExactlyTheUnionOfAllFourFamiliesForEveryByteAndDirection()
    {
        foreach (var direction in new[] { MapiDirection.Request, MapiDirection.Response })
        {
            for (var value = 0; value <= 0xFF; value++)
            {
                var ropId = (byte)value;
                var expected =
                    RopFolderTableDecoders.Supports(direction, ropId) ||
                    RopPropertyStoreDecoders.Supports(direction, ropId) ||
                    RopMessageRulesDecoders.Supports(direction, ropId) ||
                    RopFastTransferDecoders.Supports(direction, ropId);
                Assert.True(
                    expected == RopVariableDispatcher.Supports(direction, ropId),
                    $"RopVariableDispatcher.Supports disagrees with the family union for {direction} 0x{ropId:X2}.");
            }
        }
    }

    [Fact]
    public void NoTwoFamiliesEverClaimTheSameRopIdAndDirection()
    {
        foreach (var direction in new[] { MapiDirection.Request, MapiDirection.Response })
        {
            for (var value = 0; value <= 0xFF; value++)
            {
                var ropId = (byte)value;
                var claimants = new[]
                {
                    RopFolderTableDecoders.Supports(direction, ropId),
                    RopPropertyStoreDecoders.Supports(direction, ropId),
                    RopMessageRulesDecoders.Supports(direction, ropId),
                    RopFastTransferDecoders.Supports(direction, ropId),
                }.Count(supported => supported);
                Assert.True(claimants <= 1, $"{direction} 0x{ropId:X2} is claimed by {claimants} of the four families.");
            }
        }
    }

    [Fact]
    public void NoVariableFamilyEverShadowsAFixedWidthSchema()
    {
        // RopSemanticParser always prefers a fixed schema when one exists (see ParseOperations'
        // hasFixedSchema check); a family claiming a RopId that already has one would be dead code
        // that RopVariableDispatcherTests must catch immediately if ever introduced.
        for (var value = 0; value <= 0xFF; value++)
        {
            var ropId = (byte)value;
            if (RopSemanticParser.RequestSchemas.ContainsKey(ropId))
            {
                Assert.False(
                    RopFolderTableDecoders.Supports(MapiDirection.Request, ropId) ||
                    RopPropertyStoreDecoders.Supports(MapiDirection.Request, ropId) ||
                    RopMessageRulesDecoders.Supports(MapiDirection.Request, ropId) ||
                    RopFastTransferDecoders.Supports(MapiDirection.Request, ropId),
                    $"Request 0x{ropId:X2} has both a fixed schema and a variable-family decoder.");
            }

            if (RopSemanticParser.ResponseSchemas.ContainsKey(ropId))
            {
                Assert.False(
                    RopFolderTableDecoders.Supports(MapiDirection.Response, ropId) ||
                    RopPropertyStoreDecoders.Supports(MapiDirection.Response, ropId) ||
                    RopMessageRulesDecoders.Supports(MapiDirection.Response, ropId) ||
                    RopFastTransferDecoders.Supports(MapiDirection.Response, ropId),
                    $"Response 0x{ropId:X2} has both a fixed schema and a variable-family decoder.");
            }
        }
    }

    [Fact]
    public void DispatcherSupportedCountsMatchThePinnedFourFamilyTotals()
    {
        // Pinned totals cross-checked by hand against each family's own SupportedRequestRopIds /
        // SupportedResponseRopIds (or RequestRopIds / ResponseRopIds) sets at integration time:
        // Folder 19/27, PropertyStore 27/31, MessageRules 15/13, FastTransfer 18/10 = 79/81.
        // (PropertyStore's response set grew from 25 to 28 when RopSetProperties (0x0A),
        // RopDeleteProperties (0x0B), and RopGetReceiveFolderTable (0x68) responses were added as
        // deterministic, non-state-dependent semantic decoders; it then grew from 28 to 31, and its
        // request set from 26 to 27, when the five state-dependent gaps closed: RopWritePerUserInformation
        // (0x64) request ReplGuid, RopGetPropertiesSpecific (0x07) response row data, RopLogon (0xFE)
        // response mailbox/public-folder shape, and RopBufferTooSmall (0xFF) response RequestBuffersSize
        // all keyed off capture-local <see cref="MapiCaptureContext"/> state; MessageRules' request set
        // grew from 14 to 15 for the fifth, RopSetMessageReadFlag (0x11) request ClientData.)
        // Combined with the two disjointness tests above, this pins the exact reachable surface so an
        // accidental removal (not just an accidental duplicate) is also caught. Combined with the fixed
        // schema catalog (RopSemanticParser.RequestSchemas/ResponseSchemas), this reaches the full
        // 128/131 request/response dispatcher coverage target - see MapiCaptureParser.KnownGaps for the
        // remaining, genuinely state-dependent-and-out-of-scope gaps.
        var requestCount = Enumerable.Range(0, 256).Count(v => RopVariableDispatcher.Supports(MapiDirection.Request, (byte)v));
        var responseCount = Enumerable.Range(0, 256).Count(v => RopVariableDispatcher.Supports(MapiDirection.Response, (byte)v));
        Assert.Equal(79, requestCount);
        Assert.Equal(81, responseCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Multi-operation boundary parsing across families, through the real public entry point.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesAFourFamilyMixedRequestOperationListWithExactBoundaries()
    {
        // Operation 0: RopOpenFolder (0x02, RopFolderTableDecoders).
        var op0 = Concat([0x02, 0x00, 0x00, 0x01], Le((ushort)0x0201), [0, 0, 0, 0, 0, 0], [0x00]);
        // Operation 1: RopGetPropertiesAll (0x08, RopPropertyStoreDecoders).
        var op1 = Concat([0x08, 0x00, 0x02], Le((ushort)0xFFFF), Le((ushort)0x0001));
        // Operation 2: RopSetMessageStatus (0x20, RopMessageRulesDecoders).
        var op2 = Concat([0x20, 0x00, 0x03], Le((ushort)0x0403), [0, 0, 0, 0, 0, 0], Le((uint)0x00000001), Le((uint)0x00000001));
        // Operation 3: RopFastTransferDestinationConfigure (0x53, RopFastTransferDecoders).
        var op3 = new byte[] { 0x53, 0x00, 0x04, 0x05, 0x01, 0x00 };

        var ropList = Concat(op0, op1, op2, op3);
        Assert.Equal(13, op0.Length);
        Assert.Equal(7, op1.Length);
        Assert.Equal(19, op2.Length);
        Assert.Equal(6, op3.Length);

        var buffer = Frame(ropList, 0, 0, 0, 0, 0, 0);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(4, ropListNode.Children.Length);
        Assert.StartsWith("RopOpenFolder", ropListNode.Children[0].Value);
        Assert.Equal(op0.Length, ropListNode.Children[0].Length);
        Assert.StartsWith("RopGetPropertiesAll", ropListNode.Children[1].Value);
        Assert.Equal(op1.Length, ropListNode.Children[1].Length);
        Assert.StartsWith("RopSetMessageStatus", ropListNode.Children[2].Value);
        Assert.Equal(op2.Length, ropListNode.Children[2].Length);
        Assert.StartsWith("RopFastTransferDestinationConfigure", ropListNode.Children[3].Value);
        Assert.Equal(op3.Length, ropListNode.Children[3].Length);
    }

    [Fact]
    public void StopsAtAMalformedOperationInsideAMixedFamilyListWithoutGuessingFurtherBoundaries()
    {
        // Operation 0: a complete, valid RopOpenFolder (RopFolderTableDecoders).
        var op0 = Concat([0x02, 0x00, 0x00, 0x01], Le((ushort)0x0201), [0, 0, 0, 0, 0, 0], [0x00]);
        // Operation 1: RopGetPropertiesAll (RopPropertyStoreDecoders) truncated after InputHandleIndex
        // - PropertySizeLimit and WantUnicode are entirely missing.
        byte[] truncatedOp1 = [0x08, 0x00, 0x02];

        var ropList = Concat(op0, truncatedOp1);
        var buffer = Frame(ropList, 0, 0, 0);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(2, ropListNode.Children.Length);
        Assert.StartsWith("RopOpenFolder", ropListNode.Children[0].Value);
        Assert.Equal("Operation 1 (malformed)", ropListNode.Children[1].Name);
        Assert.Empty(ropListNode.Children[1].Children);
        Assert.Contains(warnings, w => w.Contains("could not be parsed", StringComparison.Ordinal) && w.Contains("no further operations", StringComparison.Ordinal));
    }

    [Fact]
    public void HostileOversizedCountThroughTheDispatcherIsCaughtAsMalformedNotAnUnhandledException()
    {
        // RopGetPropertiesSpecific (0x07, RopPropertyStoreDecoders) declares 0xFFFF property tags
        // but the list ends immediately afterwards - a classic hostile-input "claim more than exists"
        // shape. The dispatcher must never let this escape as anything other than a contained
        // MapiParseException-driven "(malformed)" fallback; it must not throw an unrelated .NET
        // exception (e.g. OutOfMemoryException/OverflowException) or hang.
        byte[] ropList = [0x07, 0x00, 0x00, .. Le((ushort)0xFFFF), 0x00, 0x00, .. Le((ushort)0xFFFF)];
        var buffer = Frame(ropList);
        var warnings = new List<string>();

        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Request, warnings, new MapiNodeBudget(), CancellationToken.None);

        var ropListNode = Find(nodes, "ROP list");
        Assert.Single(ropListNode.Children);
        Assert.Equal("Operation 0 (malformed)", ropListNode.Children[0].Name);
        Assert.Contains(warnings, w => w.Contains("could not be parsed", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesAMixedResponseOperationListAcrossPropertyStoreAndFolderFamiliesWithExactBoundaries()
    {
        // Operation 0: RopSetProperties response (0x0A, RopPropertyStoreDecoders) - success, 1 problem.
        var propertyProblem = Concat(Le((ushort)0), Le((ushort)0x0003), Le((ushort)7), Le((uint)0x80000001));
        var op0 = Concat([0x0A, 0x00], Le((uint)0), Le((ushort)1), propertyProblem);
        // Operation 1: RopGetReceiveFolderTable response (0x68, RopPropertyStoreDecoders) - success, 1 row.
        var row = Concat(
            [0x00], // PropertyRow.Flag: all values present
            Le((ulong)0x1122334455667788), // PidTagFolderId (PtypInteger64)
            Encoding.ASCII.GetBytes("IPM.Note"), [0x00], // PidTagMessageClass (PtypString8)
            Le((ulong)0x0102030405060708)); // PidTagLastModificationTime (PtypTime)
        var op1 = Concat([0x68, 0x01], Le((uint)0), Le((uint)1), row);
        // Operation 2: RopOpenFolder response (0x02, RopFolderTableDecoders) - success, not ghosted.
        var op2 = Concat([0x02, 0x02], Le((uint)0), [0x00, 0x00]);

        var ropList = Concat(op0, op1, op2);
        var buffer = Frame(ropList, 0, 0, 0);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Response, warnings, new MapiNodeBudget(), CancellationToken.None);

        Assert.Empty(warnings);
        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(3, ropListNode.Children.Length);
        Assert.StartsWith("RopSetProperties", ropListNode.Children[0].Value);
        Assert.Equal(op0.Length, ropListNode.Children[0].Length);
        Assert.Single(Find(ropListNode.Children[0].Children, "PropertyProblems").Children);
        Assert.StartsWith("RopGetReceiveFolderTable", ropListNode.Children[1].Value);
        Assert.Equal(op1.Length, ropListNode.Children[1].Length);
        Assert.Single(Find(ropListNode.Children[1].Children, "Rows").Children);
        Assert.StartsWith("RopOpenFolder", ropListNode.Children[2].Value);
        Assert.Equal(op2.Length, ropListNode.Children[2].Length);
    }

    [Fact]
    public void StopsAtATruncatedRopGetReceiveFolderTableRowInsideAMixedFamilyListWithoutGuessingFurtherBoundaries()
    {
        // Operation 0: a complete RopSetProperties response (0x0A, RopPropertyStoreDecoders), success, no problems.
        var op0 = Concat([0x0A, 0x00], Le((uint)0), Le((ushort)0));
        // Operation 1: RopGetReceiveFolderTable response (0x68) claims RowCount=1 but the row bytes are
        // entirely missing - a classic hostile-input "claim more than exists" truncation.
        byte[] truncatedOp1 = [0x68, 0x01, .. Le((uint)0), .. Le((uint)1)];

        var ropList = Concat(op0, truncatedOp1);
        var buffer = Frame(ropList, 0, 0);
        var warnings = new List<string>();
        var nodes = RopBufferParser.Parse(buffer, 0, MapiDirection.Response, warnings, new MapiNodeBudget(), CancellationToken.None);

        var ropListNode = Find(nodes, "ROP list");
        Assert.Equal(2, ropListNode.Children.Length);
        Assert.StartsWith("RopSetProperties", ropListNode.Children[0].Value);
        Assert.Equal("Operation 1 (malformed)", ropListNode.Children[1].Name);
        Assert.Contains(warnings, w => w.Contains("could not be parsed", StringComparison.Ordinal) && w.Contains("no further operations", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // FastTransfer capture-local reassembly, through the real dispatcher/buffer path.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void JoinsAFastTransferValueSplitAcrossTwoOperationsInTheSameRopListWhenAnAssemblerIsSupplied()
    {
        var value = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le((uint)value.Length), value[..8]);
        var tail = Concat(value[8..], Le(0x400D0003u));

        var op0 = BuildFastTransferGetBufferResponse(handleIndex: 3, status: 0x0001, transferBuffer: head);
        var op1 = BuildFastTransferGetBufferResponse(handleIndex: 3, status: 0x0003, transferBuffer: tail);
        const uint serverHandle = 0x11223344;
        var buffer = Frame(Concat(op0, op1), 0, 0, 0, serverHandle);

        var assembler = new FastTransferStreamAssembler();
        var scope = "dispatcher-join-scope";
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope(scope, "logical-connection");
        var joinedWarnings = new List<string>();
        var joined = RopBufferParser.Parse(
            buffer,
            0,
            MapiDirection.Response,
            joinedWarnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            assembler,
            scope,
            context);

        var joinedList = Find(joined, "ROP list");
        var joinedOp0Buffer = Find(joinedList.Children[0].Children, "TransferBuffer");
        var joinedOp1Buffer = Find(joinedList.Children[1].Children, "TransferBuffer");
        Assert.Contains("ends inside a value", joinedOp0Buffer.Value);
        Assert.DoesNotContain("ends inside a value", joinedOp1Buffer.Value);
        Assert.Equal("PartialValueContinuation", joinedOp1Buffer.Children[0].Name);
        Assert.Contains("value complete", joinedOp1Buffer.Children[0].Value);

        var key = new FastTransferStreamKey("logical-connection", serverHandle);
        Assert.Equal(2, assembler.StateFor(key).BufferCount);

        // Without a shared assembler, each buffer is lexed independently (FastTransferStreamState.Initial
        // every time), so the second buffer's tail bytes can never be recognized as a continuation -
        // proving the assembler/captureScope threading, not the lexer alone, is what joined the value
        // above.
        var standaloneWarnings = new List<string>();
        var standalone = RopBufferParser.Parse(
            buffer, 0, MapiDirection.Response, standaloneWarnings, new MapiNodeBudget(), CancellationToken.None);
        var standaloneOp1Buffer = Find(Find(standalone, "ROP list").Children[1].Children, "TransferBuffer");
        Assert.NotEqual("PartialValueContinuation", standaloneOp1Buffer.Children.Length > 0 ? standaloneOp1Buffer.Children[0].Name : null);
    }

    [Fact]
    public void ThreadsTheCaptureLocalAssemblerAndScopeThroughTheFullMapiHttpMessagePipeline()
    {
        var value = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le((uint)value.Length), value[..8]);
        var tail = Concat(value[8..], Le(0x400D0003u));

        var op0 = BuildFastTransferGetBufferResponse(handleIndex: 3, status: 0x0001, transferBuffer: head);
        var op1 = BuildFastTransferGetBufferResponse(handleIndex: 3, status: 0x0003, transferBuffer: tail);
        const uint serverHandle = 0x55667788;
        var ropBuf = Concat(
            Le((ushort)(op0.Length + op1.Length + 2)),
            op0,
            op1,
            Le(0u),
            Le(0u),
            Le(0u),
            Le(serverHandle));

        // RPC_HEADER_EXT wrapper: Version(0)+Flags(0)+Size+SizeActual, uncompressed/unencoded.
        var extendedBuffer = Concat(Le((ushort)0), Le((ushort)0), Le((ushort)ropBuf.Length), Le((ushort)ropBuf.Length), ropBuf);

        // EXECUTE response body: Flags + RopBufferSize + RopOutputBuffer (the extended buffer above).
        var executeBody = Concat(Le((uint)0), Le((uint)extendedBuffer.Length), extendedBuffer);

        // Full MAPI/HTTP response: a blank additional-headers line, StatusCode, ErrorCode, then body.
        var message = Concat([(byte)'\n'], Le((uint)0), Le((uint)0), executeBody);

        var context = new MapiCaptureContext();
        var warnings = new List<string>();
        var scope = "full-pipeline-scope";
        context.RegisterLogonCorrelationScope(scope, "full-pipeline-connection");
        var root = MapiHttpMessageParser.Parse(
            message, "Execute", MapiDirection.Response, context, warnings, new MapiNodeBudget(), CancellationToken.None,
            out var parsedBytes, scope);

        Assert.Equal(message.Length, parsedBytes);
        // The synthetic buffer omits a server object handle table (irrelevant to this test) and its
        // FastTransfer payload ends on an EndMessage marker, so a couple of benign informational
        // warnings are expected; no warning here may indicate a parse failure.
        Assert.DoesNotContain(warnings, w => w.Contains("could not be parsed", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("malformed", StringComparison.OrdinalIgnoreCase));

        var ropOutputBuffer = Find(root.Children, "RopOutputBuffer");
        var extended = Find(ropOutputBuffer.Children, "Extended buffer 0");
        var ropPayload = Find(extended.Children, "ROP payload");
        var ropListNode = Find(ropPayload.Children, "ROP list");
        var op1Buffer = Find(ropListNode.Children[1].Children, "TransferBuffer");

        Assert.Equal("PartialValueContinuation", op1Buffer.Children[0].Name);
        Assert.Contains("value complete", op1Buffer.Children[0].Value);

        // The assembler that actually accumulated this state must be the very instance the capture
        // context exposes - proving MapiHttpMessageParser reads context.FastTransferAssembler (rather
        // than constructing its own) and forwards it, together with the caller-supplied captureScope,
        // all the way down to RopFastTransferDecoders.
        var key = new FastTransferStreamKey("full-pipeline-connection", serverHandle);
        Assert.Equal(2, context.FastTransferAssembler.StateFor(key).BufferCount);
    }

    [Fact]
    public void JoinsFastTransferAcrossHttpRoundTripsByLogicalConnectionAndServerHandle()
    {
        var value = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le((uint)value.Length), value[..8]);
        var tail = Concat(value[8..], Le(0x400D0003u));
        const uint serverHandle = 0x10203040;

        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-1", "logical-connection");
        context.RegisterLogonCorrelationScope("http-2", "logical-connection");

        var first = RopBufferParser.Parse(
            Frame(BuildFastTransferGetBufferResponse(3, 0x0001, head), 0, 0, 0, serverHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-1",
            context);
        var second = RopBufferParser.Parse(
            Frame(BuildFastTransferGetBufferResponse(1, 0x0003, tail), 0, serverHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-2",
            context);

        Assert.Contains("ends inside a value", Find(Find(first, "ROP list").Children[0].Children, "TransferBuffer").Value);
        var continuation = Find(Find(second, "ROP list").Children[0].Children, "TransferBuffer");
        Assert.Equal("PartialValueContinuation", continuation.Children[0].Name);
        Assert.Contains("value complete", continuation.Children[0].Value);

        var state = context.FastTransferAssembler.StateFor(
            new FastTransferStreamKey("logical-connection", serverHandle));
        Assert.Equal(2, state.BufferCount);
        Assert.True(state.Complete);
        Assert.Null(state.Pending);
    }

    [Fact]
    public void DoesNotJoinEqualServerHandlesAcrossLogicalConnections()
    {
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le(8u), [0x01, 0x02]);
        const uint serverHandle = 0x10203040;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-a", "connection-a");
        context.RegisterLogonCorrelationScope("http-b", "connection-b");

        RopBufferParser.Parse(
            Frame(BuildFastTransferGetBufferResponse(0, 0x0001, head), serverHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-a",
            context);
        RopBufferParser.Parse(
            Frame(BuildFastTransferGetBufferResponse(0, 0x0001, head), serverHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-b",
            context);

        Assert.Equal(2, context.FastTransferAssembler.Snapshot.Count);
        Assert.NotNull(context.FastTransferAssembler.StateFor(
            new FastTransferStreamKey("connection-a", serverHandle)).Pending);
        Assert.NotNull(context.FastTransferAssembler.StateFor(
            new FastTransferStreamKey("connection-b", serverHandle)).Pending);
    }

    [Fact]
    public void ReleaseForgetsFastTransferStateBeforeAHandleCanBeReused()
    {
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le(8u), [0x01, 0x02]);
        const uint serverHandle = 0x10203040;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-get", "logical-connection");
        context.RegisterLogonCorrelationScope("http-release", "logical-connection");

        RopBufferParser.Parse(
            Frame(BuildFastTransferGetBufferResponse(0, 0x0001, head), serverHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-get",
            context);
        Assert.Single(context.FastTransferAssembler.Snapshot);

        RopBufferParser.Parse(
            Frame([0x01, 0x00, 0x00], serverHandle),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-release",
            context);

        Assert.Empty(context.FastTransferAssembler.Snapshot);
    }

    [Fact]
    public void ResolvesSameRequestUploadStateWhenTheCreationResponseAssignsItsHandle()
    {
        var value = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le((uint)value.Length), value[..8]);
        var tail = Concat(value[8..], Le(0x400D0003u));
        const uint ownerHandle = 0x01020304;
        const uint uploadHandle = 0x50607080;

        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-create", "logical-connection");
        context.RegisterLogonCorrelationScope("http-continue", "logical-connection");

        var configure = new byte[] { 0x53, 0x00, 0x00, 0x01, 0x01, 0x00 };
        var firstPut = Concat([0x54, 0x00, 0x01], Le((ushort)head.Length), head);
        RopBufferParser.Parse(
            Frame(Concat(configure, firstPut), ownerHandle, uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-create",
            context);

        var provisional = new FastTransferStreamKey("http-create", 1, Provisional: true);
        Assert.Null(context.FastTransferAssembler.StateFor(provisional).Pending);

        var firstPutResponse = Concat(
            [0x54, 0x01],
            Le(0u),
            Le((ushort)0),
            Le((ushort)0),
            Le((ushort)0),
            [0x00],
            Le((ushort)head.Length));
        RopBufferParser.Parse(
            Frame(
                Concat([0x53, 0x01, 0x00, 0x00, 0x00, 0x00], firstPutResponse),
                ownerHandle,
                uploadHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-create",
            context);
        context.CompleteHttpSession("http-create");

        var resolved = new FastTransferStreamKey("logical-connection", uploadHandle);
        Assert.Null(context.FastTransferAssembler.StateFor(provisional).Pending);
        Assert.NotNull(context.FastTransferAssembler.StateFor(resolved).Pending);

        var second = RopBufferParser.Parse(
            Frame(Concat([0x54, 0x00, 0x00], Le((ushort)tail.Length), tail), uploadHandle),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-continue",
            context);

        var continuation = Find(Find(second, "ROP list").Children[0].Children, "TransferData");
        Assert.Equal("PartialValueContinuation", continuation.Children[0].Name);
        Assert.Contains("value complete", continuation.Children[0].Value);

        RopBufferParser.Parse(
            Frame(
                Concat(
                    [0x54, 0x00],
                    Le(0u),
                    Le((ushort)0),
                    Le((ushort)0),
                    Le((ushort)0),
                    [0x00],
                    Le((ushort)tail.Length)),
                uploadHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-continue",
            context);
        Assert.Equal(2, context.FastTransferAssembler.StateFor(resolved).BufferCount);
    }

    [Fact]
    public void SynchronizationConfigureProvenanceSelectsAndValidatesContentsRootAfterHandleResolution()
    {
        const uint ownerHandle = 0x10203040;
        const uint synchronizationHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("sync-configure", "logical-connection");
        context.RegisterLogonCorrelationScope("sync-transfer", "logical-connection");

        var configure = Concat(
            [0x70, 0x00, 0x00, 0x01, 0x01, 0x00],
            Le((ushort)0),
            Le((ushort)0),
            Le(0u),
            Le((ushort)0));
        RopBufferParser.Parse(
            Frame(configure, ownerHandle, uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "sync-configure",
            context);

        var provisional = new FastTransferStreamKey("sync-configure", 1, Provisional: true);
        Assert.Equal(
            FastTransferRootKind.ContentsSync,
            context.FastTransferAssembler.StateFor(provisional).Grammar.Root);

        RopBufferParser.Parse(
            Frame(Concat([0x70, 0x01], Le(0u)), ownerHandle, synchronizationHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "sync-configure",
            context);
        context.CompleteHttpSession("sync-configure");

        var resolved = new FastTransferStreamKey("logical-connection", synchronizationHandle);
        Assert.Equal(
            FastTransferRootKind.ContentsSync,
            context.FastTransferAssembler.StateFor(resolved).Grammar.Root);
        Assert.False(context.FastTransferAssembler.Snapshot.ContainsKey(provisional));

        var stream = Concat(
            Le(0x403A0003u),
            Le(0x403B0003u),
            Le(0x40140003u));
        var warnings = new List<string>();
        RopBufferParser.Parse(
            Frame(BuildFastTransferGetBufferResponse(0, 0x0003, stream), synchronizationHandle),
            0,
            MapiDirection.Response,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "sync-transfer",
            context);

        Assert.Empty(warnings);
        Assert.True(context.FastTransferAssembler.StateFor(resolved).Grammar.IsComplete);
        Assert.True(context.FastTransferAssembler.StateFor(resolved).Complete);
    }

    [Theory]
    [InlineData((byte)0x4B, (int)FastTransferRootKind.MessageList)]
    [InlineData((byte)0x4C, (int)FastTransferRootKind.TopFolder)]
    public void CopyProvenanceSelectsAndValidatesItsFastTransferRoot(
        byte ropId,
        int expectedRootValue)
    {
        var expectedRoot = (FastTransferRootKind)expectedRootValue;
        const uint ownerHandle = 0x10203040;
        const uint transferHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("copy-configure", "logical-connection");
        context.RegisterLogonCorrelationScope("copy-transfer", "logical-connection");
        var request = ropId == 0x4B
            ? Concat([ropId, 0x00, 0x00, 0x01], Le((ushort)0), [0x00, 0x00])
            : [ropId, 0x00, 0x00, 0x01, 0x00, 0x00];

        RopBufferParser.Parse(
            Frame(request, ownerHandle, uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "copy-configure",
            context);
        RopBufferParser.Parse(
            Frame(Concat([ropId, 0x01], Le(0u)), ownerHandle, transferHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "copy-configure",
            context);
        context.CompleteHttpSession("copy-configure");

        var key = new FastTransferStreamKey("logical-connection", transferHandle);
        Assert.Equal(expectedRoot, context.FastTransferAssembler.StateFor(key).Grammar.Root);
        var transfer = expectedRoot == FastTransferRootKind.MessageList
            ? Concat(Le(0x400C0003u), Le(0x400D0003u))
            : Concat(Le(0x40090003u), Le(0x400B0003u));
        var warnings = new List<string>();
        RopBufferParser.Parse(
            Frame(BuildFastTransferGetBufferResponse(0, 0x0003, transfer), transferHandle),
            0,
            MapiDirection.Response,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "copy-transfer",
            context);

        Assert.Empty(warnings);
        Assert.True(context.FastTransferAssembler.StateFor(key).Grammar.IsComplete);
        Assert.True(context.FastTransferAssembler.StateFor(key).Complete);
    }

    [Theory]
    [InlineData((byte)0x02, (byte)0x4D, (int)FastTransferRootKind.FolderContent)]
    [InlineData((byte)0x1C, (byte)0x69, (int)FastTransferRootKind.FolderContent)]
    [InlineData((byte)0x03, (byte)0x69, (int)FastTransferRootKind.MessageContent)]
    [InlineData((byte)0x06, (byte)0x4D, (int)FastTransferRootKind.MessageContent)]
    [InlineData((byte)0x46, (byte)0x69, (int)FastTransferRootKind.MessageContent)]
    [InlineData((byte)0x22, (byte)0x4D, (int)FastTransferRootKind.AttachmentContent)]
    [InlineData((byte)0x23, (byte)0x69, (int)FastTransferRootKind.AttachmentContent)]
    public void RecoveredInputObjectTypeSelectsCopyObjectContentRoot(
        byte establishingRopId,
        byte copyRopId,
        int expectedRootValue)
    {
        const uint ownerHandle = 0x10203040;
        const uint objectHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("object-establish", "logical-connection");
        context.RecordSessionHandles("object-establish", [ownerHandle, objectHandle]);
        context.StageOutputObjectType("object-establish", establishingRopId, 1);
        Assert.Null(context.CompleteOutputObjectType(
            "object-establish",
            establishingRopId,
            1,
            success: true));
        context.CompleteHttpSession("object-establish");

        context.RegisterLogonCorrelationScope("copy-object", "logical-connection");
        var request = copyRopId == 0x4D
            ? Concat([0x4D, 0x00, 0x00, 0x01, 0x00], Le(0u), [0x00], Le((ushort)0))
            : Concat([0x69, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00], Le((ushort)0));
        RopBufferParser.Parse(
            Frame(request, objectHandle, uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "copy-object",
            context);

        var provisional = new FastTransferStreamKey("copy-object", 1, Provisional: true);
        Assert.Equal(
            (FastTransferRootKind)expectedRootValue,
            context.FastTransferAssembler.StateFor(provisional).Grammar.Root);
    }

    [Fact]
    public void SameExecuteOpenThenCopyUsesProvisionalOutputSlotType()
    {
        const uint ownerHandle = 0x10203040;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("same-execute-copy", "logical-connection");
        var openFolder = Concat([0x02, 0x00, 0x00, 0x01], new byte[8], [0x00]);
        var copyTo = Concat([0x4D, 0x00, 0x01, 0x02, 0x00], Le(0u), [0x00], Le((ushort)0));

        RopBufferParser.Parse(
            Frame(
                Concat(openFolder, copyTo),
                ownerHandle,
                uint.MaxValue,
                uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "same-execute-copy",
            context);

        var provisional = new FastTransferStreamKey("same-execute-copy", 2, Provisional: true);
        Assert.Equal(
            FastTransferRootKind.FolderContent,
            context.FastTransferAssembler.StateFor(provisional).Grammar.Root);
    }

    [Fact]
    public void SameExecuteProvisionalTypeOverridesAReusedSlotsPreviousObjectType()
    {
        const uint ownerHandle = 0x10203040;
        const uint previousMessageHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("previous-object", "logical-connection");
        context.RecordSessionHandles("previous-object", [previousMessageHandle]);
        context.StageOutputObjectType("previous-object", 0x03, 0);
        context.CompleteOutputObjectType("previous-object", 0x03, 0, success: true);
        context.CompleteHttpSession("previous-object");

        context.RegisterLogonCorrelationScope("reuse-then-copy", "logical-connection");
        var openFolder = Concat([0x02, 0x00, 0x00, 0x01], new byte[8], [0x00]);
        var copyTo = Concat([0x4D, 0x00, 0x01, 0x02, 0x00], Le(0u), [0x00], Le((ushort)0));
        RopBufferParser.Parse(
            Frame(
                Concat(openFolder, copyTo),
                ownerHandle,
                previousMessageHandle,
                uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "reuse-then-copy",
            context);

        var provisional = new FastTransferStreamKey("reuse-then-copy", 2, Provisional: true);
        Assert.Equal(
            FastTransferRootKind.FolderContent,
            context.FastTransferAssembler.StateFor(provisional).Grammar.Root);
    }

    [Fact]
    public void FailedSameExecuteOpenRefreshesCopyRootFromTheRetainedObject()
    {
        const uint ownerHandle = 0x10203040;
        const uint previousMessageHandle = 0x50607080;
        const uint transferHandle = 0x90A0B0C0;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("retained-message", "logical-connection");
        context.RecordSessionHandles("retained-message", [previousMessageHandle]);
        context.StageOutputObjectType("retained-message", 0x03, 0);
        context.CompleteOutputObjectType("retained-message", 0x03, 0, success: true);
        context.CompleteHttpSession("retained-message");

        context.RegisterLogonCorrelationScope("failed-open-copy", "logical-connection");
        var openFolder = Concat([0x02, 0x00, 0x00, 0x01], new byte[8], [0x00]);
        var copyTo = Concat([0x4D, 0x00, 0x01, 0x02, 0x00], Le(0u), [0x00], Le((ushort)0));
        RopBufferParser.Parse(
            Frame(
                Concat(openFolder, copyTo),
                ownerHandle,
                previousMessageHandle,
                uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "failed-open-copy",
            context);

        var provisional = new FastTransferStreamKey("failed-open-copy", 2, Provisional: true);
        Assert.Equal(
            FastTransferRootKind.FolderContent,
            context.FastTransferAssembler.StateFor(provisional).Grammar.Root);

        RopBufferParser.Parse(
            Frame(
                Concat(
                    [0x02, 0x01],
                    Le(0x80004005u),
                    [0x4D, 0x02],
                    Le(0u)),
                ownerHandle,
                previousMessageHandle,
                transferHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "failed-open-copy",
            context);

        var resolved = new FastTransferStreamKey("logical-connection", transferHandle);
        Assert.Equal(
            FastTransferRootKind.MessageContent,
            context.FastTransferAssembler.StateFor(resolved).Grammar.Root);
        Assert.DoesNotContain(provisional, context.FastTransferAssembler.Snapshot.Keys);
    }

    [Fact]
    public void MalformedRequestTailDiscardsPendingObjectAndFastTransferProvenance()
    {
        const uint ownerHandle = 0x10203040;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("malformed-request", "logical-connection");
        var openFolder = Concat([0x02, 0x00, 0x00, 0x01], new byte[8], [0x00]);
        var copyTo = Concat([0x4D, 0x00, 0x01, 0x02, 0x00], Le(0u), [0x00], Le((ushort)0));

        RopBufferParser.Parse(
            Frame(
                Concat(openFolder, copyTo, [0x02]),
                ownerHandle,
                uint.MaxValue,
                uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "malformed-request",
            context);

        Assert.False(context.TryGetServerObjectType("malformed-request", 1, out _));
        Assert.DoesNotContain(
            new FastTransferStreamKey("malformed-request", 2, Provisional: true),
            context.FastTransferAssembler.Snapshot.Keys);
    }

    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x02)]
    public void DestinationConfigureUsesRecoveredObjectTypeForCopyOperations(byte sourceOperation)
    {
        const uint objectHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("destination-establish", "logical-connection");
        context.RecordSessionHandles("destination-establish", [0u, objectHandle]);
        context.StageOutputObjectType("destination-establish", 0x03, 1);
        context.CompleteOutputObjectType("destination-establish", 0x03, 1, success: true);
        context.CompleteHttpSession("destination-establish");

        context.RegisterLogonCorrelationScope("destination-copy", "logical-connection");
        RopBufferParser.Parse(
            Frame(
                [0x53, 0x00, 0x00, 0x01, sourceOperation, 0x00],
                objectHandle,
                uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "destination-copy",
            context);

        var provisional = new FastTransferStreamKey("destination-copy", 1, Provisional: true);
        Assert.Equal(
            FastTransferRootKind.MessageContent,
            context.FastTransferAssembler.StateFor(provisional).Grammar.Root);
    }

    [Fact]
    public void SuccessfulOpenFolderPipelineRecordsFolderObjectType()
    {
        const uint ownerHandle = 0x10203040;
        const uint folderHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("open-folder", "logical-connection");
        RopBufferParser.Parse(
            Frame(
                Concat([0x02, 0x00, 0x00, 0x01], new byte[8], [0x00]),
                ownerHandle,
                uint.MaxValue),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "open-folder",
            context);
        RopBufferParser.Parse(
            Frame(
                Concat([0x02, 0x01], Le(0u), [0x00, 0x00]),
                ownerHandle,
                folderHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "open-folder",
            context);
        context.CompleteHttpSession("open-folder");

        context.RegisterLogonCorrelationScope("folder-use", "logical-connection");
        context.RecordSessionHandles("folder-use", [folderHandle]);
        Assert.True(context.TryGetServerObjectType("folder-use", 0, out var type));
        Assert.Equal(MapiCaptureContext.ServerObjectType.Folder, type);
    }

    [Fact]
    public void ReusedOutputSlotCommitsOnlyTheFinalSuccessfulObjectType()
    {
        const uint objectHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("reuse-object", "logical-connection");
        context.RecordSessionHandles("reuse-object", [0u, objectHandle]);
        context.StageOutputObjectType("reuse-object", 0x02, 1);
        context.StageOutputObjectType("reuse-object", 0x03, 1);

        context.CompleteOutputObjectType("reuse-object", 0x02, 1, success: true);
        Assert.True(context.TryGetServerObjectType("reuse-object", 1, out var provisional));
        Assert.Equal(MapiCaptureContext.ServerObjectType.Message, provisional);
        context.CompleteOutputObjectType("reuse-object", 0x03, 1, success: true);

        Assert.True(context.TryGetServerObjectType("reuse-object", 1, out var type));
        Assert.Equal(MapiCaptureContext.ServerObjectType.Message, type);
    }

    [Fact]
    public void ReusedOutputSlotRetainsTheLastSuccessfulTypeWhenTheFinalCreationFails()
    {
        const uint objectHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("reuse-failure", "logical-connection");
        context.RecordSessionHandles("reuse-failure", [0u, objectHandle]);
        context.StageOutputObjectType("reuse-failure", 0x02, 1);
        context.StageOutputObjectType("reuse-failure", 0x03, 1);

        context.CompleteOutputObjectType("reuse-failure", 0x02, 1, success: true);
        context.CompleteOutputObjectType("reuse-failure", 0x03, 1, success: false);

        Assert.True(context.TryGetServerObjectType("reuse-failure", 1, out var type));
        Assert.Equal(MapiCaptureContext.ServerObjectType.Folder, type);
    }

    [Fact]
    public void FailedOutputPreservesExistingTypeAndUnknownSuccessfulOutputClearsIt()
    {
        const uint objectHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("initial-object", "logical-connection");
        context.RecordSessionHandles("initial-object", [objectHandle]);
        context.StageOutputObjectType("initial-object", 0x03, 0);
        context.CompleteOutputObjectType("initial-object", 0x03, 0, success: true);
        context.CompleteHttpSession("initial-object");

        context.RegisterLogonCorrelationScope("failed-object", "logical-connection");
        context.RecordSessionHandles("failed-object", [objectHandle]);
        context.StageOutputObjectType("failed-object", 0x02, 0);
        context.CompleteOutputObjectType("failed-object", 0x02, 0, success: false);
        Assert.True(context.TryGetServerObjectType("failed-object", 0, out var preserved));
        Assert.Equal(MapiCaptureContext.ServerObjectType.Message, preserved);

        context.StageOutputObjectType("failed-object", 0x21, 0);
        context.CompleteOutputObjectType("failed-object", 0x21, 0, success: true);
        Assert.False(context.TryGetServerObjectType("failed-object", 0, out _));
    }

    [Fact]
    public void MissingOutputResponseAndReleaseDoNotLeakObjectTypeState()
    {
        const uint objectHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("missing-object", "logical-connection");
        context.RecordSessionHandles("missing-object", [objectHandle]);
        context.StageOutputObjectType("missing-object", 0x03, 0);
        context.CompleteHttpSession("missing-object");

        context.RegisterLogonCorrelationScope("object-release", "logical-connection");
        context.RecordSessionHandles("object-release", [objectHandle]);
        Assert.False(context.TryGetServerObjectType("object-release", 0, out _));

        context.StageOutputObjectType("object-release", 0x03, 0);
        context.CompleteOutputObjectType("object-release", 0x03, 0, success: true);
        Assert.True(context.TryGetServerObjectType("object-release", 0, out _));
        context.InvalidateHandleState("object-release", 0);
        Assert.False(context.TryGetServerObjectType("object-release", 0, out _));
    }

    [Fact]
    public void DoneStatusReportsAnIncompleteProvenanceSelectedRoot()
    {
        const uint serverHandle = 0x10203040;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("incomplete", "logical-connection");
        var key = new FastTransferStreamKey("logical-connection", serverHandle);
        context.FastTransferAssembler.Configure(
            key,
            FastTransferRootKind.HierarchySync,
            "test synchronization configure");

        var warnings = new List<string>();
        RopBufferParser.Parse(
            Frame(
                BuildFastTransferGetBufferResponse(0, 0x0003, Le(0x40120003u)),
                serverHandle),
            0,
            MapiDirection.Response,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "incomplete",
            context);

        Assert.Contains(warnings, warning =>
            warning.Contains("HierarchySync grammar ended in phase HierarchyFolderChange", StringComparison.Ordinal));
        Assert.True(context.FastTransferAssembler.StateFor(key).Complete);
    }

    [Fact]
    public void ReusedOutputSlotDiscardsAmbiguousRootProvenanceInsteadOfApplyingTheLastRoot()
    {
        const uint ownerHandle = 0x10203040;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("duplicate-output", "logical-connection");
        var contentsConfigure = Concat(
            [0x70, 0x00, 0x00, 0x01, 0x01, 0x00],
            Le((ushort)0),
            Le((ushort)0),
            Le(0u),
            Le((ushort)0));
        var hierarchyConfigure = Concat(
            [0x70, 0x00, 0x00, 0x01, 0x02, 0x00],
            Le((ushort)0),
            Le((ushort)0),
            Le(0u),
            Le((ushort)0));
        var warnings = new List<string>();

        RopBufferParser.Parse(
            Frame(Concat(contentsConfigure, hierarchyConfigure), ownerHandle, uint.MaxValue),
            0,
            MapiDirection.Request,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "duplicate-output",
            context);

        Assert.Contains(warnings, warning =>
            warning.Contains("configured more than once", StringComparison.Ordinal)
            && warning.Contains("discarded rather than misapplied", StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.FastTransferAssembler.Snapshot.Keys,
            key => key.Provisional && key.ConnectionScope == "duplicate-output");
    }

    [Fact]
    public void DiscardsUnexecutedUploadStateSoAResentBufferIsFoldedOnlyOnce()
    {
        var head = Concat(
            Le((ushort)0x0102),
            Le((ushort)0x1000),
            Le(8u),
            [0x01, 0x02]);
        const uint uploadHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-dropped", "logical-connection");
        context.RegisterLogonCorrelationScope("http-resend", "logical-connection");
        var request = Frame(
            Concat([0x54, 0x00, 0x00], Le((ushort)head.Length), head),
            uploadHandle);

        RopBufferParser.Parse(
            request,
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-dropped",
            context);
        context.CompleteHttpSession("http-dropped");

        var key = new FastTransferStreamKey("logical-connection", uploadHandle);
        Assert.Equal(0, context.FastTransferAssembler.StateFor(key).BufferCount);

        RopBufferParser.Parse(
            request,
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-resend",
            context);
        RopBufferParser.Parse(
            Frame(
                Concat(
                    [0x54, 0x00],
                    Le(0u),
                    Le((ushort)0),
                    Le((ushort)0),
                    Le((ushort)0),
                    [0x00],
                    Le((ushort)head.Length)),
                uploadHandle),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-resend",
            context);

        var state = context.FastTransferAssembler.StateFor(key);
        Assert.Equal(1, state.BufferCount);
        Assert.NotNull(state.Pending);
        Assert.Equal(6, state.Pending!.Value.RemainingLength);
    }

    [Fact]
    public void FailedUploadDoesNotCommitAnExchangeStyleFullBufferUsedSize()
    {
        var head = Concat(
            Le((ushort)0x0102),
            Le((ushort)0x1000),
            Le(8u),
            [0x01, 0x02]);
        const uint uploadHandle = 0x50607080;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-failed", "logical-connection");

        RopBufferParser.Parse(
            Frame(
                Concat([0x54, 0x00, 0x00], Le((ushort)head.Length), head),
                uploadHandle),
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-failed",
            context);

        var warnings = new List<string>();
        RopBufferParser.Parse(
            Frame(
                Concat(
                    [0x54, 0x00],
                    Le(0x80040115u),
                    Le((ushort)0),
                    Le((ushort)0),
                    Le((ushort)0),
                    [0x00],
                    Le((ushort)head.Length)),
                uploadHandle),
            0,
            MapiDirection.Response,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "http-failed",
            context);

        Assert.Empty(context.FastTransferAssembler.Snapshot);
        Assert.Contains(warnings, warning =>
            warning.Contains("BufferUsedSize is not reliable on failure", StringComparison.Ordinal));
    }

    [Fact]
    public void ReassemblesAndDecodesIcsStateStreamAcrossHttpRoundTrips()
    {
        var replicaGuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var stateData = Concat(replicaGuid.ToByteArray(), [0x00]);
        const uint syncHandle = 0x0A0B0C0D;
        var context = new MapiCaptureContext();
        foreach (var scope in new[] { "state-begin", "state-continue-1", "state-continue-2", "state-end" })
        {
            context.RegisterLogonCorrelationScope(scope, "logical-connection");
        }

        ParseIcsStateRoundTrip(
            context,
            "state-begin",
            Frame(
                Concat(
                    [0x75, 0x00, 0x00],
                    Le((ushort)0x0102),
                    Le((ushort)0x6796),
                    Le((uint)stateData.Length)),
                syncHandle),
            Frame(Concat([0x75, 0x00], Le(0u)), syncHandle));
        ParseIcsStateRoundTrip(
            context,
            "state-continue-1",
            Frame(
                Concat([0x76, 0x00, 0x00], Le(8u), stateData[..8]),
                syncHandle),
            Frame(Concat([0x76, 0x00], Le(0u)), syncHandle));
        ParseIcsStateRoundTrip(
            context,
            "state-continue-2",
            Frame(
                Concat([0x76, 0x00, 0x00], Le((uint)(stateData.Length - 8)), stateData[8..]),
                syncHandle),
            Frame(Concat([0x76, 0x00], Le(0u)), syncHandle));

        var endResponse = ParseIcsStateRoundTrip(
            context,
            "state-end",
            Frame([0x77, 0x00, 0x00], syncHandle),
            Frame(Concat([0x77, 0x00], Le(0u)), syncHandle));

        var completed = Find(Find(endResponse, "ROP list").Children[0].Children, "CompletedStateStream");
        Assert.Contains("MetaTagCnsetSeen", completed.Value);
        Assert.Contains("17 byte(s) in 2 chunk(s)", completed.Value);
        Assert.Equal(replicaGuid.ToString(), Find(completed.Children, "REPLGUID").Value);
        Assert.Equal(2, completed.Offset);
    }

    [Fact]
    public void FinalizesZeroLengthIcsStateAndReportsDeclaredSizeMismatch()
    {
        var context = new MapiCaptureContext();

        context.StageIcsStateBegin("empty", 0, 0x40170102, 0);
        Assert.Null(context.CompleteIcsStateOperation("empty", 0x75, 0, true, out var beginWarning));
        Assert.Null(beginWarning);
        context.StageIcsStateEnd("empty", 0);
        var empty = context.CompleteIcsStateOperation("empty", 0x77, 0, true, out var endWarning);

        Assert.Null(endWarning);
        Assert.NotNull(empty);
        Assert.Empty(empty.Data);
        Assert.Null(empty.Warning);

        context.StageIcsStateBegin("mismatch", 0, 0x67960102, 3);
        context.CompleteIcsStateOperation("mismatch", 0x75, 0, true, out _);
        context.StageIcsStateContinue("mismatch", 0, [0x01, 0x02]);
        context.CompleteIcsStateOperation("mismatch", 0x76, 0, true, out _);
        context.StageIcsStateEnd("mismatch", 0);
        var mismatch = context.CompleteIcsStateOperation("mismatch", 0x77, 0, true, out _);

        Assert.NotNull(mismatch);
        Assert.Equal(2, mismatch.Data.Length);
        Assert.Contains("declared 3", mismatch.Warning);
    }

    [Fact]
    public void FailedIcsStateContinueInvalidatesTheWholeProperty()
    {
        var context = new MapiCaptureContext();
        context.StageIcsStateBegin("failure", 0, 0x67960102, 1);
        context.CompleteIcsStateOperation("failure", 0x75, 0, true, out _);
        context.StageIcsStateContinue("failure", 0, [0x00]);

        Assert.Null(context.CompleteIcsStateOperation("failure", 0x76, 0, false, out var failureWarning));
        Assert.Contains("failed", failureWarning);

        context.StageIcsStateEnd("failure", 0);
        Assert.Null(context.CompleteIcsStateOperation("failure", 0x77, 0, true, out var endWarning));
        Assert.Contains("no successful Begin", endWarning);
    }

    [Fact]
    public void IcsStateContinueWithoutBeginIsRejected()
    {
        var context = new MapiCaptureContext();
        context.StageIcsStateContinue("orphan", 0, [0x01]);

        Assert.Null(context.CompleteIcsStateOperation("orphan", 0x76, 0, true, out var warning));
        Assert.Contains("no successful Begin", warning);
    }

    [Fact]
    public void UnresolvableIcsStateDoesNotSurviveItsHttpSession()
    {
        var context = new MapiCaptureContext();
        context.StageIcsStateBegin("provisional", 0, 0x67960102, 0);
        context.CompleteIcsStateOperation("provisional", 0x75, 0, true, out _);

        context.CompleteHttpSession("provisional");
        context.StageIcsStateEnd("provisional", 0);
        context.CompleteIcsStateOperation("provisional", 0x77, 0, true, out var warning);

        Assert.Contains("no successful Begin", warning);
    }

    [Fact]
    public void MismatchedIcsStateResponseDiscardsTheResolvedAccumulator()
    {
        const uint syncHandle = 0x10203040;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("mismatch", "logical-connection");
        context.StageIcsStateBegin("mismatch", 0, 0x67960102, 1);
        context.RecordSessionHandles("mismatch", [syncHandle]);
        context.CompleteIcsStateOperation("mismatch", 0x75, 0, true, out _);
        context.StageIcsStateContinue("mismatch", 0, [0x00]);

        Assert.Null(context.CompleteIcsStateOperation("mismatch", 0x77, 0, true, out var mismatchWarning));
        Assert.Contains("does not match", mismatchWarning);

        context.StageIcsStateContinue("mismatch", 0, [0x00]);
        context.CompleteIcsStateOperation("mismatch", 0x76, 0, true, out var continueWarning);
        Assert.Contains("no successful Begin", continueWarning);
    }

    [Fact]
    public void ReleaseAndMissingResponsesInvalidateIcsState()
    {
        const uint syncHandle = 0x50607080;
        var context = new MapiCaptureContext();
        foreach (var scope in new[] { "begin", "release", "begin-2", "missing", "end" })
        {
            context.RegisterLogonCorrelationScope(scope, "logical-connection");
            context.RecordSessionHandles(scope, [syncHandle]);
        }

        context.StageIcsStateBegin("begin", 0, 0x67960102, 1);
        context.CompleteIcsStateOperation("begin", 0x75, 0, true, out _);
        context.InvalidateHandleState("release", 0);
        context.StageIcsStateEnd("end", 0);
        context.CompleteIcsStateOperation("end", 0x77, 0, true, out var releaseWarning);
        Assert.Contains("no successful Begin", releaseWarning);

        context.StageIcsStateBegin("begin-2", 0, 0x67960102, 1);
        context.CompleteIcsStateOperation("begin-2", 0x75, 0, true, out _);
        context.StageIcsStateContinue("missing", 0, [0x00]);
        context.CompleteHttpSession("missing");
        context.StageIcsStateEnd("end", 0);
        context.CompleteIcsStateOperation("end", 0x77, 0, true, out var missingWarning);
        Assert.Contains("no successful Begin", missingWarning);
    }

    [Fact]
    public void IllegalIcsStatePropertyIsNamedAndWarnedWithoutFailingTheRequest()
    {
        var warnings = new List<string>();
        var context = new MapiCaptureContext();
        var nodes = RopBufferParser.Parse(
            Frame(Concat([0x75, 0x00, 0x00], Le((ushort)0x0102), Le((ushort)0x1234), Le(0u)), 0),
            0,
            MapiDirection.Request,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            "illegal-property",
            context);

        Assert.Contains(warnings, warning => warning.Contains("not one of the four ICS state properties"));
        Assert.Contains("0x12340102", Find(Find(nodes, "ROP list").Children[0].Children, "StateProperty").Value);
        context.CompleteHttpSession("illegal-property");
    }

    [Fact]
    public void MalformedCompletedIcsStateFallsBackToBoundedRawData()
    {
        const uint syncHandle = 0x0A0B0C0D;
        var context = new MapiCaptureContext();
        foreach (var scope in new[] { "malformed-begin", "malformed-continue", "malformed-end" })
        {
            context.RegisterLogonCorrelationScope(scope, "logical-connection");
        }

        ParseIcsStateRoundTrip(
            context,
            "malformed-begin",
            Frame(Concat([0x75, 0x00, 0x00], Le((ushort)0x0102), Le((ushort)0x6796), Le(1u)), syncHandle),
            Frame(Concat([0x75, 0x00], Le(0u)), syncHandle));
        ParseIcsStateRoundTrip(
            context,
            "malformed-continue",
            Frame(Concat([0x76, 0x00, 0x00], Le(1u), [0xFF]), syncHandle),
            Frame(Concat([0x76, 0x00], Le(0u)), syncHandle));
        var warnings = new List<string>();
        var end = ParseIcsStateRoundTrip(
            context,
            "malformed-end",
            Frame([0x77, 0x00, 0x00], syncHandle),
            Frame(Concat([0x77, 0x00], Le(0u)), syncHandle),
            warnings);

        var completed = Find(Find(end, "ROP list").Children[0].Children, "CompletedStateStream");
        var stateData = Find(completed.Children, "StateData");
        Assert.Equal("FF", stateData.Value);
        Assert.Equal(completed.Offset, stateData.Offset);
        Assert.Equal(0, stateData.Length);
        Assert.Contains(warnings, warning => warning.Contains("retained as raw", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static byte[] BuildFastTransferGetBufferResponse(byte handleIndex, ushort status, byte[] transferBuffer) =>
        Concat(
            [0x4E, handleIndex],
            Le((uint)0), // ReturnValue: Success
            Le(status),  // TransferStatus
            Le((ushort)0), // InProgressCount
            Le((ushort)0), // TotalStepCount
            [0x00],      // Reserved
            Le((ushort)transferBuffer.Length), // TransferBufferSize
            transferBuffer);

    private static ImmutableArray<MapiNode> ParseIcsStateRoundTrip(
        MapiCaptureContext context,
        string scope,
        byte[] request,
        byte[] response,
        List<string>? responseWarnings = null)
    {
        RopBufferParser.Parse(
            request,
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            scope,
            context);
        var result = RopBufferParser.Parse(
            response,
            0,
            MapiDirection.Response,
            responseWarnings ?? [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context.FastTransferAssembler,
            scope,
            context);
        context.CompleteHttpSession(scope);
        return result;
    }

    private static byte[] Le(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Frame(byte[] ropList, params uint[] handles)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Le((ushort)(ropList.Length + 2)));
        bytes.AddRange(ropList);
        foreach (var handle in handles)
        {
            bytes.AddRange(Le(handle));
        }
        return bytes.ToArray();
    }

    private static MapiNode Find(IEnumerable<MapiNode> nodes, string name)
    {
        var found = FindAll(nodes, name).FirstOrDefault();
        Assert.True(found is not null, $"Expected to find a node named '{name}'.");
        return found!;
    }

    private static IEnumerable<MapiNode> FindAll(IEnumerable<MapiNode> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (node.Name == name) yield return node;
            foreach (var match in FindAll(node.Children, name)) yield return match;
        }
    }
}
