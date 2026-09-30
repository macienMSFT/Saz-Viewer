using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SazViewer.Core;

internal static class RopBufferParser
{
    public static ImmutableArray<MapiNode> Parse(
        ReadOnlySpan<byte> decoded,
        long absoluteOffset,
        MapiDirection direction,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        FastTransferStreamAssembler? fastTransferAssembler = null,
        string? captureScope = null,
        MapiCaptureContext? context = null)
    {
        var nodes = ImmutableArray.CreateBuilder<MapiNode>();
        var reader = new MapiReader(decoded, cancellationToken, checked((int)absoluteOffset));
        if (reader.Remaining < 2)
        {
            warnings.Add("Decoded ROP buffer is shorter than its two-byte RopSize field.");
            nodes.Add(ExtendedBufferParser.RawNode("Truncated ROP buffer", decoded, absoluteOffset, budget));
            return nodes.ToImmutable();
        }

        var sizeOffset = reader.Position;
        var ropSize = reader.ReadUInt16("RopSize");
        ExtendedBufferParser.AddField(nodes, "RopSize", sizeOffset, 2, ropSize.ToString(), budget);
        if (ropSize < 2 || ropSize - 2 > reader.Remaining)
        {
            warnings.Add($"ROP buffer declares RopSize {ropSize}, which is outside the decoded payload.");
            var malformedOffset = reader.Position;
            nodes.Add(ExtendedBufferParser.RawNode(
                "Malformed ROP payload",
                reader.ReadRemaining("malformed ROP payload"),
                malformedOffset,
                budget));
            return nodes.ToImmutable();
        }

        var listOffset = reader.Position;
        var ropList = reader.ReadBytes(ropSize - 2, "RopsList");
        var malformedHandleTable = (reader.Remaining & 3) != 0;
        if (malformedHandleTable)
        {
            context?.ClearSessionHandles(captureScope);
            warnings.Add($"Server object handle table has {reader.Remaining} bytes, which is not divisible by four.");
        }
        else if (context is not null)
        {
            var values = ImmutableArray.CreateBuilder<uint>(reader.Remaining / sizeof(uint));
            var handleBytes = decoded.Slice(reader.LocalPosition, reader.Remaining);
            for (var offset = 0; offset < handleBytes.Length; offset += sizeof(uint))
            {
                values.Add(BinaryPrimitives.ReadUInt32LittleEndian(handleBytes[offset..]));
            }
            context.RecordSessionHandles(captureScope, values.ToImmutable());
        }
        var handleReferences = new List<RopHandleReference>();
        if (!ropList.IsEmpty)
        {
            var operations = RopSemanticParser.ParseOperations(
                ropList,
                listOffset,
                direction,
                warnings,
                budget,
                handleReferences,
                cancellationToken,
                fastTransferAssembler,
                captureScope,
                context);
            budget.Claim(0);
            nodes.Add(new MapiNode(
                "ROP list",
                MapiNodeKind.Array,
                listOffset,
                ropList.Length,
                direction.ToString(),
                operations));
        }

        var handlesOffset = reader.Position;
        if (malformedHandleTable)
        {
            nodes.Add(ExtendedBufferParser.RawNode(
                "Malformed server object handle table",
                reader.ReadRemaining("handle table"),
                handlesOffset,
                budget));
            return nodes.ToImmutable();
        }

        var handles = ImmutableArray.CreateBuilder<MapiNode>();
        var index = 0;
        while (!reader.End)
        {
            var offset = reader.Position;
            var value = reader.ReadUInt32($"ServerObjectHandle[{index}]");
            ExtendedBufferParser.AddField(handles, $"[{index}]", offset, 4, $"0x{value:X8}", budget);
            index++;
        }
        budget.Claim(0);
        nodes.Add(new MapiNode(
            "Server object handle table",
            MapiNodeKind.Array,
            handlesOffset,
            reader.Position - handlesOffset,
            null,
            handles.ToImmutable()));

        // Handle table preservation: the table above is parsed and emitted exactly as before; this
        // pass only adds warnings when a decoded operation's handle-index field pointed outside the
        // table's actual entry count. It never rewrites or reorders the table itself.
        foreach (var reference in handleReferences)
        {
            if (reference.Index >= index)
            {
                warnings.Add(
                    $"ROP list operation {reference.OperationIndex} field {reference.FieldName} references " +
                    $"server object handle index {reference.Index}, but the handle table has only {index} entr{(index == 1 ? "y" : "ies")}.");
            }
        }

        return nodes.ToImmutable();
    }

    /// <summary>
    /// True when <paramref name="value"/> maps to a named RopId, whether or not a fixed-width schema
    /// for it has been implemented in <see cref="RopSemanticParser"/>.
    /// </summary>
    internal static bool IsKnownRopId(byte value) => Name(value) != "Unknown ROP";

