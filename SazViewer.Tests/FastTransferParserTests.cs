using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

/// <summary>
/// Synthetic coverage for the MS-OXCFXICS FastTransfer/ICS decoder family
/// (<see cref="RopFastTransferDecoders"/>) and the bounded transfer-stream lexer
/// (<see cref="FastTransferStreamLexer"/> and <see cref="FastTransferStreamAssembler"/>).
/// Every buffer here is built byte-by-byte from the MS-OXCROPS/MS-OXCFXICS wire layouts; nothing in
/// this file touches the shared dispatcher, the fixed-width schema catalogs, or any parity fixture.
/// </summary>
public sealed class FastTransferParserTests
{
    // =============================================================================================
    // Family surface
    // =============================================================================================

    [Fact]
    public void SupportsExactlyEighteenRequestAndTenResponseRops()
    {
        Assert.Equal(18, RopFastTransferDecoders.SupportedRequestRopIds.Length);
        Assert.Equal(10, RopFastTransferDecoders.SupportedResponseRopIds.Length);
        Assert.Equal(
            RopFastTransferDecoders.SupportedRequestRopIds.Distinct().Count(),
            RopFastTransferDecoders.SupportedRequestRopIds.Length);
        Assert.Equal(
            RopFastTransferDecoders.SupportedResponseRopIds.Distinct().Count(),
            RopFastTransferDecoders.SupportedResponseRopIds.Length);

        foreach (var ropId in RopFastTransferDecoders.SupportedRequestRopIds)
        {
            Assert.True(RopFastTransferDecoders.Supports(MapiDirection.Request, ropId));
            Assert.True(RopBufferParser.IsKnownRopId(ropId), $"0x{ropId:X2} should be a known RopId.");
        }
        foreach (var ropId in RopFastTransferDecoders.SupportedResponseRopIds)
        {
            Assert.True(RopFastTransferDecoders.Supports(MapiDirection.Response, ropId));
            Assert.True(RopBufferParser.IsKnownRopId(ropId), $"0x{ropId:X2} should be a known RopId.");
        }

        Assert.False(RopFastTransferDecoders.Supports(MapiDirection.Request, 0x01));
        Assert.False(RopFastTransferDecoders.Supports(MapiDirection.Response, 0x01));
        Assert.False(RopFastTransferDecoders.Supports((MapiDirection)42, 0x4E));
    }

    [Fact]
    public void NeverOverlapsTheFixedWidthSchemaCatalogs()
    {
        foreach (var ropId in RopFastTransferDecoders.SupportedRequestRopIds)
        {
            Assert.False(
                RopSemanticParser.RequestSchemas.ContainsKey(ropId),
                $"Request 0x{ropId:X2} is already covered by a fixed-width schema.");
        }
        foreach (var ropId in RopFastTransferDecoders.SupportedResponseRopIds)
        {
            Assert.False(
                RopSemanticParser.ResponseSchemas.ContainsKey(ropId),
                $"Response 0x{ropId:X2} is already covered by a fixed-width schema.");
        }
    }

    [Fact]
    public void RejectsRopIdsOutsideTheFamily()
    {
        var warnings = new List<string>();
        var handles = new List<RopHandleReference>();
        var bytes = new byte[] { 0x01, 0x00, 0x00 };
        var thrown = Assert.Throws<MapiParseException>(() =>
        {
            var reader = new MapiReader(bytes, CancellationToken.None);
            RopFastTransferDecoders.Parse(
                ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None,
                warnings, null, null);
        });
        Assert.Contains("is not decoded by the MS-OXCFXICS", thrown.Message);
    }

    // =============================================================================================
    // Request envelopes
    // =============================================================================================

    [Fact]
    public void ParsesFastTransferSourceCopyMessagesRequest()
    {
        var bytes = Concat(
            [0x4B, 0x05, 0x01, 0x02],
            Le((ushort)2),
            MessageId(0x0001, 0x11),
            MessageId(0x0002, 0x22),
            [0x11, 0x09]);

        var (node, warnings, handles) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.StartsWith("RopFastTransferSourceCopyMessages", node.Value);
        Assert.Equal("5", Find(node.Children, "LogonId").Value);
        Assert.Equal("2", Find(node.Children, "MessageIdCount").Value);
        Assert.Equal("MessageID 1-111111111111", Find(node.Children, "MessageIds[0]").Value);
        Assert.Equal("MessageID 2-222222222222", Find(node.Children, "MessageIds[1]").Value);
        Assert.Equal("0x11 (Move | BestBody)", Find(node.Children, "CopyFlags").Value);
        Assert.Equal("0x09 (Unicode | ForceUnicode)", Find(node.Children, "SendOptions").Value);
        Assert.Equal(2, handles.Count);
        Assert.Equal(["InputHandleIndex", "OutputHandleIndex"], handles.Select(h => h.FieldName));
    }

    [Fact]
    public void ParsesFastTransferSourceCopyFolderRequest()
    {
        var (node, warnings, handles) = ParseRequest([0x4C, 0x00, 0x01, 0x02, 0x11, 0x01]);

        Assert.Empty(warnings);
        Assert.Equal("0x11 (Move | CopySubfolders)", Find(node.Children, "CopyFlags").Value);
        Assert.Equal("0x01 (Unicode)", Find(node.Children, "SendOptions").Value);
        Assert.Equal(6, node.Length);
        Assert.Equal(2, handles.Count);
    }

    [Fact]
    public void ParsesFastTransferSourceCopyToRequestWithFourByteCopyFlags()
    {
        var bytes = Concat(
            [0x4D, 0x00, 0x01, 0x02, 0x00],
            Le(0x00002001u),
            [0x01],
            Le((ushort)2),
            Le((ushort)0x001F), Le((ushort)0x3001),
            Le((ushort)0x0102), Le((ushort)0x65E0));

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x00002001 (Move | BestBody)", Find(node.Children, "CopyFlags").Value);
        Assert.Equal("2", Find(node.Children, "PropertyTagCount").Value);
        Assert.Equal(2, Find(node.Children, "PropertyTags").Children.Length);
        Assert.Contains("PidTagDisplayName", Find(node.Children, "PropertyTags").Children[0].Value);
    }

    [Fact]
    public void ParsesFastTransferSourceCopyPropertiesRequestWithOneByteCopyFlags()
    {
        var bytes = Concat(
            [0x69, 0x00, 0x01, 0x02, 0x00, 0x01, 0x01],
            Le((ushort)0));

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x01 (Move)", Find(node.Children, "CopyFlags").Value);
        Assert.Equal("0", Find(node.Children, "PropertyTagCount").Value);
        Assert.Empty(Find(node.Children, "PropertyTags").Children);
        Assert.Equal(9, node.Length);
    }

    [Fact]
    public void ParsesFastTransferSourceGetBufferRequestWithoutMaximumBufferSize()
    {
        var (node, warnings, _) = ParseRequest(Concat([0x4E, 0x00, 0x01], Le((ushort)0x4000)));

        Assert.Empty(warnings);
        Assert.Equal("16384", Find(node.Children, "BufferSize").Value);
        Assert.Equal(5, node.Length);
    }

    [Fact]
    public void ParsesFastTransferSourceGetBufferRequestWithMaximumBufferSizeSentinel()
    {
        var (node, warnings, _) = ParseRequest(Concat([0x4E, 0x00, 0x01], Le((ushort)0xBABE), Le((ushort)0x8000)));

        Assert.Empty(warnings);
        Assert.Equal("0xBABE (MaximumBufferSize follows)", Find(node.Children, "BufferSize").Value);
        Assert.Equal("32768", Find(node.Children, "MaximumBufferSize").Value);
        Assert.Equal(7, node.Length);
    }

