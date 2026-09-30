using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

/// <summary>
/// Self-contained semantic ROP decoders for the [MS-OXCPRPT] (Property object) and [MS-OXCSTOR]
/// (Store object / Logon) protocol families, plus the remaining core [MS-OXCROPS] request/response
/// ROPs (transport, streams, named properties, per-user information, and the connection-level
/// RopBackoff) that are not covered by any other family-specific decoder.
///
/// This covers every request/response ROP in those families whose exact byte width can be
/// determined purely from bytes already read within the same operation, and that is not already
/// covered by <c>RopSemanticParser</c>'s fixed-width catalog.
///
/// Wire layouts are taken directly from the pinned upstream reference implementation (see
/// docs/mapi-parity.json for the exact repository/commit), specifically every
/// <c>MAPIInspector/Source/Parsers/MSOXCPRPT/rops/Rop*.cs</c>,
/// <c>MAPIInspector/Source/Parsers/MSOXCSTOR/rops/Rop*.cs</c>, and
/// <c>MAPIInspector/Source/Parsers/MSOXCROPS/rops/Rop*.cs</c> Request/Response pairs, cross-checked
/// against [MS-OXCPRPT], [MS-OXCSTOR], [MS-OXCROPS], and [MS-OXCDATA] (PropertyTag/PropertyProblem/
/// PropertyName/FolderID/MessageID/LongTermID/PropertyValue structures).
///
/// Property values (TaggedPropertyValue, [MS-OXCDATA] 2.11.4) are parsed using the "ROP buffers"
/// counted-field width rules ([MS-OXCDATA] 2.11.1.1): PtypBinary/PtypServerId byte counts are 16
/// bits wide, and every PtypMultiple* element count is 32 bits wide, regardless of the inner
/// element's own (possibly 16-bit) count. PtypRestriction ([MS-OXCDATA] 2.11.1) has no COUNT field
/// at all, and PtypRuleAction's rule-action sub-structures are recursively variable by ActionType;
/// both are therefore genuinely undeterminable from their own bytes, as is any PtypMultiple* value
/// whose base type is not one of the 12 types the specification defines a Multi-value form for.
/// Encountering any of these - or any other property type outside the fixed [MS-OXCDATA] 2.11.1
/// table - throws <see cref="MapiParseException"/> instead of guessing a boundary, consistent with
/// how this decoder module treats every other genuinely indeterminate structure.
///
/// A handful of request/response ROPs in these families are deliberately left unsupported (absent
/// from <see cref="Supports"/>) because their wire shape can only be resolved using state from a
/// *different*, earlier operation (an originating RopLogon's LogonFlags, a paired request's declared
/// buffer size, or a per-session named-property/PropertyTag dictionary) that this decoder - by
/// design, per its stateless per-operation contract - does not have access to:
///   - Request 0x64 RopWritePerUserInformation: the trailing 16-byte ReplGuid is present only when
///     DataOffset == 0 AND the LogonId's originating RopLogon used LogonFlags.Private.
///   - Response 0x07 RopGetPropertiesSpecific: the property tags returned are implied by the
///     matching request's PropertyTags array, which is not visible from the response bytes alone.
///   - Response 0xFE RopLogon: splits into two entirely different shapes (private mailbox vs.
///     public folders) with no in-band discriminator; it depends on the request's LogonFlags.
///   - Response 0xFF RopBufferTooSmall: its RequestBuffersSize field depends on the paired request's
///     buffer size, tracked only in cross-message session state.
///
/// <c>RopVariableDispatcher</c> routes supported property/store operations here after the fixed
/// semantic catalog declines them. The same <c>Supports</c>/<c>Parse</c> contract remains directly
/// unit-testable.
/// </summary>
internal static class RopPropertyStoreDecoders
{
    // [MS-OXCDATA] 2.4.1: the only AdditionalErrorCodes value that changes a CopyTo/CopyToStream/
    // CopyProperties response's shape (an extra DestHandleIndex field).
    private const uint NullDestinationObject = 0x00000503;

    // [MS-OXCDATA] 2.11.1: the MultiValue flag bit OR'd into a base PropertyType to form its
    // "array of values" variant.
    private const ushort MultiValueFlag = 0x1000;

    // [MS-OXCDATA] 2.11.1: the only base property types the specification defines a valid
    // PtypMultiple* (array) form for. Any other base type combined with the MultiValue flag is
    // malformed/hostile input whose element width cannot be determined safely.
    private static readonly ImmutableHashSet<ushort> MultiCapableBaseTypes = ImmutableHashSet.Create<ushort>(
        0x0002, // PtypInteger16
        0x0003, // PtypInteger32
        0x0004, // PtypFloating32
        0x0005, // PtypFloating64
        0x0006, // PtypCurrency
        0x0007, // PtypFloatingTime
        0x0014, // PtypInteger64
        0x0040, // PtypTime
        0x0048, // PtypGuid
        0x001E, // PtypString8
        0x001F, // PtypString
        0x0102); // PtypBinary

