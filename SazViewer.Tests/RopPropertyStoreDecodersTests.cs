using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

/// <summary>
/// Exhaustive synthetic coverage for <see cref="RopPropertyStoreDecoders"/> - the standalone,
/// not-yet-wired semantic decoder module for the [MS-OXCPRPT], [MS-OXCSTOR], and core
/// [MS-OXCROPS] request/response ROP families. Every RopId reported by
/// <see cref="RopPropertyStoreDecoders.Supports"/> is exercised directly against
/// <see cref="RopPropertyStoreDecoders.Parse"/> (bypassing <c>RopBufferParser</c>/<c>RopSemanticParser</c>
/// entirely, since this module is intentionally not wired into either) with an exact-boundary
/// success case, plus targeted malformed/truncated/counted-mismatch/ReturnValue-gated/injection-safety
/// cases for the trickiest layouts.
/// </summary>
public sealed class RopPropertyStoreDecodersTests
{
    // ---------------------------------------------------------------------------------------------
    // Supports() enumeration - the authoritative list of every (direction, RopId) pair this module
    // claims, and confirmation that the four documented gaps remain unsupported.
    // ---------------------------------------------------------------------------------------------

    private static readonly byte[] ExpectedRequestRopIds =
    [
        0x07, 0x08, 0x0A, 0x0B, 0x26, 0x27, 0x2B, 0x2C, 0x2D, 0x34, 0x39, 0x3A, 0x51, 0x55, 0x56,
        0x5B, 0x5C, 0x5F, 0x63, 0x67, 0x6F, 0x79, 0x7A, 0x90, 0xA3, 0xFE
    ];

    private static readonly byte[] ExpectedResponseRopIds =
    [
        0x08, 0x09, 0x0A, 0x0B, 0x27, 0x2B, 0x2C, 0x2D, 0x39, 0x3A, 0x42, 0x45, 0x49, 0x4A, 0x50,
        0x55, 0x56, 0x5F, 0x60, 0x63, 0x67, 0x68, 0x6F, 0x79, 0x7A, 0x90, 0xA3, 0xF9
    ];

    [Fact]
    public void SupportsReportsExactlyTwentySixRequestRopIds()
    {
        Assert.Equal(26, ExpectedRequestRopIds.Length);
        foreach (var ropId in ExpectedRequestRopIds)
        {
            Assert.True(RopPropertyStoreDecoders.Supports(MapiDirection.Request, ropId), $"Expected request 0x{ropId:X2} to be supported.");
        }
        var actual = Enumerable.Range(0, 256).Select(v => (byte)v).Where(v => RopPropertyStoreDecoders.Supports(MapiDirection.Request, v)).ToArray();
        Assert.Equal(ExpectedRequestRopIds.OrderBy(v => v), actual);
    }

    [Fact]
    public void SupportsReportsExactlyTwentyEightResponseRopIds()
    {
        Assert.Equal(28, ExpectedResponseRopIds.Length);
        foreach (var ropId in ExpectedResponseRopIds)
        {
            Assert.True(RopPropertyStoreDecoders.Supports(MapiDirection.Response, ropId), $"Expected response 0x{ropId:X2} to be supported.");
        }
        var actual = Enumerable.Range(0, 256).Select(v => (byte)v).Where(v => RopPropertyStoreDecoders.Supports(MapiDirection.Response, v)).ToArray();
        Assert.Equal(ExpectedResponseRopIds.OrderBy(v => v), actual);
    }

    [Theory]
    [InlineData(0x64)] // RopWritePerUserInformation: trailing ReplGuid presence depends on the originating RopLogon's LogonFlags.
    public void RequestGapsRemainUnsupported(byte ropId) =>
        Assert.False(RopPropertyStoreDecoders.Supports(MapiDirection.Request, ropId));

    [Theory]
    [InlineData(0x07)] // RopGetPropertiesSpecific response: property tags are implied by the paired request, not visible in-band.
    [InlineData(0xFE)] // RopLogon response: private-mailbox vs. public-folder shape depends on the request's LogonFlags.
    [InlineData(0xFF)] // RopBufferTooSmall response: RequestBuffersSize depends on cross-message session state.
    public void ResponseGapsRemainUnsupported(byte ropId) =>
        Assert.False(RopPropertyStoreDecoders.Supports(MapiDirection.Response, ropId));

    [Fact]
    public void UnrelatedRopIdIsNotSupportedInEitherDirection()
    {
        Assert.False(RopPropertyStoreDecoders.Supports(MapiDirection.Request, 0x01)); // RopRelease
        Assert.False(RopPropertyStoreDecoders.Supports(MapiDirection.Response, 0x01));
    }

    [Fact]
    public void ParseThrowsForUnsupportedRequestRopId()
    {
        var bytes = new byte[] { 0x64, 0x00, 0x00 };
        AssertThrowsParse(bytes, MapiDirection.Request);
    }