    [Fact]
    public void ParsesFastTransferDestinationConfigureRequest()
    {
        var (node, warnings, handles) = ParseRequest([0x53, 0x00, 0x01, 0x02, 0x03, 0x01]);

        Assert.Empty(warnings);
        Assert.Equal("0x03 CopyMessages", Find(node.Children, "SourceOperation").Value);
        Assert.Equal("0x01 (Move)", Find(node.Children, "CopyFlags").Value);
        Assert.Equal(2, handles.Count);
    }

    [Theory]
    [InlineData((byte)0x54)]
    [InlineData((byte)0x9D)]
    public void ParsesDestinationPutBufferRequestAndLexesItsTransferData(byte ropId)
    {
        var stream = Concat(Le(0x400C0003u), Le(0x400D0003u));
        var bytes = Concat([ropId, 0x00, 0x01], Le((ushort)stream.Length), stream);

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        var data = Find(node.Children, "TransferData");
        Assert.Equal("8 byte(s), 2 lexical element(s)", data.Value);
        Assert.Equal(2, data.Children.Length);
        Assert.StartsWith("StartMessage", data.Children[0].Value);
        Assert.StartsWith("EndMessage", data.Children[1].Value);
    }

    [Fact]
    public void ParsesSynchronizationConfigureRequestWithRestrictionAndTags()
    {
        var restriction = Concat([0x08], Le((ushort)0x001F), Le((ushort)0x3001));
        var bytes = Concat(
            [0x70, 0x00, 0x01, 0x02, 0x01, 0x01],
            Le((ushort)0x8021),
            Le((ushort)restriction.Length),
            restriction,
            Le(0x00000005u),
            Le((ushort)1),
            Le((ushort)0x0003), Le((ushort)0x3013));

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x01 Contents", Find(node.Children, "SynchronizationType").Value);
        Assert.Equal("0x8021 (Unicode | Normal | Progress)", Find(node.Children, "SynchronizationFlags").Value);
        Assert.Equal("0x00000005 (Eid | CN)", Find(node.Children, "SynchronizationExtraFlags").Value);
        var restrictionNode = Find(node.Children, "RestrictionData");
        Assert.Equal(5, restrictionNode.Length);
        Assert.Single(restrictionNode.Children);
        Assert.Equal("1", Find(node.Children, "PropertyTagCount").Value);
    }

    [Fact]
    public void ParsesSynchronizationConfigureRequestWithoutRestriction()
    {
        var bytes = Concat(
            [0x70, 0x00, 0x01, 0x02, 0x02, 0x01],
            Le((ushort)0x0001),
            Le((ushort)0),
            Le(0x00000000u),
            Le((ushort)0));

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x02 Hierarchy", Find(node.Children, "SynchronizationType").Value);
        Assert.Equal("0x00000000 (none)", Find(node.Children, "SynchronizationExtraFlags").Value);
        Assert.Empty(FindAll(node.Children, "RestrictionData"));
    }

    [Fact]
    public void RetainsAnUndecodableRestrictionAsRawWithoutLosingTheOperationBoundary()
    {
        var restriction = new byte[] { 0xEE, 0x01, 0x02, 0x03 };
        var bytes = Concat(
            [0x70, 0x00, 0x01, 0x02, 0x01, 0x01],
            Le((ushort)0x0001),
            Le((ushort)restriction.Length),
            restriction,
            Le(0x00000000u),
            Le((ushort)0));

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Contains(warnings, w => w.Contains("RestrictionData could not be decoded", StringComparison.Ordinal));
        var restrictionNode = Find(node.Children, "RestrictionData");
        Assert.Equal("EE010203", restrictionNode.Children.Single().Value);
        Assert.Equal(bytes.Length, node.Length);
    }

    [Fact]
    public void ParsesSynchronizationImportMessageChangeRequest()
    {
        var bytes = Concat(
            [0x72, 0x00, 0x01, 0x02, 0x50],
            Le((ushort)1),
            Le((ushort)0x0003), Le((ushort)0x3013), Le(7u));

        var (node, warnings, handles) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x50 (Associated | FailOnConflict)", Find(node.Children, "ImportFlag").Value);
        Assert.Equal("1", Find(node.Children, "PropertyValueCount").Value);
        Assert.Single(Find(node.Children, "PropertyValues").Children);
        Assert.Equal(2, handles.Count);
    }

    /// <summary>
    /// Regression for a real-capture width bug: PtypBinary's byte-count prefix is 16-bit in an
    /// MS-OXCROPS ROP buffer ([MS-OXCDATA] 2.11.1.1), not the 32-bit NSPI/extended-rule/MAPI-HTTP
    /// form. A capture previously showed an ICS PtypBinary length of 0xF8400016 - four bytes misread
    /// as one 32-bit count - when the value (e.g. a PidTagChangeKey-shaped XID) was actually 22 bytes
    /// long, prefixed by a 16-bit length. This builds a RopSynchronizationImportMessageChange request
    /// carrying exactly such a 22-byte PtypBinary tagged value via <c>ReadTaggedValues</c>, then
    /// proves the value's consumed length is exact by successfully decoding a second, independent
    /// RopSynchronizationImportDeletes operation immediately afterward in the same buffer.
    /// </summary>
    [Fact]
    public void ParsesSynchronizationImportMessageChangeRequestWithSixteenBitPtypBinaryLengthAndProvesFollowingOperationBoundary()
    {
        var binaryValue = Enumerable.Range(0, 22).Select(i => (byte)(0xA0 + i)).ToArray();
        var op1 = Concat(
            [0x72, 0x00, 0x01, 0x02, 0x50],
            Le((ushort)1),
            Le((ushort)0x0102), Le((ushort)0x67F2), Le((ushort)binaryValue.Length), binaryValue);
        var op2 = Concat([0x74, 0x00, 0x01, 0x03], Le((ushort)0));
        var buffer = Concat(op1, op2);

        var warnings = new List<string>();
        var handles = new List<RopHandleReference>();
        var reader = new MapiReader(buffer, CancellationToken.None);
        var node1 = RopFastTransferDecoders.Parse(
            ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None, warnings, null, null);

        Assert.Empty(warnings);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);
        var propertyValue = Find(node1.Children, "PropertyValues").Children.Single();
        Assert.Equal("22 bytes", Find(propertyValue.Children, "PropertyValue").Value);

