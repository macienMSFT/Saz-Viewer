using System.Buffers.Binary;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

/// <summary>
/// Exhaustive synthetic coverage for <see cref="RopFolderTableDecoders"/>, the standalone
/// [MS-OXCFOLD]/[MS-OXCTABL] semantic decoder module. This module is not wired into
/// <see cref="RopVariableDispatcher"/> or <see cref="RopSemanticParser"/>'s fixed catalog in this
/// change, so every test below calls <see cref="RopFolderTableDecoders.Supports"/> and
/// <see cref="RopFolderTableDecoders.Parse"/> directly against synthetic byte buffers - it never
/// goes through <see cref="RopBufferParser.Parse"/>.
/// </summary>
public sealed class RopFolderTableDecodersTests
{
    // ---------------------------------------------------------------------------------------------
    // Exact enumeration of supported (direction, ropId) pairs.
    // ---------------------------------------------------------------------------------------------

    private static readonly byte[] ExpectedRequestIds =
    [
        0x02, 0x1C, 0x1E, 0x30, 0x31, 0x33, 0x35, 0x36, 0x91, 0x92, // MSOXCFOLD (10)
        0x12, 0x13, 0x14, 0x15, 0x18, 0x19, 0x4F, 0x6C, 0x89, // MSOXCTABL (9)
    ];

    private static readonly byte[] ExpectedResponseIds =
    [
        0x02, 0x1C, 0x1D, 0x1E, 0x33, 0x35, 0x36, 0x58, 0x91, 0x92, 0x05, 0x04, 0x31, // MSOXCFOLD (13)
        0x38, 0x12, 0x13, 0x14, 0x18, 0x19, 0x1B, 0x37, 0x59, 0x4F, 0x15, 0x6B, 0x6C, 0x5A, // MSOXCTABL (14)
    ];

    [Fact]
    public void SupportsExactlyNineteenRequestRopIdsAndNoOthers()
    {
        Assert.Equal(19, ExpectedRequestIds.Length);
        Assert.Equal(ExpectedRequestIds.Distinct().Count(), ExpectedRequestIds.Length);
        for (var id = 0; id <= byte.MaxValue; id++)
        {
            var expected = ExpectedRequestIds.Contains((byte)id);
            Assert.True(
                expected == RopFolderTableDecoders.Supports(MapiDirection.Request, (byte)id),
                $"Request 0x{id:X2}: expected Supports()=={expected}");
        }
    }

