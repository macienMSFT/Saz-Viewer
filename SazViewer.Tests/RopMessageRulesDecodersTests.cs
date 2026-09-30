using System.Buffers.Binary;
using System.Text;
using SazViewer.Core;

namespace SazViewer.Tests;

/// <summary>
/// Exhaustive synthetic coverage for the <see cref="RopMessageRulesDecoders"/> module (MS-OXCMSG /
/// MS-OXORULE / MS-OXCPERM / MS-OXCNOTIF variable-width semantic decoders), wired into the shared
/// <see cref="RopVariableDispatcher"/>/<see cref="RopSemanticParser"/> ROP-buffer path. Most tests
/// drive <see cref="RopMessageRulesDecoders.Supports"/> and <see cref="RopMessageRulesDecoders.Parse"/>
/// directly against synthetic byte buffers for precise, family-scoped boundary coverage.
/// </summary>
public sealed class RopMessageRulesDecodersTests
{
    // ---------------------------------------------------------------------------------------------
    // Supports(): the exact, locked-in set of (direction, RopId) pairs this module claims.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SupportsExactlyFifteenRequestRopIds()
    {
        byte[] supported =
        [
            0x03, 0x06, 0x0C, 0x0E, 0x0F, 0x11, 0x20, 0x21, 0x22, 0x29, 0x40, 0x41, 0x46, 0x57, 0x66,
        ];
        Assert.Equal(15, supported.Length);
        foreach (var ropId in supported)
        {
            Assert.True(RopMessageRulesDecoders.Supports(MapiDirection.Request, ropId), $"Expected request 0x{ropId:X2} to be supported.");
        }

        var supportedCount = 0;
        for (var ropId = 0; ropId <= 0xFF; ropId++)
        {
            if (RopMessageRulesDecoders.Supports(MapiDirection.Request, (byte)ropId))
            {
                supportedCount++;
                Assert.Contains((byte)ropId, supported);
            }
        }
        Assert.Equal(15, supportedCount);
    }

    [Fact]
    public void SupportsExactlyThirteenResponseRopIds()
    {
        byte[] supported =
        [
            0x03, 0x06, 0x0C, 0x0F, 0x10, 0x1F, 0x20, 0x23, 0x2A, 0x46, 0x52, 0x66, 0x6E,
        ];
        Assert.Equal(13, supported.Length);
        foreach (var ropId in supported)
        {
            Assert.True(RopMessageRulesDecoders.Supports(MapiDirection.Response, ropId), $"Expected response 0x{ropId:X2} to be supported.");
        }

        var supportedCount = 0;
        for (var ropId = 0; ropId <= 0xFF; ropId++)
        {
            if (RopMessageRulesDecoders.Supports(MapiDirection.Response, (byte)ropId))
            {
                supportedCount++;
                Assert.Contains((byte)ropId, supported);
            }
        }
        Assert.Equal(13, supportedCount);
    }

    [Theory]
    [InlineData(0x0D)] // RopRemoveAllRecipients: fully covered by the main dispatcher's fixed schemas.
    [InlineData(0x3F)] // RopGetRulesTable: fully covered elsewhere.
    [InlineData(0x3E)] // RopGetPermissionsTable: fully covered elsewhere.
    [InlineData(0x52)] // RopGetValidAttachments *request* is a bare fixed-6 header covered elsewhere (only its response is ours).
    [InlineData(0x23)] // RopCreateAttachment *request* is covered elsewhere (only its response is ours).
    [InlineData(0x1F)] // RopGetMessageStatus *request* is covered elsewhere (only its response is ours).
    [InlineData(0x10)] // RopReloadCachedInformation *request* is covered elsewhere (only its response is ours).
    [InlineData(0xC8)] // Genuinely unknown RopId.
    public void DoesNotSupportOutOfScopeOrAlreadyCoveredRequestRopIds(byte ropId)
    {
        Assert.False(RopMessageRulesDecoders.Supports(MapiDirection.Request, ropId));
    }

    [Theory]
    [InlineData(0x11)] // RopSetMessageReadFlag response: gated shape fully covered elsewhere.
    [InlineData(0x0E)] // RopModifyRecipients *response* is fixed-6 covered elsewhere (only its request is ours).
    [InlineData(0x29)] // RopRegisterNotification *response* is fixed-6 covered elsewhere (only its request is ours).
    [InlineData(0x40)] // RopModifyPermissions *response* is fixed-6 covered elsewhere (only its request is ours).
    [InlineData(0x41)] // RopModifyRules *response* is fixed-6 covered elsewhere (only its request is ours).
    [InlineData(0x57)] // RopUpdateDeferredActionMessages *response* is fixed-6 covered elsewhere (only its request is ours).
    [InlineData(0x21)] // RopGetAttachmentTable *response* is fixed-6 covered elsewhere (only its request is ours).
    [InlineData(0x22)] // RopOpenAttachment *response* is fixed-6 covered elsewhere (only its request is ours).
    [InlineData(0xC8)] // Genuinely unknown RopId.
    public void DoesNotSupportOutOfScopeOrAlreadyCoveredResponseRopIds(byte ropId)
    {
        Assert.False(RopMessageRulesDecoders.Supports(MapiDirection.Response, ropId));
    }