        var node2 = RopFastTransferDecoders.Parse(
            ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None, warnings, null, null);
        Assert.Empty(warnings);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("0x03 (Hierarchy | HardDelete)", Find(node2.Children, "ImportDeleteFlags").Value);
    }

    [Fact]
    public void ParsesSynchronizationImportHierarchyChangeRequestWithTwoValueArrays()
    {
        var bytes = Concat(
            [0x73, 0x00, 0x01],
            Le((ushort)1),
            Le((ushort)0x0003), Le((ushort)0x6748), Le(1u),
            Le((ushort)2),
            Le((ushort)0x0003), Le((ushort)0x3013), Le(2u),
            Le((ushort)0x000B), Le((ushort)0x3014), [0x01]);

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Single(Find(node.Children, "HierarchyValues").Children);
        Assert.Equal(2, Find(node.Children, "PropertyValues").Children.Length);
        Assert.Equal(bytes.Length, node.Length);
    }

    [Fact]
    public void ParsesSynchronizationImportDeletesRequest()
    {
        var bytes = Concat(
            [0x74, 0x00, 0x01, 0x03],
            Le((ushort)0));

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x03 (Hierarchy | HardDelete)", Find(node.Children, "ImportDeleteFlags").Value);
        Assert.Equal(6, node.Length);
    }

    [Fact]
    public void ParsesSynchronizationUploadStateStreamContinueRequestAndKeepsStateDataRaw()
    {
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var bytes = Concat([0x76, 0x00, 0x01], Le((uint)data.Length), data);

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("4", Find(node.Children, "StreamDataSize").Value);
        Assert.Equal("DEADBEEF", Find(node.Children, "StreamData").Value);
    }

    [Fact]
    public void ParsesAndNamesSynchronizationUploadStateStreamBeginAndEnd()
    {
        var begin = Concat(
            [0x75, 0x00, 0x01],
            Le((ushort)0x0102),
            Le((ushort)0x6796),
            Le(17u));
        var (beginNode, beginWarnings, _) = ParseRequest(begin);
        Assert.Empty(beginWarnings);
        Assert.Contains("MetaTagCnsetSeen", Find(beginNode.Children, "StateProperty").Value);
        Assert.Equal("17", Find(beginNode.Children, "TransferBufferSize").Value);

        var (endNode, endWarnings, _) = ParseRequest([0x77, 0x00, 0x01]);
        Assert.Empty(endWarnings);
        Assert.Equal(3, endNode.Length);
    }

    [Fact]
    public void ParsesSynchronizationImportMessageMoveRequestIncludingPredecessorChangeList()
    {
        var guid = Guid.Parse("11112222-3333-4444-5555-666677778888");
        var longTerm = Concat(guid.ToByteArray(), [0x01, 0x02, 0x03, 0x04, 0x05, 0x06]);
        var changeList = Concat([0x16], longTerm);
        var bytes = Concat(
            [0x78, 0x00, 0x01],
            Le((uint)longTerm.Length), longTerm,
            Le((uint)longTerm.Length), longTerm,
            Le((uint)changeList.Length), changeList,
            Le((uint)longTerm.Length), longTerm,
            Le((uint)longTerm.Length), longTerm);

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal($"{guid}-010203040506", Find(node.Children, "SourceFolderId").Value);
        Assert.Equal($"{guid}-010203040506", Find(node.Children, "DestinationMessageId").Value);
        var list = Find(node.Children, "PredecessorChangeList");
        Assert.Equal("PredecessorChangeList", list.Value);
        Assert.Equal("1 SizedXid entry", Find(list.Children, "PredecessorChangeList").Value);
        Assert.Equal(bytes.Length, node.Length);
    }

    [Fact]
    public void ParsesSynchronizationImportReadStateChangesRequest()
    {
        var states = Concat(
            Le((ushort)8), MessageId(0x0003, 0x33), [0x01],
            Le((ushort)8), MessageId(0x0004, 0x44), [0x00]);
        var bytes = Concat([0x80, 0x00, 0x01], Le((ushort)states.Length), states);

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        var array = Find(node.Children, "MessageReadStates");
        Assert.Equal("2 state(s)", array.Value);
        Assert.Equal("true", Find(array.Children[0].Children, "MarkAsRead").Value);
        Assert.Equal("false", Find(array.Children[1].Children, "MarkAsRead").Value);
        Assert.Equal(bytes.Length, node.Length);
    }

    [Fact]
    public void ParsesSetLocalReplicaMidsetDeletedRequestWithDataSizeBoundedRanges()
    {
        var guid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var min = Concat(guid.ToByteArray(), [0, 0, 0, 0, 0, 1], Le((ushort)0));
        var max = Concat(guid.ToByteArray(), [0, 0, 0, 0, 0, 9], Le((ushort)0));
        var payload = Concat(Le(1u), min, max);
        var bytes = Concat([0x93, 0x00, 0x01], Le((ushort)payload.Length), payload);

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        Assert.Equal("52", Find(node.Children, "DataSize").Value);
        var ranges = Find(node.Children, "LongTermIdRanges");
        Assert.Equal("1 range(s)", ranges.Value);
        Assert.Equal($"{guid}-000000000001", Find(ranges.Children, "MinLongTermId").Value);
        Assert.Equal($"{guid}-000000000009", Find(ranges.Children, "MaxLongTermId").Value);
        Assert.Equal(bytes.Length, node.Length);
    }

    // =============================================================================================
    // Response envelopes
    // =============================================================================================

    [Fact]
    public void ParsesFastTransferSourceGetBufferSuccessResponseAndLexesTheTransferBuffer()
    {
        var stream = Concat(Le(0x40090003u), Le(0x400B0003u));
        var bytes = Concat(
            [0x4E, 0x01],
            Le(0u),
            Le((ushort)1), Le((ushort)10), Le((ushort)100), [0x00],
            Le((ushort)stream.Length),
            stream);

        var (node, warnings, handles) = ParseResponse(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x0001 Partial", Find(node.Children, "TransferStatus").Value);
        Assert.Equal("10", Find(node.Children, "InProgressCount").Value);
        Assert.Equal("100", Find(node.Children, "TotalStepCount").Value);
        var buffer = Find(node.Children, "TransferBuffer");
        Assert.Equal(2, buffer.Children.Length);
        Assert.Single(handles);
        Assert.Equal("InputHandleIndex", handles[0].FieldName);
        Assert.Equal(bytes.Length, node.Length);
    }

    [Fact]
    public void ParsesFastTransferSourceGetBufferServerBusyResponseWithBackoffTime()
    {
        var bytes = Concat(
            [0x4E, 0x01],
            Le(0x00000480u),
            Le((ushort)0), Le((ushort)0), Le((ushort)0), [0x00],
            Le((ushort)0),
            Le(5000u));

        var (node, warnings, _) = ParseResponse(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x00000480 (ecServerBusy)", Find(node.Children, "ReturnValue").Value);
        Assert.Equal("5,000 ms", Find(node.Children, "BackoffTime").Value);
        Assert.Equal(19, node.Length);
        Assert.Empty(FindAll(node.Children, "TransferBuffer"));
    }

    [Fact]
    public void ParsesFastTransferSourceGetBufferGenericFailureResponseAsFifteenBytes()
    {
        var bytes = Concat(
            [0x4E, 0x01],
            Le(0x80040115u),
            Le((ushort)0), Le((ushort)0), Le((ushort)0), [0x00],
            Le((ushort)0));

        var (node, warnings, _) = ParseResponse(bytes);

        Assert.Empty(warnings);
        Assert.Equal(15, node.Length);
        Assert.Empty(FindAll(node.Children, "TransferBuffer"));
        Assert.Empty(FindAll(node.Children, "BackoffTime"));
    }

    [Fact]
    public void RetainsAFailedGetBufferResponseThatStillDeclaresATransferBuffer()
    {
        var bytes = Concat(
            [0x4E, 0x01],
            Le(0x80040115u),
            Le((ushort)0), Le((ushort)0), Le((ushort)0), [0x00],
            Le((ushort)4),
            [0x01, 0x02, 0x03, 0x04]);

        var (node, warnings, _) = ParseResponse(bytes);

        Assert.Equal("01020304", Find(node.Children, "TransferBuffer (failed operation)").Value);
        Assert.Contains(warnings, warning => warning.Contains("retained as raw", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesDestinationPutBufferResponseAsFifteenBytes()
    {
        var bytes = Concat(
            [0x54, 0x02],
            Le(0u),
            Le((ushort)3), Le((ushort)7), Le((ushort)7), [0x00],
            Le((ushort)512));

        var (node, warnings, _) = ParseResponse(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x0003 (clients MUST ignore)", Find(node.Children, "TransferStatus").Value);
        Assert.Equal("512", Find(node.Children, "BufferUsedSize").Value);
        Assert.Equal(15, node.Length);
    }

    [Fact]
    public void ParsesDestinationPutBufferExtendedResponseAsNineteenBytes()
    {
        var bytes = Concat(
            [0x9D, 0x02],
            Le(0u),
            Le((ushort)2),
            Le(70000u), Le(90000u),
            [0x00],
            Le((ushort)1024));

        var (node, warnings, _) = ParseResponse(bytes);

        Assert.Empty(warnings);
        Assert.Equal("0x0002 (clients MUST ignore)", Find(node.Children, "TransferStatus").Value);
        Assert.Equal("70000", Find(node.Children, "InProgressCount").Value);
        Assert.Equal("90000", Find(node.Children, "TotalStepCount").Value);
        Assert.Equal("1024", Find(node.Children, "BufferUsedSize").Value);
        Assert.Equal(19, node.Length);
    }

    [Theory]
    [InlineData((byte)0x72, "MessageId")]
    [InlineData((byte)0x73, "FolderId")]
    [InlineData((byte)0x78, "MessageId")]
    public void ParsesIcsImportSuccessResponsesWithTheirTrailingId(byte ropId, string fieldName)
    {
        var success = Concat([ropId, 0x01], Le(0u), MessageId(0x0007, 0x77));
        var (node, warnings, _) = ParseResponse(success);
        Assert.Empty(warnings);
        Assert.EndsWith("7-777777777777", Find(node.Children, fieldName).Value);
        Assert.Equal(14, node.Length);
        if (ropId == 0x72)
        {
            Assert.Equal("OutputHandleIndex", Find(node.Children, "OutputHandleIndex").Name);
        }

        var failure = Concat([ropId, 0x01], Le(0x80040115u));
        var (failureNode, _, _) = ParseResponse(failure);
        Assert.Equal(6, failureNode.Length);
        Assert.Empty(FindAll(failureNode.Children, fieldName));
    }

    [Fact]
    public void ParsesGetLocalReplicaIdsSuccessResponse()
    {
        var guid = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");
        var bytes = Concat([0x7F, 0x03], Le(0u), guid.ToByteArray(), [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);

        var (node, warnings, _) = ParseResponse(bytes);

        Assert.Empty(warnings);
        Assert.Equal(guid.ToString(), Find(node.Children, "ReplGuid").Value);
        Assert.Equal("AABBCCDDEEFF", Find(node.Children, "GlobalCount").Value);
        Assert.Equal(28, node.Length);
    }

    // =============================================================================================
    // Lexical coverage
    // =============================================================================================

    [Fact]
    public void LexesEveryDefinedMarkerAndTracksNesting()
    {
        var stream = Concat([.. FastTransferStreamLexer.MarkerNames.Keys.Select(Le)]);
        var result = Lex(stream);

        Assert.Equal(FastTransferStreamLexer.MarkerNames.Count, result.ElementCount);
        Assert.Equal(FastTransferStreamLexer.MarkerNames.Count, result.Nodes.Length);
        Assert.All(result.Nodes, n => Assert.Equal("Marker", n.Name));
        Assert.False(result.State.Desynchronized);
    }

    [Fact]
    public void TracksMarkerNestingDepthAcrossBuffers()
    {
        var first = Lex(Concat(Le(0x400C0003u), Le(0x40000003u)));
        Assert.Equal(2, first.State.MarkerDepth);

        var second = FastTransferStreamLexer.Lex(
            Concat(Le(0x400E0003u), Le(0x400D0003u)),
            0,
            first.State,
            new MapiNodeBudget(),
            0,
            CancellationToken.None);
        Assert.Equal(0, second.State.MarkerDepth);
        Assert.Empty(second.Warnings);
    }

    [Fact]
    public void WarnsOnAnEndMarkerWithNoMatchingStartMarker()
    {
        var result = Lex(Le(0x400D0003u));

        Assert.Single(result.Nodes);
        Assert.Contains(result.Warnings, w => w.Contains("no matching start marker", StringComparison.Ordinal));
        Assert.Equal(0, result.State.MarkerDepth);
    }

    [Fact]
    public void LeavesAMismatchedSyntacticalProductionOpen()
    {
        var result = Lex(Concat(Le(0x400C0003u), Le(0x400E0003u)));

        Assert.Equal(1, result.State.MarkerDepth);
        Assert.Equal("Message", result.State.SyntaxStack[0].Production);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("requires EndMessage", StringComparison.Ordinal));
        Assert.Contains("mismatched end marker for Message", result.Nodes[1].Value);
    }

    [Fact]
    public void ClassifiesIncrementalSyncMarkersWithoutInventingNesting()
    {
        var result = Lex(Concat(
            Le(0x40120003u),
            Le(0x40130003u),
            Le(0x402F0003u),
            Le(0x40140003u)));

        Assert.Equal(0, result.State.MarkerDepth);
        Assert.Empty(result.Warnings);
        Assert.Contains("starts IncrementalSyncChange", result.Nodes[0].Value);
        Assert.Contains("starts Deletions", result.Nodes[1].Value);
        Assert.Contains("starts ReadStateChanges", result.Nodes[2].Value);
        Assert.Contains("starts IncrementalSyncEnd", result.Nodes[3].Value);
    }

    [Fact]
    public void DecodesPublishedProgressInformationSpecialProperty()
    {
        var payload = Concat(
            Le((ushort)0),
            Le((ushort)0),
            Le(0x98765432u),
            Le(0xBABEBABEBABEBABEul),
            Le(0x00ABCDEFu),
            Le(0u),
            Le(0x1234567890ABCDEFul));
        var result = Lex(Concat(
            Le(0x4074000Bu),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le((uint)payload.Length),
            payload));

        Assert.Empty(result.Warnings);
        Assert.Equal(0x98765432u.ToString(), Find(result.Nodes, "FAIMessageCount").Value);
        Assert.Equal(0xBABEBABEBABEBABEul.ToString(), Find(result.Nodes, "FAIMessageTotalSize").Value);
        Assert.Equal(0x00ABCDEFu.ToString(), Find(result.Nodes, "NormalMessageCount").Value);
        Assert.Equal(0x1234567890ABCDEFul.ToString(), Find(result.Nodes, "NormalMessageTotalSize").Value);
        Assert.Contains("ProgressInformation (special)", Find(result.Nodes, "PropID").Value);
    }

    [Fact]
    public void DecodesPropertyGroupInfoIncludingNamedPropertyNames()
    {
        var propertySet = Guid.Parse("00062008-0000-0000-c000-000000000046");
        var name = Encoding.Unicode.GetBytes("CustomName");
        var group = Concat(
            Le(2u),
            Le((ushort)0x0003), Le((ushort)0x3001),
            Le((ushort)0x001F), Le((ushort)0x8001),
            propertySet.ToByteArray(),
            Le(1u),
            Le((uint)name.Length),
            name);
        var payload = Concat(Le(7u), Le(0u), Le(1u), group);
        var result = Lex(Concat(
            Le(0x407B0102u),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le((uint)payload.Length),
            payload));

        Assert.Empty(result.Warnings);
        Assert.Equal("7", Find(result.Nodes, "GroupId").Value);
        Assert.Equal("1", Find(result.Nodes, "GroupCount").Value);
        Assert.Equal("2", Find(result.Nodes, "PropertyTagCount").Value);
        Assert.Equal("CustomName", Find(result.Nodes, "Name").Value);
        Assert.Contains("PropertyGroupInfo (special)", Find(result.Nodes, "PropID").Value);
    }

    [Fact]
    public void ReconstructsSplitProgressInformationBeforeSemanticDecoding()
    {
        var payload = Concat(
            Le((ushort)0),
            Le((ushort)0),
            Le(2u),
            Le(3ul),
            Le(4u),
            Le(0u),
            Le(5ul));
        var prefix = Concat(
            Le(0x4074000Bu),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le((uint)payload.Length));
        var first = Lex(Concat(prefix, payload[..10]));

        Assert.NotNull(first.State.Pending);
        Assert.Equal(0x4074000Bu, first.State.Pending!.Value.SpecialMarker);

        var second = FastTransferStreamLexer.Lex(
            payload[10..],
            0,
            first.State,
            new MapiNodeBudget(),
            0,
            CancellationToken.None);

        Assert.Null(second.State.Pending);
        Assert.Equal("2", Find(second.Nodes, "FAIMessageCount").Value);
        Assert.Equal("5", Find(second.Nodes, "NormalMessageTotalSize").Value);
        Assert.Equal(0, Find(second.Nodes, "ProgressInformation").Length);
        Assert.All(
            Find(second.Nodes, "ProgressInformation").Children,
            child => Assert.Equal(0, child.Length));
    }

    [Fact]
    public void FallsBackTransactionallyForHostilePropertyGroupCounts()
    {
        var payload = Concat(Le(1u), Le(1u), Le(uint.MaxValue));
        var result = Lex(Concat(
            Le(0x407B0102u),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le((uint)payload.Length),
            payload));

        Assert.False(result.State.Desynchronized);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("GroupCount", StringComparison.Ordinal)
            && warning.Contains("retained as raw", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains("Reserved is", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(payload), Find(result.Nodes, "Value").Value);
    }

    [Fact]
    public void DoesNotAccumulateOversizedSpecialStructures()
    {
        var declared = (uint)FastTransferLimits.MaxSpecialStructureBytes + 1;
        var result = Lex(Concat(
            Le(0x407B0102u),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le(declared),
            [0x01]));

        Assert.NotNull(result.State.Pending);
        Assert.Null(result.State.Pending!.Value.SpecialMarker);
        Assert.True(result.State.Pending!.Value.AccumulatedBytes.IsDefault);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("reconstruction limit", StringComparison.Ordinal));
    }

    [Fact]
    public void RetainsTrailingBytesFromAReconstructedPropertyGroupInfo()
    {
        var payload = Concat(
            Le(1u),
            Le(0u),
            Le(1u),
            Le(1u),
            Le((ushort)0x0003),
            Le((ushort)0x3001),
            [0xAA, 0xBB]);
        var prefix = Concat(
            Le(0x407B0102u),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le((uint)payload.Length));
        var first = Lex(Concat(prefix, payload[..8]));
        var second = FastTransferStreamLexer.Lex(
            payload[8..],
            0,
            first.State,
            new MapiNodeBudget(),
            0,
            CancellationToken.None);

        Assert.Equal("AABB; reconstructed", Find(second.Nodes, "Value trailing bytes").Value);
        Assert.Contains(second.Warnings, warning =>
            warning.Contains("left unparsed", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnknownGroupPropertyNameKindsTransactionally()
    {
        var propertySet = Guid.Parse("00062008-0000-0000-c000-000000000046");
        var group = Concat(
            Le(1u),
            Le((ushort)0x001F),
            Le((ushort)0x8001),
            propertySet.ToByteArray(),
            Le(2u));
        var payload = Concat(Le(7u), Le(0u), Le(1u), group);
        var result = Lex(Concat(
            Le(0x407B0102u),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le((uint)payload.Length),
            payload));

        Assert.False(result.State.Desynchronized);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("Kind 0x00000002 is not defined", StringComparison.Ordinal)
            && warning.Contains("retained as raw", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(payload), Find(result.Nodes, "Value").Value);
    }

    [Fact]
    public void KeepsWrongSizedProgressInformationRawWithoutAccumulatingIt()
    {
        var payload = new byte[31];
        var result = Lex(Concat(
            Le(0x4074000Bu),
            Le((ushort)0x0102),
            Le((ushort)0x0000),
            Le((uint)payload.Length),
            payload));

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("requires exactly 32", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(payload), Find(result.Nodes, "Value").Value);
    }

    [Fact]
    public void RejectsMarkerNestingBeyondTheDepthLimit()
    {
        var stream = Concat([.. Enumerable.Repeat(Le(0x400C0003u), 65)]);
        var result = Lex(stream);

        Assert.True(result.State.Desynchronized);
        Assert.Contains(result.Warnings, w => w.Contains("marker nesting exceeds", StringComparison.Ordinal));
        Assert.Contains(result.Nodes, n => n.Name == "Unlexed stream remainder");
    }

    [Fact]
    public void LexesFixedPropertyTypesIncludingTheTwoByteFastTransferBoolean()
    {
        var stream = Concat(
            Le((ushort)0x000B), Le((ushort)0x0E1F), Le((ushort)1),
            Le((ushort)0x0003), Le((ushort)0x3013), Le(42u),
            Le((ushort)0x0002), Le((ushort)0x0E07), Le((ushort)7),
            Le((ushort)0x0048), Le((ushort)0x3018), Guid.Empty.ToByteArray());

        var result = Lex(stream);

        Assert.Equal(4, result.ElementCount);
        Assert.Equal(4, result.Nodes.Length);
        Assert.Contains("true", result.Nodes[0].Value);
        Assert.Equal(6, result.Nodes[0].Length);
        Assert.Contains("42", result.Nodes[1].Value);
        Assert.Contains("7", result.Nodes[2].Value);
        Assert.Contains(Guid.Empty.ToString(), result.Nodes[3].Value);
        Assert.Equal(20, result.Nodes[3].Length);
    }

    [Fact]
    public void LexesInteger64IdentifiersAsMessageFolderAndChangeNumbers()
    {
        var stream = Concat(
            Le((ushort)0x0014), Le((ushort)0x674A), MessageId(0x0001, 0x11),
            Le((ushort)0x0014), Le((ushort)0x6748), MessageId(0x0002, 0x22),
            Le((ushort)0x0014), Le((ushort)0x67A4), MessageId(0x0003, 0x33),
            Le((ushort)0x0014), Le((ushort)0x3018), Le(5ul));

        var result = Lex(stream);

        Assert.Equal(4, result.ElementCount);
        Assert.Contains("MessageID 1-111111111111", result.Nodes[0].Value);
        Assert.Contains("FolderID 2-222222222222", result.Nodes[1].Value);
        Assert.Contains("CN 3-333333333333", result.Nodes[2].Value);
        Assert.Contains("5", result.Nodes[3].Value);
    }

    [Fact]
    public void LexesVariableLengthStringsAndBinaries()
    {
        var text = Encoding.Unicode.GetBytes("Hello\0");
        var ansi = Encoding.ASCII.GetBytes("plain\0");
        var stream = Concat(
            Le((ushort)0x001F), Le((ushort)0x3001), Le((uint)text.Length), text,
            Le((ushort)0x001E), Le((ushort)0x3A00), Le((uint)ansi.Length), ansi,
            Le((ushort)0x0102), Le((ushort)0x1000), Le(3u), [0x01, 0x02, 0x03]);

        var result = Lex(stream);

        Assert.Equal(3, result.ElementCount);
        Assert.Equal("Hello", Find(result.Nodes[0].Children, "Value").Value);
        Assert.Equal("plain", Find(result.Nodes[1].Children, "Value").Value);
        Assert.Equal("010203", Find(result.Nodes[2].Children, "Value").Value);
    }

    [Fact]
    public void LexesCodePageStringTypes()
    {
        var text = Encoding.Unicode.GetBytes("cp\0");
        var stream = Concat(Le((ushort)0x84B0), Le((ushort)0x3001), Le((uint)text.Length), text);

        var result = Lex(stream);

        Assert.Single(result.Nodes);
        Assert.Contains("PtypCodePageUnicode", Find(result.Nodes[0].Children, "PropType").Value);
        Assert.Equal("cp", Find(result.Nodes[0].Children, "Value").Value);
    }

    [Fact]
    public void LexesMultiValueFixedAndVariableArrays()
    {
        var a = Encoding.Unicode.GetBytes("a\0");
        var b = Encoding.Unicode.GetBytes("bb\0");
        var stream = Concat(
            Le((ushort)0x1003), Le((ushort)0x3013), Le(3u), Le(1u), Le(2u), Le(3u),
            Le((ushort)0x101F), Le((ushort)0x3001), Le(2u),
            Le((uint)a.Length), a, Le((uint)b.Length), b);

        var result = Lex(stream);

        Assert.Equal(2, result.ElementCount);
        Assert.Equal("3", Find(result.Nodes[0].Children, "Count").Value);
        Assert.Equal(3, result.Nodes[0].Children.Count(c => c.Name.StartsWith('[')));
        Assert.Equal("2", Find(result.Nodes[1].Children, "Count").Value);
        Assert.Equal("a", Find(result.Nodes[1].Children[3].Children, "Value").Value);
        Assert.Equal("bb", Find(result.Nodes[1].Children[4].Children, "Value").Value);
    }

    [Fact]
    public void LexesNamedPropertiesByLidAndByName()
    {
        var addressSet = Guid.Parse("00062004-0000-0000-C000-000000000046");
        var headers = Guid.Parse("00020386-0000-0000-C000-000000000046");
        var name = Encoding.Unicode.GetBytes("Content-Class\0");
        var stream = Concat(
            Le((ushort)0x001F), Le((ushort)0x8005), addressSet.ToByteArray(), [0x00], Le(0x8005u),
            Le(2u), Encoding.Unicode.GetBytes("x"),
            Le((ushort)0x001F), Le((ushort)0x8010), headers.ToByteArray(), [0x01], name,
            Le(2u), Encoding.Unicode.GetBytes("y"));

        var result = Lex(stream);

        Assert.Equal(2, result.ElementCount);
        var lid = Find(result.Nodes[0].Children, "NamedPropInfo");
        Assert.Equal("0x00 LID", Find(lid.Children, "Kind").Value);
        Assert.Contains("PSETID_Address", Find(lid.Children, "PropertySet").Value);
        var named = Find(result.Nodes[1].Children, "NamedPropInfo");
        Assert.Equal("0x01 Name", Find(named.Children, "Kind").Value);
        Assert.StartsWith("Content-Class", Find(named.Children, "Name").Value);
    }

    [Fact]
    public void RejectsAnUnknownNamedPropertyKind()
    {
        var stream = Concat(
            Le((ushort)0x0003), Le((ushort)0x8005), Guid.Empty.ToByteArray(), [0x07], Le(0u));

        var result = Lex(stream);

        Assert.True(result.State.Desynchronized);
        Assert.Contains(result.Warnings, w => w.Contains("neither LID", StringComparison.Ordinal));
    }

    [Fact]
    public void LexesTheThreeMetaPropertyValueShapes()
    {
        var dn = Encoding.ASCII.GetBytes("/o=First\0");
        var replica = FolderReplicaInfo();
        var stream = Concat(
            Le(0x400F0003u), Le(3u),
            Le(0x4008001Eu), Le((uint)dn.Length), dn,
            Le(0x40110102u), Le((uint)replica.Length), replica,
            Le(0x40160003u), Le(0x3001001Fu));

        var result = Lex(stream);

        Assert.Equal(4, result.ElementCount);
        Assert.Equal("MetaTagEcWarning", result.Nodes[0].Value);
        Assert.Equal("0x00000003 (3)", Find(result.Nodes[0].Children, "Value").Value);
        Assert.Equal("MetaTagDnPrefix", result.Nodes[1].Value);
        Assert.Equal("/o=First", Find(result.Nodes[1].Children, "Value").Value);
        Assert.Equal("MetaTagNewFXFolder", result.Nodes[2].Value);
        var info = Find(result.Nodes[2].Children, "FolderReplicaInfo");
        Assert.Equal("1 server DN(s)", info.Value);
        Assert.Equal("/o=Server", Find(info.Children, "ServerDNArray[0]").Value);
        Assert.Equal("MetaTagFXDelProp", result.Nodes[3].Value);
        Assert.Contains("PtypString", Find(result.Nodes[3].Children, "Value").Value);
    }

    [Fact]
    public void LexesTheObjectLengthSentinelWithoutConsumingAValue()
    {
        var stream = Concat(
            Le((ushort)0x000D), Le((ushort)0x3701), Le(0xFFFFFFFFu),
            Le(0x400D0003u));

        var result = Lex(stream);

        Assert.Equal(2, result.ElementCount);
        Assert.Equal(8, result.Nodes[0].Length);
        Assert.Contains("embedded object placeholder", result.Nodes[0].Value);
        Assert.StartsWith("EndMessage", result.Nodes[1].Value);
    }

    [Fact]
    public void LexesXidAndPredecessorChangeListBinaries()
    {
        var guid = Guid.Parse("11112222-3333-4444-5555-666677778888");
        var xid = Concat(guid.ToByteArray(), [0x01, 0x02, 0x03, 0x04, 0x05, 0x06]);
        var changeList = Concat([0x16], xid, [0x11], guid.ToByteArray(), [0x09]);
        var stream = Concat(
            Le((ushort)0x0102), Le((ushort)0x65E0), Le((uint)xid.Length), xid,
            Le((ushort)0x0102), Le((ushort)0x65E3), Le((uint)changeList.Length), changeList);

        var result = Lex(stream);

        Assert.Equal(2, result.ElementCount);
        Assert.Contains("XID", result.Nodes[0].Value);
        Assert.Equal($"{guid}-010203040506", Find(result.Nodes[0].Children, "XID").Value);
        Assert.Equal("2 SizedXid entries", Find(result.Nodes[1].Children, "PredecessorChangeList").Value);
    }

    [Fact]
    public void LexesSerializedIdsetsWithGlobsetCommands()
    {
        var guid = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00001111");
        var globset = new byte[] { 0x01, 0xAA, 0x42, 0x01, 0x0F, 0x50, 0x00 };
        var cnset = Concat(guid.ToByteArray(), globset);
        var replidSet = Concat(Le((ushort)5), [0x52, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x01, 0x02, 0x03, 0x04, 0x05, 0x09, 0x00]);
        var stream = Concat(
            Le((ushort)0x0102), Le((ushort)0x6796), Le((uint)cnset.Length), cnset,
            Le((ushort)0x0102), Le((ushort)0x67E5), Le((uint)replidSet.Length), replidSet);

        var result = Lex(stream);

        Assert.Equal(2, result.ElementCount);
        Assert.Contains("REPLGUID", result.Nodes[0].Value);
        var guidEntry = Find(result.Nodes[0].Children, "IDSET_REPLGUID[0]");
        Assert.Equal(guid.ToString(), guidEntry.Value);
        Assert.Equal("4 command(s)", Find(guidEntry.Children, "GLOBSET").Value);
        Assert.Contains("REPLID", result.Nodes[1].Value);
        Assert.Equal("2 command(s)", Find(result.Nodes[1].Children, "GLOBSET").Value);
    }

    [Fact]
    public void GlobsetPushCompletingSixBytesEmitsSingletonWithoutExtendingPrefix()
    {
        var guid = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00001111");
        var globset = new byte[]
        {
            0x02, 0xAA, 0xBB,
            0x04, 0x01, 0x02, 0x03, 0x04,
            0x01, 0xCC,
            0x52, 0x10, 0x11, 0x12, 0x20, 0x21, 0x22,
            0x50,
            0x50,
            0x00,
        };
        var cnset = Concat(guid.ToByteArray(), globset);
        var stream = Concat(Le((ushort)0x0102), Le((ushort)0x6796), Le((uint)cnset.Length), cnset);

        var result = Lex(stream);

        Assert.Empty(result.Warnings);
        var parsed = Find(Find(result.Nodes[0].Children, "IDSET_REPLGUID[0]").Children, "GLOBSET");
        Assert.Equal("7 command(s)", parsed.Value);
        Assert.Contains(
            parsed.Children,
            node => node.Name == "[1] Push" && node.Value!.Contains("completes one GLOBCNT", StringComparison.Ordinal));
    }

    [Fact]
    public void LexesMetaTagIdsetGivenAsAVariableLengthValueDespiteItsInteger32Type()
    {
        var guid = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00001111");
        var payload = Concat(guid.ToByteArray(), [0x01, 0x01, 0x00]);
        var stream = Concat(Le((ushort)0x0003), Le((ushort)0x4017), Le((uint)payload.Length), payload);

        var result = Lex(stream);

        Assert.Single(result.Nodes);
        Assert.Contains("MetaTagIdsetGiven", Find(result.Nodes[0].Children, "PropID").Value);
        Assert.Contains("REPLGUID", result.Nodes[0].Value);
    }

    [Fact]
    public void FallsBackToRawWhenAnIdsetIsStructurallyInvalid()
    {
        // A pop command with no matching push: upstream faults here, the lexer must not.
        var guid = Guid.NewGuid();
        var payload = Concat(guid.ToByteArray(), [0x50, 0x00]);
        var stream = Concat(Le((ushort)0x0102), Le((ushort)0x6796), Le((uint)payload.Length), payload);

        var result = Lex(stream);

        Assert.Single(result.Nodes);
        Assert.False(result.State.Desynchronized);
        Assert.Contains(result.Warnings, w => w.Contains("could not be decoded", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(payload), Find(result.Nodes[0].Children, "Value").Value);
    }

    // =============================================================================================
    // Cross-buffer (capture-local) reconstruction
    // =============================================================================================

    [Fact]
    public void ReconstructsAValueSplitAcrossTwoTransferBuffers()
    {
        var value = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le((uint)value.Length), value[..8]);
        var tail = Concat(value[8..], Le(0x400D0003u));

        var assembler = new FastTransferStreamAssembler();
        var key = new FastTransferStreamKey("connection-1", 0x1003);
        var budget = new MapiNodeBudget();

        var first = assembler.Continue(key, head, 0, budget, 0, CancellationToken.None);
        Assert.True(first.EndedInsideValue);
        Assert.NotNull(first.State.Pending);
        Assert.Equal(12, first.State.Pending!.Value.RemainingLength);
        Assert.Equal(20, first.State.Pending!.Value.DeclaredLength);
        Assert.Equal("PartialValue", first.Nodes[0].Children[^1].Name);

        var second = assembler.Continue(key, tail, head.Length, budget, 0, CancellationToken.None);
        Assert.Null(second.State.Pending);
        Assert.False(second.EndedInsideValue);
        Assert.Equal("PartialValueContinuation", second.Nodes[0].Name);
        Assert.Contains("value complete", second.Nodes[0].Value);
        Assert.StartsWith("EndMessage", second.Nodes[1].Value);

        Assert.Equal(2, assembler.StateFor(key).BufferCount);
        Assert.Equal(head.Length + tail.Length, assembler.StateFor(key).TotalBytes);
    }

    [Fact]
    public void ResumesTheRemainingElementsOfASplitMultiValueArray()
    {
        var first = Encoding.Unicode.GetBytes("aaaa");
        var head = Concat(
            Le((ushort)0x101F), Le((ushort)0x3001), Le(2u),
            Le((uint)first.Length), first[..4]);
        var tailText = Encoding.Unicode.GetBytes("bb");
        var tail = Concat(first[4..], Le((uint)tailText.Length), tailText);

        var assembler = new FastTransferStreamAssembler();
        var key = new FastTransferStreamKey("connection-2", 0x2000);
        var budget = new MapiNodeBudget();

        var head1 = assembler.Continue(key, head, 0, budget, 0, CancellationToken.None);
        Assert.NotNull(head1.State.Pending);
        Assert.Equal(1, head1.State.Pending!.Value.RemainingMultiValueElements);

        var tail1 = assembler.Continue(key, tail, head.Length, budget, 0, CancellationToken.None);
        Assert.Null(tail1.State.Pending);
        Assert.Equal("PartialValueContinuation", tail1.Nodes[0].Name);
        var continuation = Find(tail1.Nodes, "PartialMultiValueContinuation");
        Assert.Equal("1 of 1 remaining element(s)", continuation.Value);
        Assert.Equal("bb", Find(continuation.Children, "Value").Value);
    }

    [Fact]
    public void KeepsStreamsForDistinctCaptureScopesCompletelySeparate()
    {
        var head = Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le(8u), [0x01, 0x02]);
        var assembler = new FastTransferStreamAssembler();
        var a = new FastTransferStreamKey("connection-a", 0x3000);
        var b = new FastTransferStreamKey("connection-b", 0x3000);
        var budget = new MapiNodeBudget();

        assembler.Continue(a, head, 0, budget, 0, CancellationToken.None);
        Assert.NotNull(assembler.StateFor(a).Pending);
        Assert.Null(assembler.StateFor(b).Pending);
        Assert.Single(assembler.Snapshot);

        assembler.Forget(a);
        Assert.Null(assembler.StateFor(a).Pending);
        Assert.Empty(assembler.Snapshot);
    }

    [Fact]
    public void RetainsEveryLaterBufferAsRawOnceAStreamIsDesynchronized()
    {
        var assembler = new FastTransferStreamAssembler();
        var key = new FastTransferStreamKey("connection-3", 0x4000);
        var budget = new MapiNodeBudget();

        var bad = assembler.Continue(key, Le((ushort)0x0009).Concat(Le((ushort)0x1234)).ToArray(), 0, budget, 0, CancellationToken.None);
        Assert.True(bad.State.Desynchronized);

        var later = assembler.Continue(key, Le(0x400D0003u), 4, budget, 0, CancellationToken.None);
        Assert.Equal(0, later.ElementCount);
        Assert.Equal("Unsynchronized stream bytes", later.Nodes.Single().Name);
        Assert.Contains(later.Warnings, w => w.Contains("desynchronized", StringComparison.Ordinal));
    }

    // =============================================================================================
    // Hostile input
    // =============================================================================================

    [Fact]
    public void RetainsATrailingPartialAtomAsRaw()
    {
        var result = Lex(Concat(Le(0x400D0003u), [0xAA, 0xBB]));

        Assert.Equal(1, result.ElementCount);
        Assert.True(result.EndedInsideValue);
        Assert.Equal("Trailing partial atom", result.Nodes[^1].Name);
        Assert.Contains(result.Warnings, w => w.Contains("forbids splitting", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAPropertyTypeWhoseLengthCannotBeDerived()
    {
        var result = Lex(Concat(Le((ushort)0x0009), Le((ushort)0x1234), [0x01, 0x02, 0x03, 0x04]));

        Assert.Equal(0, result.ElementCount);
        Assert.True(result.State.Desynchronized);
        Assert.Contains(result.Warnings, w => w.Contains("cannot be determined", StringComparison.Ordinal));
        Assert.Equal("Unlexed stream remainder", result.Nodes.Single().Name);
    }

    [Fact]
    public void RefusesAnAbsurdDeclaredValueLength()
    {
        var result = Lex(Concat(Le((ushort)0x0102), Le((ushort)0x1000), Le(0x7FFFFFF0u), [0x01]));

        Assert.True(result.State.Desynchronized);
        Assert.Contains(result.Warnings, w => w.Contains("exceeds the", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAnAbsurdMultiValueElementCount()
    {
        var result = Lex(Concat(Le((ushort)0x1003), Le((ushort)0x3013), Le(0x00FFFFFFu)));

        Assert.True(result.State.Desynchronized);
        Assert.Contains(result.Warnings, w => w.Contains("count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StopsAtAMultiValueArrayThatOverrunsTheBuffer()
    {
        var result = Lex(Concat(Le((ushort)0x1003), Le((ushort)0x3013), Le(50u), Le(1u)));

        Assert.True(result.State.Desynchronized);
        Assert.Contains(result.Nodes, n => n.Name == "Unlexed stream remainder");
    }

    [Fact]
    public void RefusesATruncatedRequestEnvelopeWithoutInventingBytes()
    {
        var bytes = Concat([0x4B, 0x00, 0x01, 0x02], Le((ushort)4), MessageId(0x0001, 0x11));
        Assert.Throws<MapiParseException>(() =>
        {
            var reader = new MapiReader(bytes, CancellationToken.None);
            RopFastTransferDecoders.Parse(
                ref reader, 0, MapiDirection.Request, [], new MapiNodeBudget(), CancellationToken.None,
                null, null, null);
        });
    }

    [Fact]
    public void RefusesAMessageReadStateThatOverrunsItsDeclaredExtent()
    {
        var states = Concat(Le((ushort)200), MessageId(0x0003, 0x33), [0x01]);
        var bytes = Concat([0x80, 0x00, 0x01], Le((ushort)states.Length), states);

        var thrown = Assert.Throws<MapiParseException>(() =>
        {
            var reader = new MapiReader(bytes, CancellationToken.None);
            RopFastTransferDecoders.Parse(
                ref reader, 0, MapiDirection.Request, [], new MapiNodeBudget(), CancellationToken.None,
                null, null, null);
        });
        Assert.Contains("MessageIdSize", thrown.Message);
    }

    [Fact]
    public void RefusesALongTermIdRangeCountThatOverrunsItsDeclaredExtent()
    {
        var payload = Concat(Le(1000u), new byte[8]);
        var bytes = Concat([0x93, 0x00, 0x01], Le((ushort)payload.Length), payload);

        var thrown = Assert.Throws<MapiParseException>(() =>
        {
            var reader = new MapiReader(bytes, CancellationToken.None);
            RopFastTransferDecoders.Parse(
                ref reader, 0, MapiDirection.Request, [], new MapiNodeBudget(), CancellationToken.None,
                null, null, null);
        });
        Assert.Contains("LongTermIdRangeCount", thrown.Message);
    }

    [Fact]
    public void HonoursCancellationWhileLexing()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var stream = Concat([.. Enumerable.Repeat(Le(0x400C0003u), 4)]);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            FastTransferStreamLexer.Lex(
                stream, 0, FastTransferStreamState.Initial, new MapiNodeBudget(), 0, source.Token));
    }

    [Fact]
    public void HonoursCancellationWhileDecodingARequestEnvelope()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var bytes = Concat([0x72, 0x00, 0x01, 0x02, 0x00], Le((ushort)1), Le((ushort)0x0003), Le((ushort)0x3013), Le(1u));

        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            var reader = new MapiReader(bytes, source.Token);
            RopFastTransferDecoders.Parse(
                ref reader, 0, MapiDirection.Request, [], new MapiNodeBudget(), source.Token, null, null, null);
        });
    }

    [Fact]
    public void SurfacesLexerWarningsAsInTreeNodesWhenNoWarningSinkIsSupplied()
    {
        var stream = Concat(Le(0x400D0003u));
        var bytes = Concat([0x54, 0x00, 0x01], Le((ushort)stream.Length), stream);

        var reader = new MapiReader(bytes, CancellationToken.None);
        var node = RopFastTransferDecoders.Parse(
            ref reader, 0, MapiDirection.Request, [], new MapiNodeBudget(), CancellationToken.None,
            null, null, null);

        var warning = Find(node.Children, "Warning");
        Assert.Equal(MapiNodeKind.Warning, warning.Kind);
        Assert.Contains("no matching start marker", warning.Value);
    }

    [Fact]
    public void EmptyTransferBuffersProduceNoElementsAndNoWarnings()
    {
        var bytes = Concat([0x54, 0x00, 0x01], Le((ushort)0));

        var (node, warnings, _) = ParseRequest(bytes);

        Assert.Empty(warnings);
        var data = Find(node.Children, "TransferData");
        Assert.Empty(data.Children);
        Assert.Equal(5, node.Length);
    }

    [Fact]
    public void EveryOperationNodeUsesTheSharedOperationShape()
    {
        var bytes = Concat([0x4E, 0x00, 0x01], Le((ushort)0x1000));
        var reader = new MapiReader(bytes, CancellationToken.None);
        var node = RopFastTransferDecoders.Parse(
            ref reader, 7, MapiDirection.Request, [], new MapiNodeBudget(), CancellationToken.None);

        Assert.Equal("Operation 7", node.Name);
        Assert.Equal(MapiNodeKind.Operation, node.Kind);
        Assert.Equal("RopFastTransferSourceGetBuffer (0x4E)", node.Value);
        Assert.True(node.Length >= 3);
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private static (MapiNode Node, List<string> Warnings, List<RopHandleReference> Handles) ParseRequest(
        byte[] bytes) => ParseOperation(bytes, MapiDirection.Request);

    private static (MapiNode Node, List<string> Warnings, List<RopHandleReference> Handles) ParseResponse(
        byte[] bytes) => ParseOperation(bytes, MapiDirection.Response);

    private static (MapiNode Node, List<string> Warnings, List<RopHandleReference> Handles) ParseOperation(
        byte[] bytes,
        MapiDirection direction)
    {
        var warnings = new List<string>();
        var handles = new List<RopHandleReference>();
        var reader = new MapiReader(bytes, CancellationToken.None);
        var node = RopFastTransferDecoders.Parse(
            ref reader, 0, direction, handles, new MapiNodeBudget(), CancellationToken.None, warnings, null, null);
        Assert.True(
            reader.End,
            $"Expected the operation to consume all {bytes.Length} byte(s) but {reader.Remaining} remain.");
        Assert.Equal(bytes.Length, node.Length);
        return (node, warnings, handles);
    }

    private static FastTransferLexResult Lex(byte[] bytes) =>
        FastTransferStreamLexer.Lex(
            bytes, 0, FastTransferStreamState.Initial, new MapiNodeBudget(), 0, CancellationToken.None);

    private static byte[] FolderReplicaInfo()
    {
        var dn = Encoding.ASCII.GetBytes("/o=Server\0");
        return Concat(
            Le(0u),
            Le(1u),
            Guid.Empty.ToByteArray(), [0x00, 0x00, 0x00, 0x00, 0x00, 0x01], Le((ushort)0),
            Le(1u),
            Le(1u),
            dn);
    }

    private static byte[] MessageId(ushort replicaId, byte fill) =>
        Concat(Le(replicaId), [.. Enumerable.Repeat(fill, 6)]);

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
            if (node.Name == name)
            {
                yield return node;
            }
            foreach (var match in FindAll(node.Children, name))
            {
                yield return match;
            }
        }
    }
}