    internal static string Name(byte value) => value switch
    {
        0x01 => "RopRelease",
        0x02 => "RopOpenFolder",
        0x03 => "RopOpenMessage",
        0x04 => "RopGetHierarchyTable",
        0x05 => "RopGetContentsTable",
        0x06 => "RopCreateMessage",
        0x07 => "RopGetPropertiesSpecific",
        0x08 => "RopGetPropertiesAll",
        0x09 => "RopGetPropertiesList",
        0x0A => "RopSetProperties",
        0x0B => "RopDeleteProperties",
        0x0C => "RopSaveChangesMessage",
        0x0D => "RopRemoveAllRecipients",
        0x0E => "RopModifyRecipients",
        0x0F => "RopReadRecipients",
        0x10 => "RopReloadCachedInformation",
        0x11 => "RopSetMessageReadFlag",
        0x12 => "RopSetColumns",
        0x13 => "RopSortTable",
        0x14 => "RopRestrict",
        0x15 => "RopQueryRows",
        0x16 => "RopGetStatus",
        0x17 => "RopQueryPosition",
        0x18 => "RopSeekRow",
        0x19 => "RopSeekRowBookmark",
        0x1A => "RopSeekRowFractional",
        0x1B => "RopCreateBookmark",
        0x1C => "RopCreateFolder",
        0x1D => "RopDeleteFolder",
        0x1E => "RopDeleteMessages",
        0x1F => "RopGetMessageStatus",
        0x20 => "RopSetMessageStatus",
        0x21 => "RopGetAttachmentTable",
        0x22 => "RopOpenAttachment",
        0x23 => "RopCreateAttachment",
        0x24 => "RopDeleteAttachment",
        0x25 => "RopSaveChangesAttachment",
        0x26 => "RopSetReceiveFolder",
        0x27 => "RopGetReceiveFolder",
        0x29 => "RopRegisterNotification",
        0x2A => "RopNotify",
        0x2B => "RopOpenStream",
        0x2C => "RopReadStream",
        0x2D => "RopWriteStream",
        0x2E => "RopSeekStream",
        0x2F => "RopSetStreamSize",
        0x30 => "RopSetSearchCriteria",
        0x31 => "RopGetSearchCriteria",
        0x32 => "RopSubmitMessage",
        0x33 => "RopMoveCopyMessages",
        0x34 => "RopAbortSubmit",
        0x35 => "RopMoveFolder",
        0x36 => "RopCopyFolder",
        0x37 => "RopQueryColumnsAll",
        0x38 => "RopAbort",
        0x39 => "RopCopyTo",
        0x3A => "RopCopyToStream",
        0x3B => "RopCloneStream",
        0x3E => "RopGetPermissionsTable",
        0x3F => "RopGetRulesTable",
        0x40 => "RopModifyPermissions",
        0x41 => "RopModifyRules",
        0x42 => "RopGetOwningServers",
        0x43 => "RopLongTermIdFromId",
        0x44 => "RopIdFromLongTermId",
        0x45 => "RopPublicFolderIsGhosted",
        0x46 => "RopOpenEmbeddedMessage",
        0x47 => "RopSetSpooler",
        0x48 => "RopSpoolerLockMessage",
        0x49 => "RopGetAddressTypes",
        0x4A => "RopTransportSend",
        0x4B => "RopFastTransferSourceCopyMessages",
        0x4C => "RopFastTransferSourceCopyFolder",
        0x4D => "RopFastTransferSourceCopyTo",
        0x4E => "RopFastTransferSourceGetBuffer",
        0x4F => "RopFindRow",
        0x50 => "RopProgress",
        0x51 => "RopTransportNewMail",
        0x52 => "RopGetValidAttachments",
        0x53 => "RopFastTransferDestinationConfigure",
        0x54 => "RopFastTransferDestinationPutBuffer",
        0x55 => "RopGetNamesFromPropertyIds",
        0x56 => "RopGetPropertyIdsFromNames",
        0x57 => "RopUpdateDeferredActionMessages",
        0x58 => "RopEmptyFolder",
        0x59 => "RopExpandRow",
        0x5A => "RopCollapseRow",
        0x5B => "RopLockRegionStream",
        0x5C => "RopUnlockRegionStream",
        0x5D => "RopCommitStream",
        0x5E => "RopGetStreamSize",
        0x5F => "RopQueryNamedProperties",
        0x60 => "RopGetPerUserLongTermIds",
        0x61 => "RopGetPerUserGuid",
        0x63 => "RopReadPerUserInformation",
        0x64 => "RopWritePerUserInformation",
        0x66 => "RopSetReadFlags",
        0x67 => "RopCopyProperties",
        0x68 => "RopGetReceiveFolderTable",
        0x69 => "RopFastTransferSourceCopyProperties",
        0x6B => "RopGetCollapseState",
        0x6C => "RopSetCollapseState",
        0x6D => "RopGetTransportFolder",
        0x6E => "RopPending",
        0x6F => "RopOptionsData",
        0x70 => "RopSynchronizationConfigure",
        0x72 => "RopSynchronizationImportMessageChange",
        0x73 => "RopSynchronizationImportHierarchyChange",
        0x74 => "RopSynchronizationImportDeletes",
        0x75 => "RopSynchronizationUploadStateStreamBegin",
        0x76 => "RopSynchronizationUploadStateStreamContinue",
        0x77 => "RopSynchronizationUploadStateStreamEnd",
        0x78 => "RopSynchronizationImportMessageMove",
        0x79 => "RopSetPropertiesNoReplicate",
        0x7A => "RopDeletePropertiesNoReplicate",
        0x7B => "RopGetStoreState",
        0x7E => "RopSynchronizationOpenCollector",
        0x7F => "RopGetLocalReplicaIds",
        0x80 => "RopSynchronizationImportReadStateChanges",
        0x81 => "RopResetTable",
        0x82 => "RopSynchronizationGetTransferState",
        0x86 => "RopTellVersion",
        0x89 => "RopFreeBookmark",
        0x90 => "RopWriteAndCommitStream",
        0x91 => "RopHardDeleteMessages",
        0x92 => "RopHardDeleteMessagesAndSubfolders",
        0x93 => "RopSetLocalReplicaMidsetDeleted",
        0x9D => "RopFastTransferDestinationPutBufferExtended",
        0xA3 => "RopWriteStreamExtended",
        0xF9 => "RopBackoff",
        0xFE => "RopLogon",
        0xFF => "RopBufferTooSmall",
        _ => "Unknown ROP"
    };
}