    [Fact]
    public void ParseHonorsCancellation()
    {
        var bytes = new byte[] { 0x08, 0x00, 0x00, 0x00, 0x00 };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new MapiReader(bytes, cts.Token);
        var threw = false;
        try
        {
            RopPropertyStoreDecoders.Parse(ref reader, 0, MapiDirection.Request, [], new MapiNodeBudget(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected OperationCanceledException.");
    }

    // ---------------------------------------------------------------------------------------------
    // MSOXCPRPT requests
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RequestGetPropertiesSpecificParsesExactBoundary()
    {
        var bytes = Concat(
            [0x07, 0x00, 0x00],
            Le((ushort)0xFFFF),
            Le((ushort)1),
            Le((ushort)2),
            Tag(0x0003, 0x0E08),
            Tag(0x001F, 0x0037));

        var (node, handles) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(handles);
        Assert.Equal("true", Find(node.Children, "WantUnicode").Value);
        var tags = Find(node.Children, "PropertyTags");
        Assert.Equal(2, tags.Children.Length);
    }

    [Fact]
    public void RequestGetPropertiesSpecificThrowsWhenTagsTruncated()
    {
        var bytes = Concat(
            [0x07, 0x00, 0x00], Le((ushort)0xFFFF), Le((ushort)0), Le((ushort)2), Tag(0x0003, 0x0E08)); // declares 2 tags, only 1 present
        AssertThrowsParse(bytes, MapiDirection.Request);
    }

    [Fact]
    public void RequestGetPropertiesAllParsesExactBoundary()
    {
        var bytes = Concat([0x08, 0x00, 0x00], Le((ushort)0x1000), Le((ushort)0));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("false", Find(node.Children, "WantUnicode").Value);
    }

    [Fact]
    public void RequestSetPropertiesAppliesCapAndReportsJunkWhenOversized()
    {
        var value = TaggedInt32(0x3007, 42);
        var bytes = Concat([0x0A, 0x00, 0x00], Le((ushort)(2 + value.Length + 4)), Le((ushort)1), value, [0xAA, 0xBB, 0xCC, 0xDD]);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        var junk = Find(node.Children, "Junk");
        Assert.Contains("AABBCCDD", junk.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestSetPropertiesExactBoundaryNoJunk()
    {
        var value = TaggedInt32(0x3007, 42);
        var bytes = Concat([0x0A, 0x00, 0x00], Le((ushort)(2 + value.Length)), Le((ushort)1), value);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Empty(node.Children.Where(c => c.Name == "Junk"));
    }

    [Fact]
    public void RequestSetPropertiesHostileCountStopsEarlyWithoutThrowingWhenCapEmpty()
    {
        // PropertyValueSize implies a zero-length window, but PropertyValueCount hostilely claims 5 entries.
        var bytes = Concat([0x0A, 0x00, 0x00], Le((ushort)0), Le((ushort)5));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("0 entrie(s)", Find(node.Children, "PropertyValues").Value);
    }

    [Fact]
    public void RequestSetPropertiesNoReplicateIsUncappedAndThrowsWhenTruncated()
    {
        // No PushCap here: PropertyValueCount is trusted directly against the main reader, so a
        // hostile/truncated count must surface as a real MapiParseException, not a silent stop.
        var bytes = Concat([0x79, 0x00, 0x00], Le((ushort)999), Le((ushort)3), TaggedInt32(1, 1));
        AssertThrowsParse(bytes, MapiDirection.Request);
    }

    [Fact]
    public void RequestSetPropertiesNoReplicateParsesExactBoundary()
    {
        var v1 = TaggedInt32(1, 1);
        var v2 = TaggedInt32(2, 2);
        var bytes = Concat([0x79, 0x00, 0x00], Le((ushort)999), Le((ushort)2), v1, v2);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(2, Find(node.Children, "PropertyValues").Children.Length);
    }

    [Fact]
    public void RequestDeletePropertiesParsesExactBoundary()
    {
        var bytes = Concat([0x0B, 0x00, 0x00], Le((ushort)1), Tag(0x0003, 1));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "PropertyTags").Children);
    }

    [Fact]
    public void RequestOpenStreamParsesExactBoundary()
    {
        var bytes = Concat([0x2B, 0x00, 0x00, 0x01], Tag(0x0102, 0x6700), [0x02]);
        var (node, handles) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(2, handles.Count);
        Assert.Equal("0x02", Find(node.Children, "OpenModeFlags").Value);
    }

    [Theory]
    [InlineData((ushort)0x1000, false)]
    [InlineData((ushort)0xBABE, true)]
    public void RequestReadStreamGatesMaximumByteCountOnSentinel(ushort byteCount, bool expectMaximum)
    {
        var bytes = expectMaximum
            ? Concat([0x2C, 0x00, 0x00], Le(byteCount), Le((uint)65536))
            : Concat([0x2C, 0x00, 0x00], Le(byteCount));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(expectMaximum, node.Children.Any(c => c.Name == "MaximumByteCount"));
    }

    [Fact]
    public void RequestWriteStreamParsesExactBoundary()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var bytes = Concat([0x2D, 0x00, 0x00], Le((ushort)data.Length), data);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Contains("01020304", Find(node.Children, "Data").Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequestWriteStreamThrowsWhenDataTruncated()
    {
        var bytes = Concat([0x2D, 0x00, 0x00], Le((ushort)10), new byte[] { 1, 2 });
        AssertThrowsParse(bytes, MapiDirection.Request);
    }

    [Fact]
    public void RequestCopyToParsesExactBoundary()
    {
        var bytes = Concat([0x39, 0x00, 0x01, 0x02], [0x01, 0x00, 0x03], Le((ushort)1), Tag(0x0003, 1));
        var (node, handles) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(2, handles.Count);
        Assert.Equal("true", Find(node.Children, "WantAsynchronous").Value);
        Assert.Equal("false", Find(node.Children, "WantSubObjects").Value);
        Assert.Equal("0x03", Find(node.Children, "CopyFlags").Value);
    }

    [Fact]
    public void RequestCopyToStreamParsesExactBoundary()
    {
        var bytes = Concat([0x3A, 0x00, 0x01, 0x02], Le((ulong)4096));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("4096", Find(node.Children, "ByteCount").Value);
    }

    [Fact]
    public void RequestGetNamesFromPropertyIdsParsesExactBoundary()
    {
        var bytes = Concat([0x55, 0x00, 0x00], Le((ushort)2), Le((ushort)0x3001), Le((ushort)0x3002));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(2, Find(node.Children, "PropertyIds").Children.Length);
    }

    [Fact]
    public void RequestGetPropertyIdsFromNamesParsesLidAndNamedAndNoneVariants()
    {
        var guid = Guid.NewGuid();
        var lid = PropertyNameLid(guid, 0x8001);
        var named = PropertyNameNamed(guid, "X");
        var none = PropertyNameNone();
        var bytes = Concat([0x56, 0x00, 0x00, 0x00], Le((ushort)3), lid, named, none);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(3, Find(node.Children, "PropertyNames").Children.Length);
    }

    [Fact]
    public void RequestGetPropertyIdsFromNamesThrowsOnUnknownPropertyNameKind()
    {
        var bytes = Concat([0x56, 0x00, 0x00, 0x00], Le((ushort)1), [0x02]); // Kind 0x02 is genuinely undeterminable.
        AssertThrowsParse(bytes, MapiDirection.Request);
    }

    [Fact]
    public void RequestGetPropertyIdsFromNamesHandlesOddNameSizePadding()
    {
        var guid = Guid.NewGuid();
        // NameSize = 5 (odd): 4 bytes of UTF-16LE content + 1 raw pad byte, totalling exactly 5.
        var named = Concat([0x01], guid.ToByteArray(), [5], [0x41, 0x00, 0x42, 0x00, 0xAA]);
        var bytes = Concat([0x56, 0x00, 0x00, 0x00], Le((ushort)1), named);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
    }

    [Theory]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void RequestLockUnlockRegionStreamParsesExactBoundary(byte ropId)
    {
        var bytes = Concat([ropId, 0x00, 0x00], Le((ulong)10), Le((ulong)20), Le((uint)0x00000001));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("10", Find(node.Children, "RegionOffset").Value);
        Assert.Equal("20", Find(node.Children, "RegionSize").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestQueryNamedPropertiesGatesGuidOnHasGuid(bool hasGuid)
    {
        var bytes = hasGuid
            ? Concat([0x5F, 0x00, 0x00, 0x02, 0x01], Guid.NewGuid().ToByteArray())
            : Concat([0x5F, 0x00, 0x00, 0x02, 0x00]);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(hasGuid, node.Children.Any(c => c.Name == "PropertyGuid"));
    }

    [Fact]
    public void RequestReadPerUserInformationParsesExactBoundary()
    {
        var longTermId = LongTermId(Guid.NewGuid(), new byte[] { 1, 2, 3, 4, 5, 6 });
        var bytes = Concat([0x63, 0x00, 0x00], longTermId, [0x00], Le((uint)0), Le((ushort)4096));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("4096", Find(node.Children, "MaxDataSize").Value);
    }

    [Fact]
    public void RequestReadPerUserInformationThrowsWhenLongTermIdTruncated()
    {
        var bytes = Concat([0x63, 0x00, 0x00], new byte[10]); // LongTermId needs 24 bytes.
        AssertThrowsParse(bytes, MapiDirection.Request);
    }

    [Fact]
    public void RequestCopyPropertiesParsesExactBoundary()
    {
        var bytes = Concat([0x67, 0x00, 0x01, 0x02, 0x01, 0x04], Le((ushort)0));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("0x04", Find(node.Children, "CopyFlags").Value);
    }

    [Theory]
    [InlineData(0x26)]
    public void RequestSetReceiveFolderParsesExactBoundary(byte ropId)
    {
        var folderId = Fid(1, new byte[] { 1, 2, 3, 4, 5, 6 });
        var bytes = Concat([ropId, 0x00, 0x00], folderId, Ascii("IPM.Note"));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("IPM.Note", Find(node.Children, "MessageClass").Value);
    }

    [Fact]
    public void RequestGetReceiveFolderParsesExactBoundary()
    {
        var bytes = Concat([0x27, 0x00, 0x00], Ascii("IPM"));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("IPM", Find(node.Children, "MessageClass").Value);
    }

    [Fact]
    public void RequestGetReceiveFolderThrowsWhenMessageClassMissingTerminator()
    {
        var bytes = Concat([0x27, 0x00, 0x00], Encoding.ASCII.GetBytes("IPM")); // no trailing 0x00
        AssertThrowsParse(bytes, MapiDirection.Request);
    }

    [Fact]
    public void RequestTransportNewMailParsesExactBoundary()
    {
        var messageId = Fid(1, new byte[6]);
        var folderId = Fid(2, new byte[6]);
        var bytes = Concat([0x51, 0x00, 0x00], messageId, folderId, Ascii("IPM.Note"), Le((uint)0x00000001));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("0x00000001", Find(node.Children, "MessageFlags").Value);
    }

    [Fact]
    public void RequestAbortSubmitParsesExactBoundary()
    {
        var folderId = Fid(1, new byte[6]);
        var messageId = Fid(2, new byte[6]);
        var bytes = Concat([0x34, 0x00, 0x00], folderId, messageId);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
    }

    [Fact]
    public void RequestOptionsDataParsesExactBoundary()
    {
        var bytes = Concat([0x6F, 0x00, 0x00], Ascii("SMTP"), [0x01]);
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("SMTP", Find(node.Children, "AddressType").Value);
    }

    [Theory]
    [InlineData(0x7A)]
    public void RequestDeletePropertiesNoReplicateParsesExactBoundary(byte ropId)
    {
        var bytes = Concat([ropId, 0x00, 0x00], Le((ushort)1), Tag(0x0003, 1));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "PropertyTags").Children);
    }

    [Theory]
    [InlineData(0x90)]
    [InlineData(0xA3)]
    public void RequestWriteAndCommitAndExtendedStreamParseExactBoundary(byte ropId)
    {
        var data = new byte[] { 9, 9 };
        var bytes = Concat([ropId, 0x00, 0x00], Le((ushort)data.Length), data);
        var (_, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
    }

    [Fact]
    public void RequestLogonEssdnSizeIsOnlyAPresenceGateNotALiteralLength()
    {
        // EssdnSize is set to a bogus value (10) that does not match the actual null-terminated
        // Essdn string's real length (4 + terminator) - the real boundary must come purely from the
        // null terminator, per the confirmed upstream quirk.
        var bytes = Concat([0xFE, 0x00, 0x00, 0x01], Le((uint)0), Le((uint)0), Le((ushort)10), Ascii("abcd"));
        var (node, handles) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(handles);
        Assert.Equal("abcd", Find(node.Children, "Essdn").Value);
    }

    [Fact]
    public void RequestLogonSkipsEssdnWhenSizeIsZero()
    {
        var bytes = Concat([0xFE, 0x00, 0x00, 0x00], Le((uint)0), Le((uint)0), Le((ushort)0));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.False(node.Children.Any(c => c.Name == "Essdn"));
    }

    // ---------------------------------------------------------------------------------------------
    // Responses
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ResponseGetPropertiesAllParsesSuccessAndFailure()
    {
        var success = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), TaggedInt32(1, 7));
        var (node, _) = ParseOne(success, MapiDirection.Response);
        Assert.Equal(success.Length, LastPosition);
        Assert.Equal("0x00000000 (Success)", Find(node.Children, "ReturnValue").Value);
        Assert.Single(Find(node.Children, "PropertyValues").Children);

        var failure = Concat([0x08, 0x00], Le((uint)0x80000001));
        var (failureNode, _) = ParseOne(failure, MapiDirection.Response);
        Assert.Equal(failure.Length, LastPosition);
        Assert.False(failureNode.Children.Any(c => c.Name == "PropertyValues"));
    }

    [Fact]
    public void ResponseGetPropertiesListParsesExactBoundary()
    {
        var bytes = Concat([0x09, 0x00], Le((uint)0), Le((ushort)1), Tag(0x0003, 1));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "PropertyTags").Children);
    }

    [Fact]
    public void ResponseGetReceiveFolderParsesExactBoundary()
    {
        var folderId = Fid(1, new byte[6]);
        var bytes = Concat([0x27, 0x00], Le((uint)0), folderId, Ascii("IPM.Note"));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("IPM.Note", Find(node.Children, "ExplicitMessageClass").Value);
    }

    [Fact]
    public void ResponseOpenStreamGatesStreamSizeOnSuccess()
    {
        var success = Concat([0x2B, 0x00], Le((uint)0), Le((uint)8192));
        var (node, _) = ParseOne(success, MapiDirection.Response);
        Assert.Equal(success.Length, LastPosition);
        Assert.Equal("8192", Find(node.Children, "StreamSize").Value);

        var failure = Concat([0x2B, 0x00], Le((uint)0x80000001));
        var (failNode, _) = ParseOne(failure, MapiDirection.Response);
        Assert.Equal(failure.Length, LastPosition);
        Assert.False(failNode.Children.Any(c => c.Name == "StreamSize"));
    }

    [Fact]
    public void ResponseReadStreamAlwaysReadsDataRegardlessOfReturnValue()
    {
        var data = new byte[] { 5, 6, 7 };
        var bytes = Concat([0x2C, 0x00], Le((uint)0x80000001), Le((ushort)data.Length), data);
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Contains("050607", Find(node.Children, "Data").Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResponseWriteStreamAlwaysReadsWrittenSize()
    {
        var bytes = Concat([0x2D, 0x00], Le((uint)0x80000001), Le((ushort)0));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("0", Find(node.Children, "WrittenSize").Value);
    }

    [Theory]
    [InlineData(0x39)]
    [InlineData(0x67)]
    public void ResponseCopyToAndCopyPropertiesGateOnReturnValueVariant(byte ropId)
    {
        var success = Concat([ropId, 0x00], Le((uint)0), Le((ushort)1), PropertyProblem(0, 0x0003, 1, 0x80000001));
        var (successNode, _) = ParseOne(success, MapiDirection.Response);
        Assert.Equal(success.Length, LastPosition);
        Assert.Single(Find(successNode.Children, "PropertyProblems").Children);

        var nullDest = Concat([ropId, 0x00], Le((uint)0x00000503), Le((uint)3));
        var (nullDestNode, _) = ParseOne(nullDest, MapiDirection.Response);
        Assert.Equal(nullDest.Length, LastPosition);
        Assert.Equal("0x00000503 (Failure - NullDestinationObject)", Find(nullDestNode.Children, "ReturnValue").Value);
        Assert.Equal("3", Find(nullDestNode.Children, "DestHandleIndex").Value);

        var genericFailure = Concat([ropId, 0x00], Le((uint)0x80000001));
        var (failNode, _) = ParseOne(genericFailure, MapiDirection.Response);
        Assert.Equal(genericFailure.Length, LastPosition);
        Assert.False(failNode.Children.Any(c => c.Name is "PropertyProblems" or "DestHandleIndex"));
    }

    [Fact]
    public void ResponseCopyToStreamAlwaysReadsByteCountersAndGatesDestHandle()
    {
        var success = Concat([0x3A, 0x00], Le((uint)0), Le((ulong)10), Le((ulong)10));
        var (node, _) = ParseOne(success, MapiDirection.Response);
        Assert.Equal(success.Length, LastPosition);
        Assert.False(node.Children.Any(c => c.Name == "DestHandleIndex"));

        var nullDest = Concat([0x3A, 0x00], Le((uint)0x00000503), Le((uint)9), Le((ulong)10), Le((ulong)10));
        var (nullDestNode, _) = ParseOne(nullDest, MapiDirection.Response);
        Assert.Equal(nullDest.Length, LastPosition);
        Assert.Equal("9", Find(nullDestNode.Children, "DestHandleIndex").Value);
        Assert.Equal("10", Find(nullDestNode.Children, "ReadByteCount").Value);
        Assert.Equal("10", Find(nullDestNode.Children, "WrittenByteCount").Value);
    }

    [Fact]
    public void ResponseGetOwningServersParsesExactBoundary()
    {
        var bytes = Concat([0x42, 0x00], Le((uint)0), Le((ushort)2), Le((ushort)0), Ascii("srv1"), Ascii("srv2"));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(2, Find(node.Children, "OwningServers").Children.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponsePublicFolderIsGhostedGatesServersOnFlag(bool isGhosted)
    {
        var bytes = isGhosted
            ? Concat([0x45, 0x00], Le((uint)0), [0x01], Le((ushort)1), Le((ushort)0), Ascii("srv"))
            : Concat([0x45, 0x00], Le((uint)0), [0x00]);
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(isGhosted, node.Children.Any(c => c.Name == "Servers"));
    }

    [Fact]
    public void ResponseGetAddressTypesParsesExactBoundary()
    {
        var bytes = Concat([0x49, 0x00], Le((uint)0), Le((ushort)1), Le((ushort)0), Ascii("SMTP"));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "AddressTypes").Children);
    }

    [Fact]
    public void ResponseTransportSendParsesExactBoundary()
    {
        var bytes = Concat([0x4A, 0x00], Le((uint)0), [0x00], Le((ushort)1), TaggedInt32(1, 99));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "PropertyValues").Children);
    }

    [Fact]
    public void ResponseProgressParsesExactBoundary()
    {
        var bytes = Concat([0x50, 0x00], Le((uint)0), [0x00], Le((uint)5), Le((uint)10));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("5", Find(node.Children, "CompletedTaskCount").Value);
        Assert.Equal("10", Find(node.Children, "TotalTaskCount").Value);
    }

    [Fact]
    public void ResponseGetNamesFromPropertyIdsParsesExactBoundary()
    {
        var guid = Guid.NewGuid();
        var bytes = Concat([0x55, 0x00], Le((uint)0), Le((ushort)1), PropertyNameLid(guid, 1));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "PropertyNames").Children);
    }

    [Fact]
    public void ResponseGetPropertyIdsFromNamesParsesExactBoundary()
    {
        var bytes = Concat([0x56, 0x00], Le((uint)0), Le((ushort)2), Le((ushort)0x3001), Le((ushort)0x3002));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(2, Find(node.Children, "PropertyIds").Children.Length);
    }

    [Fact]
    public void ResponseQueryNamedPropertiesParsesExactBoundary()
    {
        var guid = Guid.NewGuid();
        var bytes = Concat([0x5F, 0x00], Le((uint)0), Le((ushort)1), Le((ushort)0x3001), PropertyNameLid(guid, 5));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "PropertyIds").Children);
        Assert.Single(Find(node.Children, "PropertyNames").Children);
    }

    [Fact]
    public void ResponseGetPerUserLongTermIdsParsesExactBoundary()
    {
        var bytes = Concat([0x60, 0x00], Le((uint)0), Le((ushort)1), LongTermId(Guid.NewGuid(), new byte[6]));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "LongTermIds").Children);
    }

    [Fact]
    public void ResponseReadPerUserInformationParsesExactBoundary()
    {
        var data = new byte[] { 1, 2 };
        var bytes = Concat([0x63, 0x00], Le((uint)0), [0x01], Le((ushort)data.Length), data);
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("true", Find(node.Children, "HasFinished").Value);
    }

    [Fact]
    public void ResponseOptionsDataParsesExactBoundaryWithAndWithoutHelpFile()
    {
        var withoutHelp = Concat([0x6F, 0x00], Le((uint)0), [0x00], Le((ushort)0), Le((ushort)0));
        var (node1, _) = ParseOne(withoutHelp, MapiDirection.Response);
        Assert.Equal(withoutHelp.Length, LastPosition);

        var helpFile = new byte[] { 1, 2, 3 };
        var withHelp = Concat([0x6F, 0x00], Le((uint)0), [0x00], Le((ushort)0), Le((ushort)helpFile.Length), helpFile, Ascii("help.txt"));
        var (node2, _) = ParseOne(withHelp, MapiDirection.Response);
        Assert.Equal(withHelp.Length, LastPosition);
        Assert.Equal("help.txt", Find(node2.Children, "HelpFileName").Value);
    }

    [Theory]
    [InlineData(0x79)]
    [InlineData(0x7A)]
    public void ResponseNoReplicatePropertyProblemsParseExactBoundary(byte ropId)
    {
        var bytes = Concat([ropId, 0x00], Le((uint)0), Le((ushort)1), PropertyProblem(0, 0x0003, 5, 0x80000001));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Single(Find(node.Children, "PropertyProblems").Children);
    }

    [Theory]
    [InlineData(0x0A)] // RopSetProperties
    [InlineData(0x0B)] // RopDeleteProperties
    public void ResponseSetOrDeletePropertiesParsesExactBoundaryAndProvesFollowingOperationProgress(byte ropId)
    {
        // [MS-OXCROPS] 2.2.8.6.2/2.2.8.8.2: identical ReturnValue-gated PropertyProblem array shape to
        // RopSetPropertiesNoReplicate/RopDeletePropertiesNoReplicate (0x79/0x7A) above.
        var opBytes = Concat([ropId, 0x00], Le((uint)0), Le((ushort)2), PropertyProblem(0, 0x0003, 5, 0x80000001), PropertyProblem(1, 0x001F, 6, 0x80000001));
        var sentinel = new byte[] { 0x16, 0x02, 0x00, 0x00, 0x00, 0x00 }; // RopGetStatus response: success, universal 6 bytes
        var (node, _) = ParseOne(Concat(opBytes, sentinel), MapiDirection.Response);
        Assert.Equal(opBytes.Length, LastPosition);
        Assert.Equal(2, Find(node.Children, "PropertyProblems").Children.Length);
    }

    [Theory]
    [InlineData(0x0A)]
    [InlineData(0x0B)]
    public void ResponseSetOrDeletePropertiesFailureStaysAtSixBytesWithoutPropertyProblems(byte ropId)
    {
        var opBytes = Concat([ropId, 0x00], Le((uint)0x80000001));
        var sentinel = new byte[] { 0xEE, 0xFF };
        var (node, _) = ParseOne(Concat(opBytes, sentinel), MapiDirection.Response);
        Assert.Equal(opBytes.Length, LastPosition);
        Assert.False(node.Children.Any(c => c.Name == "PropertyProblems"));
    }

    [Fact]
    public void ResponseGetReceiveFolderTableParsesMultipleRowsExactBoundaryAndProvesFollowingOperationProgress()
    {
        // [MS-OXCSTOR] 2.2.3.4.2: RowCount(UInt32) followed by that many fixed-column PropertyRow
        // structures against PidTagFolderId(PtypInteger64)/PidTagMessageClass(PtypString8)/
        // PidTagLastModificationTime(PtypTime) - a protocol-fixed column list, not one supplied by an
        // earlier RopSetColumns (unlike RopQueryRows/RopFindRow/RopExpandRow, which remain unsupported).
        var row1 = Concat([0x00], Le((ulong)1), Encoding.ASCII.GetBytes("IPM.Note"), [0x00], Le((ulong)0x0102030405060708));
        var row2 = Concat([0x00], Le((ulong)2), Encoding.ASCII.GetBytes("IPM.Appointment"), [0x00], Le((ulong)0x0807060504030201));
        var opBytes = Concat([0x68, 0x00], Le((uint)0), Le((uint)2), row1, row2);
        var sentinel = new byte[] { 0x16, 0x02, 0x00, 0x00, 0x00, 0x00 }; // RopGetStatus response sentinel
        var (node, _) = ParseOne(Concat(opBytes, sentinel), MapiDirection.Response);
        Assert.Equal(opBytes.Length, LastPosition);
        var rows = Find(node.Children, "Rows");
        Assert.Equal(2, rows.Children.Length);
        var firstMessageClass = Find(rows.Children[0].Children[2].Children, "Value");
        var secondMessageClass = Find(rows.Children[1].Children[2].Children, "Value");
        Assert.Equal("IPM.Note", firstMessageClass.Value);
        Assert.Equal("IPM.Appointment", secondMessageClass.Value);
        Assert.NotEqual(MapiNodeKind.Raw, firstMessageClass.Kind);
        Assert.NotEqual(MapiNodeKind.Raw, secondMessageClass.Kind);
    }

    [Fact]
    public void ResponseGetReceiveFolderTableFailureStaysAtSixBytesWithoutRowCount()
    {
        var opBytes = Concat([0x68, 0x00], Le((uint)0x80000001));
        var sentinel = new byte[] { 0xEE, 0xFF };
        var (node, _) = ParseOne(Concat(opBytes, sentinel), MapiDirection.Response);
        Assert.Equal(opBytes.Length, LastPosition);
        Assert.False(node.Children.Any(c => c.Name is "Rows" or "RowCount"));
    }

    [Fact]
    public void ResponseGetReceiveFolderTableThrowsWhenRowCountExceedsCollectionLimit()
    {
        // RowCount claims ~4 billion rows with no row bytes present - a hostile "claim more than
        // exists" shape that must be rejected by ReadCount32's own bounds check rather than
        // attempting to allocate an oversized builder or looping unbounded.
        var bytes = Concat([0x68, 0x00], Le((uint)0), Le((uint)0xFFFFFFFF));
        AssertThrowsParse(bytes, MapiDirection.Response);
    }

    [Fact]
    public void ResponseGetReceiveFolderTableThrowsWhenRowTruncated()
    {
        var bytes = Concat([0x68, 0x00], Le((uint)0), Le((uint)1), [0x00], Le((ulong)1)); // MessageClass/LastModificationTime missing
        AssertThrowsParse(bytes, MapiDirection.Response);
    }

    [Fact]
    public void ResponseWriteAndCommitStreamAlwaysReadsWrittenSize()
    {
        var bytes = Concat([0x90, 0x00], Le((uint)0x80000001), Le((ushort)128));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("128", Find(node.Children, "WrittenSize").Value);
    }

    [Fact]
    public void ResponseWriteStreamExtendedAlwaysReadsWrittenSize()
    {
        var bytes = Concat([0xA3, 0x00], Le((uint)0x80000001), Le((uint)4096));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal("4096", Find(node.Children, "WrittenSize").Value);
    }

    [Fact]
    public void ResponseBackoffParsesExactBoundaryWithNoHandleOrReturnValue()
    {
        var backoff1 = Concat([0x01], Le((uint)500));
        var backoff2 = Concat([0x0A], Le((uint)750));
        var bytes = Concat([0xF9, 0x00], Le((uint)1000), [0x02], backoff1, backoff2, Le((ushort)2), [0xEE, 0xFF]);
        var reader = new MapiReader(bytes, CancellationToken.None);
        var node = RopPropertyStoreDecoders.Parse(ref reader, 0, MapiDirection.Response, [], new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(bytes.Length, reader.Position);
        Assert.False(node.Children.Any(c => c.Name == "ReturnValue"));
        var data = Find(node.Children, "BackoffRopData");
        Assert.Equal(2, data.Children.Length);
        Assert.Equal("0x02", Find(node.Children, "BackoffRopCount").Value);
        Assert.Contains("EEFF", Find(node.Children, "AdditionalData").Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResponseBackoffThrowsWhenAdditionalDataTruncated()
    {
        var bytes = Concat([0xF9, 0x00], Le((uint)1000), [0x00], Le((ushort)5), [0xEE]);
        AssertThrowsParse(bytes, MapiDirection.Response);
    }

    // ---------------------------------------------------------------------------------------------
    // PropertyValue dispatch boundary/hostile-input coverage.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void PropertyValueThrowsForRestrictionAndRuleActionTypes()
    {
        var restriction = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), Tag(0x00FD, 1), [0x00]);
        AssertThrowsParse(restriction, MapiDirection.Response);

        var ruleAction = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), Tag(0x00FE, 1), [0x00]);
        AssertThrowsParse(ruleAction, MapiDirection.Response);
    }

    [Fact]
    public void PropertyValueThrowsForMultiValueOnNonMultiCapableBaseType()
    {
        // PtypBoolean (0x000B) has no PtypMultiple* form.
        var bytes = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), Tag((ushort)(0x000B | 0x1000), 1), Le((uint)0));
        AssertThrowsParse(bytes, MapiDirection.Response);
    }

    [Fact]
    public void PropertyValueThrowsForUnknownPropertyType()
    {
        var bytes = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), Tag(0x1234, 1));
        AssertThrowsParse(bytes, MapiDirection.Response);
    }

    [Fact]
    public void PropertyValueRejectsHostileMultiValueCountBeyondCollectionLimit()
    {
        // PtypMultipleInteger32 with a Count field claiming ~4 billion elements must be rejected
        // safely (ReadCount32's own MaxCollectionCount/int.MaxValue guard) rather than attempting to
        // allocate an oversized builder or looping unbounded.
        var bytes = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), Tag((ushort)(0x0003 | 0x1000), 1), Le((uint)0xFFFFFFFF));
        AssertThrowsParse(bytes, MapiDirection.Response);
    }

    [Fact]
    public void PropertyValueParsesMultiValueArrayExactBoundary()
    {
        var bytes = Concat(
            [0x08, 0x00], Le((uint)0), Le((ushort)1), Tag((ushort)(0x0003 | 0x1000), 1), Le((uint)2), Le((uint)11), Le((uint)22));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        var values = Find(node.Children, "PropertyValues");
        var tagged = values.Children[0];
        var value = Find(tagged.Children, "PropertyValue");
        Assert.Equal(2, value.Children.Length - 1); // Count field + 2 elements
    }

    [Fact]
    public void PropertyValueBinaryUsesSixteenBitCountEvenWithLargeDeclaredValue()
    {
        var data = new byte[] { 0xAA, 0xBB, 0xCC };
        var bytes = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), Tag(0x0102, 1), Le((ushort)data.Length), data);
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
    }

    [Fact]
    public void PropertyValueBinaryThrowsWhenSixteenBitCountExceedsAvailableBytes()
    {
        var bytes = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), Tag(0x0102, 1), Le((ushort)9000), new byte[2]);
        AssertThrowsParse(bytes, MapiDirection.Response);
    }

    [Fact]
    public void PropertyValueStringPassesThroughHostileControlAndMarkupCharactersUnaltered()
    {
        const string payload = "<script>alert(1)</script>{0}%s\"';--";
        var bytes = Concat([0x08, 0x00], Le((uint)0), Le((ushort)1), TaggedString(1, payload));
        var (node, _) = ParseOne(bytes, MapiDirection.Response);
        Assert.Equal(bytes.Length, LastPosition);
        var tagged = Find(node.Children, "PropertyValues").Children[0];
        var value = Find(tagged.Children, "PropertyValue");
        Assert.Equal(payload, value.Value);
    }

    [Fact]
    public void MessageClassFieldPassesThroughHostileContentUnaltered()
    {
        const string payload = "IPM.Note\"; DROP TABLE x;--<b>";
        var bytes = Concat([0x27, 0x00, 0x00], Ascii(payload));
        var (node, _) = ParseOne(bytes, MapiDirection.Request);
        Assert.Equal(bytes.Length, LastPosition);
        Assert.Equal(payload, Find(node.Children, "MessageClass").Value);
    }

    // ---------------------------------------------------------------------------------------------
    // Test helpers
    // ---------------------------------------------------------------------------------------------

    private static int LastPosition { get; set; }

    private static (MapiNode Node, List<RopHandleReference> Handles) ParseOne(byte[] bytes, MapiDirection direction, int operationIndex = 0)
    {
        Assert.True(RopPropertyStoreDecoders.Supports(direction, bytes[0]), $"Expected 0x{bytes[0]:X2} to be supported for {direction}.");
        var reader = new MapiReader(bytes, CancellationToken.None);
        var handles = new List<RopHandleReference>();
        var budget = new MapiNodeBudget();
        var node = RopPropertyStoreDecoders.Parse(ref reader, operationIndex, direction, handles, budget, CancellationToken.None);
        LastPosition = reader.Position;
        return (node, handles);
    }

    private static void AssertThrowsParse(byte[] bytes, MapiDirection direction)
    {
        var reader = new MapiReader(bytes, CancellationToken.None);
        var threw = false;
        try
        {
            RopPropertyStoreDecoders.Parse(ref reader, 0, direction, [], new MapiNodeBudget(), CancellationToken.None);
        }
        catch (MapiParseException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected MapiParseException.");
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

    private static byte[] Ascii(string value) => Concat(Encoding.ASCII.GetBytes(value), [0x00]);

    private static byte[] Tag(ushort type, ushort id) => Concat(Le(type), Le(id));

    private static byte[] Fid(ushort replicaId, byte[] globalCounter) => Concat(Le(replicaId), globalCounter);

    private static byte[] LongTermId(Guid databaseGuid, byte[] globalCounter, ushort pad = 0) =>
        Concat(databaseGuid.ToByteArray(), globalCounter, Le(pad));

    private static byte[] TaggedInt32(ushort id, int value) => Concat(Tag(0x0003, id), Le(unchecked((uint)value)));

    private static byte[] TaggedString(ushort id, string value) => Concat(Tag(0x001F, id), Encoding.Unicode.GetBytes(value), [0x00, 0x00]);

    private static byte[] PropertyProblem(ushort index, ushort type, ushort id, uint errorCode) =>
        Concat(Le(index), Tag(type, id), Le(errorCode));

    private static byte[] PropertyNameLid(Guid guid, uint lid) => Concat([0x00], guid.ToByteArray(), Le(lid));

    private static byte[] PropertyNameNamed(Guid guid, string name)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        return Concat([0x01], guid.ToByteArray(), [(byte)encoded.Length], encoded);
    }

    private static byte[] PropertyNameNone() => [0xFF];

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
