using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

/// <summary>
/// Self-contained semantic ROP decoders for the [MS-OXCFOLD] (Folder object) and [MS-OXCTABL]
/// (Table object) protocol families, covering every request/response ROP in those two families
/// whose exact byte width can be determined purely from bytes already read within the same
/// operation, that is not already covered by <c>RopSemanticParser</c>'s fixed-width catalog.
///
/// Wire layouts are taken directly from the pinned upstream reference implementation (see
/// docs/mapi-parity.json for the exact repository/commit), specifically every
/// <c>MAPIInspector/Source/Parsers/MSOXCFOLD/rops/Rop*.cs</c> and
/// <c>MAPIInspector/Source/Parsers/MSOXCTABL/rops/Rop*.cs</c> Request/Response pair, cross-checked
/// against [MS-OXCFOLD], [MS-OXCTABL], [MS-OXCDATA] (FolderID/MessageID/PropertyTag/restriction
/// structures), and [MS-OXCROPS] (buffer framing).
///
/// Row-bearing table responses (RopQueryRows, RopFindRow, RopExpandRow) embed a PropertyRow whose
/// exact byte width depends on the property list established by a *different* (earlier)
/// RopSetColumns operation. No cross-operation/table-column state is threaded into this decoder
/// (per-operation, no global state), so those three responses decode their deterministic prefix
/// (handle/ReturnValue/Origin/RowCount or RowNoLongerVisible+HasRowData) and, only when the actual
/// captured bytes indicate row data truly follows (RowCount != 0, or HasRowData == true), throw
/// <see cref="MapiParseException"/> instead of guessing a boundary. That exception is caught by the
/// (future) caller's existing transactional per-operation fallback - identical to how any other
/// malformed operation is handled - so every failure/empty-result instance of these three ROPs is
/// still fully decoded, and only the genuinely indeterminate instances fall back to raw.
///
/// <c>RopVariableDispatcher</c> routes supported folder/table operations here after the fixed
/// semantic catalog declines them. The same <c>Supports</c>/<c>Parse</c> contract remains directly
/// unit-testable.
/// </summary>
internal static class RopFolderTableDecoders
{
    // MS-OXCDATA 2.4.1: the only AdditionalErrorCodes value that changes a folder/message
    // move-or-copy response's shape (an extra DestHandleIndex field before PartialCompletion).
    private const uint NullDestinationObject = 0x00000503;

    private static readonly ImmutableHashSet<byte> RequestRopIds = ImmutableHashSet.Create<byte>(
        // MSOXCFOLD
        0x02, // RopOpenFolder
        0x1C, // RopCreateFolder
        0x1E, // RopDeleteMessages
        0x30, // RopSetSearchCriteria
        0x31, // RopGetSearchCriteria
        0x33, // RopMoveCopyMessages
        0x35, // RopMoveFolder
        0x36, // RopCopyFolder
        0x91, // RopHardDeleteMessages
        0x92, // RopHardDeleteMessagesAndSubfolders
        // MSOXCTABL
        0x12, // RopSetColumns
        0x13, // RopSortTable
        0x14, // RopRestrict
        0x15, // RopQueryRows
        0x18, // RopSeekRow
        0x19, // RopSeekRowBookmark
        0x4F, // RopFindRow
        0x6C, // RopSetCollapseState
        0x89); // RopFreeBookmark

    private static readonly ImmutableHashSet<byte> ResponseRopIds = ImmutableHashSet.Create<byte>(
        // MSOXCFOLD
        0x02, // RopOpenFolder
        0x1C, // RopCreateFolder
        0x1D, // RopDeleteFolder
        0x1E, // RopDeleteMessages
        0x33, // RopMoveCopyMessages
        0x35, // RopMoveFolder
        0x36, // RopCopyFolder
        0x58, // RopEmptyFolder
        0x91, // RopHardDeleteMessages
        0x92, // RopHardDeleteMessagesAndSubfolders
        0x05, // RopGetContentsTable
        0x04, // RopGetHierarchyTable
        0x31, // RopGetSearchCriteria
        // MSOXCTABL
        0x38, // RopAbort
        0x12, // RopSetColumns
        0x13, // RopSortTable
        0x14, // RopRestrict
        0x18, // RopSeekRow
        0x19, // RopSeekRowBookmark
        0x1B, // RopCreateBookmark
        0x37, // RopQueryColumnsAll
        0x59, // RopExpandRow (deterministic prefix only; throws when rows are actually present)
        0x4F, // RopFindRow (deterministic prefix only; throws when a row is actually present)
        0x15, // RopQueryRows (deterministic prefix only; throws when rows are actually present)
        0x6B, // RopGetCollapseState
        0x6C, // RopSetCollapseState
        0x5A); // RopCollapseRow

