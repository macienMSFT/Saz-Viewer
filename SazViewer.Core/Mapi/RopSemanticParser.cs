using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

/// <summary>
/// Records that a decoded ROP operation referenced a byte-sized slot in the trailing Server object
/// handle table (as an InputHandleIndex/OutputHandleIndex/ResponseHandleIndex field), so the
/// reference can be cross-checked once the handle table's own size is known.
/// </summary>
internal readonly record struct RopHandleReference(int OperationIndex, string FieldName, long Offset, byte Index);

/// <summary>
/// The wire shape of a single leaf (or compound) field inside a fixed-width ROP operation schema.
/// </summary>
internal enum RopFieldKind
{
    RopId,
    LogonId,
    HandleIndex,
    ReturnValue,
    Byte,
    UInt16,
    UInt32,
    UInt64,
    Int32,
    Int64,
    Bool,
    Guid,
    FixedBytes,
    Compound,
}

internal sealed class RopField
{
    public required string Name { get; init; }
    public required RopFieldKind Kind { get; init; }
    public int Length { get; init; }
    public ImmutableArray<RopField> Members { get; init; } = ImmutableArray<RopField>.Empty;

    public int Size => Kind switch
    {
        RopFieldKind.RopId or RopFieldKind.LogonId or RopFieldKind.HandleIndex
            or RopFieldKind.Byte or RopFieldKind.Bool => 1,
        RopFieldKind.UInt16 => 2,
        RopFieldKind.UInt32 or RopFieldKind.ReturnValue or RopFieldKind.Int32 => 4,
        RopFieldKind.UInt64 or RopFieldKind.Int64 => 8,
        RopFieldKind.Guid => 16,
        RopFieldKind.FixedBytes => Length,
        RopFieldKind.Compound => Members.Sum(m => m.Size),
        _ => throw new NotSupportedException($"Unhandled ROP field kind {Kind}."),
    };

    public static RopField Of(string name, RopFieldKind kind) => new() { Name = name, Kind = kind };

    public static RopField Bytes(string name, int length) =>
        new() { Name = name, Kind = RopFieldKind.FixedBytes, Length = length };

    public static RopField Group(string name, params RopField[] members) =>
        new() { Name = name, Kind = RopFieldKind.Compound, Members = [.. members] };
}

/// <summary>
/// A contiguous run of fields that is always present once reached, optionally followed by another
/// tier that is present only when the last field of this tier reads as "true" (a boolean field) or
/// "success" (a ReturnValue field). This lets a single declarative schema describe both universally
/// fixed-width operations (no <see cref="WhenTrue"/>) and operations whose response carries a small,
/// finite number of discrete fixed widths that are entirely self-determined from bytes already read
/// in the same operation (an optional <see cref="WhenTrue"/> chain).
/// </summary>
internal sealed class RopFieldTier
{
    public required ImmutableArray<RopField> Fields { get; init; }
    public RopFieldTier? WhenTrue { get; init; }

    public static RopFieldTier Of(params RopField[] fields) => new() { Fields = [.. fields] };
}

internal sealed class RopOperationSchema
{
    public required byte RopId { get; init; }
    public required RopFieldTier Root { get; init; }
}