    private static readonly ImmutableHashSet<byte> RequestRopIds = ImmutableHashSet.Create<byte>(
        // MSOXCPRPT
        0x07, // RopGetPropertiesSpecific
        0x08, // RopGetPropertiesAll
        0x0A, // RopSetProperties
        0x0B, // RopDeleteProperties
        0x2B, // RopOpenStream
        0x2C, // RopReadStream
        0x2D, // RopWriteStream
        0x39, // RopCopyTo
        0x3A, // RopCopyToStream
        0x55, // RopGetNamesFromPropertyIds
        0x56, // RopGetPropertyIdsFromNames
        0x5B, // RopLockRegionStream
        0x5C, // RopUnlockRegionStream
        0x5F, // RopQueryNamedProperties
        0x63, // RopReadPerUserInformation
        0x67, // RopCopyProperties
        0x79, // RopSetPropertiesNoReplicate
        0x7A, // RopDeletePropertiesNoReplicate
        0x90, // RopWriteAndCommitStream
        0xA3, // RopWriteStreamExtended
        // MSOXCSTOR
        0x26, // RopSetReceiveFolder
        0x27, // RopGetReceiveFolder
        0x51, // RopTransportNewMail
        0x6F, // RopOptionsData
        0xFE, // RopLogon
        // Core MSOXCROPS
        0x34); // RopAbortSubmit