    [Fact]
    public void ParseThrowsForAnUnsupportedRopIdRatherThanGuessingABoundary()
    {
        var ex = ExpectMapiParseException([0x0D, 0x00, 0x00], MapiDirection.Request);
        Assert.Contains("0x0D", ex.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // MSOXCMSG requests.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopOpenMessageRequest()
    {
        var ropList = Concat(
            [0x03, 0x00, 0x00, 0x01], // RopId, LogonId, InputHandleIndex, OutputHandleIndex
            Le((ushort)1252), // CodePageId
            FolderOrMessageId(1, 0x0A0B0C0D0E0F),
            [0x03], // OpenModeFlags
            FolderOrMessageId(2, 0x1A1B1C1D1E1F));

        var handles = new List<RopHandleReference>();
        var node = ParseRequest(ropList, 0x03, handles);

        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("1252", Find(node, "CodePageId").Value);
        Assert.Equal("0x03", Find(node, "OpenModeFlags").Value);
        Assert.Equal(2, handles.Count);
        Assert.Equal(2, FindAll(node.Children, "GlobalCounter").Count());
    }

    [Fact]
    public void ParsesRopCreateMessageRequest()
    {
        var ropList = Concat(
            [0x06, 0x00, 0x00, 0x01],
            Le((ushort)0),
            FolderOrMessageId(),
            [0x01]); // AssociatedFlag = true
        var node = ParseRequest(ropList, 0x06, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("true", Find(node, "AssociatedFlag").Value);
    }

    [Fact]
    public void ParsesRopSaveChangesMessageRequestWithReorderedHandleFields()
    {
        var ropList = Concat([0x0C, 0x00, 0x05, 0x02, 0x0C]); // RopId,LogonId,ResponseHandleIndex,InputHandleIndex,SaveFlags
        var handles = new List<RopHandleReference>();
        var node = ParseRequest(ropList, 0x0C, handles);
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("0x0C", Find(node, "SaveFlags").Value);
        Assert.Equal("ResponseHandleIndex", handles[0].FieldName);
        Assert.Equal(5, handles[0].Index);
        Assert.Equal("InputHandleIndex", handles[1].FieldName);
        Assert.Equal(2, handles[1].Index);
    }

    [Fact]
    public void ParsesRopModifyRecipientsRequestWithRecipientRowsDecodedAgainstItsOwnColumns()
    {
        var columns = new (ushort Type, ushort Id)[] { (0x0003, 0x0037) };
        var row0 = RecipientRow(columns, 42);
        var ropList = Concat(
            [0x0E, 0x00, 0x00],
            Le((ushort)1), // ColumnCount
            PropTag(columns[0].Type, columns[0].Id),
            Le((ushort)1), // RowCount
            Le((uint)7), // RowId
            [0x01], // RecipientType
            Le((ushort)row0.Length),
            row0);

        var node = ParseRequest(ropList, 0x0E, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        var value = Find(node, "Value[0]");
        Assert.Equal("42", Find(value.Children, "Value").Value);
    }

    [Fact]
    public void ParsesRopModifyRecipientsRequestWithZeroSizeOptionalRecipientRow()
    {
        var ropList = Concat(
            [0x0E, 0x00, 0x00],
            Le((ushort)0), // ColumnCount
            Le((ushort)1), // RowCount
            Le((uint)3), // RowId
            [0x00], // RecipientType
            Le((ushort)0)); // RecipientRowSize = 0: no RecipientRow bytes follow

        var node = ParseRequest(ropList, 0x0E, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
    }

    /// <summary>
    /// Regression for a real-capture width bug: PtypBinary's byte-count prefix is 16-bit in an
    /// MS-OXCROPS ROP buffer ([MS-OXCDATA] 2.11.1.1), not the 32-bit NSPI/extended-rule form. Covers
    /// the recipient-row path (<c>ParseRopPropertyRow</c>, used by RopModifyRecipients/RopOpenMessage/
    /// RopReadRecipients). Proves the value's consumed length is exact by successfully decoding a
    /// second, independent RopReadRecipients operation immediately afterward in the same buffer.
    /// </summary>
    [Fact]
    public void ParsesRopModifyRecipientsRequestWithSixteenBitPtypBinaryRecipientRowValueAndProvesFollowingOperationBoundary()
    {
        var columns = new (ushort Type, ushort Id)[] { (0x0102, 0x0FFF) }; // PtypBinary column
        var binaryValue = Enumerable.Range(0, 22).Select(i => (byte)(0xC0 + i)).ToArray();
        var row0 = Concat(
            new byte[] { 0x00, 0x00 }, // RecipientFlags: NoType, no R/S/T/D/E/O/I/U/N
            Le((ushort)columns.Length),
            [0x00], // PropertyRow.Flag = all values present
            Le((ushort)binaryValue.Length), binaryValue);
        var op1 = Concat(
            [0x0E, 0x00, 0x00],
            Le((ushort)1), // ColumnCount
            PropTag(columns[0].Type, columns[0].Id),
            Le((ushort)1), // RowCount
            Le((uint)7), // RowId
            [0x01], // RecipientType
            Le((ushort)row0.Length),
            row0);
        var op2 = Concat([0x0F, 0x00, 0x00], Le((uint)5), Le((ushort)0)); // RopReadRecipients
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);
        var value = Find(node1, "Value[0]");
        Assert.Equal("22 bytes", Find(value.Children, "Value").Value);

        var node2 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("5", Find(node2, "RowId").Value);
    }

    [Fact]
    public void ParsesRopReadRecipientsRequest()
    {
        var ropList = Concat([0x0F, 0x00, 0x00], Le((uint)5), Le((ushort)0));
        var node = ParseRequest(ropList, 0x0F, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("5", Find(node, "RowId").Value);
    }

    // ---------------------------------------------------------------------------------------------
    // RopSetMessageReadFlag (0x11) request: [MS-OXCMSG] 2.2.3.9 / upstream RopSetMessageReadFlagRequest.
    // The trailing 24-byte ClientData block is present only when the object was opened against a
    // NON-private (public folders) logon - state this operation's own bytes cannot reveal, so it is
    // gated by a same-capture RopLogon request's recorded LogonFlags.Private bit.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopSetMessageReadFlagRequestWithoutClientDataForAPrivateLogon()
    {
        var context = new MapiCaptureContext();
        context.RecordLogonPrivacy("mailbox-a", logonId: 0, isPrivate: true);
        var ropList = new byte[] { 0x11, 0x00, 0x01, 0x02, 0x01 };

        var handles = new List<RopHandleReference>();
        var reader = NewReader(ropList);
        var node = RopMessageRulesDecoders.Parse(
            ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None, context, "mailbox-a");

        Assert.Equal(ropList.Length, node.Length);
        Assert.True(reader.End);
        Assert.Equal("0x01", Find(node, "ReadFlags").Value);
        Assert.False(node.Children.Any(c => c.Name == "ClientData"));
        Assert.Equal(2, handles.Count);
    }

    [Fact]
    public void ParsesRopSetMessageReadFlagRequestWithClientDataForAPublicLogonAndProvesFollowingOperationBoundary()
    {
        var context = new MapiCaptureContext();
        context.RecordLogonPrivacy("mailbox-a", logonId: 3, isPrivate: false);
        var clientData = Enumerable.Range(0, 24).Select(i => (byte)(0x40 + i)).ToArray();
        var op1 = Concat(new byte[] { 0x11, 0x03, 0x01, 0x02, 0x00 }, clientData);
        var op2 = Concat([0x0F, 0x00, 0x00], Le((uint)9), Le((ushort)0)); // RopReadRecipients
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        var node1 = RopMessageRulesDecoders.Parse(
            ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None, context, "mailbox-a");
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);
        Assert.Equal(24, Find(node1, "ClientData").Length);
        Assert.Equal(Convert.ToHexString(clientData), Find(node1, "ClientData").Value);

        var node2 = RopMessageRulesDecoders.Parse(
            ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None, context, "mailbox-a");
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("9", Find(node2, "RowId").Value);
    }

    [Fact]
    public void RopSetMessageReadFlagRequestThrowsWithoutAnyRecordedLogonPrivacyContext()
    {
        var ropList = new byte[] { 0x11, 0x07, 0x01, 0x02, 0x00 };
        var reader = NewReader(ropList);
        var threw = false;
        try
        {
            RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None, context: null);
        }
        catch (MapiParseException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected MapiParseException.");
    }

    [Fact]
    public void RopSetMessageReadFlagRequestThrowsWhenTheReferencedLogonIdWasNeverRecorded()
    {
        var context = new MapiCaptureContext();
        context.RecordLogonPrivacy("mailbox-a", logonId: 1, isPrivate: true); // a different LogonId than the one this request references.
        var ropList = new byte[] { 0x11, 0x02, 0x01, 0x02, 0x00 };
        var reader = NewReader(ropList);
        var threw = false;
        try
        {
            RopMessageRulesDecoders.Parse(
                ref reader, 0, MapiDirection.Request, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None, context, "mailbox-a");
        }
        catch (MapiParseException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected MapiParseException.");
    }

    [Fact]
    public void RopSetMessageReadFlagRequestThrowsWhenTruncatedBeforeReadFlags()
    {
        var context = new MapiCaptureContext();
        context.RecordLogonPrivacy("mailbox-a", logonId: 0, isPrivate: true);
        var ropList = new byte[] { 0x11, 0x00, 0x01, 0x02 }; // missing ReadFlags byte
        var reader = NewReader(ropList);
        var threw = false;
        try
        {
            RopMessageRulesDecoders.Parse(
                ref reader, 0, MapiDirection.Request, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None, context, "mailbox-a");
        }
        catch (MapiParseException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected MapiParseException.");
    }

    [Fact]
    public void RopSetMessageReadFlagRequestThrowsWhenClientDataIsTruncated()
    {
        var context = new MapiCaptureContext();
        context.RecordLogonPrivacy("mailbox-a", logonId: 0, isPrivate: false);
        var ropList = Concat(new byte[] { 0x11, 0x00, 0x01, 0x02, 0x00 }, new byte[10]); // needs 24 bytes of ClientData, only 10 present
        var reader = NewReader(ropList);
        var threw = false;
        try
        {
            RopMessageRulesDecoders.Parse(
                ref reader, 0, MapiDirection.Request, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None, context, "mailbox-a");
        }
        catch (MapiParseException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected MapiParseException.");
    }

    [Fact]
    public void RopSetMessageReadFlagRequestPrivacyStateFlowsAcrossHttpSessionsInTheSameLogicalConnection()
    {
        // A LogonId's privacy is established once per logical MAPI connection and is legitimately
        // referenced by RopSetMessageReadFlag requests in later HTTP round-trips on that connection.
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-logon", "mailbox-a");
        context.RegisterLogonCorrelationScope("http-read-flag", "mailbox-a");
        context.RecordLogonPrivacy("http-logon", logonId: 5, isPrivate: true);
        var ropList = new byte[] { 0x11, 0x05, 0x01, 0x02, 0x00 };
        var reader = NewReader(ropList);
        var node = RopMessageRulesDecoders.Parse(
            ref reader,
            0,
            MapiDirection.Request,
            new List<RopHandleReference>(),
            new MapiNodeBudget(),
            CancellationToken.None,
            context,
            "http-read-flag");
        Assert.True(reader.End);
        Assert.False(node.Children.Any(c => c.Name == "ClientData"));
    }

    [Fact]
    public void RopSetMessageReadFlagRequestKeepsReusedLogonIdsIsolatedByLogicalConnection()
    {
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-private", "mailbox-private");
        context.RegisterLogonCorrelationScope("http-public", "mailbox-public");
        context.RecordLogonPrivacy("http-private", logonId: 1, isPrivate: true);
        context.RecordLogonPrivacy("http-public", logonId: 1, isPrivate: false);

        var privateReader = NewReader([0x11, 0x01, 0x01, 0x02, 0x00]);
        var privateNode = RopMessageRulesDecoders.Parse(
            ref privateReader,
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context,
            "http-private");
        Assert.False(privateNode.Children.Any(c => c.Name == "ClientData"));

        var clientData = new byte[24];
        var publicReader = NewReader(Concat([0x11, 0x01, 0x01, 0x02, 0x00], clientData));
        var publicNode = RopMessageRulesDecoders.Parse(
            ref publicReader,
            0,
            MapiDirection.Request,
            [],
            new MapiNodeBudget(),
            CancellationToken.None,
            context,
            "http-public");
        Assert.Equal(24, Find(publicNode, "ClientData").Length);
    }

    [Fact]
    public void RopSetMessageReadFlagRequestRefusesConflictingPrivacyWithinOneLogicalConnection()
    {
        var context = new MapiCaptureContext();
        context.RegisterLogonCorrelationScope("http-logon-1", "mailbox-a");
        context.RegisterLogonCorrelationScope("http-logon-2", "mailbox-a");
        context.RegisterLogonCorrelationScope("http-read-flag", "mailbox-a");
        context.RecordLogonPrivacy("http-logon-1", logonId: 1, isPrivate: true);
        context.RecordLogonPrivacy("http-logon-2", logonId: 1, isPrivate: false);

        var reader = NewReader([0x11, 0x01, 0x01, 0x02, 0x00]);
        var threw = false;
        try
        {
            RopMessageRulesDecoders.Parse(
                ref reader,
                0,
                MapiDirection.Request,
                [],
                new MapiNodeBudget(),
                CancellationToken.None,
                context,
                "http-read-flag");
        }
        catch (MapiParseException)
        {
            threw = true;
        }
        Assert.True(threw, "Conflicting privacy observations must make the correlation ambiguous.");
    }

    [Fact]
    public void ParsesRopSetMessageStatusRequest()
    {
        var ropList = Concat([0x20, 0x00, 0x00], FolderOrMessageId(), Le((uint)0x00000020), Le((uint)0x00000020));
        var node = ParseRequest(ropList, 0x20, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("0x00000020", Find(node, "MessageStatusFlags").Value);
    }

    [Fact]
    public void ParsesRopGetAttachmentTableRequest()
    {
        var ropList = new byte[] { 0x21, 0x00, 0x00, 0x01, 0x02 };
        var node = ParseRequest(ropList, 0x21, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("0x02", Find(node, "TableFlags").Value);
    }

    [Fact]
    public void ParsesRopOpenAttachmentRequest()
    {
        var ropList = Concat([0x22, 0x00, 0x00, 0x01, 0x00], Le((uint)9));
        var node = ParseRequest(ropList, 0x22, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("9", Find(node, "AttachmentID").Value);
    }

    [Fact]
    public void ParsesRopOpenEmbeddedMessageRequest()
    {
        var ropList = Concat([0x46, 0x00, 0x00, 0x01], Le((ushort)1200), [0x02]);
        var node = ParseRequest(ropList, 0x46, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("1200", Find(node, "CodePageId").Value);
    }

    [Fact]
    public void ParsesRopSetReadFlagsRequestWithMultipleMessageIds()
    {
        var ropList = Concat(
            [0x66, 0x00, 0x00, 0x01, 0x03],
            Le((ushort)2),
            FolderOrMessageId(1, 1),
            FolderOrMessageId(2, 2));
        var node = ParseRequest(ropList, 0x66, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal(2, Find(node, "MessageIds").Children.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // MSOXORULE / MSOXCPERM requests (RuleData / PermissionData reuse of tagged-property parsing).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopModifyRulesRequestWithMultipleRulesAndPropertyValues()
    {
        var item0 = RuleOrPermissionItem(0x01, TaggedValueInt32(0x6680, 1), TaggedValueInt32(0x6681, 2));
        var item1 = RuleOrPermissionItem(0x04);
        var ropList = Concat([0x41, 0x00, 0x00, 0x01], Le((ushort)2), item0, item1);

        var node = ParseRequest(ropList, 0x41, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("0x01 (REPLACE)", Find(node, "ModifyRulesFlags").Value);
        var rulesData = Find(node, "RulesData");
        Assert.Equal(2, rulesData.Children.Length);
        Assert.Equal(2, Find(rulesData.Children[0].Children, "PropertyValues").Children.Length);
        Assert.Empty(Find(rulesData.Children[1].Children, "PropertyValues").Children);
    }

    /// <summary>
    /// Regression for a real-capture width bug: PtypBinary's byte-count prefix is 16-bit in an
    /// MS-OXCROPS ROP buffer ([MS-OXCDATA] 2.11.1.1), not the 32-bit NSPI/extended-rule form. Covers
    /// both RopModifyRules (MS-OXORULE RuleData) and RopModifyPermissions (MS-OXCPERM PermissionData),
    /// which share <c>ParseRuleOrPermissionDataArray</c>. Proves the value's consumed length is exact
    /// by successfully decoding a second, independent operation immediately afterward in the same
    /// buffer.
    /// </summary>
    [Theory]
    [InlineData((byte)0x41, "RulesData")]
    [InlineData((byte)0x40, "PermissionsData")]
    public void ParsesRuleOrPermissionDataRequestWithSixteenBitPtypBinaryLengthAndProvesFollowingOperationBoundary(
        byte ropId, string arrayName)
    {
        var binaryValue = Enumerable.Range(0, 22).Select(i => (byte)(0xB0 + i)).ToArray();
        var binaryTagged = Concat(PropTag(0x0102, 0x6684), Le((ushort)binaryValue.Length), binaryValue);
        var item0 = RuleOrPermissionItem(0x01, binaryTagged);
        var op1 = Concat([ropId, 0x00, 0x00, 0x01], Le((ushort)1), item0);

        byte[] serverEntryId = [0xAA, 0xBB];
        var op2 = Concat([0x57, 0x00, 0x00], Le((ushort)serverEntryId.Length), serverEntryId, Le((ushort)0));
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        Assert.True(RopMessageRulesDecoders.Supports(MapiDirection.Request, ropId));
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);

        var arrayData = Find(node1, arrayName);
        var item = Assert.Single(arrayData.Children);
        var value = Find(item.Children, "PropertyValues").Children.Single();
        Assert.Equal("22 bytes", Find(value.Children, "PropertyValue").Value);

        var node2 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("AABB", Find(node2, "ServerEntryId").Value);
    }

    /// <summary>
    /// Regression for the standard-rule (MS-OXCROPS ROP buffer) PtypRuleAction wire shape: a
    /// PidTagRuleActions value embedded in a RopModifyRules RuleData array uses a 16-bit
    /// NoOfActions/ActionLength ([MS-OXORULE] 2.2.5) rather than the 32-bit extended-rule form.
    /// OP_MARK_AS_READ has no action data at all, so this also covers the minimal 9-byte
    /// action-header boundary. Proves the value's consumed length is exact by successfully
    /// decoding a second, independent operation immediately afterward in the same buffer.
    /// </summary>
    [Fact]
    public void ParsesRopModifyRulesRuleActionWithMarkAsReadAndProvesFollowingOperationBoundary()
    {
        var ruleAction = RuleActionArray(ActionBlockRopBuffer(0x0B, 0, 0, []));
        var taggedRuleAction = Concat(PropTag(0x00FE, 0x6680), ruleAction);
        var item0 = RuleOrPermissionItem(0x01, taggedRuleAction);
        var op1 = Concat([0x41, 0x00, 0x00, 0x01], Le((ushort)1), item0);

        byte[] serverEntryId = [0xAA, 0xBB];
        var op2 = Concat([0x57, 0x00, 0x00], Le((ushort)serverEntryId.Length), serverEntryId, Le((ushort)0));
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);

        var ruleActionNode = Find(node1, "RuleAction");
        Assert.Equal("1 action(s)", ruleActionNode.Value);
        var actionBlock = Find(node1, "ActionBlock[0]");
        Assert.Equal("OP_MARK_AS_READ", actionBlock.Value);
        Assert.Equal("9", Find(actionBlock.Children, "ActionLength").Value);
        Assert.Empty(FindAll(actionBlock.Children, "Malformed action data"));
        Assert.Empty(FindAll(actionBlock.Children, "Trailing action data"));

        var node2 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("AABB", Find(node2, "ServerEntryId").Value);
    }

    /// <summary>
    /// Standard-rule OP_MOVE with FolderInThisStore = false: the destination folder is an opaque
    /// [MS-OXCDATA] 2.2.4.1 Folder EntryID in another store ([MS-OXORULE] 2.2.5.1.2.1 "... for
    /// Standard Rules"), using 16-bit StoreEIDSize/FolderEIDSize. Proves boundary progress with a
    /// following operation.
    /// </summary>
    [Fact]
    public void ParsesRopModifyRulesRuleActionWithStandardMoveToOtherStoreAndProvesFollowingOperationBoundary()
    {
        byte[] storeEid = [0x11, 0x22, 0x33, 0x44];
        var folderEid = FolderEntryIdBytes();
        var actionData = Concat(
            [0x00], // FolderInThisStore = false
            Le((ushort)storeEid.Length), storeEid,
            Le((ushort)folderEid.Length), folderEid);
        var ruleAction = RuleActionArray(ActionBlockRopBuffer(0x01, 0, 0, actionData));
        var taggedRuleAction = Concat(PropTag(0x00FE, 0x6680), ruleAction);
        var item0 = RuleOrPermissionItem(0x01, taggedRuleAction);
        var op1 = Concat([0x41, 0x00, 0x00, 0x01], Le((ushort)1), item0);

        byte[] serverEntryId = [0xAA, 0xBB];
        var op2 = Concat([0x57, 0x00, 0x00], Le((ushort)serverEntryId.Length), serverEntryId, Le((ushort)0));
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);

        var actionBlock = Find(node1, "ActionBlock[0]");
        Assert.Equal("OP_MOVE", actionBlock.Value);
        Assert.Equal("false", Find(actionBlock.Children, "FolderInThisStore").Value);
        Assert.Equal("4", Find(actionBlock.Children, "StoreEIDSize").Value);
        Assert.Equal("46", Find(actionBlock.Children, "FolderEIDSize").Value);
        Assert.Equal("FolderEID", Find(actionBlock.Children, "FolderEID").Name);
        Assert.Empty(FindAll(actionBlock.Children, "Malformed action data"));
        Assert.Empty(FindAll(actionBlock.Children, "Trailing action data"));

        var node2 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("AABB", Find(node2, "ServerEntryId").Value);
    }

    /// <summary>
    /// Standard-rule OP_COPY with FolderInThisStore = true: the destination folder is a fixed
    /// 21-byte ServerEid ([MS-OXORULE] 2.2.5.1.2.1.1), not a length-prefixed EntryID. Proves
    /// boundary progress with a following operation.
    /// </summary>
    [Fact]
    public void ParsesRopModifyRulesRuleActionWithStandardCopyToSameStoreServerEidAndProvesFollowingOperationBoundary()
    {
        byte[] storeEid = [0x11, 0x22, 0x33, 0x44];
        var serverEid = ServerEidBytes();
        var actionData = Concat(
            [0x01], // FolderInThisStore = true
            Le((ushort)storeEid.Length), storeEid,
            Le((ushort)serverEid.Length), serverEid);
        var ruleAction = RuleActionArray(ActionBlockRopBuffer(0x02, 0, 0, actionData));
        var taggedRuleAction = Concat(PropTag(0x00FE, 0x6680), ruleAction);
        var item0 = RuleOrPermissionItem(0x01, taggedRuleAction);
        var op1 = Concat([0x41, 0x00, 0x00, 0x01], Le((ushort)1), item0);

        byte[] serverEntryId = [0xAA, 0xBB];
        var op2 = Concat([0x57, 0x00, 0x00], Le((ushort)serverEntryId.Length), serverEntryId, Le((ushort)0));
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);

        var actionBlock = Find(node1, "ActionBlock[0]");
        Assert.Equal("OP_COPY", actionBlock.Value);
        Assert.Equal("true", Find(actionBlock.Children, "FolderInThisStore").Value);
        Assert.Equal("21", Find(actionBlock.Children, "FolderEIDSize").Value);
        var folderEidNode = Find(actionBlock.Children, "FolderEID (ServerEid)");
        Assert.Equal("true", Find(folderEidNode.Children, "Ours").Value);
        Assert.Equal("0", Find(folderEidNode.Children, "Instance").Value);
        Assert.Empty(FindAll(actionBlock.Children, "Malformed action data"));
        Assert.Empty(FindAll(actionBlock.Children, "Trailing action data"));

        var node2 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("AABB", Find(node2, "ServerEntryId").Value);
    }

    /// <summary>
    /// Standard-rule OP_REPLY/OP_OOF_REPLY: a fixed FolderID+MessageID pair plus a GUID
    /// ([MS-OXORULE] 2.2.5.1.2.2 "... for Standard Rules") with no length-prefixed EntryID at all,
    /// structurally distinct from the extended-rule form. Proves boundary progress with a following
    /// operation.
    /// </summary>
    [Fact]
    public void ParsesRopModifyRulesRuleActionWithStandardReplyAndProvesFollowingOperationBoundary()
    {
        var actionData = Concat(
            FolderOrMessageId(1, 0x0102030405), // ReplyTemplateFID
            FolderOrMessageId(2, 0x0605040302), // ReplyTemplateMID
            Enumerable.Repeat((byte)0x99, 16).ToArray()); // ReplyTemplateGUID
        var ruleAction = RuleActionArray(ActionBlockRopBuffer(0x04, 0, 0, actionData));
        var taggedRuleAction = Concat(PropTag(0x00FE, 0x6680), ruleAction);
        var item0 = RuleOrPermissionItem(0x01, taggedRuleAction);
        var op1 = Concat([0x41, 0x00, 0x00, 0x01], Le((ushort)1), item0);

        byte[] serverEntryId = [0xAA, 0xBB];
        var op2 = Concat([0x57, 0x00, 0x00], Le((ushort)serverEntryId.Length), serverEntryId, Le((ushort)0));
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);

        var actionBlock = Find(node1, "ActionBlock[0]");
        Assert.Equal("OP_OOF_REPLY", actionBlock.Value);
        Assert.Equal("99999999-9999-9999-9999-999999999999", Find(actionBlock.Children, "ReplyTemplateGUID").Value);
        Assert.Empty(FindAll(actionBlock.Children, "Malformed action data"));
        Assert.Empty(FindAll(actionBlock.Children, "Trailing action data"));

        var node2 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("AABB", Find(node2, "ServerEntryId").Value);
    }

    /// <summary>
    /// Regression proving OP_FORWARD/OP_DELEGATE's RecipientCount and each recipient's
    /// NoOfProperties use the 16-bit standard-rule (MS-OXCROPS ROP buffer) width, resolved against
    /// current MS-OXORULE rather than merely widening the pinned upstream parser's narrower reads,
    /// and that a nested PtypBinary property inside a recipient's property list uses the matching
    /// 16-bit standard-rule length prefix. Proves boundary progress with a following operation.
    /// </summary>
    [Theory]
    [InlineData((byte)0x07, "OP_FORWARD")]
    [InlineData((byte)0x08, "OP_DELEGATE")]
    public void ParsesRopModifyRulesRuleActionWithStandardForwardOrDelegateRecipientsAndProvesFollowingOperationBoundary(
        byte type, string expectedName)
    {
        var binaryValue = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var binaryTagged = Concat(PropTag(0x0102, 0x6684), Le((ushort)binaryValue.Length), binaryValue);
        var recipient = Concat(
            [0x00], // Reserved
            Le((ushort)2), // Recipient.NoOfProperties (16-bit, standard rule)
            TaggedValueInt32(0x3001, 42),
            binaryTagged);
        var actionData = Concat(Le((ushort)1), recipient); // RecipientCount (16-bit, standard rule)
        var ruleAction = RuleActionArray(ActionBlockRopBuffer(type, 0, 0, actionData));
        var taggedRuleAction = Concat(PropTag(0x00FE, 0x6680), ruleAction);
        var item0 = RuleOrPermissionItem(0x01, taggedRuleAction);
        var op1 = Concat([0x41, 0x00, 0x00, 0x01], Le((ushort)1), item0);

        byte[] serverEntryId = [0xAA, 0xBB];
        var op2 = Concat([0x57, 0x00, 0x00], Le((ushort)serverEntryId.Length), serverEntryId, Le((ushort)0));
        var buffer = Concat(op1, op2);

        var handles = new List<RopHandleReference>();
        var reader = NewReader(buffer);
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op1.Length, node1.Length);
        Assert.Equal(op1.Length, reader.Position);

        var actionBlock = Find(node1, "ActionBlock[0]");
        Assert.Equal(expectedName, actionBlock.Value);
        Assert.Equal(2, Find(actionBlock.Children, "RecipientCount").Length);
        Assert.Equal("1", Find(actionBlock.Children, "RecipientCount").Value);
        Assert.Equal(2, Find(actionBlock.Children, "NoOfProperties").Length);
        Assert.Equal("2", Find(actionBlock.Children, "NoOfProperties").Value);

        var longProperty = Find(actionBlock.Children, "PropertyValue[0]");
        Assert.Equal("42", Find(longProperty.Children, "PropertyValue").Value);
        var binaryProperty = Find(actionBlock.Children, "PropertyValue[1]");
        Assert.Equal($"{binaryValue.Length} bytes", Find(binaryProperty.Children, "PropertyValue").Value);

        Assert.Empty(FindAll(actionBlock.Children, "Malformed action data"));
        Assert.Empty(FindAll(actionBlock.Children, "Trailing action data"));

        var node2 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(op2.Length, node2.Length);
        Assert.True(reader.End);
        Assert.Equal("AABB", Find(node2, "ServerEntryId").Value);
    }

    [Fact]
    public void ParsesRopUpdateDeferredActionMessagesRequest()
    {
        byte[] serverEntryId = [0xAA, 0xBB, 0xCC];
        byte[] clientEntryId = [0x01, 0x02];
        var ropList = Concat(
            [0x57, 0x00, 0x00],
            Le((ushort)serverEntryId.Length), serverEntryId,
            Le((ushort)clientEntryId.Length), clientEntryId);
        var node = ParseRequest(ropList, 0x57, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("AABBCC", Find(node, "ServerEntryId").Value);
    }

    [Fact]
    public void ParsesRopModifyPermissionsRequest()
    {
        var item0 = RuleOrPermissionItem(0x02, TaggedValueInt32(0x6680, 99));
        var ropList = Concat([0x40, 0x00, 0x00, 0x03], Le((ushort)1), item0);
        var node = ParseRequest(ropList, 0x40, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("0x03 (ReplaceRows, IncludeFreeBusy)", Find(node, "ModifyFlags").Value);
    }

    // ---------------------------------------------------------------------------------------------
    // MSOXCNOTIF request.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopRegisterNotificationRequestWantingWholeStore()
    {
        var ropList = Concat([0x29, 0x00, 0x00, 0x01], Le((ushort)0x0010), [0x01]);
        var node = ParseRequest(ropList, 0x29, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("true", Find(node, "WantWholeStore").Value);
    }

    [Fact]
    public void ParsesRopRegisterNotificationRequestWithExtendedReservedByteAndExplicitFolder()
    {
        var ropList = Concat(
            [0x29, 0x00, 0x00, 0x01],
            Le((ushort)0x0410), // ObjectModified | Extended
            [0x00], // Reserved (present because Extended is set)
            [0x00], // WantWholeStore = false
            FolderOrMessageId(),
            FolderOrMessageId());
        var node = ParseRequest(ropList, 0x29, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("false", Find(node, "WantWholeStore").Value);
    }

    // ---------------------------------------------------------------------------------------------
    // MSOXCMSG responses.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopOpenMessageResponseOnSuccessWithNamedPropsAndOneRecipient()
    {
        var columns = new (ushort Type, ushort Id)[] { (0x0003, 0x0037) };
        var row = RecipientRow(columns, 7);
        var openRow = Concat([0x01], Le((ushort)0), Le((ushort)0), Le((ushort)row.Length), row);
        var ropList = Concat(
            [0x03, 0x00], Le((uint)0),
            [0x01], // HasNamedProperties
            TypedStringNone(),
            TypedStringAscii("Re: hi"),
            Le((ushort)1), // RecipientCount
            Le((ushort)1), PropTag(columns[0].Type, columns[0].Id), // ColumnCount + RecipientColumns
            [0x01], openRow); // RowCount + RecipientRows

        var node = ParseResponse(ropList, 0x03, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("Re: hi", Find(node, "NormalizedSubject").Value);
        var value0 = Find(node, "RecipientProperties").Children.First(c => c.Name == "Value[0]");
        Assert.Equal("7", value0.Children.First(c => c.Name == "Value").Value);
    }

    [Fact]
    public void ParsesRopOpenMessageResponseOnFailureWithNoTrailingBytes()
    {
        var ropList = Concat([0x03, 0x00], Le((uint)0x80000001));
        var node = ParseResponse(ropList, 0x03, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.DoesNotContain(node.Children, c => c.Name == "HasNamedProperties");
    }

    [Fact]
    public void ParsesRopCreateMessageResponseGatedTwoLevelsDeep()
    {
        var withId = Concat([0x06, 0x00], Le((uint)0), [0x01], FolderOrMessageId());
        var node = ParseResponse(withId, 0x06, new List<RopHandleReference>());
        Assert.Equal(withId.Length, node.Length);
        Assert.NotNull(Find(node, "MessageId"));

        var withoutId = Concat([0x06, 0x00], Le((uint)0), [0x00]);
        var node2 = ParseResponse(withoutId, 0x06, new List<RopHandleReference>());
        Assert.Equal(withoutId.Length, node2.Length);
        Assert.DoesNotContain(node2.Children, c => c.Name == "MessageId");
    }

    [Fact]
    public void ParsesRopSaveChangesMessageResponse()
    {
        var ropList = Concat([0x0C, 0x00], Le((uint)0), [0x03], FolderOrMessageId());
        var node = ParseResponse(ropList, 0x0C, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
    }

    [Fact]
    public void ParsesRopReadRecipientsResponseWithRawRecipientRow()
    {
        byte[] rawRow = [0xDE, 0xAD, 0xBE, 0xEF];
        var readRow = Concat(Le((uint)11), [0x01], Le((ushort)0), Le((ushort)0), Le((ushort)rawRow.Length), rawRow);
        var ropList = Concat([0x0F, 0x00], Le((uint)0), [0x01], readRow);
        var node = ParseResponse(ropList, 0x0F, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("DEADBEEF", Find(node, "RecipientRow (no RecipientColumns are carried by this response; retained as raw)").Value);
    }

    [Fact]
    public void ParsesRopReloadCachedInformationResponse()
    {
        var ropList = Concat(
            [0x10, 0x00], Le((uint)0),
            [0x00], TypedStringNone(), TypedStringNone(),
            Le((ushort)0), Le((ushort)0), [0x00]);
        var node = ParseResponse(ropList, 0x10, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
    }

    [Fact]
    public void ParsesRopGetMessageStatusResponse()
    {
        var ropList = Concat([0x1F, 0x00], Le((uint)0), Le((uint)0x00000002));
        var node = ParseResponse(ropList, 0x1F, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("0x00000002", Find(node, "MessageStatusFlags").Value);
    }

    [Fact]
    public void ParsesRopSetMessageStatusResponse()
    {
        var ropList = Concat([0x20, 0x00], Le((uint)0), Le((uint)0x00000004));
        var node = ParseResponse(ropList, 0x20, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
    }

    [Fact]
    public void ParsesRopCreateAttachmentResponse()
    {
        var ropList = Concat([0x23, 0x00], Le((uint)0), Le((uint)3));
        var node = ParseResponse(ropList, 0x23, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("3", Find(node, "AttachmentID").Value);
    }

    [Fact]
    public void ParsesRopOpenEmbeddedMessageResponse()
    {
        var ropList = Concat(
            [0x46, 0x00], Le((uint)0),
            [0x00], FolderOrMessageId(),
            [0x00], TypedStringNone(), TypedStringNone(),
            Le((ushort)0), Le((ushort)0), [0x00]);
        var node = ParseResponse(ropList, 0x46, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
    }

    [Fact]
    public void ParsesRopGetValidAttachmentsResponse()
    {
        var ropList = Concat([0x52, 0x00], Le((uint)0), Le((ushort)2), LeInt32(1), LeInt32(2));
        var node = ParseResponse(ropList, 0x52, new List<RopHandleReference>());
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal(2, Find(node, "AttachmentIdArray").Children.Length);
    }

    [Fact]
    public void ParsesRopSetReadFlagsResponseWithUnconditionalPartialCompletion()
    {
        var failure = Concat([0x66, 0x00], Le((uint)0x80000001), [0x01]);
        var node = ParseResponse(failure, 0x66, new List<RopHandleReference>());
        Assert.Equal(failure.Length, node.Length);
        Assert.Equal("true", Find(node, "PartialCompletion").Value);
    }

    // ---------------------------------------------------------------------------------------------
    // MSOXCNOTIF responses.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesRopPendingResponse()
    {
        var ropList = Concat([0x6E], Le((ushort)3));
        var reader = NewReader(ropList);
        var node = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Response, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("3", Find(node, "SessionIndex").Value);
    }

    [Fact]
    public void ParsesRopNotifyResponseForANonTableEvent()
    {
        var body = Concat(Le((ushort)0x0010), FolderOrMessageId(), Le((ushort)0xFFFF));
        var ropList = Concat([0x2A], Le((uint)0xAABBCCDD), [0x00], body);
        var reader = NewReader(ropList);
        var node = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Response, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("0xAABBCCDD", Find(node, "NotificationHandle").Value);
        Assert.Empty(FindAll(node.Children, "TableEventType"));
    }

    [Fact]
    public void ParsesRopNotifyResponseForATableRowAddedEventWithRawTableRowData()
    {
        byte[] rowData = [0x01, 0x02, 0x03];
        var body = Concat(
            Le((ushort)0x8100), // TableModified | M
            Le((ushort)3), // TableEventType = TableRowAdded
            FolderOrMessageId(), // TableRowFolderID
            FolderOrMessageId(), // TableRowMessageID
            Le((uint)0), // TableRowInstance
            FolderOrMessageId(), // InsertAfterTableRowFolderID
            FolderOrMessageId(), // InsertAfterTableRowID
            Le((uint)0), // InsertAfterTableRowInstance
            Le((ushort)rowData.Length), rowData);
        var ropList = Concat([0x2A], Le((uint)1), [0x00], body);
        var reader = NewReader(ropList);
        var node = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Response, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("TableRowAdded", Find(node, "TableEventType").Value);
        Assert.Equal(
            "010203",
            Find(node, "TableRowData (property columns require a prior RopSetColumns on this table; not decodable from this operation alone)").Value);
    }

    [Fact]
    public void ParsesRopNotifyResponseForNewMailWithAsciiMessageClass()
    {
        var body = Concat(
            Le((ushort)0x0002), // NewMail
            FolderOrMessageId(),
            Le((uint)0x00001000), // MessageFlags
            [0x00], // UnicodeFlag = ANSI
            AsciiZ("IPM.Note"));
        var ropList = Concat([0x2A], Le((uint)2), [0x01], body);
        var reader = NewReader(ropList);
        var node = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Response, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(ropList.Length, node.Length);
        Assert.Equal("IPM.Note", Find(node, "MessageClass").Value);
    }

    [Fact]
    public void ParsesRopNotifyResponseForMovedMessageWithOldIds()
    {
        var body = Concat(
            Le((ushort)(0x0020 | 0x8000)), // ObjectMoved | M
            FolderOrMessageId(), // FolderId (notModifiedExtended)
            FolderOrMessageId(), // MessageId
            FolderOrMessageId(), // OldFolderId
            FolderOrMessageId()); // OldMessageId
        var ropList = Concat([0x2A], Le((uint)3), [0x00], body);
        var reader = NewReader(ropList);
        var node = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Response, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None);
        Assert.Equal(ropList.Length, node.Length);
        Assert.NotNull(Find(node, "OldFolderId"));
        Assert.NotNull(Find(node, "OldMessageId"));
    }

    // ---------------------------------------------------------------------------------------------
    // Malformed / truncated input hardening.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ThrowsWhenRopOpenMessageRequestIsTruncatedBeforeFolderId()
    {
        var ropList = new byte[] { 0x03, 0x00, 0x00, 0x01, 0x00, 0x00 }; // CodePageId present, FolderId missing
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsWhenRecipientRowSizeClaimsMoreBytesThanRemain()
    {
        var ropList = Concat(
            [0x0F, 0x00], Le((uint)0),
            [0x01],
            Le((uint)1), [0x00], Le((ushort)0), Le((ushort)0),
            Le((ushort)500)); // RecipientRowSize claims 500 bytes that do not exist
        ExpectMapiParseException(ropList, MapiDirection.Response);
    }

    [Fact]
    public void ThrowsWhenRecipientRowUnderConsumesItsDeclaredSize()
    {
        // RecipientColumnCount says 0 (so PropertyRow ends after its Flag byte), but
        // RecipientRowSize reserves 10 bytes - the slice is not fully consumed.
        var row = Concat([0x00, 0x00], Le((ushort)0), new byte[] { 0x00 });
        var padded = Concat(row, new byte[10 - row.Length]);
        var ropList = Concat(
            [0x0E, 0x00, 0x00],
            Le((ushort)0), // ColumnCount
            Le((ushort)1), // RowCount
            Le((uint)1), [0x00], Le((ushort)padded.Length), padded);
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsWhenRecipientRowDeclaresMoreColumnsThanWereProvidedForTheOperation()
    {
        var row = Concat([0x00, 0x00], Le((ushort)5), new byte[] { 0x00 }); // claims 5 columns; none were declared
        var ropList = Concat(
            [0x0E, 0x00, 0x00],
            Le((ushort)0), // ColumnCount = 0
            Le((ushort)1), // RowCount
            Le((uint)1), [0x00], Le((ushort)row.Length), row);
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsForAnInvalidPropertyRowFlagByte()
    {
        var row = Concat([0x00, 0x00], Le((ushort)0), new byte[] { 0x02 }); // Flag=0x02 is invalid
        var ropList = Concat(
            [0x0E, 0x00, 0x00],
            Le((ushort)0),
            Le((ushort)1),
            Le((uint)1), [0x00], Le((ushort)row.Length), row);
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsForAnInvalidTypedStringTypeByte()
    {
        var ropList = Concat([0x03, 0x00], Le((uint)0), [0x01], new byte[] { 0x7F });
        ExpectMapiParseException(ropList, MapiDirection.Response);
    }

    [Fact]
    public void ThrowsWhenModifyRulesPropertyValueCountExceedsAvailableBytes()
    {
        // PropertyValueCount = 5000, but no TaggedPropertyValue bytes follow at all.
        var ropList = Concat([0x41, 0x00, 0x00, 0x01], Le((ushort)1), [0x01], Le((ushort)5000));
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    [Fact]
    public void ThrowsForAnUnsupportedPropertyTypeInsideATaggedPropertyValue()
    {
        // Property type 0x00FF has no defined fixed/variable-length rule in the shared value parser.
        var item = RuleOrPermissionItem(0x01, Concat(PropTag(0x00FF, 0x6680)));
        var ropList = Concat([0x40, 0x00, 0x00, 0x01], Le((ushort)1), item);
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    // ---------------------------------------------------------------------------------------------
    // Multi-operation boundary correctness: consecutive operations parsed back-to-back over one
    // shared reader must never overrun into (or stop short of) the next operation's first byte.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ParsesTwoChainedOperationsWithDistinctVariableWidthsAtExactBoundaries()
    {
        var first = Concat([0x21, 0x00, 0x00, 0x01, 0x09]); // RopGetAttachmentTable request
        var second = Concat([0x22, 0x00, 0x00, 0x01, 0x00], Le((uint)77)); // RopOpenAttachment request
        var sentinel = new byte[] { 0xEE, 0xEE };
        var ropList = Concat(first, second, sentinel);

        var reader = NewReader(ropList);
        var handles = new List<RopHandleReference>();
        var budget = new MapiNodeBudget();
        var node0 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, budget, CancellationToken.None);
        Assert.Equal(first.Length, node0.Length);
        Assert.Equal(first.Length, reader.LocalPosition);

        var node1 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Request, handles, budget, CancellationToken.None);
        Assert.Equal(second.Length, node1.Length);
        Assert.Equal(first.Length + second.Length, reader.LocalPosition);
        Assert.Equal("77", Find(node1, "AttachmentID").Value);

        Assert.Equal(sentinel.Length, reader.Remaining);
    }

    [Fact]
    public void ParsesThreeChainedResponsesIncludingAGatedVariableWidthResponseAtExactBoundaries()
    {
        var first = Concat([0x23, 0x00], Le((uint)0), Le((uint)5)); // RopCreateAttachment success
        var second = Concat([0x23, 0x01], Le((uint)0x80000001)); // RopCreateAttachment failure: shorter
        var third = Concat([0x6E], Le((ushort)9)); // RopPending
        var ropList = Concat(first, second, third);

        var reader = NewReader(ropList);
        var handles = new List<RopHandleReference>();
        var budget = new MapiNodeBudget();
        var node0 = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Response, handles, budget, CancellationToken.None);
        Assert.Equal(first.Length, node0.Length);
        var node1 = RopMessageRulesDecoders.Parse(ref reader, 1, MapiDirection.Response, handles, budget, CancellationToken.None);
        Assert.Equal(second.Length, node1.Length);
        var node2 = RopMessageRulesDecoders.Parse(ref reader, 2, MapiDirection.Response, handles, budget, CancellationToken.None);
        Assert.Equal(third.Length, node2.Length);
        Assert.True(reader.End);
    }

    // ---------------------------------------------------------------------------------------------
    // Hostile/injection data: oversized declared counts must fail fast (bounded), never hang or
    // allocate unboundedly, and must never silently desynchronize the boundary.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RejectsAHostileColumnCountThatExceedsTheCollectionCountLimit()
    {
        // 0x0186A1 as a u16 cannot be represented (max ushort 65535), so instead exercise a
        // ushort-max ColumnCount (65535) against a tiny buffer: it must fail fast on the very
        // first PropertyTag read rather than attempting to allocate anything close to that count.
        var ropList = Concat([0x0E, 0x00, 0x00], Le(ushort.MaxValue));
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    [Fact]
    public void RejectsAHostileRowCountThatWouldReadWellPastTheBuffer()
    {
        // RowCount (a single byte for RopReadRecipients response) claims 255 rows, zero bytes follow.
        var ropList = Concat([0x0F, 0x00], Le((uint)0), new byte[] { 0xFF });
        ExpectMapiParseException(ropList, MapiDirection.Response);
    }

    [Fact]
    public void RejectsAHostileNotificationTagCountThatExceedsRemainingBytes()
    {
        // ObjectModified alone takes the notModifiedExtended + isCreateModify path (no ParentFolderId
        // is required), so TagCount is the very next field after FolderId - a huge declared TagCount
        // with nothing behind it must fail fast rather than attempt anything close to that allocation.
        var body = Concat(Le((ushort)0x0010), FolderOrMessageId(), Le((ushort)40000));
        var ropList = Concat([0x2A], Le((uint)1), [0x00], body);
        ExpectMapiParseException(ropList, MapiDirection.Response);
    }

    [Fact]
    public void RejectsInjectedTrailingGarbageMasqueradingAsAnotherOperationWithinARecipientRowSlice()
    {
        // A crafted RecipientRow that, if naively over-read, would spill into bytes belonging to
        // "the next operation". The strict Slice+End check must catch the under/over consumption
        // rather than silently accepting a misaligned parse.
        var maliciousRow = Concat([0x00, 0x00], Le((ushort)0), new byte[] { 0x00 }, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }); // trailing junk inside the declared size
        var ropList = Concat(
            [0x0E, 0x00, 0x00],
            Le((ushort)0),
            Le((ushort)1),
            Le((uint)1), [0x00], Le((ushort)maliciousRow.Length), maliciousRow,
            [0xDE, 0xAD]); // sentinel bytes for a hypothetical next operation
        ExpectMapiParseException(ropList, MapiDirection.Request);
    }

    // ---------------------------------------------------------------------------------------------
    // Byte-building helpers.
    // ---------------------------------------------------------------------------------------------

    private static MapiReader NewReader(byte[] bytes) => new(bytes, CancellationToken.None, 0);

    /// <summary>
    /// Drives <see cref="RopMessageRulesDecoders.Parse"/> and asserts it throws
    /// <see cref="MapiParseException"/>, without capturing the ref-struct <see cref="MapiReader"/>
    /// inside a lambda (which the C# compiler forbids for ref-struct locals).
    /// </summary>
    private static MapiParseException ExpectMapiParseException(byte[] ropList, MapiDirection direction)
    {
        var reader = NewReader(ropList);
        try
        {
            RopMessageRulesDecoders.Parse(ref reader, 0, direction, new List<RopHandleReference>(), new MapiNodeBudget(), CancellationToken.None);
        }
        catch (MapiParseException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("Expected a MapiParseException to be thrown, but Parse completed without one.");
    }

    private static MapiNode ParseRequest(byte[] ropList, byte expectedRopId, List<RopHandleReference> handles)
    {
        var reader = NewReader(ropList);
        Assert.True(RopMessageRulesDecoders.Supports(MapiDirection.Request, expectedRopId));
        var node = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Request, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.True(reader.End);
        return node;
    }

    private static MapiNode ParseResponse(byte[] ropList, byte expectedRopId, List<RopHandleReference> handles)
    {
        var reader = NewReader(ropList);
        Assert.True(RopMessageRulesDecoders.Supports(MapiDirection.Response, expectedRopId));
        var node = RopMessageRulesDecoders.Parse(ref reader, 0, MapiDirection.Response, handles, new MapiNodeBudget(), CancellationToken.None);
        Assert.True(reader.End);
        return node;
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

    private static byte[] LeInt32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] AsciiZ(string s) => Concat(Encoding.ASCII.GetBytes(s), [0]);

    private static byte[] FolderOrMessageId(ushort replicaId = 1, ulong counter = 0x0102030405)
    {
        var counterBytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            counterBytes[i] = (byte)(counter >> (8 * i));
        }
        return Concat(Le(replicaId), counterBytes);
    }

    private static byte[] PropTag(ushort type, ushort id) => Concat(Le(type), Le(id));

    private static byte[] TypedStringNone() => [0x00];

    private static byte[] TypedStringAscii(string s) => Concat([0x02], AsciiZ(s));

    private static byte[] TaggedValueInt32(ushort id, int value) => Concat(PropTag(0x0003, id), LeInt32(value));

    private static byte[] RuleOrPermissionItem(byte flags, params byte[][] taggedValues)
    {
        var bytes = new List<byte> { flags };
        bytes.AddRange(Le((ushort)taggedValues.Length));
        foreach (var tv in taggedValues)
        {
            bytes.AddRange(tv);
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// Builds a standard-rule (MS-OXCROPS ROP buffer) RuleAction structure: a 16-bit NoOfActions
    /// count followed by the given ActionBlocks, matching <c>MapiWireWidthContext.RopBuffer</c>.
    /// </summary>
    private static byte[] RuleActionArray(params byte[][] actionBlocks) =>
        Concat(Le((ushort)actionBlocks.Length), Concat(actionBlocks));

    /// <summary>
    /// Builds a single standard-rule (16-bit ActionLength) ActionBlock: ActionLength + ActionType +
    /// ActionFlavor + ActionFlags + ActionData.
    /// </summary>
    private static byte[] ActionBlockRopBuffer(byte type, uint flavor, uint flags, byte[] actionData)
    {
        var body = Concat([type], Le(flavor), Le(flags), actionData);
        return Concat(Le((ushort)body.Length), body);
    }

    /// <summary>A syntactically valid 46-byte [MS-OXCDATA] 2.2.4.1 Folder EntryID.</summary>
    private static byte[] FolderEntryIdBytes() => Concat(
        new byte[4], // Flags = 0
        Enumerable.Repeat((byte)0x11, 16).ToArray(), // ProviderUID
        Le((ushort)1), // FolderType
        Enumerable.Repeat((byte)0x22, 16).ToArray(), // DatabaseGuid
        Enumerable.Repeat((byte)0x33, 6).ToArray(), // GlobalCounter
        new byte[2]); // Pad = 0

    /// <summary>A syntactically valid 21-byte [MS-OXORULE] 2.2.5.1.2.1.1 ServerEid.</summary>
    private static byte[] ServerEidBytes() => Concat(
        [0x01], // Ours = true
        FolderOrMessageId(1, 0x0102030405), // FolderId
        new byte[8], // MessageId, reserved
        new byte[4]); // Instance, reserved

    /// <summary>
    /// A minimal RecipientRow: NoType address, no optional strings, then a ROP PropertyRow (all
    /// values unflagged/present) built from Int32 values against the given columns.
    /// </summary>
    private static byte[] RecipientRow((ushort Type, ushort Id)[] columns, params int[] values)
    {
        var bytes = new List<byte> { 0x00, 0x00 }; // RecipientFlags: NoType, no R/S/T/D/E/O/I/U/N
        bytes.AddRange(Le((ushort)columns.Length));
        bytes.Add(0x00); // PropertyRow.Flag = all values present
        foreach (var value in values)
        {
            bytes.AddRange(LeInt32(value));
        }
        return bytes.ToArray();
    }

    private static MapiNode Find(MapiNode root, string name) => Find(new[] { root }, name);

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