    internal static bool Supports(MapiDirection direction, byte ropId) =>
        (direction == MapiDirection.Request ? RequestRopIds : ResponseRopIds).Contains(ropId);

    internal static MapiNode Parse(
        ref MapiReader reader,
        int operationIndex,
        MapiDirection direction,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = reader.Position;
        var ropIdOffset = reader.Position;
        var ropId = reader.ReadByte("RopId");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(
            children, "RopId", ropIdOffset, 1, $"0x{ropId:X2} ({RopBufferParser.Name(ropId)})", budget);

        if (direction == MapiDirection.Request)
        {
            ParseRequest(ref reader, ropId, operationIndex, handleReferences, budget, children);
        }
        else
        {
            ParseResponse(ref reader, ropId, operationIndex, handleReferences, budget, children);
        }

        budget.Claim(0);
        return new MapiNode(
            $"Operation {operationIndex}",
            MapiNodeKind.Operation,
            start,
            reader.Position - start,
            $"{RopBufferParser.Name(ropId)} (0x{ropId:X2})",
            children.ToImmutable());
    }

    // -------------------------------------------------------------------------------------------
    // Request dispatch
    // -------------------------------------------------------------------------------------------

    private static void ParseRequest(
        ref MapiReader reader,
        byte ropId,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        ImmutableArray<MapiNode>.Builder children)
    {
        switch (ropId)
        {
            case 0x02: // RopOpenFolder
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "OutputHandleIndex", operationIndex, handleReferences, budget);
                AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                AddByteField(ref reader, children, "OpenModeFlags", budget);
                return;
            case 0x1C: // RopCreateFolder
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "OutputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "FolderType", budget);
                var useUnicode = AddBoolField(ref reader, children, "UseUnicodeStrings", budget);
                AddBoolField(ref reader, children, "OpenExisting", budget);
                AddByteField(ref reader, children, "Reserved", budget);
                AddNullTerminatedStringField(ref reader, children, "DisplayName", useUnicode, budget);
                AddNullTerminatedStringField(ref reader, children, "Comment", useUnicode, budget);
                return;
            }
            case 0x1E: // RopDeleteMessages
            case 0x91: // RopHardDeleteMessages
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddBoolField(ref reader, children, "WantAsynchronous", budget);
                AddBoolField(ref reader, children, "NotifyNonRead", budget);
                AddCountedMessageIds(ref reader, children, "MessageIdCount", "MessageIds", budget);
                return;
            case 0x92: // RopHardDeleteMessagesAndSubfolders
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddBoolField(ref reader, children, "WantAsynchronous", budget);
                AddBoolField(ref reader, children, "WantDeleteAssociated", budget);
                return;
            case 0x30: // RopSetSearchCriteria
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddRestriction(ref reader, children, "RestrictionDataSize", "RestrictionData", budget);
                AddCountedFolderIds(ref reader, children, "FolderIdCount", "FolderIds", budget);
                AddUInt32Field(ref reader, children, "SearchFlags", budget, hex: true);
                return;
            case 0x31: // RopGetSearchCriteria
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddBoolField(ref reader, children, "UseUnicode", budget);
                AddBoolField(ref reader, children, "IncludeRestriction", budget);
                AddBoolField(ref reader, children, "IncludeFolders", budget);
                return;
            case 0x33: // RopMoveCopyMessages
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "DestHandleIndex", operationIndex, handleReferences, budget);
                AddCountedMessageIds(ref reader, children, "MessageIdCount", "MessageIds", budget);
                AddBoolField(ref reader, children, "WantAsynchronous", budget);
                AddBoolField(ref reader, children, "WantCopy", budget);
                return;
            case 0x35: // RopMoveFolder
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "DestHandleIndex", operationIndex, handleReferences, budget);
                AddBoolField(ref reader, children, "WantAsynchronous", budget);
                var useUnicode = AddBoolField(ref reader, children, "UseUnicode", budget);
                AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                AddNullTerminatedStringField(ref reader, children, "NewFolderName", useUnicode, budget);
                return;
            }
            case 0x36: // RopCopyFolder
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "DestHandleIndex", operationIndex, handleReferences, budget);
                AddBoolField(ref reader, children, "WantAsynchronous", budget);
                AddBoolField(ref reader, children, "WantRecursive", budget);
                var useUnicode = AddBoolField(ref reader, children, "UseUnicode", budget);
                AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                AddNullTerminatedStringField(ref reader, children, "NewFolderName", useUnicode, budget);
                return;
            }
            case 0x12: // RopSetColumns
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "SetColumnsFlags", budget);
                AddCountedPropertyTags(ref reader, children, "PropertyTagCount", "PropertyTags", budget);
                return;
            case 0x13: // RopSortTable
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "SortTableFlags", budget);
                var countOffset = reader.Position;
                var count = reader.ReadCount16("SortOrderCount");
                ExtendedBufferParser.AddField(
                    children, "SortOrderCount", countOffset, 2, count.ToString(CultureInfo.InvariantCulture), budget);
                AddUInt16Field(ref reader, children, "CategoryCount", budget);
                AddUInt16Field(ref reader, children, "ExpandedCount", budget);
                var array = ImmutableArray.CreateBuilder<MapiNode>(count);
                for (var i = 0; i < count; i++)
                {
                    array.Add(ReadSortOrder(ref reader, $"SortOrders[{i}]", budget));
                }
                budget.Claim(0, count);
                children.Add(new MapiNode(
                    "SortOrders", MapiNodeKind.Array, array.Count == 0 ? reader.Position : array[0].Offset,
                    array.Sum(n => n.Length), $"{count:N0} entrie(s)", array.ToImmutable()));
                return;
            }
            case 0x14: // RopRestrict
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "RestrictFlags", budget);
                AddRestriction(ref reader, children, "RestrictionDataSize", "RestrictionData", budget);
                return;
            case 0x15: // RopQueryRows
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "QueryRowsFlags", budget);
                AddBoolField(ref reader, children, "ForwardRead", budget);
                AddUInt16Field(ref reader, children, "RowCount", budget);
                return;
            case 0x18: // RopSeekRow
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "Origin", budget);
                AddInt32Field(ref reader, children, "RowCount", budget);
                AddBoolField(ref reader, children, "WantRowMovedCount", budget);
                return;
            case 0x19: // RopSeekRowBookmark
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddLengthPrefixedBytes(ref reader, children, "BookmarkSize", "Bookmark", budget);
                AddInt32Field(ref reader, children, "RowCount", budget);
                AddBoolField(ref reader, children, "WantRowMovedCount", budget);
                return;
            case 0x4F: // RopFindRow
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "FindRowFlags", budget);
                AddRestriction(ref reader, children, "RestrictionDataSize", "RestrictionData", budget);
                AddByteField(ref reader, children, "Origin", budget);
                AddLengthPrefixedBytes(ref reader, children, "BookmarkSize", "Bookmark", budget);
                return;
            case 0x6C: // RopSetCollapseState
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddLengthPrefixedBytes(ref reader, children, "CollapseStateSize", "CollapseState", budget);
                return;
            case 0x89: // RopFreeBookmark
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddLengthPrefixedBytes(ref reader, children, "BookmarkSize", "Bookmark", budget);
                return;
            default:
                throw new MapiParseException(reader.Position, $"RopFolderTableDecoders has no request decoder for 0x{ropId:X2}.");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Response dispatch
    // -------------------------------------------------------------------------------------------

    private static void ParseResponse(
        ref MapiReader reader,
        byte ropId,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        ImmutableArray<MapiNode>.Builder children)
    {
        switch (ropId)
        {
            case 0x02: // RopOpenFolder
            {
                AddHandleIndex(ref reader, children, "OutputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (!success)
                {
                    return;
                }
                AddBoolField(ref reader, children, "HasRules", budget);
                var isGhosted = AddBoolField(ref reader, children, "IsGhosted", budget);
                if (isGhosted)
                {
                    AddServerList(ref reader, children, budget);
                }
                return;
            }
            case 0x1C: // RopCreateFolder
            {
                AddHandleIndex(ref reader, children, "OutputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (!success)
                {
                    return;
                }
                AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                var isExisting = AddBoolField(ref reader, children, "IsExistingFolder", budget);
                if (!isExisting)
                {
                    return;
                }
                AddBoolField(ref reader, children, "HasRules", budget);
                var isGhosted = AddBoolField(ref reader, children, "IsGhosted", budget);
                if (isGhosted)
                {
                    AddServerList(ref reader, children, budget);
                }
                return;
            }
            case 0x1D: // RopDeleteFolder
            case 0x1E: // RopDeleteMessages
            case 0x58: // RopEmptyFolder
            case 0x91: // RopHardDeleteMessages
            case 0x92: // RopHardDeleteMessagesAndSubfolders
                // These five responses always carry PartialCompletion, on both success and failure.
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddReturnValue(ref reader, children, budget, out _);
                AddBoolField(ref reader, children, "PartialCompletion", budget);
                return;
            case 0x33: // RopMoveCopyMessages
            case 0x35: // RopMoveFolder
            case 0x36: // RopCopyFolder
            {
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddReturnValue(ref reader, children, budget, out var raw);
                if (raw == NullDestinationObject)
                {
                    // Per the pinned upstream reference (and unlike every other handle-index field in
                    // these two families), DestHandleIndex here is a 4-byte value, only present in this
                    // one specific failure branch - it is not registered as a byte RopHandleReference.
                    AddUInt32Field(ref reader, children, "DestHandleIndex", budget, hex: true);
                }
                AddBoolField(ref reader, children, "PartialCompletion", budget);
                return;
            }
            case 0x05: // RopGetContentsTable
            case 0x04: // RopGetHierarchyTable
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddUInt32Field(ref reader, children, "RowCount", budget);
                }
                return;
            }
            case 0x31: // RopGetSearchCriteria
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (!success)
                {
                    return;
                }
                AddRestriction(ref reader, children, "RestrictionDataSize", "RestrictionData", budget);
                AddLogonId(ref reader, children, budget);
                AddCountedFolderIds(ref reader, children, "FolderIdCount", "FolderIds", budget);
                AddUInt32Field(ref reader, children, "SearchFlags", budget, hex: true);
                return;
            }
            case 0x38: // RopAbort
            case 0x12: // RopSetColumns
            case 0x13: // RopSortTable
            case 0x14: // RopRestrict
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddByteField(ref reader, children, "TableStatus", budget);
                }
                return;
            }
            case 0x18: // RopSeekRow
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddBoolField(ref reader, children, "HasSoughtLess", budget);
                    AddInt32Field(ref reader, children, "RowsSought", budget);
                }
                return;
            }
            case 0x19: // RopSeekRowBookmark
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddBoolField(ref reader, children, "RowNoLongerVisible", budget);
                    AddBoolField(ref reader, children, "HasSoughtLess", budget);
                    AddUInt32Field(ref reader, children, "RowsSought", budget);
                }
                return;
            }
            case 0x1B: // RopCreateBookmark
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddLengthPrefixedBytes(ref reader, children, "BookmarkSize", "Bookmark", budget);
                }
                return;
            }
            case 0x37: // RopQueryColumnsAll
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedPropertyTags(ref reader, children, "PropertyTagCount", "PropertyTags", budget);
                }
                return;
            }
            case 0x6B: // RopGetCollapseState
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddLengthPrefixedBytes(ref reader, children, "CollapseStateSize", "CollapseState", budget);
                }
                return;
            }
            case 0x6C: // RopSetCollapseState
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddLengthPrefixedBytes(ref reader, children, "BookmarkSize", "Bookmark", budget);
                }
                return;
            }
            case 0x5A: // RopCollapseRow
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddUInt32Field(ref reader, children, "CollapsedRowCount", budget);
                }
                return;
            }
            case 0x15: // RopQueryRows - deterministic prefix; throws when row data is actually present.
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (!success)
                {
                    return;
                }
                AddByteField(ref reader, children, "Origin", budget);
                var rowCountOffset = reader.Position;
                var rowCount = AddUInt16Field(ref reader, children, "RowCount", budget);
                if (rowCount != 0)
                {
                    throw new MapiParseException(
                        rowCountOffset,
                        $"RopQueryRows response reports {rowCount} row(s); their byte width depends on a prior " +
                        "RopSetColumns request's property list, which is not tracked across operations, so the " +
                        "operation boundary cannot be determined safely.");
                }
                return;
            }
            case 0x4F: // RopFindRow - deterministic prefix; throws when a row is actually present.
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (!success)
                {
                    return;
                }
                AddBoolField(ref reader, children, "RowNoLongerVisible", budget);
                var hasRowDataOffset = reader.Position;
                var hasRowData = AddBoolField(ref reader, children, "HasRowData", budget);
                if (hasRowData)
                {
                    throw new MapiParseException(
                        hasRowDataOffset,
                        "RopFindRow response reports HasRowData=true; the row's byte width depends on a prior " +
                        "RopSetColumns request's property list, which is not tracked across operations, so the " +
                        "operation boundary cannot be determined safely.");
                }
                return;
            }
            case 0x59: // RopExpandRow - deterministic prefix; throws when row data is actually present.
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (!success)
                {
                    return;
                }
                AddUInt32Field(ref reader, children, "ExpandedRowCount", budget);
                var rowCountOffset = reader.Position;
                var rowCount = AddUInt16Field(ref reader, children, "RowCount", budget);
                if (rowCount != 0)
                {
                    throw new MapiParseException(
                        rowCountOffset,
                        $"RopExpandRow response reports {rowCount} row(s); their byte width depends on a prior " +
                        "RopSetColumns request's property list, which is not tracked across operations, so the " +
                        "operation boundary cannot be determined safely.");
                }
                return;
            }
            default:
                throw new MapiParseException(reader.Position, $"RopFolderTableDecoders has no response decoder for 0x{ropId:X2}.");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Shared primitive field helpers - deliberately independent of RopSemanticParser's private
    // RopField/RopFieldTier engine (which this file must not modify or otherwise depend on).
    // -------------------------------------------------------------------------------------------

    private static void AddLogonId(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte("LogonId");
        ExtendedBufferParser.AddField(children, "LogonId", offset, 1, value.ToString(CultureInfo.InvariantCulture), budget);
    }

    private static byte AddHandleIndex(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, value.ToString(CultureInfo.InvariantCulture), budget);
        handleReferences.Add(new RopHandleReference(operationIndex, name, offset, value));
        return value;
    }

    /// <summary>Reads the universal 4-byte ReturnValue field and returns whether it is Success.</summary>
    private static bool AddReturnValue(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget, out uint rawValue)
    {
        var offset = reader.Position;
        rawValue = reader.ReadUInt32("ReturnValue");
        ExtendedBufferParser.AddField(
            children, "ReturnValue", offset, 4, rawValue == 0 ? "0x00000000 (Success)" : $"0x{rawValue:X8} (Failure)", budget);
        return rawValue == 0;
    }

    private static byte AddByteField(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, $"0x{value:X2}", budget);
        return value;
    }

    private static bool AddBoolField(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, value != 0 ? "true" : "false", budget);
        return value != 0;
    }

    private static ushort AddUInt16Field(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        ExtendedBufferParser.AddField(children, name, offset, 2, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static uint AddUInt32Field(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget, bool hex = false)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(
            children, name, offset, 4, hex ? $"0x{value:X8}" : value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static int AddInt32Field(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadInt32(name);
        ExtendedBufferParser.AddField(children, name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static string AddNullTerminatedStringField(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, bool unicode, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = unicode ? reader.ReadNullTerminatedUnicode(name) : reader.ReadNullTerminatedAscii(name);
        ExtendedBufferParser.AddField(children, name, offset, reader.Position - offset, value, budget);
        return value;
    }

    /// <summary>Reads a UInt16 size, then that many raw bytes, as a single "Field" + raw-bytes pair.</summary>
    private static void AddLengthPrefixedBytes(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string sizeFieldName,
        string dataFieldName,
        MapiNodeBudget budget)
    {
        var sizeOffset = reader.Position;
        var size = reader.ReadUInt16(sizeFieldName);
        ExtendedBufferParser.AddField(children, sizeFieldName, sizeOffset, 2, size.ToString(CultureInfo.InvariantCulture), budget);
        var dataOffset = reader.Position;
        var data = reader.ReadBytes(size, dataFieldName);
        children.Add(ExtendedBufferParser.RawNode(dataFieldName, data, dataOffset, budget));
    }

    /// <summary>
    /// Reads a UInt16 RestrictionDataSize, and - only when it is non-zero - a bounded restriction
    /// packet (MS-OXCDATA 2.12) parsed via the shared <see cref="NspiRestrictionParser"/> in
    /// <see cref="MapiWireWidthContext.RopBuffer"/> mode: identical to NSPI/extended-rule
    /// restrictions except AndRestriction/OrRestriction's RestrictCount, which is 16-bit here
    /// instead of 32-bit ([MS-OXCDATA] 2.11.3/2.11.4). Any bytes declared by the size but not
    /// consumed by the restriction parse are preserved as a trailing raw node, since this decoder
    /// has no warnings list to record a soft mismatch through.
    /// </summary>
    private static void AddRestriction(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string sizeFieldName,
        string dataFieldName,
        MapiNodeBudget budget)
    {
        var sizeOffset = reader.Position;
        var size = reader.ReadUInt16(sizeFieldName);
        ExtendedBufferParser.AddField(children, sizeFieldName, sizeOffset, 2, size.ToString(CultureInfo.InvariantCulture), budget);
        if (size == 0)
        {
            return;
        }
        var sub = reader.SliceReader(size, dataFieldName);
        var restriction = NspiRestrictionParser.Parse(ref sub, budget, 0, null, null, MapiWireWidthContext.RopBuffer) with { Name = dataFieldName };
        children.Add(restriction);
        if (!sub.End)
        {
            var trailingOffset = sub.Position;
            var trailing = sub.ReadRemaining($"{dataFieldName}.TrailingBytes");
            children.Add(ExtendedBufferParser.RawNode($"{dataFieldName}.TrailingBytes", trailing, trailingOffset, budget));
        }
    }

    private static void AddCountedMessageIds(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string countFieldName,
        string arrayFieldName,
        MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount16(countFieldName);
        ExtendedBufferParser.AddField(children, countFieldName, countOffset, 2, count.ToString(CultureInfo.InvariantCulture), budget);
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            array.Add(AddFolderOrMessageId(ref reader, $"{arrayFieldName}[{i}]", budget));
        }
        budget.Claim(0, count);
        children.Add(new MapiNode(
            arrayFieldName,
            MapiNodeKind.Array,
            array.Count == 0 ? reader.Position : array[0].Offset,
            array.Sum(n => n.Length),
            $"{count:N0} entrie(s)",
            array.ToImmutable()));
    }

    private static void AddCountedFolderIds(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string countFieldName,
        string arrayFieldName,
        MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount16(countFieldName);
        ExtendedBufferParser.AddField(children, countFieldName, countOffset, 2, count.ToString(CultureInfo.InvariantCulture), budget);
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            array.Add(AddFolderOrMessageId(ref reader, $"{arrayFieldName}[{i}]", budget));
        }
        budget.Claim(0, count);
        children.Add(new MapiNode(
            arrayFieldName,
            MapiNodeKind.Array,
            array.Count == 0 ? reader.Position : array[0].Offset,
            array.Sum(n => n.Length),
            $"{count:N0} entrie(s)",
            array.ToImmutable()));
    }

    private static void AddCountedPropertyTags(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string countFieldName,
        string arrayFieldName,
        MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount16(countFieldName);
        ExtendedBufferParser.AddField(children, countFieldName, countOffset, 2, count.ToString(CultureInfo.InvariantCulture), budget);
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            array.Add(ReadPropertyTag(ref reader, $"{arrayFieldName}[{i}]", budget));
        }
        budget.Claim(0, count);
        children.Add(new MapiNode(
            arrayFieldName,
            MapiNodeKind.Array,
            array.Count == 0 ? reader.Position : array[0].Offset,
            array.Sum(n => n.Length),
            $"{count:N0} entrie(s)",
            array.ToImmutable()));
    }

    /// <summary>
    /// [MS-OXCFOLD] RopOpenFolder/RopCreateFolder response tail: ServerCount + CheapServerCount,
    /// followed by ServerCount null-terminated ASCII strings.
    /// </summary>
    private static void AddServerList(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var serverCount = reader.ReadCount16("ServerCount");
        ExtendedBufferParser.AddField(children, "ServerCount", countOffset, 2, serverCount.ToString(CultureInfo.InvariantCulture), budget);
        AddUInt16Field(ref reader, children, "CheapServerCount", budget);
        var array = ImmutableArray.CreateBuilder<MapiNode>(serverCount);
        for (var i = 0; i < serverCount; i++)
        {
            var offset = reader.Position;
            var value = reader.ReadNullTerminatedAscii($"Servers[{i}]");
            array.Add(MapiNode.Leaf($"Servers[{i}]", MapiNodeKind.Field, offset, reader.Position - offset, value));
        }
        budget.Claim(0, serverCount);
        children.Add(new MapiNode(
            "Servers",
            MapiNodeKind.Array,
            array.Count == 0 ? reader.Position : array[0].Offset,
            array.Sum(n => n.Length),
            $"{serverCount:N0} entrie(s)",
            array.ToImmutable()));
    }

    /// <summary>[MS-OXCDATA] 2.2.1.1/2.2.1.2: ReplicaId(UInt16) + GlobalCounter(6 raw bytes).</summary>
    private static MapiNode AddFolderOrMessageId(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var node = AddFolderOrMessageId(ref reader, name, budget);
        children.Add(node);
        return node;
    }

    private static MapiNode AddFolderOrMessageId(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var replicaOffset = reader.Position;
        var replicaId = reader.ReadUInt16($"{name}.ReplicaId");
        var globalCounterOffset = reader.Position;
        var globalCounter = reader.ReadBytes(6, $"{name}.GlobalCounter");
        var nested = ImmutableArray.CreateBuilder<MapiNode>(2);
        ExtendedBufferParser.AddField(nested, "ReplicaId", replicaOffset, 2, replicaId.ToString(CultureInfo.InvariantCulture), budget);
        nested.Add(ExtendedBufferParser.RawNode("GlobalCounter", globalCounter, globalCounterOffset, budget));
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, nested.ToImmutable());
    }

    /// <summary>[MS-OXCDATA] 2.9: PropertyType(UInt16) + PropertyId(UInt16).</summary>
    private static MapiNode ReadPropertyTag(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var typeOffset = reader.Position;
        var propertyType = reader.ReadUInt16($"{name}.PropertyType");
        var idOffset = reader.Position;
        var propertyId = reader.ReadUInt16($"{name}.PropertyId");
        var nested = ImmutableArray.CreateBuilder<MapiNode>(2);
        ExtendedBufferParser.AddField(nested, "PropertyType", typeOffset, 2, NspiPropertyParser.PropertyTypeName(propertyType), budget);
        ExtendedBufferParser.AddField(nested, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(propertyId), budget);
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, nested.ToImmutable());
    }

    /// <summary>[MS-OXCDATA] 2.13.1 SortOrder: PropertyType(UInt16) + PropertyId(UInt16) + Order(byte).</summary>
    private static MapiNode ReadSortOrder(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var typeOffset = reader.Position;
        var propertyType = reader.ReadUInt16($"{name}.PropertyType");
        var idOffset = reader.Position;
        var propertyId = reader.ReadUInt16($"{name}.PropertyId");
        var orderOffset = reader.Position;
        var order = reader.ReadByte($"{name}.Order");
        var nested = ImmutableArray.CreateBuilder<MapiNode>(3);
        ExtendedBufferParser.AddField(nested, "PropertyType", typeOffset, 2, NspiPropertyParser.PropertyTypeName(propertyType), budget);
        ExtendedBufferParser.AddField(nested, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(propertyId), budget);
        ExtendedBufferParser.AddField(
            nested,
            "Order",
            orderOffset,
            1,
            order switch { 0 => "0x00 (Ascending)", 1 => "0x01 (Descending)", _ => $"0x{order:X2}" },
            budget);
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, nested.ToImmutable());
    }
}