    private static readonly ImmutableHashSet<byte> ResponseRopIds = ImmutableHashSet.Create<byte>(
        // MSOXCPRPT
        0x08, // RopGetPropertiesAll
        0x09, // RopGetPropertiesList
        0x0A, // RopSetProperties
        0x0B, // RopDeleteProperties
        0x2B, // RopOpenStream
        0x2C, // RopReadStream
        0x2D, // RopWriteStream
        0x39, // RopCopyTo
        0x3A, // RopCopyToStream
        0x55, // RopGetNamesFromPropertyIds
        0x56, // RopGetPropertyIdsFromNames
        0x5F, // RopQueryNamedProperties
        0x63, // RopReadPerUserInformation
        0x67, // RopCopyProperties
        0x79, // RopSetPropertiesNoReplicate
        0x7A, // RopDeletePropertiesNoReplicate
        0x90, // RopWriteAndCommitStream
        0xA3, // RopWriteStreamExtended
        // MSOXCSTOR
        0x27, // RopGetReceiveFolder
        0x42, // RopGetOwningServers
        0x45, // RopPublicFolderIsGhosted
        0x60, // RopGetPerUserLongTermIds
        0x68, // RopGetReceiveFolderTable
        0x6F, // RopOptionsData
        // Core MSOXCROPS
        0x49, // RopGetAddressTypes
        0x4A, // RopTransportSend
        0x50, // RopProgress
        0xF9); // RopBackoff

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
            case 0x07: // RopGetPropertiesSpecific
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddUInt16Field(ref reader, children, "PropertySizeLimit", budget);
                AddBoolAsUInt16Field(ref reader, children, "WantUnicode", budget);
                AddCountedPropertyTags(ref reader, children, "PropertyTagCount", "PropertyTags", budget);
                return;
            case 0x08: // RopGetPropertiesAll
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddUInt16Field(ref reader, children, "PropertySizeLimit", budget);
                AddBoolAsUInt16Field(ref reader, children, "WantUnicode", budget);
                return;
            case 0x0A: // RopSetProperties
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var propertyValueSize = AddUInt16Field(ref reader, children, "PropertyValueSize", budget);
                var count = AddUInt16Field(ref reader, children, "PropertyValueCount", budget);
                var cap = Math.Max(0, propertyValueSize - sizeof(ushort));
                var sub = reader.SliceReader(cap, "PropertyValues");
                AddTaggedPropertyValueArray(ref sub, children, "PropertyValues", count, budget, stopWhenSubEmpty: true);
                if (!sub.End)
                {
                    var junkOffset = sub.Position;
                    var junk = sub.ReadRemaining("Junk");
                    children.Add(ExtendedBufferParser.RawNode("Junk", junk, junkOffset, budget));
                }
                return;
            }
            case 0x0B: // RopDeleteProperties
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddCountedPropertyTags(ref reader, children, "PropertyTagCount", "PropertyTags", budget);
                return;
            case 0x26: // RopSetReceiveFolder
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                AddAsciiField(ref reader, children, "MessageClass", budget);
                return;
            case 0x27: // RopGetReceiveFolder
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddAsciiField(ref reader, children, "MessageClass", budget);
                return;
            case 0x2B: // RopOpenStream
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "OutputHandleIndex", operationIndex, handleReferences, budget);
                children.Add(AddPropertyTagField(ref reader, "PropertyTag", budget));
                AddByteField(ref reader, children, "OpenModeFlags", budget);
                return;
            case 0x2C: // RopReadStream
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var byteCount = AddUInt16Field(ref reader, children, "ByteCount", budget);
                if (byteCount == 0xBABE)
                {
                    AddUInt32Field(ref reader, children, "MaximumByteCount", budget);
                }
                return;
            }
            case 0x2D: // RopWriteStream
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddLengthPrefixedBytes(ref reader, children, "DataSize", "Data", budget);
                return;
            case 0x34: // RopAbortSubmit
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                AddFolderOrMessageId(ref reader, children, "MessageId", budget);
                return;
            case 0x39: // RopCopyTo
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "DestHandleIndex", operationIndex, handleReferences, budget);
                AddBoolField(ref reader, children, "WantAsynchronous", budget);
                AddBoolField(ref reader, children, "WantSubObjects", budget);
                AddByteField(ref reader, children, "CopyFlags", budget);
                AddCountedPropertyTags(ref reader, children, "ExcludedTagCount", "ExcludedTags", budget);
                return;
            case 0x3A: // RopCopyToStream
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "DestHandleIndex", operationIndex, handleReferences, budget);
                AddUInt64Field(ref reader, children, "ByteCount", budget);
                return;
            case 0x51: // RopTransportNewMail
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddFolderOrMessageId(ref reader, children, "MessageId", budget);
                AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                AddAsciiField(ref reader, children, "MessageClass", budget);
                AddUInt32Field(ref reader, children, "MessageFlags", budget, hex: true);
                return;
            case 0x55: // RopGetNamesFromPropertyIds
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddCountedUInt16Array(ref reader, children, "PropertyIdCount", "PropertyIds", budget);
                return;
            case 0x56: // RopGetPropertyIdsFromNames
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "Flags", budget);
                AddCountedPropertyNames(ref reader, children, "PropertyNameCount", "PropertyNames", budget);
                return;
            case 0x5B: // RopLockRegionStream
            case 0x5C: // RopUnlockRegionStream
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddUInt64Field(ref reader, children, "RegionOffset", budget);
                AddUInt64Field(ref reader, children, "RegionSize", budget);
                AddUInt32Field(ref reader, children, "LockFlags", budget, hex: true);
                return;
            case 0x5F: // RopQueryNamedProperties
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "QueryFlags", budget);
                var hasGuid = AddBoolField(ref reader, children, "HasGuid", budget);
                if (hasGuid)
                {
                    AddGuidField(ref reader, children, "PropertyGuid", budget);
                }
                return;
            }
            case 0x63: // RopReadPerUserInformation
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                children.Add(AddLongTermId(ref reader, "FolderId", budget));
                AddByteField(ref reader, children, "Reserved", budget);
                AddUInt32Field(ref reader, children, "DataOffset", budget);
                AddUInt16Field(ref reader, children, "MaxDataSize", budget);
                return;
            case 0x67: // RopCopyProperties
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddHandleIndex(ref reader, children, "DestHandleIndex", operationIndex, handleReferences, budget);
                AddBoolField(ref reader, children, "WantAsynchronous", budget);
                AddByteField(ref reader, children, "CopyFlags", budget);
                AddCountedPropertyTags(ref reader, children, "PropertyTagCount", "PropertyTags", budget);
                return;
            case 0x6F: // RopOptionsData
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddAsciiField(ref reader, children, "AddressType", budget);
                AddByteField(ref reader, children, "WantWin32", budget);
                return;
            case 0x79: // RopSetPropertiesNoReplicate
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddUInt16Field(ref reader, children, "PropertyValueSize", budget);
                var count = AddUInt16Field(ref reader, children, "PropertyValueCount", budget);
                AddTaggedPropertyValueArray(ref reader, children, "PropertyValues", count, budget, stopWhenSubEmpty: false);
                return;
            }
            case 0x7A: // RopDeletePropertiesNoReplicate
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddCountedPropertyTags(ref reader, children, "PropertyTagCount", "PropertyTags", budget);
                return;
            case 0x90: // RopWriteAndCommitStream
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddLengthPrefixedBytes(ref reader, children, "DataSize", "Data", budget);
                return;
            case 0xA3: // RopWriteStreamExtended
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddLengthPrefixedBytes(ref reader, children, "DataSize", "Data", budget);
                return;
            case 0xFE: // RopLogon
            {
                AddLogonId(ref reader, children, budget);
                AddHandleIndex(ref reader, children, "OutputHandleIndex", operationIndex, handleReferences, budget);
                AddByteField(ref reader, children, "LogonFlags", budget);
                AddUInt32Field(ref reader, children, "OpenFlags", budget, hex: true);
                AddUInt32Field(ref reader, children, "StoreState", budget, hex: true);
                var essdnSize = AddUInt16Field(ref reader, children, "EssdnSize", budget);
                if (essdnSize > 0)
                {
                    AddAsciiField(ref reader, children, "Essdn", budget);
                }
                return;
            }
            default:
                throw new MapiParseException(reader.Position, $"RopPropertyStoreDecoders has no request decoder for 0x{ropId:X2}.");
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
            case 0x08: // RopGetPropertiesAll
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedTaggedPropertyValues(ref reader, children, "PropertyValueCount", "PropertyValues", budget);
                }
                return;
            }
            case 0x09: // RopGetPropertiesList
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedPropertyTags(ref reader, children, "PropertyTagCount", "PropertyTags", budget);
                }
                return;
            }
            case 0x0A: // RopSetProperties
            case 0x0B: // RopDeleteProperties
            {
                // [MS-OXCROPS] 2.2.8.6.2/2.2.8.6.3 and 2.2.8.8.2/2.2.8.8.3: identical response shape to
                // RopSetPropertiesNoReplicate/RopDeletePropertiesNoReplicate (0x79/0x7A) below - a
                // ReturnValue-gated PropertyProblem array, present only on success.
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedPropertyProblems(ref reader, children, "PropertyProblemCount", "PropertyProblems", budget);
                }
                return;
            }
            case 0x27: // RopGetReceiveFolder
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddFolderOrMessageId(ref reader, children, "FolderId", budget);
                    AddAsciiField(ref reader, children, "ExplicitMessageClass", budget);
                }
                return;
            }
            case 0x2B: // RopOpenStream
            {
                AddHandleIndex(ref reader, children, "OutputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddUInt32Field(ref reader, children, "StreamSize", budget);
                }
                return;
            }
            case 0x2C: // RopReadStream
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddReturnValue(ref reader, children, budget, out _);
                AddLengthPrefixedBytes(ref reader, children, "DataSize", "Data", budget);
                return;
            case 0x2D: // RopWriteStream
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddReturnValue(ref reader, children, budget, out _);
                AddUInt16Field(ref reader, children, "WrittenSize", budget);
                return;
            case 0x39: // RopCopyTo
            case 0x67: // RopCopyProperties
            {
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out var raw);
                if (success)
                {
                    AddCountedPropertyProblems(ref reader, children, "PropertyProblemCount", "PropertyProblems", budget);
                }
                else if (raw == NullDestinationObject)
                {
                    AddUInt32Field(ref reader, children, "DestHandleIndex", budget);
                }
                return;
            }
            case 0x3A: // RopCopyToStream
            {
                AddHandleIndex(ref reader, children, "SourceHandleIndex", operationIndex, handleReferences, budget);
                AddReturnValue(ref reader, children, budget, out var raw);
                if (raw == NullDestinationObject)
                {
                    AddUInt32Field(ref reader, children, "DestHandleIndex", budget);
                }
                AddUInt64Field(ref reader, children, "ReadByteCount", budget);
                AddUInt64Field(ref reader, children, "WrittenByteCount", budget);
                return;
            }
            case 0x42: // RopGetOwningServers
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    var count = AddUInt16Field(ref reader, children, "OwningServersCount", budget);
                    AddUInt16Field(ref reader, children, "CheapServersCount", budget);
                    AddNullTerminatedAsciiArray(ref reader, children, "OwningServers", count, budget);
                }
                return;
            }
            case 0x45: // RopPublicFolderIsGhosted
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    var isGhosted = AddBoolField(ref reader, children, "IsGhosted", budget);
                    if (isGhosted)
                    {
                        var count = AddUInt16Field(ref reader, children, "ServersCount", budget);
                        AddUInt16Field(ref reader, children, "CheapServersCount", budget);
                        AddNullTerminatedAsciiArray(ref reader, children, "Servers", count, budget);
                    }
                }
                return;
            }
            case 0x49: // RopGetAddressTypes
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    var count = AddUInt16Field(ref reader, children, "AddressTypeCount", budget);
                    AddUInt16Field(ref reader, children, "AddressTypeSize", budget);
                    AddNullTerminatedAsciiArray(ref reader, children, "AddressTypes", count, budget);
                }
                return;
            }
            case 0x4A: // RopTransportSend
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddByteField(ref reader, children, "NoPropertiesReturned", budget);
                    AddCountedTaggedPropertyValues(ref reader, children, "PropertyValueCount", "PropertyValues", budget);
                }
                return;
            }
            case 0x50: // RopProgress
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddLogonId(ref reader, children, budget);
                    AddUInt32Field(ref reader, children, "CompletedTaskCount", budget);
                    AddUInt32Field(ref reader, children, "TotalTaskCount", budget);
                }
                return;
            }
            case 0x55: // RopGetNamesFromPropertyIds
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedPropertyNames(ref reader, children, "PropertyNameCount", "PropertyNames", budget);
                }
                return;
            }
            case 0x56: // RopGetPropertyIdsFromNames
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedUInt16Array(ref reader, children, "PropertyIdCount", "PropertyIds", budget);
                }
                return;
            }
            case 0x5F: // RopQueryNamedProperties
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    var count = AddUInt16Field(ref reader, children, "IdCount", budget);
                    AddUInt16Array(ref reader, children, "PropertyIds", count, budget);
                    AddPropertyNameArray(ref reader, children, "PropertyNames", count, budget);
                }
                return;
            }
            case 0x60: // RopGetPerUserLongTermIds
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedLongTermIds(ref reader, children, "LongTermIdCount", "LongTermIds", budget);
                }
                return;
            }
            case 0x63: // RopReadPerUserInformation
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddBoolField(ref reader, children, "HasFinished", budget);
                    AddLengthPrefixedBytes(ref reader, children, "DataSize", "Data", budget);
                }
                return;
            }
            case 0x68: // RopGetReceiveFolderTable
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddReceiveFolderTableRows(ref reader, children, budget);
                }
                return;
            }
            case 0x6F: // RopOptionsData
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddByteField(ref reader, children, "Reserved", budget);
                    var infoSize = AddUInt16Field(ref reader, children, "OptionalInfoSize", budget);
                    AddRawBytesField(ref reader, children, "OptionalInfo", infoSize, budget);
                    var helpFileSize = AddUInt16Field(ref reader, children, "HelpFileSize", budget);
                    if (helpFileSize != 0)
                    {
                        AddRawBytesField(ref reader, children, "HelpFile", helpFileSize, budget);
                        AddAsciiField(ref reader, children, "HelpFileName", budget);
                    }
                }
                return;
            }
            case 0x79: // RopSetPropertiesNoReplicate
            case 0x7A: // RopDeletePropertiesNoReplicate
            {
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                var success = AddReturnValue(ref reader, children, budget, out _);
                if (success)
                {
                    AddCountedPropertyProblems(ref reader, children, "PropertyProblemCount", "PropertyProblems", budget);
                }
                return;
            }
            case 0x90: // RopWriteAndCommitStream
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddReturnValue(ref reader, children, budget, out _);
                AddUInt16Field(ref reader, children, "WrittenSize", budget);
                return;
            case 0xA3: // RopWriteStreamExtended
                AddHandleIndex(ref reader, children, "InputHandleIndex", operationIndex, handleReferences, budget);
                AddReturnValue(ref reader, children, budget, out _);
                AddUInt32Field(ref reader, children, "WrittenSize", budget);
                return;
            case 0xF9: // RopBackoff - genuinely different shape: no handle index, no ReturnValue at all.
            {
                AddLogonId(ref reader, children, budget);
                AddUInt32Field(ref reader, children, "Duration", budget);
                var backoffCount = AddByteField(ref reader, children, "BackoffRopCount", budget);
                var array = ImmutableArray.CreateBuilder<MapiNode>(backoffCount);
                for (var i = 0; i < backoffCount; i++)
                {
                    array.Add(AddBackoffRop(ref reader, $"BackoffRopData[{i}]", budget));
                }
                budget.Claim(0, backoffCount);
                children.Add(new MapiNode(
                    "BackoffRopData",
                    MapiNodeKind.Array,
                    array.Count == 0 ? reader.Position : array[0].Offset,
                    array.Sum(n => n.Length),
                    $"{backoffCount:N0} entrie(s)",
                    array.ToImmutable()));
                var additionalDataSize = AddUInt16Field(ref reader, children, "AdditionalDataSize", budget);
                AddRawBytesField(ref reader, children, "AdditionalData", additionalDataSize, budget);
                return;
            }
            default:
                throw new MapiParseException(reader.Position, $"RopPropertyStoreDecoders has no response decoder for 0x{ropId:X2}.");
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
        var text = rawValue switch
        {
            0 => "0x00000000 (Success)",
            NullDestinationObject => $"0x{rawValue:X8} (Failure - NullDestinationObject)",
            _ => $"0x{rawValue:X8} (Failure)"
        };
        ExtendedBufferParser.AddField(children, "ReturnValue", offset, 4, text, budget);
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

    /// <summary>[MS-OXCROPS]: a handful of Boolean request fields (e.g. WantUnicode) are encoded as a 2-byte UInt16, not a single byte.</summary>
    private static bool AddBoolAsUInt16Field(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        ExtendedBufferParser.AddField(children, name, offset, 2, value != 0 ? "true" : "false", budget);
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

    private static ulong AddUInt64Field(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt64(name);
        ExtendedBufferParser.AddField(children, name, offset, 8, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static Guid AddGuidField(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadGuid(name);
        ExtendedBufferParser.AddField(children, name, offset, 16, value.ToString(), budget);
        return value;
    }

    private static string AddAsciiField(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadNullTerminatedAscii(name);
        ExtendedBufferParser.AddField(children, name, offset, reader.Position - offset, value, budget);
        return value;
    }

    /// <summary>Reads a fixed number of raw bytes (its size already known from a preceding size field) as a single raw node.</summary>
    private static void AddRawBytesField(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, int count, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var data = reader.ReadBytes(count, name);
        children.Add(ExtendedBufferParser.RawNode(name, data, offset, budget));
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
        AddRawBytesField(ref reader, children, dataFieldName, size, budget);
    }

    /// <summary>[MS-OXCSTOR]/[MS-OXCROPS] server-list tail: Count null-terminated ASCII strings.</summary>
    private static void AddNullTerminatedAsciiArray(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string arrayFieldName, int count, MapiNodeBudget budget)
    {
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = reader.Position;
            var value = reader.ReadNullTerminatedAscii($"{arrayFieldName}[{i}]");
            array.Add(MapiNode.Leaf($"{arrayFieldName}[{i}]", MapiNodeKind.Field, offset, reader.Position - offset, value));
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

    // -------------------------------------------------------------------------------------------
    // Shared structure helpers ([MS-OXCDATA] common structures)
    // -------------------------------------------------------------------------------------------

    /// <summary>[MS-OXCDATA] 2.9: PropertyType(UInt16) + PropertyId(UInt16).</summary>
    private static MapiNode AddPropertyTagField(ref MapiReader reader, string name, MapiNodeBudget budget)
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
            array.Add(AddPropertyTagField(ref reader, $"{arrayFieldName}[{i}]", budget));
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

    /// <summary>[MS-OXCDATA] 2.7: Index(UInt16) + PropertyTag(4 bytes) + ErrorCode(UInt32).</summary>
    private static MapiNode AddPropertyProblem(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var indexOffset = reader.Position;
        var index = reader.ReadUInt16($"{name}.Index");
        var nested = ImmutableArray.CreateBuilder<MapiNode>(3);
        ExtendedBufferParser.AddField(nested, "Index", indexOffset, 2, index.ToString(CultureInfo.InvariantCulture), budget);
        nested.Add(AddPropertyTagField(ref reader, "PropertyTag", budget));
        var errorOffset = reader.Position;
        var errorCode = reader.ReadUInt32($"{name}.ErrorCode");
        ExtendedBufferParser.AddField(nested, "ErrorCode", errorOffset, 4, $"0x{errorCode:X8}", budget);
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, nested.ToImmutable());
    }

    private static void AddCountedPropertyProblems(
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
            array.Add(AddPropertyProblem(ref reader, $"{arrayFieldName}[{i}]", budget));
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

    // [MS-OXCSTOR] 2.2.3.4.2: the receive-folder table's row shape is a fixed three-column
    // PidTagFolderId/PidTagMessageClass/PidTagLastModificationTime list defined by the protocol
    // itself (not a client-supplied RopSetColumns), so - unlike RopQueryRows/RopFindRow/RopExpandRow,
    // whose column list is genuinely state-dependent - this response's row shape is entirely
    // self-determined and safe to decode.
    private static readonly ImmutableArray<(ushort Type, ushort Id)> ReceiveFolderTableColumns = ImmutableArray.Create<(ushort Type, ushort Id)>(
        (0x0014, 0x6748), // PidTagFolderId, PtypInteger64
        (0x001E, 0x001A), // PidTagMessageClass, PtypString8 (all characters are ASCII 0x20-0x7F per [MS-OXCSTOR])
        (0x0040, 0x3008)); // PidTagLastModificationTime, PtypTime

    /// <summary>
    /// [MS-OXCSTOR] 2.2.3.4.2 RopGetReceiveFolderTable success response: RowCount(UInt32) followed
    /// by that many [MS-OXCDATA] 2.8.1 PropertyRow structures against
    /// <see cref="ReceiveFolderTableColumns"/>, reusing <see cref="RopMessageRulesDecoders"/>'s
    /// PropertyRow decoder (identical StandardPropertyRow/FlaggedPropertyRow shape, only the column
    /// list differs) rather than duplicating its width-sensitive value-parsing logic.
    /// </summary>
    private static void AddReceiveFolderTableRows(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount32("RowCount");
        ExtendedBufferParser.AddField(children, "RowCount", countOffset, 4, count.ToString(CultureInfo.InvariantCulture), budget);
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            array.Add(RopMessageRulesDecoders.ParseRopPropertyRow(
                ref reader,
                $"Rows[{i}]",
                ReceiveFolderTableColumns,
                ReceiveFolderTableColumns.Length,
                budget,
                depth: 1,
                CancellationToken.None,
                codePage: 20127));
        }
        budget.Claim(0, count);
        children.Add(new MapiNode(
            "Rows",
            MapiNodeKind.Array,
            array.Count == 0 ? reader.Position : array[0].Offset,
            array.Sum(n => n.Length),
            $"{count:N0} entrie(s)",
            array.ToImmutable()));
    }

    /// <summary>[MS-OXCDATA] 2.2.1.1/2.2.1.2: ReplicaId(UInt16) + GlobalCounter(6 raw bytes).</summary>
    private static void AddFolderOrMessageId(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        children.Add(AddFolderOrMessageId(ref reader, name, budget));
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

    /// <summary>[MS-OXCDATA] 2.2.1.3.1: DatabaseGuid(16 bytes) + GlobalCounter(6 raw bytes) + Pad(UInt16).</summary>
    private static MapiNode AddLongTermId(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var guidOffset = reader.Position;
        var databaseGuid = reader.ReadGuid($"{name}.DatabaseGuid");
        var globalCounterOffset = reader.Position;
        var globalCounter = reader.ReadBytes(6, $"{name}.GlobalCounter");
        var padOffset = reader.Position;
        var pad = reader.ReadUInt16($"{name}.Pad");
        var nested = ImmutableArray.CreateBuilder<MapiNode>(3);
        ExtendedBufferParser.AddField(nested, "DatabaseGuid", guidOffset, 16, databaseGuid.ToString(), budget);
        nested.Add(ExtendedBufferParser.RawNode("GlobalCounter", globalCounter, globalCounterOffset, budget));
        ExtendedBufferParser.AddField(nested, "Pad", padOffset, 2, $"0x{pad:X4}", budget);
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, nested.ToImmutable());
    }

    private static void AddCountedLongTermIds(
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
            array.Add(AddLongTermId(ref reader, $"{arrayFieldName}[{i}]", budget));
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
    /// [MS-OXCDATA] 2.6.1 PropertyName: Kind(byte) then, only for Kind 0x00 (LID) or 0x01 (Name), a
    /// GUID(16 bytes) followed by either a LID(UInt32) or a NameSize(byte) + Name (NameSize bytes of
    /// UTF-16LE, per spec "the number of bytes in the Name string"). Kind 0xFF (NoPropertyName) has
    /// no further fields. Any other Kind value makes the remaining width genuinely indeterminate.
    /// </summary>
    private static MapiNode AddPropertyName(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var kindOffset = reader.Position;
        var kind = reader.ReadByte($"{name}.Kind");
        var nested = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(
            nested,
            "Kind",
            kindOffset,
            1,
            kind switch { 0x00 => "0x00 (LID)", 0x01 => "0x01 (Name)", 0xFF => "0xFF (NoPropertyName)", _ => $"0x{kind:X2}" },
            budget);
        switch (kind)
        {
            case 0x00:
            {
                AddGuidField(ref reader, nested, "Guid", budget);
                var lidOffset = reader.Position;
                var lid = reader.ReadUInt32($"{name}.Lid");
                ExtendedBufferParser.AddField(nested, "Lid", lidOffset, 4, $"0x{lid:X8}", budget);
                break;
            }
            case 0x01:
            {
                AddGuidField(ref reader, nested, "Guid", budget);
                var nameSizeOffset = reader.Position;
                var nameSize = reader.ReadByte($"{name}.NameSize");
                ExtendedBufferParser.AddField(nested, "NameSize", nameSizeOffset, 1, nameSize.ToString(CultureInfo.InvariantCulture), budget);
                var charBytes = nameSize / 2 * 2;
                var nameOffset = reader.Position;
                var value = charBytes > 0 ? reader.ReadUnicode(charBytes, $"{name}.Name") : string.Empty;
                ExtendedBufferParser.AddField(nested, "Name", nameOffset, charBytes, value, budget);
                if (nameSize % 2 != 0)
                {
                    var padOffset = reader.Position;
                    var pad = reader.ReadBytes(1, $"{name}.NamePad");
                    nested.Add(ExtendedBufferParser.RawNode("NamePad", pad, padOffset, budget));
                }
                break;
            }
            case 0xFF:
                break;
            default:
                throw new MapiParseException(
                    kindOffset, $"{name}.Kind has unsupported value 0x{kind:X2}; its length cannot be determined safely.");
        }
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, nested.ToImmutable());
    }

    private static void AddPropertyNameArray(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string arrayFieldName, int count, MapiNodeBudget budget)
    {
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            array.Add(AddPropertyName(ref reader, $"{arrayFieldName}[{i}]", budget));
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

    private static void AddCountedPropertyNames(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string countFieldName,
        string arrayFieldName,
        MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount16(countFieldName);
        ExtendedBufferParser.AddField(children, countFieldName, countOffset, 2, count.ToString(CultureInfo.InvariantCulture), budget);
        AddPropertyNameArray(ref reader, children, arrayFieldName, count, budget);
    }

    private static void AddUInt16Array(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string arrayFieldName, int count, MapiNodeBudget budget)
    {
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = reader.Position;
            var value = reader.ReadUInt16($"{arrayFieldName}[{i}]");
            array.Add(MapiNode.Leaf($"{arrayFieldName}[{i}]", MapiNodeKind.Field, offset, 2, MapiPropertyNames.FormatPidTag(value)));
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

    private static void AddCountedUInt16Array(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string countFieldName,
        string arrayFieldName,
        MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount16(countFieldName);
        ExtendedBufferParser.AddField(children, countFieldName, countOffset, 2, count.ToString(CultureInfo.InvariantCulture), budget);
        AddUInt16Array(ref reader, children, arrayFieldName, count, budget);
    }

    /// <summary>[MS-OXCDATA] 2.11.1.1 BackoffRop: RopIdBackoff(byte) + Duration(UInt32).</summary>
    private static MapiNode AddBackoffRop(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var ropIdOffset = reader.Position;
        var ropIdBackoff = reader.ReadByte($"{name}.RopIdBackoff");
        var durationOffset = reader.Position;
        var duration = reader.ReadUInt32($"{name}.Duration");
        var nested = ImmutableArray.CreateBuilder<MapiNode>(2);
        ExtendedBufferParser.AddField(
            nested, "RopIdBackoff", ropIdOffset, 1, $"0x{ropIdBackoff:X2} ({RopBufferParser.Name(ropIdBackoff)})", budget);
        ExtendedBufferParser.AddField(nested, "Duration", durationOffset, 4, duration.ToString(CultureInfo.InvariantCulture), budget);
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, nested.ToImmutable());
    }

    // -------------------------------------------------------------------------------------------
    // TaggedPropertyValue / PropertyValue dispatch ([MS-OXCDATA] 2.11.1 "ROP buffers" width rules)
    // -------------------------------------------------------------------------------------------

    /// <summary>[MS-OXCDATA] 2.11.4: PropertyTag(4 bytes) + PropertyValue (dispatched by PropertyType).</summary>
    private static MapiNode AddTaggedPropertyValue(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var typeOffset = reader.Position;
        var type = reader.ReadUInt16($"{name}.PropertyType");
        var idOffset = reader.Position;
        var id = reader.ReadUInt16($"{name}.PropertyId");
        var nested = ImmutableArray.CreateBuilder<MapiNode>(3);
        ExtendedBufferParser.AddField(nested, "PropertyType", typeOffset, 2, NspiPropertyParser.PropertyTypeName(type), budget);
        ExtendedBufferParser.AddField(nested, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(id), budget);
        nested.Add(AddPropertyValue(ref reader, type, "PropertyValue", budget));
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Property, start, reader.Position - start, $"0x{id:X4}:{type:X4}", nested.ToImmutable());
    }

    /// <summary>
    /// Reads <paramref name="count"/> TaggedPropertyValue structures against <paramref name="reader"/>
    /// (which may be a capped sub-reader for RopSetProperties' PropertyValueSize window). When
    /// <paramref name="stopWhenSubEmpty"/> is set, the loop also stops early once the reader is
    /// exhausted (matching RopSetProperties' "for (...) &amp;&amp; !parser.Empty" upstream behavior).
    /// </summary>
    private static void AddTaggedPropertyValueArray(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string arrayFieldName,
        int count,
        MapiNodeBudget budget,
        bool stopWhenSubEmpty)
    {
        var array = ImmutableArray.CreateBuilder<MapiNode>(count);
        for (var i = 0; i < count; i++)
        {
            if (stopWhenSubEmpty && reader.End)
            {
                break;
            }
            array.Add(AddTaggedPropertyValue(ref reader, $"{arrayFieldName}[{i}]", budget));
        }
        budget.Claim(0, array.Count);
        children.Add(new MapiNode(
            arrayFieldName,
            MapiNodeKind.Array,
            array.Count == 0 ? reader.Position : array[0].Offset,
            array.Sum(n => n.Length),
            $"{array.Count:N0} entrie(s)",
            array.ToImmutable()));
    }

    private static void AddCountedTaggedPropertyValues(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string countFieldName,
        string arrayFieldName,
        MapiNodeBudget budget)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount16(countFieldName);
        ExtendedBufferParser.AddField(children, countFieldName, countOffset, 2, count.ToString(CultureInfo.InvariantCulture), budget);
        AddTaggedPropertyValueArray(ref reader, children, arrayFieldName, count, budget, stopWhenSubEmpty: false);
    }

    /// <summary>
    /// [MS-OXCDATA] 2.11.2.1 PropertyValue dispatch, using "ROP buffers" counted-field widths.
    /// Handles both scalar values and the PtypMultiple* (array-of-values) form.
    /// </summary>
    private static MapiNode AddPropertyValue(ref MapiReader reader, ushort type, string name, MapiNodeBudget budget)
    {
        budget.Claim(0);
        var start = reader.Position;
        if ((type & MultiValueFlag) != 0)
        {
            var baseType = (ushort)(type & ~MultiValueFlag);
            if (!MultiCapableBaseTypes.Contains(baseType))
            {
                throw new MapiParseException(
                    start, $"{name} has property type 0x{type:X4} with no valid Multi-value element form; its length cannot be determined safely.");
            }
            var countOffset = reader.Position;
            var count = reader.ReadCount32($"{name}.Count"); // Always 32-bit, even in ROP-buffer context.
            var nested = ImmutableArray.CreateBuilder<MapiNode>(count + 1);
            ExtendedBufferParser.AddField(nested, "Count", countOffset, 4, count.ToString(CultureInfo.InvariantCulture), budget);
            for (var i = 0; i < count; i++)
            {
                nested.Add(AddScalarPropertyValue(ref reader, baseType, $"[{i}]", budget));
            }
            budget.Claim(0, count);
            return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, NspiPropertyParser.PropertyTypeName(type), nested.ToImmutable());
        }
        return AddScalarPropertyValue(ref reader, type, name, budget);
    }

    private static MapiNode AddScalarPropertyValue(ref MapiReader reader, ushort type, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        switch (type)
        {
            case 0x0000: // PtypUnspecified
            case 0x0001: // PtypNull
            case 0x000D: // PtypObject_Or_PtypEmbeddedTable (not represented inline in a property row)
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, 0, "null");
            case 0x0002: // PtypInteger16
            {
                var value = reader.ReadInt16(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value.ToString(CultureInfo.InvariantCulture));
            }
            case 0x0003: // PtypInteger32
            {
                var value = reader.ReadInt32(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value.ToString(CultureInfo.InvariantCulture));
            }
            case 0x0004: // PtypFloating32
            {
                var value = reader.ReadSingle(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value.ToString("R", CultureInfo.InvariantCulture));
            }
            case 0x0005: // PtypFloating64
            case 0x0007: // PtypFloatingTime
            {
                var value = reader.ReadDouble(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value.ToString("R", CultureInfo.InvariantCulture));
            }
            case 0x0006: // PtypCurrency
            case 0x0014: // PtypInteger64
            {
                var value = reader.ReadInt64(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value.ToString(CultureInfo.InvariantCulture));
            }
            case 0x000A: // PtypErrorCode
            {
                var value = reader.ReadUInt32(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, $"0x{value:X8}");
            }
            case 0x000B: // PtypBoolean (1 byte, per [MS-OXCDATA] 2.11.1)
            {
                var value = reader.ReadByte(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value != 0 ? "true" : "false");
            }
            case 0x001E: // PtypString8 (null-terminated, no count prefix, in ROP-buffer context)
            {
                var value = reader.ReadNullTerminatedAscii(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value);
            }
            case 0x001F: // PtypString (null-terminated UTF-16LE, no count prefix)
            {
                var value = reader.ReadNullTerminatedUnicode(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value);
            }
            case 0x0040: // PtypTime (rendered as a raw FILETIME number to avoid conversion-exception risk on hostile input)
            {
                var value = reader.ReadUInt64(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, $"0x{value:X16}");
            }
            case 0x0048: // PtypGuid
            {
                var value = reader.ReadGuid(name);
                budget.Claim(0);
                return MapiNode.Leaf(name, MapiNodeKind.Property, start, reader.Position - start, value.ToString());
            }
            case 0x00FB: // PtypServerId: always a 16-bit COUNT (per [MS-OXCDATA] 2.11.1), regardless of context.
            case 0x0102: // PtypBinary: 16-bit COUNT in ROP-buffer context ([MS-OXCDATA] 2.11.1.1).
                return AddCountedBinary16(ref reader, name, budget);
            case 0x00FD: // PtypRestriction: no COUNT field at all; genuinely undeterminable from its own bytes.
            case 0x00FE: // PtypRuleAction: recursive rule-action structures whose width varies by ActionType.
                throw new MapiParseException(
                    start, $"{name} has property type 0x{type:X4}; its length cannot be determined without full recursive parsing.");
            default:
                throw new MapiParseException(start, $"{name} has unsupported property type 0x{type:X4}; its length cannot be determined safely.");
        }
    }

    private static MapiNode AddCountedBinary16(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var count = reader.ReadCount16($"{name}.Count");
        var dataOffset = reader.Position;
        var data = reader.ReadBytes(count, name);
        var nested = ImmutableArray.CreateBuilder<MapiNode>(1);
        nested.Add(ExtendedBufferParser.RawNode("Bytes", data, dataOffset, budget));
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Property, start, reader.Position - start, $"{count:N0} bytes", nested.ToImmutable());
    }
}