    [Fact]
    public void SupportsExactlyTwentySevenResponseRopIdsAndNoOthers()
    {
        Assert.Equal(27, ExpectedResponseIds.Length);
        Assert.Equal(ExpectedResponseIds.Distinct().Count(), ExpectedResponseIds.Length);
        for (var id = 0; id <= byte.MaxValue; id++)
        {
            var expected = ExpectedResponseIds.Contains((byte)id);
            Assert.True(
                expected == RopFolderTableDecoders.Supports(MapiDirection.Response, (byte)id),
                $"Response 0x{id:X2}: expected Supports()=={expected}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Requests: every decoder exercised at least once, each immediately followed by a sentinel byte
    // to prove the operation boundary lands exactly (multi-operation-safe exact widths).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopOpenFolderRequest()
    {
        var bytes = Concat([0x02, 0x00, 0x01, 0x02], FolderId(0x0001, 1), [0x00]);
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal("0", Find(op.Children, "LogonId").Value);
        Assert.Equal("1", Find(op.Children, "InputHandleIndex").Value);
        Assert.Equal("2", Find(op.Children, "OutputHandleIndex").Value);
        Assert.Equal("0x00", Find(op.Children, "OpenModeFlags").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopCreateFolderRequestAsciiAndUnicodeVariants()
    {
        var ascii = Concat(
            [0x1C, 0x00, 0x01, 0x02, 0x01, 0x00, 0x00, 0x00],
            Encoding.ASCII.GetBytes("Inbox\0"), Encoding.ASCII.GetBytes("A comment\0"));
        var op = ParseOneWithSentinel(ascii, MapiDirection.Request, out var remainingAscii);
        Assert.Equal("Inbox", Find(op.Children, "DisplayName").Value);
        Assert.Equal("A comment", Find(op.Children, "Comment").Value);
        Assert.Equal([0xEE], remainingAscii);

        var unicode = Concat(
            [0x1C, 0x00, 0x01, 0x02, 0x01, 0x01, 0x00, 0x00],
            Encoding.Unicode.GetBytes("Sent Items\0"), Encoding.Unicode.GetBytes("\0"));
        var opUnicode = ParseOneWithSentinel(unicode, MapiDirection.Request, out var remainingUnicode);
        Assert.Equal("Sent Items", Find(opUnicode.Children, "DisplayName").Value);
        Assert.Equal(string.Empty, Find(opUnicode.Children, "Comment").Value);
        Assert.Equal([0xEE], remainingUnicode);
    }

    [Fact]
    public void ParsesRopDeleteMessagesAndHardDeleteMessagesRequestsWithMultipleIds()
    {
        foreach (byte ropId in new byte[] { 0x1E, 0x91 })
        {
            var bytes = Concat(
                [ropId, 0x00, 0x00, 0x01, 0x00],
                Le((ushort)2), FolderId(1, 1), FolderId(2, 2));
            var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
            Assert.Equal("true", Find(op.Children, "WantAsynchronous").Value);
            Assert.Equal("false", Find(op.Children, "NotifyNonRead").Value);
            var array = Find(op.Children, "MessageIds");
            Assert.Equal(2, array.Children.Length);
            Assert.Equal([0xEE], remaining);
        }
    }

    [Fact]
    public void ParsesRopHardDeleteMessagesAndSubfoldersRequest()
    {
        var bytes = new byte[] { 0x92, 0x00, 0x00, 0x01, 0x01 };
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal("true", Find(op.Children, "WantAsynchronous").Value);
        Assert.Equal("true", Find(op.Children, "WantDeleteAssociated").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopSetSearchCriteriaRequestWithAndWithoutRestriction()
    {
        var noRestriction = Concat(
            [0x30, 0x00, 0x00], Le((ushort)0), Le((ushort)1), FolderId(9, 9), Le((uint)0x00000001));
        var op = ParseOneWithSentinel(noRestriction, MapiDirection.Request, out var remaining);
        Assert.Equal("0", Find(op.Children, "RestrictionDataSize").Value);
        Assert.Single(Find(op.Children, "FolderIds").Children);
        Assert.Equal([0xEE], remaining);

        var exists = ExistsRestriction();
        var withRestriction = Concat(
            [0x30, 0x00, 0x00], Le((ushort)exists.Length), exists, Le((ushort)0), Le((uint)0));
        var op2 = ParseOneWithSentinel(withRestriction, MapiDirection.Request, out var remaining2);
        Assert.Equal("RestrictionData", Find(op2.Children, "RestrictionData").Name);
        Assert.Equal(MapiNodeKind.Structure, Find(op2.Children, "RestrictionData").Kind);
        Assert.Equal([0xEE], remaining2);
    }

    [Fact]
    public void ParsesRopGetSearchCriteriaRequest()
    {
        var bytes = new byte[] { 0x31, 0x00, 0x00, 0x01, 0x00, 0x01 };
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal("true", Find(op.Children, "UseUnicode").Value);
        Assert.Equal("false", Find(op.Children, "IncludeRestriction").Value);
        Assert.Equal("true", Find(op.Children, "IncludeFolders").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopMoveCopyMessagesRequest()
    {
        var bytes = Concat(
            [0x33, 0x00, 0x01, 0x02], Le((ushort)1), FolderId(3, 3), [0x00, 0x01]);
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal("1", Find(op.Children, "SourceHandleIndex").Value);
        Assert.Equal("2", Find(op.Children, "DestHandleIndex").Value);
        Assert.Equal("false", Find(op.Children, "WantAsynchronous").Value);
        Assert.Equal("true", Find(op.Children, "WantCopy").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopMoveFolderAndCopyFolderRequests()
    {
        var moveBytes = Concat(
            [0x35, 0x00, 0x01, 0x02, 0x00, 0x00], FolderId(4, 4), Encoding.ASCII.GetBytes("NewName\0"));
        var move = ParseOneWithSentinel(moveBytes, MapiDirection.Request, out var moveRemaining);
        Assert.Equal("NewName", Find(move.Children, "NewFolderName").Value);
        Assert.Equal([0xEE], moveRemaining);

        var copyBytes = Concat(
            [0x36, 0x00, 0x01, 0x02, 0x01, 0x01, 0x00], FolderId(5, 5), Encoding.ASCII.GetBytes("Copy\0"));
        var copy = ParseOneWithSentinel(copyBytes, MapiDirection.Request, out var copyRemaining);
        Assert.Equal("true", Find(copy.Children, "WantAsynchronous").Value);
        Assert.Equal("true", Find(copy.Children, "WantRecursive").Value);
        Assert.Equal("Copy", Find(copy.Children, "NewFolderName").Value);
        Assert.Equal([0xEE], copyRemaining);
    }

    [Fact]
    public void ParsesRopSetColumnsRequest()
    {
        var bytes = Concat([0x12, 0x00, 0x00, 0x00], Le((ushort)2), PropertyTag(0x001F, 0x3001), PropertyTag(0x0003, 0x6638));
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        var tags = Find(op.Children, "PropertyTags");
        Assert.Equal(2, tags.Children.Length);
        Assert.Contains("0x6638", Find(tags.Children[1].Children, "PropertyId").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopSortTableRequest()
    {
        var bytes = Concat(
            [0x13, 0x00, 0x00, 0x00],
            Le((ushort)1), Le((ushort)0), Le((ushort)0),
            SortOrder(0x0003, 0x6638, 0x00));
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        var orders = Find(op.Children, "SortOrders");
        Assert.Single(orders.Children);
        Assert.Contains("Ascending", Find(orders.Children[0].Children, "Order").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopRestrictRequestWithAndWithoutRestriction()
    {
        var empty = new byte[] { 0x14, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var op = ParseOneWithSentinel(empty, MapiDirection.Request, out var remaining);
        Assert.DoesNotContain(op.Children, c => c.Name == "RestrictionData");
        Assert.Equal([0xEE], remaining);

        var exists = ExistsRestriction();
        var withData = Concat([0x14, 0x00, 0x00, 0x00], Le((ushort)exists.Length), exists);
        var op2 = ParseOneWithSentinel(withData, MapiDirection.Request, out var remaining2);
        Assert.Equal(MapiNodeKind.Structure, Find(op2.Children, "RestrictionData").Kind);
        Assert.Equal([0xEE], remaining2);
    }

    [Fact]
    public void ParsesRopQueryRowsRequest()
    {
        var bytes = Concat([0x15, 0x00, 0x00, 0x00, 0x01], Le((ushort)25));
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal("true", Find(op.Children, "ForwardRead").Value);
        Assert.Equal("25", Find(op.Children, "RowCount").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopSeekRowRequest()
    {
        var bytes = Concat([0x18, 0x00, 0x00, 0x00], Le(-5), [0x01]);
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal("-5", Find(op.Children, "RowCount").Value);
        Assert.Equal("true", Find(op.Children, "WantRowMovedCount").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopSeekRowBookmarkRequest()
    {
        var bytes = Concat([0x19, 0x00, 0x00], Le((ushort)3), new byte[] { 1, 2, 3 }, Le(7), [0x00]);
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal("7", Find(op.Children, "RowCount").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopFindRowRequestWithAndWithoutRestriction()
    {
        var noRestriction = Concat([0x4F, 0x00, 0x00, 0x00], Le((ushort)0), [0x00], Le((ushort)0));
        var op = ParseOneWithSentinel(noRestriction, MapiDirection.Request, out var remaining);
        Assert.Equal("0x00", Find(op.Children, "Origin").Value);
        Assert.Equal([0xEE], remaining);

        var exists = ExistsRestriction();
        var withRestriction = Concat(
            [0x4F, 0x00, 0x00, 0x00], Le((ushort)exists.Length), exists, [0x01], Le((ushort)2), new byte[] { 9, 9 });
        var op2 = ParseOneWithSentinel(withRestriction, MapiDirection.Request, out var remaining2);
        Assert.Equal(MapiNodeKind.Structure, Find(op2.Children, "RestrictionData").Kind);
        Assert.Equal([0xEE], remaining2);
    }

    /// <summary>
    /// Regression for a real-capture width bug: an AndRestriction/OrRestriction's RestrictCount is
    /// 16-bit in an MS-OXCROPS ROP buffer ([MS-OXCDATA] 2.11.3/2.11.4), not the 32-bit NSPI/extended
    /// rule form. A capture previously showed a RopFindRow RestrictionCount of 0x04040003 - four
    /// bytes misread as one 32-bit count - when it was actually a 16-bit count followed by the next
    /// restriction's RestrictionType (0x04, PropertyRestriction) and RelOp (0x04). This builds a
    /// nested AndRestriction(OrRestriction(PropertyRestriction, ExistRestriction), ExistRestriction)
    /// fixture reproducing that same leading-byte shape (count then 0x04/0x04), decoded via
    /// RopFindRow, and proves the restriction's consumed length is exact by successfully decoding a
    /// second RopFindRow operation immediately afterward in the same buffer.
    /// </summary>
    [Fact]
    public void ParsesNestedAndOrRestrictionWithSixteenBitRopBufferCountAndProvesFollowingOperationBoundary()
    {
        var propertyRestriction = PropertyRestrictionLong(relOp: 0x04, propertyType: 0x0003, propertyId: 0x0FFF, value: 42);
        var innerExists = ExistsRestriction();
        var orRestriction = AndOrRestrictionRop(0x01, propertyRestriction, innerExists);
        var outerExists = ExistsRestriction();
        var andRestriction = AndOrRestrictionRop(0x00, orRestriction, outerExists);
        // The leading four bytes of andRestriction's OrRestriction child are exactly 02 00 04 04:
        // a 16-bit RestrictCount of 2 followed by PropertyRestriction(0x04)/RelOp(0x04). Misread as
        // one 32-bit count (the pre-fix bug), these same four bytes decode as 0x04040002 - the same
        // shape as the real-capture evidence's misread RestrictionCount 0x04040003.
        Assert.Equal(new byte[] { 0x01, 0x02, 0x00, 0x04, 0x04 }, orRestriction[..5]);

        var op1 = Concat(
            [0x4F, 0x00, 0x00, 0x00], Le((ushort)andRestriction.Length), andRestriction, [0x00], Le((ushort)0));
        var op2 = Concat([0x4F, 0x00, 0x00, 0x00], Le((ushort)0), [0x01], Le((ushort)0));
        var buffer = Concat(op1, op2);

        var reader = new MapiReader(buffer, CancellationToken.None);
        var node1 = RopFolderTableDecoders.Parse(ref reader, 0, MapiDirection.Request, [], new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);

        var restrictionData = Find(node1.Children, "RestrictionData");
        Assert.Equal("AndRestriction", Find(restrictionData.Children, "RestrictionType").Value);
        Assert.Equal("2", Find(restrictionData.Children, "RestrictionCount").Value);

        var restriction0 = Find(restrictionData.Children, "Restriction[0]");
        Assert.Equal("OrRestriction", Find(restriction0.Children, "RestrictionType").Value);
        Assert.Equal("2", Find(restriction0.Children, "RestrictionCount").Value);
        Assert.Equal("PropertyRestriction", Find(Find(restriction0.Children, "Restriction[0]").Children, "RestrictionType").Value);
        Assert.Equal("ExistRestriction", Find(Find(restriction0.Children, "Restriction[1]").Children, "RestrictionType").Value);

        var restriction1 = Find(restrictionData.Children, "Restriction[1]");
        Assert.Equal("ExistRestriction", Find(restriction1.Children, "RestrictionType").Value);

        var node2 = RopFolderTableDecoders.Parse(ref reader, 1, MapiDirection.Request, [], new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("0x01", Find(node2.Children, "Origin").Value);
        Assert.DoesNotContain(node2.Children, c => c.Name == "RestrictionData");
    }

    [Fact]
    public void ParsesRopSetCollapseStateAndFreeBookmarkRequests()
    {
        var collapse = Concat([0x6C, 0x00, 0x00], Le((ushort)4), new byte[] { 1, 2, 3, 4 });
        var op = ParseOneWithSentinel(collapse, MapiDirection.Request, out var remaining);
        Assert.Equal(MapiNodeKind.Raw, Find(op.Children, "CollapseState").Kind);
        Assert.Equal([0xEE], remaining);

        var freeBookmark = Concat([0x89, 0x00, 0x00], Le((ushort)2), new byte[] { 5, 6 });
        var op2 = ParseOneWithSentinel(freeBookmark, MapiDirection.Request, out var remaining2);
        Assert.Equal(MapiNodeKind.Raw, Find(op2.Children, "Bookmark").Kind);
        Assert.Equal([0xEE], remaining2);
    }

    // ---------------------------------------------------------------------------------------------
    // Responses: gated (success/failure) fields, always-present PartialCompletion, and the 3-way
    // NullDestinationObject gate.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopOpenFolderResponseFailureSuccessAndGhostedVariants()
    {
        var failure = Concat([0x02, 0x02], Le((uint)0x80000001));
        var op = ParseOneWithSentinel(failure, MapiDirection.Response, out var remaining);
        Assert.DoesNotContain(op.Children, c => c.Name == "HasRules");
        Assert.Equal([0xEE], remaining);

        var successNoGhost = Concat([0x02, 0x02], Le((uint)0), [0x01, 0x00]);
        var op2 = ParseOneWithSentinel(successNoGhost, MapiDirection.Response, out var remaining2);
        Assert.Equal("true", Find(op2.Children, "HasRules").Value);
        Assert.Equal("false", Find(op2.Children, "IsGhosted").Value);
        Assert.Equal([0xEE], remaining2);

        var ghosted = Concat(
            [0x02, 0x02], Le((uint)0), [0x00, 0x01], Le((ushort)1), Le((ushort)1), Encoding.ASCII.GetBytes("srv1\0"));
        var op3 = ParseOneWithSentinel(ghosted, MapiDirection.Response, out var remaining3);
        var servers = Find(op3.Children, "Servers");
        Assert.Single(servers.Children);
        Assert.Equal("srv1", servers.Children[0].Value);
        Assert.Equal([0xEE], remaining3);
    }

    [Fact]
    public void ParsesRopCreateFolderResponseAllThreeDepths()
    {
        var failure = Concat([0x1C, 0x02], Le((uint)0x80000001));
        var op = ParseOneWithSentinel(failure, MapiDirection.Response, out var remaining);
        Assert.DoesNotContain(op.Children, c => c.Name == "FolderId");
        Assert.Equal([0xEE], remaining);

        var newlyCreated = Concat([0x1C, 0x02], Le((uint)0), FolderId(1, 1), [0x00]);
        var op2 = ParseOneWithSentinel(newlyCreated, MapiDirection.Response, out var remaining2);
        Assert.Equal("false", Find(op2.Children, "IsExistingFolder").Value);
        Assert.DoesNotContain(op2.Children, c => c.Name == "HasRules");
        Assert.Equal([0xEE], remaining2);

        var existing = Concat([0x1C, 0x02], Le((uint)0), FolderId(1, 1), [0x01, 0x00, 0x00]);
        var op3 = ParseOneWithSentinel(existing, MapiDirection.Response, out var remaining3);
        Assert.Equal("true", Find(op3.Children, "IsExistingFolder").Value);
        Assert.Equal("false", Find(op3.Children, "IsGhosted").Value);
        Assert.Equal([0xEE], remaining3);
    }

    [Theory]
    [InlineData((byte)0x1D)] // RopDeleteFolder
    [InlineData((byte)0x1E)] // RopDeleteMessages
    [InlineData((byte)0x58)] // RopEmptyFolder
    [InlineData((byte)0x91)] // RopHardDeleteMessages
    [InlineData((byte)0x92)] // RopHardDeleteMessagesAndSubfolders
    public void ParsesAlwaysPartialCompletionResponsesOnBothSuccessAndFailure(byte ropId)
    {
        var success = Concat([ropId, 0x02], Le((uint)0), [0x01]);
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal("true", Find(op.Children, "PartialCompletion").Value);
        Assert.Equal([0xEE], remaining);

        var failure = Concat([ropId, 0x02], Le((uint)0x80000001), [0x00]);
        var op2 = ParseOneWithSentinel(failure, MapiDirection.Response, out var remaining2);
        Assert.Equal("false", Find(op2.Children, "PartialCompletion").Value);
        Assert.Equal([0xEE], remaining2);
    }

    [Theory]
    [InlineData((byte)0x33)] // RopMoveCopyMessages
    [InlineData((byte)0x35)] // RopMoveFolder
    [InlineData((byte)0x36)] // RopCopyFolder
    public void ParsesMoveCopyResponsesAcrossAllThreeGateBranches(byte ropId)
    {
        // Success: no DestHandleIndex, just PartialCompletion.
        var success = Concat([ropId, 0x01], Le((uint)0), [0x00]);
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.DoesNotContain(op.Children, c => c.Name == "DestHandleIndex");
        Assert.Equal("false", Find(op.Children, "PartialCompletion").Value);
        Assert.Equal([0xEE], remaining);

        // Ordinary failure: also no DestHandleIndex.
        var ordinaryFailure = Concat([ropId, 0x01], Le((uint)0x80000001), [0x01]);
        var op2 = ParseOneWithSentinel(ordinaryFailure, MapiDirection.Response, out var remaining2);
        Assert.DoesNotContain(op2.Children, c => c.Name == "DestHandleIndex");
        Assert.Equal("true", Find(op2.Children, "PartialCompletion").Value);
        Assert.Equal([0xEE], remaining2);

        // NullDestinationObject failure: DestHandleIndex (uint32) IS present.
        var nullDest = Concat([ropId, 0x01], Le((uint)0x00000503), Le((uint)0xAABBCCDD), [0x01]);
        var op3 = ParseOneWithSentinel(nullDest, MapiDirection.Response, out var remaining3);
        Assert.Equal("0xAABBCCDD", Find(op3.Children, "DestHandleIndex").Value);
        Assert.Equal("true", Find(op3.Children, "PartialCompletion").Value);
        Assert.Equal([0xEE], remaining3);
    }

    [Theory]
    [InlineData((byte)0x05)] // RopGetContentsTable
    [InlineData((byte)0x04)] // RopGetHierarchyTable
    public void ParsesGetContentsAndHierarchyTableResponses(byte ropId)
    {
        var success = Concat([ropId, 0x00], Le((uint)0), Le((uint)42));
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal("42", Find(op.Children, "RowCount").Value);
        Assert.Equal([0xEE], remaining);

        var failure = Concat([ropId, 0x00], Le((uint)0x80000001));
        var op2 = ParseOneWithSentinel(failure, MapiDirection.Response, out var remaining2);
        Assert.DoesNotContain(op2.Children, c => c.Name == "RowCount");
        Assert.Equal([0xEE], remaining2);
    }

    [Fact]
    public void ParsesRopGetSearchCriteriaResponse()
    {
        var bytes = Concat(
            [0x31, 0x00], Le((uint)0), Le((ushort)0), [0x07], Le((ushort)1), FolderId(1, 1), Le((uint)0x00000001));
        var op = ParseOneWithSentinel(bytes, MapiDirection.Response, out var remaining);
        Assert.Equal("7", Find(op.Children, "LogonId").Value);
        Assert.Single(Find(op.Children, "FolderIds").Children);
        Assert.Equal([0xEE], remaining);
    }

    [Theory]
    [InlineData((byte)0x38)] // RopAbort
    [InlineData((byte)0x12)] // RopSetColumns
    [InlineData((byte)0x13)] // RopSortTable
    [InlineData((byte)0x14)] // RopRestrict
    public void ParsesTableStatusGatedResponses(byte ropId)
    {
        var success = Concat([ropId, 0x00], Le((uint)0), [0x00]);
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal("0x00", Find(op.Children, "TableStatus").Value);
        Assert.Equal([0xEE], remaining);

        var failure = Concat([ropId, 0x00], Le((uint)0x80000001));
        var op2 = ParseOneWithSentinel(failure, MapiDirection.Response, out var remaining2);
        Assert.DoesNotContain(op2.Children, c => c.Name == "TableStatus");
        Assert.Equal([0xEE], remaining2);
    }

    [Fact]
    public void ParsesRopSeekRowResponse()
    {
        var success = Concat([0x18, 0x00], Le((uint)0), [0x01], Le(-3));
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal("true", Find(op.Children, "HasSoughtLess").Value);
        Assert.Equal("-3", Find(op.Children, "RowsSought").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopSeekRowBookmarkResponse()
    {
        var success = Concat([0x19, 0x00], Le((uint)0), [0x00, 0x01], Le((uint)9));
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal("false", Find(op.Children, "RowNoLongerVisible").Value);
        Assert.Equal("true", Find(op.Children, "HasSoughtLess").Value);
        Assert.Equal("9", Find(op.Children, "RowsSought").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopCreateBookmarkResponse()
    {
        var success = Concat([0x1B, 0x00], Le((uint)0), Le((ushort)3), new byte[] { 1, 2, 3 });
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal(MapiNodeKind.Raw, Find(op.Children, "Bookmark").Kind);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopQueryColumnsAllResponse()
    {
        var success = Concat([0x37, 0x00], Le((uint)0), Le((ushort)1), PropertyTag(0x0003, 0x0FFF));
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Single(Find(op.Children, "PropertyTags").Children);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopGetCollapseStateResponse()
    {
        var success = Concat([0x6B, 0x00], Le((uint)0), Le((ushort)2), new byte[] { 9, 9 });
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal(MapiNodeKind.Raw, Find(op.Children, "CollapseState").Kind);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void ParsesRopSetCollapseStateResponse()
    {
        var success = Concat([0x6C, 0x03], Le((uint)0), Le((ushort)4), new byte[] { 1, 2, 3, 4 });
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remainingSuccess);
        Assert.Equal("3", Find(op.Children, "InputHandleIndex").Value);
        Assert.Equal("4", Find(op.Children, "BookmarkSize").Value);
        Assert.Equal(MapiNodeKind.Raw, Find(op.Children, "Bookmark").Kind);
        Assert.Equal([0xEE], remainingSuccess);

        // On failure, per the pinned upstream reference, BookmarkSize/Bookmark are absent entirely.
        var failure = Concat([0x6C, 0x03], Le((uint)0x80040111));
        var failureOp = ParseOneWithSentinel(failure, MapiDirection.Response, out var remainingFailure);
        Assert.Empty(FindAll(failureOp.Children, "Bookmark"));
        Assert.Equal([0xEE], remainingFailure);
    }

    [Fact]
    public void ParsesRopCollapseRowResponse()
    {
        var success = Concat([0x5A, 0x00], Le((uint)0), Le((uint)77));
        var op = ParseOneWithSentinel(success, MapiDirection.Response, out var remaining);
        Assert.Equal("77", Find(op.Children, "CollapsedRowCount").Value);
        Assert.Equal([0xEE], remaining);
    }

    // ---------------------------------------------------------------------------------------------
    // Table rows decode only against a successfully committed RopSetColumns list for the same logical
    // connection and server object handle. Missing state retains the previous safe raw-fallback behavior.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RopQueryRowsResponseDecodesCleanlyWhenRowCountIsZeroButThrowsWhenRowsArePresent()
    {
        var clean = Concat([0x15, 0x00], Le((uint)0), [0x00], Le((ushort)0));
        var op = ParseOneWithSentinel(clean, MapiDirection.Response, out var remaining);
        Assert.Equal("0", Find(op.Children, "RowCount").Value);
        Assert.Equal([0xEE], remaining);

        var withRows = Concat([0x15, 0x00], Le((uint)0), [0x00], Le((ushort)3));
        var exception = CaptureParseException(withRows, MapiDirection.Response);
        Assert.Contains("RopQueryRows", exception.Message, StringComparison.Ordinal);

        // Failure responses never reach RowCount at all, so they always decode cleanly.
        var failure = Concat([0x15, 0x00], Le((uint)0x80000001));
        var opFailure = ParseOneWithSentinel(failure, MapiDirection.Response, out var remainingFailure);
        Assert.DoesNotContain(opFailure.Children, c => c.Name == "RowCount");
        Assert.Equal([0xEE], remainingFailure);
    }

    [Fact]
    public void RopFindRowResponseDecodesCleanlyWhenHasRowDataFalseButThrowsWhenTrue()
    {
        var clean = Concat([0x4F, 0x00], Le((uint)0), [0x00, 0x00]);
        var op = ParseOneWithSentinel(clean, MapiDirection.Response, out var remaining);
        Assert.Equal("false", Find(op.Children, "HasRowData").Value);
        Assert.Equal([0xEE], remaining);

        var withRow = Concat([0x4F, 0x00], Le((uint)0), [0x00, 0x01]);
        var exception = CaptureParseException(withRow, MapiDirection.Response);
        Assert.Contains("RopFindRow", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RopExpandRowResponseDecodesCleanlyWhenRowCountIsZeroButThrowsWhenRowsArePresent()
    {
        var clean = Concat([0x59, 0x00], Le((uint)0), Le((uint)0), Le((ushort)0));
        var op = ParseOneWithSentinel(clean, MapiDirection.Response, out var remaining);
        Assert.Equal("0", Find(op.Children, "ExpandedRowCount").Value);
        Assert.Equal([0xEE], remaining);

        var withRows = Concat([0x59, 0x00], Le((uint)0), Le((uint)5), Le((ushort)5));
        var exception = CaptureParseException(withRows, MapiDirection.Response);
        Assert.Contains("RopExpandRow", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodesQueryRowsAgainstColumnsCommittedBySuccessfulSetColumnsResponse()
    {
        var context = EstablishColumns(
            "set-columns",
            "mailbox-a",
            0xAABBCCDD,
            PropertyTag(0x0003, 0x3001),
            PropertyTag(0x001F, 0x3002));
        context.RegisterLogonCorrelationScope("query-rows", "mailbox-a");
        context.RecordSessionHandles("query-rows", [0xAABBCCDD]);

        var row1 = Concat([0x00], Le(42), Encoding.Unicode.GetBytes("Alpha\0"));
        var row2 = Concat([0x00], Le(84), Encoding.Unicode.GetBytes("Beta\0"));
        var response = Concat(
            [0x15, 0x00],
            Le((uint)0),
            [0x00],
            Le((ushort)2),
            row1,
            row2);

        var op = ParseOneWithSentinel(
            response,
            MapiDirection.Response,
            out var remaining,
            context,
            "query-rows");

        var rows = Find(op.Children, "RowData");
        Assert.Equal(2, rows.Children.Length);
        Assert.Contains("42", Flatten(rows.Children[0]).Select(node => node.Value));
        Assert.Contains("Alpha", Flatten(rows.Children[0]).Select(node => node.Value));
        Assert.Contains("84", Flatten(rows.Children[1]).Select(node => node.Value));
        Assert.Contains("Beta", Flatten(rows.Children[1]).Select(node => node.Value));
        Assert.Equal([0xEE], remaining);
    }

    [Theory]
    [InlineData((byte)0x4F)]
    [InlineData((byte)0x59)]
    public void DecodesFindAndExpandRowsAgainstCommittedColumns(byte ropId)
    {
        var context = EstablishColumns(
            "set-columns",
            "mailbox-a",
            0x01020304,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("rows", "mailbox-a");
        context.RecordSessionHandles("rows", [0x01020304]);
        var row = Concat([0x00], Le(123));
        var response = ropId == 0x4F
            ? Concat([ropId, 0x00], Le((uint)0), [0x00, 0x01], row)
            : Concat([ropId, 0x00], Le((uint)0), Le((uint)1), Le((ushort)1), row);

        var op = ParseOneWithSentinel(response, MapiDirection.Response, out var remaining, context, "rows");

        Assert.Contains("123", Flatten(Find(op.Children, "RowData")).Select(node => node.Value));
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void FailedSetColumnsInvalidatesThePreviouslyCommittedColumnList()
    {
        var context = EstablishColumns(
            "first-set",
            "mailbox-a",
            0x11111111,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("failed-set", "mailbox-a");
        context.RecordSessionHandles("failed-set", [0x11111111]);
        var request = Concat(
            [0x12, 0x00, 0x00, 0x00],
            Le((ushort)1),
            PropertyTag(0x0002, 0x3001));
        ParseOneWithSentinel(request, MapiDirection.Request, out _, context, "failed-set");
        var failure = Concat([0x12, 0x00], Le((uint)0x80000001));
        ParseOneWithSentinel(failure, MapiDirection.Response, out _, context, "failed-set");

        context.RegisterLogonCorrelationScope("query", "mailbox-a");
        context.RecordSessionHandles("query", [0x11111111]);
        var row = Concat([0x15, 0x00], Le((uint)0), [0x00], Le((ushort)1), [0x00], Le(123456));
        var exception = CaptureParseException(row, MapiDirection.Response, context, "query");

        Assert.Contains("no unambiguous active column list", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TableColumnsNeverLeakAcrossLogicalConnectionsThatReuseAHandleValue()
    {
        var context = EstablishColumns(
            "set-columns",
            "mailbox-a",
            0x22222222,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("other-mailbox", "mailbox-b");
        context.RecordSessionHandles("other-mailbox", [0x22222222]);
        var response = Concat(
            [0x15, 0x00],
            Le((uint)0),
            [0x00],
            Le((ushort)1),
            [0x00],
            Le(7));

        var exception = CaptureParseException(
            response,
            MapiDirection.Response,
            context,
            "other-mailbox");

        Assert.Contains("no unambiguous active column list", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RopReleaseInvalidatesColumnsForTheReleasedServerHandle()
    {
        var context = EstablishColumns(
            "set-columns",
            "mailbox-a",
            0x33333333,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("release", "mailbox-a");
        context.RecordSessionHandles("release", [0x33333333]);
        Assert.True(context.TryGetTableColumns("release", 0, out _));

        var operations = RopSemanticParser.ParseOperations(
            [0x01, 0x00, 0x00],
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            [],
            CancellationToken.None,
            captureScope: "release",
            context: context);

        Assert.Single(operations);
        Assert.False(context.TryGetTableColumns("release", 0, out _));
    }

    [Fact]
    public void SuccessfulRopResetTableInvalidatesColumns()
    {
        var context = EstablishColumns(
            "set-columns",
            "mailbox-a",
            0x44444444,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("reset", "mailbox-a");
        context.RecordSessionHandles("reset", [0x44444444]);
        Assert.True(context.TryGetTableColumns("reset", 0, out _));

        var requestOperations = RopSemanticParser.ParseOperations(
            [0x81, 0x00, 0x00],
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            [],
            CancellationToken.None,
            captureScope: "reset",
            context: context);
        context.RecordSessionHandles("reset", [uint.MaxValue]);
        var responseOperations = RopSemanticParser.ParseOperations(
            Concat([0x81, 0x00], Le((uint)0)),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            [],
            CancellationToken.None,
            captureScope: "reset",
            context: context);

        context.RegisterLogonCorrelationScope("query-after-reset", "mailbox-a");
        context.RecordSessionHandles("query-after-reset", [0x44444444]);
        Assert.Single(requestOperations);
        Assert.Single(responseOperations);
        Assert.False(context.TryGetTableColumns("query-after-reset", 0, out _));
    }

    [Fact]
    public void SetColumnsResolvesRequestPlaceholderFromTheResponseHandleTable()
    {
        const string scope = "set-columns";
        const uint serverHandle = 0x55555555;
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope(scope, "mailbox-a");
        context.RecordSessionHandles(scope, [uint.MaxValue]);
        var request = Concat(
            [0x12, 0x00, 0x00, 0x00],
            Le((ushort)1),
            PropertyTag(0x0003, 0x3001));
        ParseOneWithSentinel(request, MapiDirection.Request, out _, context, scope);

        context.RecordSessionHandles(scope, [serverHandle]);
        var response = Concat([0x12, 0x00], Le((uint)0), [0x00]);
        ParseOneWithSentinel(response, MapiDirection.Response, out _, context, scope);

        Assert.True(context.TryGetTableColumns(scope, 0, out var columns));
        Assert.Equal([(Type: (ushort)0x0003, Id: (ushort)0x3001)], columns);
    }

    [Fact]
    public void MissingSetColumnsResponseInvalidatesTheOldColumnListAtSessionEnd()
    {
        var context = EstablishColumns(
            "original",
            "mailbox-a",
            0x66666666,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("missing-response", "mailbox-a");
        context.RecordSessionHandles("missing-response", [0x66666666]);
        var replacementRequest = Concat(
            [0x12, 0x00, 0x00, 0x00],
            Le((ushort)1),
            PropertyTag(0x0002, 0x3001));
        ParseOneWithSentinel(
            replacementRequest,
            MapiDirection.Request,
            out _,
            context,
            "missing-response");

        context.CompleteHttpSession("missing-response");
        context.RegisterLogonCorrelationScope("query", "mailbox-a");
        context.RecordSessionHandles("query", [0x66666666]);

        Assert.False(context.TryGetTableColumns("query", 0, out _));
    }

    [Fact]
    public void SuccessfulOutputHandleAssignmentInvalidatesColumnsForAReusedHandle()
    {
        var context = EstablishColumns(
            "original",
            "mailbox-a",
            0x77777777,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("reuse", "mailbox-a");
        context.RecordSessionHandles("reuse", [0x77777777]);
        Assert.True(context.TryGetTableColumns("reuse", 0, out _));

        var operations = RopSemanticParser.ParseOperations(
            Concat([0x21, 0x00], Le((uint)0)),
            0,
            MapiDirection.Response,
            [],
            new MapiNodeBudget(),
            [],
            CancellationToken.None,
            captureScope: "reuse",
            context: context);

        Assert.Single(operations);
        Assert.False(context.TryGetTableColumns("reuse", 0, out _));
    }

    [Fact]
    public void MalformedHandleTableClearsTheCurrentSessionsHandleResolution()
    {
        var context = EstablishColumns(
            "original",
            "mailbox-a",
            0x88888888,
            PropertyTag(0x0003, 0x3001));
        context.RegisterLogonCorrelationScope("malformed", "mailbox-a");
        context.RecordSessionHandles("malformed", [0x88888888]);
        Assert.True(context.TryGetTableColumns("malformed", 0, out _));
        var warnings = new List<string>();

        var nodes = RopBufferParser.Parse(
            [0x05, 0x00, 0x16, 0x00, 0x00, 0xEE],
            0,
            MapiDirection.Request,
            warnings,
            new MapiNodeBudget(),
            CancellationToken.None,
            captureScope: "malformed",
            context: context);

        Assert.False(context.TryGetTableColumns("malformed", 0, out _));
        Assert.Contains(warnings, warning => warning.Contains("not divisible by four", StringComparison.Ordinal));
        Assert.Equal("RopGetStatus", Find(nodes, "ROP list").Children[0].Value!.Split(' ')[0]);
        Assert.Contains(nodes, node => node.Name == "Malformed server object handle table");
    }

    // ---------------------------------------------------------------------------------------------
    // Malformed input hardening: truncated counts/lengths must throw MapiParseException, never
    // silently guess or overrun.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ThrowsOnTruncatedPropertyTagCountInSetColumnsRequest()
    {
        // Declares 5 PropertyTags but supplies none.
        var bytes = Concat([0x12, 0x00, 0x00, 0x00], Le((ushort)5));
        AssertThrowsMapiParseException(bytes, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsOnOversizedRestrictionDataSizeInRestrictRequest()
    {
        // Declares a 100-byte restriction but supplies none of it.
        var bytes = Concat([0x14, 0x00, 0x00, 0x00], Le((ushort)100));
        AssertThrowsMapiParseException(bytes, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsOnOversizedBookmarkSizeInFreeBookmarkRequest()
    {
        var bytes = Concat([0x89, 0x00, 0x00], Le((ushort)50), new byte[] { 1, 2 });
        AssertThrowsMapiParseException(bytes, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsOnTruncatedMessageIdCountInDeleteMessagesRequest()
    {
        var bytes = Concat([0x1E, 0x00, 0x00, 0x00, 0x00], Le((ushort)10), FolderId(1, 1));
        AssertThrowsMapiParseException(bytes, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsOnTruncatedFolderIdCountInSetSearchCriteriaRequest()
    {
        var bytes = Concat([0x30, 0x00, 0x00], Le((ushort)0), Le((ushort)9));
        AssertThrowsMapiParseException(bytes, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsOnMissingNullTerminatorInCreateFolderRequestDisplayName()
    {
        var bytes = Concat([0x1C, 0x00, 0x01, 0x02, 0x01, 0x00, 0x00, 0x00], Encoding.ASCII.GetBytes("NoTerminator"));
        AssertThrowsMapiParseException(bytes, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsOnTruncatedSortOrderCountInSortTableRequest()
    {
        var bytes = Concat([0x13, 0x00, 0x00, 0x00], Le((ushort)3), Le((ushort)0), Le((ushort)0));
        AssertThrowsMapiParseException(bytes, MapiDirection.Request);
    }

    // ---------------------------------------------------------------------------------------------
    // Injection text: control characters / escape-like sequences embedded in strings must be stored
    // as-is (bounded by MapiParseLimits.MaxStringBytes, already enforced by MapiReader) rather than
    // crashing or being silently altered.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PreservesControlCharacterAndInjectionLikeTextInDisplayNameAndCommentVerbatim()
    {
        const string injection = "<script>alert(1)</script>\t\u0007\u001b[31mRED\u001b[0m\";DROP TABLE x;--";
        var bytes = Concat(
            [0x1C, 0x00, 0x01, 0x02, 0x01, 0x00, 0x00, 0x00],
            Encoding.ASCII.GetBytes(injection), [0x00],
            Encoding.ASCII.GetBytes("normal\0"));
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal(injection, Find(op.Children, "DisplayName").Value);
        Assert.Equal("normal", Find(op.Children, "Comment").Value);
        Assert.Equal([0xEE], remaining);
    }

    [Fact]
    public void PreservesInjectionLikeTextInMoveFolderNewFolderNameVerbatim()
    {
        const string injection = "..\\..\\etc\\passwd%00' OR '1'='1";
        var bytes = Concat(
            [0x35, 0x00, 0x01, 0x02, 0x00, 0x00], FolderId(1, 1), Encoding.ASCII.GetBytes(injection), [0x00]);
        var op = ParseOneWithSentinel(bytes, MapiDirection.Request, out var remaining);
        Assert.Equal(injection, Find(op.Children, "NewFolderName").Value);
        Assert.Equal([0xEE], remaining);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses a single operation from <paramref name="bytes"/> followed by a single 0xEE sentinel
    /// byte, and asserts the decoder consumed exactly <paramref name="bytes"/>.Length bytes (proving
    /// the operation boundary is exact and multi-operation-safe), leaving only the sentinel behind.
    /// </summary>
    private static MapiNode ParseOneWithSentinel(
        byte[] bytes,
        MapiDirection direction,
        out byte[] remaining,
        MapiCaptureContext? context = null,
        string? captureScope = null)
    {
        var framed = Concat(bytes, [0xEE]);
        var reader = new MapiReader(framed, CancellationToken.None);
        var node = RopFolderTableDecoders.Parse(
            ref reader,
            0,
            direction,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context,
            captureScope);
        Assert.Equal(bytes.Length, node.Length);
        Assert.Equal(bytes.Length, reader.Position);
        remaining = reader.ReadRemaining("sentinel").ToArray();
        return node;
    }

    private static void AssertThrowsMapiParseException(byte[] bytes, MapiDirection direction) =>
        CaptureParseException(bytes, direction);

    /// <summary>
    /// Runs <see cref="RopFolderTableDecoders.Parse"/> and captures the resulting
    /// <see cref="MapiParseException"/>, failing the test if none is thrown. Written as an explicit
    /// try/catch (rather than <c>Assert.Throws</c> with a lambda) because <see cref="MapiReader"/> is
    /// a ref struct and cannot be captured by a delegate.
    /// </summary>
    private static MapiParseException CaptureParseException(
        byte[] bytes,
        MapiDirection direction,
        MapiCaptureContext? context = null,
        string? captureScope = null)
    {
        var reader = new MapiReader(bytes, CancellationToken.None);
        try
        {
            RopFolderTableDecoders.Parse(
                ref reader,
                0,
                direction,
                [],
                new MapiNodeBudget(),
                CancellationToken.None,
                context,
                captureScope);
        }
        catch (MapiParseException ex)
        {
            return ex;
        }
        Assert.Fail("Expected a MapiParseException to be thrown.");
        return null!; // Unreachable; Assert.Fail always throws.
    }

    private static MapiCaptureContext EstablishColumns(
        string captureScope,
        string connectionScope,
        uint serverHandle,
        params byte[][] columns)
    {
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope(captureScope, connectionScope);
        context.RecordSessionHandles(captureScope, [serverHandle]);
        var request = Concat(
            [0x12, 0x00, 0x00, 0x00],
            Le((ushort)columns.Length),
            Concat(columns));
        ParseOneWithSentinel(request, MapiDirection.Request, out _, context, captureScope);
        var response = Concat([0x12, 0x00], Le((uint)0), [0x00]);
        ParseOneWithSentinel(response, MapiDirection.Response, out _, context, captureScope);
        return context;
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

    /// <summary>MS-OXCDATA 2.12.3.4 Exist restriction: RestrictionType(0x08) + PropertyTag(4 bytes).</summary>
    private static byte[] ExistsRestriction() => Concat([0x08], PropertyTag(0x0003, 0x0FFF));

    /// <summary>
    /// MS-OXCDATA 2.12.3.1/2.12.3.2 And/OrRestriction with an explicit 16-bit RestrictCount (the
    /// ROP-buffer wire form): RestrictionType(1) + RestrictCount(2) + the concatenated child
    /// restriction bytes.
    /// </summary>
    private static byte[] AndOrRestrictionRop(byte restrictionType, params byte[][] children) =>
        Concat([restrictionType], Le((ushort)children.Length), Concat(children));

    /// <summary>
    /// MS-OXCDATA 2.12.3.3 PropertyRestriction carrying a PT_LONG value: RestrictionType(0x04) +
    /// RelOp(1) + PropertyTag(4) + TaggedValue(PropertyType(2)+PropertyId(2)+Value(4)).
    /// </summary>
    private static byte[] PropertyRestrictionLong(byte relOp, ushort propertyType, ushort propertyId, int value) =>
        Concat([0x04, relOp], PropertyTag(propertyType, propertyId), Le(propertyType), Le(propertyId), Le(value));

    private static byte[] FolderId(ushort replicaId, byte counterSeed)
    {
        var counter = new byte[6];
        for (var i = 0; i < counter.Length; i++)
        {
            counter[i] = (byte)(counterSeed + i);
        }
        return Concat(Le(replicaId), counter);
    }

    private static byte[] PropertyTag(ushort propertyType, ushort propertyId) => Concat(Le(propertyType), Le(propertyId));

    private static byte[] SortOrder(ushort propertyType, ushort propertyId, byte order) =>
        Concat(Le(propertyType), Le(propertyId), [order]);

    private static byte[] Le(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
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
            foreach (var child in FindAll(node.Children, name))
            {
                yield return child;
            }
        }
    }
}