/// <summary>
/// Decodes the multi-operation ROP list inside a RopInputBuffer/RopOutputBuffer using a catalog of
/// self-contained, context-independent fixed-width request and response schemas derived from the
/// pinned upstream MAPIInspector ROP parsers (cross-checked against the MS-OXCROPS RopIdType table).
/// Only operations whose exact byte width can be determined purely from bytes already read within
/// the same operation (never from earlier operations, prior ROPs, or external session state) are
/// implemented; every other RopId - known or not - stops the boundary walk and is retained as raw so
/// that no operation boundary is ever guessed. All lookup tables here are immutable and built once;
/// no mutable global state is kept across parses.
/// </summary>
internal static class RopSemanticParser
{
    public static ImmutableArray<MapiNode> ParseOperations(
        ReadOnlySpan<byte> ropList,
        long absoluteOffset,
        MapiDirection direction,
        List<string> warnings,
        MapiNodeBudget budget,
        List<RopHandleReference> handleReferences,
        CancellationToken cancellationToken,
        FastTransferStreamAssembler? fastTransferAssembler = null,
        string? captureScope = null,
        MapiCaptureContext? context = null)
    {
        var schemas = direction == MapiDirection.Request ? RequestSchemas : ResponseSchemas;
        var operations = ImmutableArray.CreateBuilder<MapiNode>();
        var reader = new MapiReader(ropList, cancellationToken, checked((int)absoluteOffset));
        var index = 0;
        // Only a request-direction ROP list can ever be the paired request of a same-session
        // RopBufferTooSmall response, so these checkpoints are only ever collected here.
        // checkpoints[j] is the byte offset (local to this ROP list) reached once exactly j
        // *response*-producing request operations have been fully parsed - checkpoints[0] is always
        // 0 before parsing starts. [MS-OXCROPS] 2.2.1: RopRelease (0x01) is the one request ROP that
        // never yields a response operation, but it is still executed and therefore excluded from a
        // later RopBufferTooSmall response's RequestBuffers tail. It replaces the current checkpoint
        // with its post-operation offset instead of appending a response-producing checkpoint.
        var checkpoints = direction == MapiDirection.Request ? ImmutableArray.CreateBuilder<int>() : null;
        checkpoints?.Add(0);
        while (!reader.End)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opStartLocal = reader.LocalPosition;
            var opStartAbsolute = reader.Position;
            var ropId = reader.PeekByte("RopId");

            var hasFixedSchema = schemas.TryGetValue(ropId, out var schema);
            if (!hasFixedSchema && !RopVariableDispatcher.Supports(direction, ropId))
            {
                // A fresh, throwaway budget guarantees this terminal fallback node can always be
                // constructed - even if `budget` itself is already exhausted (e.g. by many prior
                // operations) - mirroring MapiCaptureParser's top-level "guaranteed raw fallback"
                // convention. The loop always stops right after this branch, so the extra node(s)
                // it allocates are never subject to (and never inflate) the shared budget.
                var fallbackBudget = new MapiNodeBudget();
                var known = RopBufferParser.IsKnownRopId(ropId);
                var fallbackChildren = ImmutableArray.CreateBuilder<MapiNode>();
                var consumedRopId = reader.ReadByte("RopId");
                ExtendedBufferParser.AddField(
                    fallbackChildren,
                    "RopId",
                    opStartAbsolute,
                    1,
                    $"0x{consumedRopId:X2} ({RopBufferParser.Name(consumedRopId)})",
                    fallbackBudget);
                if (!reader.End)
                {
                    var tail = ropList[(opStartLocal + 1)..];
                    fallbackChildren.Add(ExtendedBufferParser.RawNode("Operation bytes", tail, opStartAbsolute + 1, fallbackBudget));
                }
                operations.Add(new MapiNode(
                    $"Operation {index} (undecoded)",
                    MapiNodeKind.Operation,
                    opStartAbsolute,
                    ropList.Length - opStartLocal,
                    RopBufferParser.Name(ropId),
                    fallbackChildren.ToImmutable()));
                warnings.Add(
                    $"ROP list operation {index} is 0x{ropId:X2} ({RopBufferParser.Name(ropId)}); individual ROP fields and " +
                    "additional operation boundaries are retained as raw because " +
                    (known
                        ? $"no fixed-width {direction.ToString().ToLowerInvariant()} schema is implemented for it"
                        : "the RopId is not recognized") +
                    $"; no further operations in this list are decoded.");
                break;
            }

            try
            {
                var node = hasFixedSchema
                    ? ParseOperation(ref reader, index, schema!, handleReferences, budget)
                    : RopVariableDispatcher.Parse(
                        ref reader,
                        index,
                        direction,
                        handleReferences,
                        budget,
                        cancellationToken,
                        warnings,
                        fastTransferAssembler,
                        captureScope,
                        context);
                if (reader.LocalPosition <= opStartLocal)
                {
                    // Defensive: every schema consumes at least 3 bytes, so this should be unreachable.
                    // Guard explicit forward progress anyway so hostile input can never spin the loop.
                    throw new MapiParseException(opStartAbsolute, "Internal ROP schema made no progress.");
                }
                operations.Add(node);
                index++;
                var newReferences = handleReferences
                    .Where(reference => reference.OperationIndex == index - 1)
                    .ToArray();
                if (direction == MapiDirection.Response
                    && ropList.Length - opStartLocal >= 6
                    && BinaryPrimitives.ReadUInt32LittleEndian(ropList[(opStartLocal + 2)..]) == 0)
                {
                    foreach (var output in newReferences.Where(reference => reference.FieldName == "OutputHandleIndex"))
                    {
                        context?.InvalidateTableColumns(captureScope, output.Index);
                    }
                }
                if (ropId == 0x01)
                {
                    var releaseHandle = newReferences.LastOrDefault(
                        reference => reference.FieldName == "InputHandleIndex");
                    context?.InvalidateTableColumns(captureScope, releaseHandle.Index);
                    if (checkpoints is not null)
                    {
                        checkpoints[^1] = reader.LocalPosition;
                    }
                }
                else
                {
                    checkpoints?.Add(reader.LocalPosition);
                }
                if (direction == MapiDirection.Response && ropId == 0x81)
                {
                    context?.CompleteResetTable(
                        captureScope,
                        ropList[opStartLocal + 1],
                        BinaryPrimitives.ReadUInt32LittleEndian(ropList[(opStartLocal + 2)..]) == 0);
                }
                else if (direction == MapiDirection.Request && ropId == 0x81)
                {
                    context?.EnqueueResetTable(captureScope, ropList[opStartLocal + 2]);
                }
            }
            catch (MapiParseException ex)
            {
                // Same guaranteed-fallback reasoning as above: `budget` may itself be the reason this
                // operation failed (e.g. the node budget was exceeded while parsing its fields), so the
                // raw fallback node must not depend on it having any headroom left.
                var raw = ropList[opStartLocal..];
                operations.Add(ExtendedBufferParser.RawNode(
                    $"Operation {index} (malformed)",
                    raw,
                    opStartAbsolute,
                    new MapiNodeBudget()));
                warnings.Add(
                    $"ROP list operation {index} (0x{ropId:X2} {RopBufferParser.Name(ropId)}) could not be parsed: " +
                    $"{ex.Message}; the remaining {raw.Length:N0} byte(s) are retained as raw and no further operations in this list are decoded.");
                break;
            }
        }
        if (checkpoints is not null && context is not null)
        {
            context.RecordRequestRopList(captureScope, ropList.Length, checkpoints.ToImmutable());
        }
        return operations.ToImmutable();
    }

    private static MapiNode ParseOperation(
        ref MapiReader reader,
        int operationIndex,
        RopOperationSchema schema,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ParseTier(ref reader, schema.Root, children, operationIndex, handleReferences, budget);
        budget.Claim(0);
        return new MapiNode(
            $"Operation {operationIndex}",
            MapiNodeKind.Operation,
            start,
            reader.Position - start,
            $"{RopBufferParser.Name(schema.RopId)} (0x{schema.RopId:X2})",
            children.ToImmutable());
    }

    private static void ParseTier(
        ref MapiReader reader,
        RopFieldTier tier,
        ImmutableArray<MapiNode>.Builder children,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget)
    {
        var gate = false;
        foreach (var field in tier.Fields)
        {
            gate = ParseField(ref reader, field, children, operationIndex, handleReferences, budget);
        }
        if (gate && tier.WhenTrue is { } next)
        {
            ParseTier(ref reader, next, children, operationIndex, handleReferences, budget);
        }
    }

    /// <summary>
    /// Reads one field and returns the "gate" value (only meaningful for <see cref="RopFieldKind.Bool"/>
    /// and <see cref="RopFieldKind.ReturnValue"/> fields) that determines whether the tier's optional
    /// next tier is present.
    /// </summary>
    private static bool ParseField(
        ref MapiReader reader,
        RopField field,
        ImmutableArray<MapiNode>.Builder children,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        switch (field.Kind)
        {
            case RopFieldKind.RopId:
            {
                var value = reader.ReadByte(field.Name);
                ExtendedBufferParser.AddField(
                    children, field.Name, offset, 1, $"0x{value:X2} ({RopBufferParser.Name(value)})", budget);
                return false;
            }
            case RopFieldKind.LogonId:
            {
                var value = reader.ReadByte(field.Name);
                ExtendedBufferParser.AddField(
                    children, field.Name, offset, 1, value.ToString(CultureInfo.InvariantCulture), budget);
                return false;
            }
            case RopFieldKind.HandleIndex:
            {
                var value = reader.ReadByte(field.Name);
                ExtendedBufferParser.AddField(
                    children, field.Name, offset, 1, value.ToString(CultureInfo.InvariantCulture), budget);
                handleReferences.Add(new RopHandleReference(operationIndex, field.Name, offset, value));
                return false;
            }
            case RopFieldKind.ReturnValue:
            {
                var value = reader.ReadUInt32(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 4, FormatReturnValue(value), budget);
                return value == 0;
            }
            case RopFieldKind.Byte:
            {
                var value = reader.ReadByte(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 1, $"0x{value:X2}", budget);
                return false;
            }
            case RopFieldKind.Bool:
            {
                var value = reader.ReadByte(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 1, value != 0 ? "true" : "false", budget);
                return value != 0;
            }
            case RopFieldKind.UInt16:
            {
                var value = reader.ReadUInt16(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 2, value.ToString(CultureInfo.InvariantCulture), budget);
                return false;
            }
            case RopFieldKind.UInt32:
            {
                var value = reader.ReadUInt32(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
                return false;
            }
            case RopFieldKind.UInt64:
            {
                var value = reader.ReadUInt64(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 8, value.ToString(CultureInfo.InvariantCulture), budget);
                return false;
            }
            case RopFieldKind.Int32:
            {
                var value = reader.ReadInt32(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
                return false;
            }
            case RopFieldKind.Int64:
            {
                var value = reader.ReadInt64(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 8, value.ToString(CultureInfo.InvariantCulture), budget);
                return false;
            }
            case RopFieldKind.Guid:
            {
                var value = reader.ReadGuid(field.Name);
                ExtendedBufferParser.AddField(children, field.Name, offset, 16, value.ToString(), budget);
                return false;
            }
            case RopFieldKind.FixedBytes:
            {
                var bytes = reader.ReadBytes(field.Length, field.Name);
                children.Add(ExtendedBufferParser.RawNode(field.Name, bytes, offset, budget));
                return false;
            }
            case RopFieldKind.Compound:
            {
                var nested = ImmutableArray.CreateBuilder<MapiNode>();
                foreach (var member in field.Members)
                {
                    ParseField(ref reader, member, nested, operationIndex, handleReferences, budget);
                }
                budget.Claim(0);
                children.Add(new MapiNode(
                    field.Name, MapiNodeKind.Structure, offset, reader.Position - offset, null, nested.ToImmutable()));
                return false;
            }
            default:
                throw new NotSupportedException($"Unhandled ROP field kind {field.Kind}.");
        }
    }

    private static string FormatReturnValue(uint value) =>
        value == 0 ? "0x00000000 (Success)" : $"0x{value:X8} (Failure)";

    // Compound wire types shared by several schemas. Sizes and member order are taken directly from
    // the pinned upstream FolderID.cs / MessageID.cs / LongTermID.cs / PropertyTag.cs, which mirror
    // [MS-OXCDATA] 2.2.1.1, 2.2.1.2, 2.2.1.3.1, and 2.9 respectively.
    private static RopField FolderOrMessageId(string name) =>
        RopField.Group(name, RopField.Of("ReplicaId", RopFieldKind.UInt16), RopField.Bytes("GlobalCounter", 6));

    private static RopField LongTermId(string name) =>
        RopField.Group(
            name,
            RopField.Of("DatabaseGuid", RopFieldKind.Guid),
            RopField.Bytes("GlobalCounter", 6),
            RopField.Of("Pad", RopFieldKind.UInt16));

    private static RopField PropertyTagField(string name) =>
        RopField.Group(name, RopField.Of("PropertyType", RopFieldKind.UInt16), RopField.Of("PropertyId", RopFieldKind.UInt16));

    // ---- Request schemas -----------------------------------------------------------------------
    // Every request schema begins with RopId(1) + LogonId(1) + InputHandleIndex(1), exactly as
    // specified for each ROP's request buffer in MS-OXCROPS. Only ROPs whose upstream *Request.cs
    // Parse() method is fully unconditional (no loops, no counted arrays, no null-terminated
    // strings) are included below; every other request ROP is left undecoded (raw) on purpose.

    // Internal (not private) so tests can enumerate the full catalog and drive exhaustive,
    // schema-derived coverage of every implemented dispatcher through the public parse entry point.
    internal static readonly ImmutableDictionary<byte, RopOperationSchema> RequestSchemas = BuildRequestSchemas();

    private static ImmutableDictionary<byte, RopOperationSchema> BuildRequestSchemas()
    {
        static RopField[] Header(params RopField[] extra)
        {
            var fields = new RopField[3 + extra.Length];
            fields[0] = RopField.Of("RopId", RopFieldKind.RopId);
            fields[1] = RopField.Of("LogonId", RopFieldKind.LogonId);
            fields[2] = RopField.Of("InputHandleIndex", RopFieldKind.HandleIndex);
            Array.Copy(extra, 0, fields, 3, extra.Length);
            return fields;
        }

        static RopOperationSchema S(byte ropId, RopField[] fields) =>
            new() { RopId = ropId, Root = RopFieldTier.Of(fields) };

        var builder = ImmutableDictionary.CreateBuilder<byte, RopOperationSchema>();
        void Add(RopOperationSchema schema) => builder.Add(schema.RopId, schema);

        // Group A: bare 3-byte requests - RopId + LogonId + InputHandleIndex only.
        Add(S(0x01, Header())); // RopRelease
        Add(S(0x09, Header())); // RopGetPropertiesList
        Add(S(0x16, Header())); // RopGetStatus
        Add(S(0x17, Header())); // RopQueryPosition
        Add(S(0x1B, Header())); // RopCreateBookmark
        Add(S(0x37, Header())); // RopQueryColumnsAll
        Add(S(0x38, Header())); // RopAbort
        Add(S(0x47, Header())); // RopSetSpooler
        Add(S(0x49, Header())); // RopGetAddressTypes
        Add(S(0x4A, Header())); // RopTransportSend
        Add(S(0x52, Header())); // RopGetValidAttachments
        Add(S(0x5D, Header())); // RopCommitStream
        Add(S(0x5E, Header())); // RopGetStreamSize
        Add(S(0x68, Header())); // RopGetReceiveFolderTable
        Add(S(0x6D, Header())); // RopGetTransportFolder
        Add(S(0x77, Header())); // RopSynchronizationUploadStateStreamEnd
        Add(S(0x7B, Header())); // RopGetStoreState
        Add(S(0x81, Header())); // RopResetTable

        // Group B: 3-byte header plus one fixed-width extra field.
        Add(S(0x2F, Header(RopField.Of("StreamSize", RopFieldKind.UInt64)))); // RopSetStreamSize
        Add(S(0x3B, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex)))); // RopCloneStream
        Add(S(0x82, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex)))); // RopSynchronizationGetTransferState
        Add(S(0x86, Header(RopField.Bytes("Version", 6)))); // RopTellVersion
        Add(S(0x61, Header(LongTermId("LongTermId")))); // RopGetPerUserGuid
        Add(S(0x43, Header(RopField.Bytes("ObjectId", 8)))); // RopLongTermIdFromId
        Add(S(0x44, Header(LongTermId("LongTermId")))); // RopIdFromLongTermId
        Add(S(0x24, Header(RopField.Of("AttachmentID", RopFieldKind.UInt32)))); // RopDeleteAttachment
        Add(S(0x23, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex)))); // RopCreateAttachment
        Add(S(0x5A, Header(RopField.Of("CategoryId", RopFieldKind.Int64)))); // RopCollapseRow
        Add(S(0x1F, Header(FolderOrMessageId("MessageId")))); // RopGetMessageStatus
        Add(S(0x10, Header(RopField.Of("Reserved", RopFieldKind.UInt16)))); // RopReloadCachedInformation
        Add(S(0x50, Header(RopField.Of("WantCancel", RopFieldKind.Bool)))); // RopProgress
        Add(S(0x7F, Header(RopField.Of("IdCount", RopFieldKind.UInt32)))); // RopGetLocalReplicaIds
        Add(S(0x60, Header(RopField.Of("DatabaseGuid", RopFieldKind.Guid)))); // RopGetPerUserLongTermIds
        Add(S(0x45, Header(FolderOrMessageId("FolderId")))); // RopPublicFolderIsGhosted
        Add(S(0x0D, Header(RopField.Of("Reserved", RopFieldKind.UInt32)))); // RopRemoveAllRecipients
        Add(S(0x42, Header(FolderOrMessageId("FolderId")))); // RopGetOwningServers

        // Group C: 3-byte header plus two fixed-width extra fields (or an extra handle index).
        Add(S(0x2E, Header(RopField.Of("Origin", RopFieldKind.Byte), RopField.Of("Offset", RopFieldKind.UInt64)))); // RopSeekStream
        Add(S(0x32, Header(RopField.Of("SubmitFlags", RopFieldKind.Byte)))); // RopSubmitMessage
        Add(S(0x05, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex), RopField.Of("TableFlags", RopFieldKind.Byte)))); // RopGetContentsTable
        Add(S(0x04, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex), RopField.Of("TableFlags", RopFieldKind.Byte)))); // RopGetHierarchyTable
        Add(S(0x58, Header(RopField.Of("WantAsynchronous", RopFieldKind.Bool), RopField.Of("WantDeleteAssociated", RopFieldKind.Bool)))); // RopEmptyFolder
        Add(S(0x1D, Header(RopField.Of("DeleteFolderFlags", RopFieldKind.Byte), FolderOrMessageId("FolderId")))); // RopDeleteFolder
        Add(S(0x3F, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex), RopField.Of("TableFlags", RopFieldKind.Byte)))); // RopGetRulesTable
        Add(new RopOperationSchema
        {
            RopId = 0x25, // RopSaveChangesAttachment - field order per upstream differs from Header().
            Root = RopFieldTier.Of(
                RopField.Of("RopId", RopFieldKind.RopId),
                RopField.Of("LogonId", RopFieldKind.LogonId),
                RopField.Of("ResponseHandleIndex", RopFieldKind.HandleIndex),
                RopField.Of("InputHandleIndex", RopFieldKind.HandleIndex),
                RopField.Of("SaveFlags", RopFieldKind.Byte)),
        });
        Add(S(0x3E, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex), RopField.Of("TableFlags", RopFieldKind.Byte)))); // RopGetPermissionsTable
        Add(S(0x1A, Header(RopField.Of("Numerator", RopFieldKind.UInt32), RopField.Of("Denominator", RopFieldKind.UInt32)))); // RopSeekRowFractional
        Add(S(0x7E, Header(RopField.Of("OutputHandleIndex", RopFieldKind.HandleIndex), RopField.Of("IsContentsCollector", RopFieldKind.Bool)))); // RopSynchronizationOpenCollector
        Add(S(0x75, Header(PropertyTagField("StateProperty"), RopField.Of("TransferBufferSize", RopFieldKind.UInt32)))); // RopSynchronizationUploadStateStreamBegin
        Add(S(0x6B, Header(RopField.Of("RowId", RopFieldKind.Int64), RopField.Of("RowInstanceNumber", RopFieldKind.UInt32)))); // RopGetCollapseState
        Add(S(0x59, Header(RopField.Of("MaxRowCount", RopFieldKind.UInt16), RopField.Of("CategoryId", RopFieldKind.Int64)))); // RopExpandRow
        Add(S(0x48, Header(FolderOrMessageId("MessageId"), RopField.Of("LockState", RopFieldKind.Byte)))); // RopSpoolerLockMessage

        return builder.ToImmutable();
    }

    // ---- Response schemas -----------------------------------------------------------------------
    // Every response schema begins with RopId(1) + a handle-index field(1) + ReturnValue(4) = 6
    // bytes, exactly as specified for each ROP's response buffer in MS-OXCROPS. Group D is present
    // unconditionally (the response is exactly 6 bytes on both success and failure). Group E's
    // response carries additional bytes only when ReturnValue is Success (or, for
    // RopSetMessageReadFlag, a further nested boolean read after Success) - a finite, self-determined
    // set of discrete fixed widths derived solely from bytes already read in the same operation.

    internal static readonly ImmutableDictionary<byte, RopOperationSchema> ResponseSchemas = BuildResponseSchemas();

    private static ImmutableDictionary<byte, RopOperationSchema> BuildResponseSchemas()
    {
        static RopOperationSchema Fixed6(byte ropId, string handleName) => new()
        {
            RopId = ropId,
            Root = RopFieldTier.Of(
                RopField.Of("RopId", RopFieldKind.RopId),
                RopField.Of(handleName, RopFieldKind.HandleIndex),
                RopField.Of("ReturnValue", RopFieldKind.ReturnValue)),
        };

        static RopOperationSchema Gated(byte ropId, string handleName, RopFieldTier whenSuccess) => new()
        {
            RopId = ropId,
            Root = new RopFieldTier
            {
                Fields =
                [
                    RopField.Of("RopId", RopFieldKind.RopId),
                    RopField.Of(handleName, RopFieldKind.HandleIndex),
                    RopField.Of("ReturnValue", RopFieldKind.ReturnValue),
                ],
                WhenTrue = whenSuccess,
            },
        };

        var builder = ImmutableDictionary.CreateBuilder<byte, RopOperationSchema>();
        void Add(RopOperationSchema schema) => builder.Add(schema.RopId, schema);

        // Group D: universal fixed 6-byte responses (RopId + handle index + ReturnValue only).
        Add(Fixed6(0x0D, "InputHandleIndex")); // RopRemoveAllRecipients
        Add(Fixed6(0x0E, "InputHandleIndex")); // RopModifyRecipients
        Add(Fixed6(0x1A, "InputHandleIndex")); // RopSeekRowFractional
        Add(Fixed6(0x21, "OutputHandleIndex")); // RopGetAttachmentTable
        Add(Fixed6(0x22, "OutputHandleIndex")); // RopOpenAttachment
        Add(Fixed6(0x24, "InputHandleIndex")); // RopDeleteAttachment
        Add(Fixed6(0x25, "ResponseHandleIndex")); // RopSaveChangesAttachment
        Add(Fixed6(0x26, "InputHandleIndex")); // RopSetReceiveFolder
        Add(Fixed6(0x29, "OutputHandleIndex")); // RopRegisterNotification
        Add(Fixed6(0x2F, "InputHandleIndex")); // RopSetStreamSize
        Add(Fixed6(0x30, "InputHandleIndex")); // RopSetSearchCriteria
        Add(Fixed6(0x32, "InputHandleIndex")); // RopSubmitMessage
        Add(Fixed6(0x34, "InputHandleIndex")); // RopAbortSubmit
        Add(Fixed6(0x3B, "OutputHandleIndex")); // RopCloneStream
        Add(Fixed6(0x3E, "OutputHandleIndex")); // RopGetPermissionsTable
        Add(Fixed6(0x3F, "OutputHandleIndex")); // RopGetRulesTable
        Add(Fixed6(0x40, "InputHandleIndex")); // RopModifyPermissions
        Add(Fixed6(0x41, "InputHandleIndex")); // RopModifyRules
        Add(Fixed6(0x47, "InputHandleIndex")); // RopSetSpooler
        Add(Fixed6(0x48, "InputHandleIndex")); // RopSpoolerLockMessage
        Add(Fixed6(0x4B, "OutputHandleIndex")); // RopFastTransferSourceCopyMessages
        Add(Fixed6(0x4C, "OutputHandleIndex")); // RopFastTransferSourceCopyFolder
        Add(Fixed6(0x4D, "OutputHandleIndex")); // RopFastTransferSourceCopyTo
        Add(Fixed6(0x51, "InputHandleIndex")); // RopTransportNewMail
        Add(Fixed6(0x53, "OutputHandleIndex")); // RopFastTransferDestinationConfigure
        Add(Fixed6(0x57, "InputHandleIndex")); // RopUpdateDeferredActionMessages
        Add(Fixed6(0x5B, "InputHandleIndex")); // RopLockRegionStream
        Add(Fixed6(0x5C, "InputHandleIndex")); // RopUnlockRegionStream
        Add(Fixed6(0x5D, "InputHandleIndex")); // RopCommitStream
        Add(Fixed6(0x64, "InputHandleIndex")); // RopWritePerUserInformation
        Add(Fixed6(0x69, "OutputHandleIndex")); // RopFastTransferSourceCopyProperties
        Add(Fixed6(0x70, "OutputHandleIndex")); // RopSynchronizationConfigure
        Add(Fixed6(0x74, "InputHandleIndex")); // RopSynchronizationImportDeletes
        Add(Fixed6(0x75, "InputHandleIndex")); // RopSynchronizationUploadStateStreamBegin
        Add(Fixed6(0x76, "InputHandleIndex")); // RopSynchronizationUploadStateStreamContinue
        Add(Fixed6(0x77, "InputHandleIndex")); // RopSynchronizationUploadStateStreamEnd
        Add(Fixed6(0x7E, "OutputHandleIndex")); // RopSynchronizationOpenCollector
        Add(Fixed6(0x80, "InputHandleIndex")); // RopSynchronizationImportReadStateChanges
        Add(Fixed6(0x81, "InputHandleIndex")); // RopResetTable
        Add(Fixed6(0x82, "OutputHandleIndex")); // RopSynchronizationGetTransferState
        Add(Fixed6(0x86, "InputHandleIndex")); // RopTellVersion
        Add(Fixed6(0x89, "InputHandleIndex")); // RopFreeBookmark
        Add(Fixed6(0x93, "InputHandleIndex")); // RopSetLocalReplicaMidsetDeleted

        // Group E: ReturnValue-gated responses - 6 bytes on failure, plus a self-determined fixed
        // extension on success.
        Add(Gated(0x16, "InputHandleIndex", RopFieldTier.Of(RopField.Of("TableStatus", RopFieldKind.Byte)))); // RopGetStatus
        Add(Gated(0x17, "InputHandleIndex", RopFieldTier.Of(
            RopField.Of("Numerator", RopFieldKind.UInt32), RopField.Of("Denominator", RopFieldKind.UInt32)))); // RopQueryPosition
        Add(Gated(0x43, "InputHandleIndex", RopFieldTier.Of(LongTermId("LongTermId")))); // RopLongTermIdFromId
        Add(Gated(0x44, "InputHandleIndex", RopFieldTier.Of(RopField.Bytes("ObjectId", 8)))); // RopIdFromLongTermId
        Add(Gated(0x2E, "InputHandleIndex", RopFieldTier.Of(RopField.Of("NewPosition", RopFieldKind.UInt64)))); // RopSeekStream
        Add(Gated(0x5E, "InputHandleIndex", RopFieldTier.Of(RopField.Of("StreamSize", RopFieldKind.UInt32)))); // RopGetStreamSize
        Add(Gated(0x61, "InputHandleIndex", RopFieldTier.Of(RopField.Of("DatabaseGuid", RopFieldKind.Guid)))); // RopGetPerUserGuid
        Add(Gated(0x6D, "InputHandleIndex", RopFieldTier.Of(FolderOrMessageId("FolderId")))); // RopGetTransportFolder
        Add(Gated(0x7B, "InputHandleIndex", RopFieldTier.Of(RopField.Of("StoreState", RopFieldKind.UInt32)))); // RopGetStoreState
        Add(Gated(0x11, "ResponseHandleIndex", new RopFieldTier
        {
            // RopSetMessageReadFlag: Success gates ReadStatusChanged; ReadStatusChanged==true then
            // gates a further LogonId + fixed 24-byte ClientData tier.
            Fields = [RopField.Of("ReadStatusChanged", RopFieldKind.Bool)],
            WhenTrue = RopFieldTier.Of(
                RopField.Of("LogonId", RopFieldKind.Byte),
                RopField.Bytes("ClientData", 24)),
        })); // RopSetMessageReadFlag

        return builder.ToImmutable();
    }
}
