using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

/// <summary>
/// The mutable-free parse context threaded through the FastTransfer ROP decoders. It holds only the
/// collaborators handed in by the caller for one operation; it is never cached, never static, and
/// never shared between parses.
/// </summary>
internal sealed class FastTransferParseContext
{
    public required int OperationIndex { get; init; }
    public required MapiDirection Direction { get; init; }
    public required List<RopHandleReference> HandleReferences { get; init; }
    public required MapiNodeBudget Budget { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Optional warning sink; when absent, warnings become in-tree Warning nodes.</summary>
    public List<string>? Warnings { get; init; }

    /// <summary>Optional capture-local stream reassembler (see <see cref="FastTransferStreamAssembler"/>).</summary>
    public FastTransferStreamAssembler? Assembler { get; init; }

    /// <summary>Opaque capture-local scope (e.g. the HTTP session id) used to key streams.</summary>
    public string? CaptureScope { get; init; }

    /// <summary>Capture state used to resolve buffer-local handle indices to stable server handles.</summary>
    public MapiCaptureContext? CaptureContext { get; init; }
}

/// <summary>
/// Semantic decoders for the MS-OXCFXICS (Bulk Data Transfer / Incremental Change Synchronization)
/// ROP family. This class exposes the same <c>Supports</c>/<c>Parse</c> pair as
/// <see cref="RopVariableDispatcher"/> and is registered there: every RopId in
/// <see cref="SupportedRequestRopIds"/>/<see cref="SupportedResponseRopIds"/> is routed to this
/// family's extended <c>Parse</c> overload
/// overload, together with the capture-local <see cref="FastTransferStreamAssembler"/> and capture
/// scope threaded down from <c>MapiCaptureContext</c>/<c>MapiCaptureParser</c>, so transfer buffers
/// split across several RopFastTransferSourceGetBuffer/RopFastTransferDestinationPutBuffer
/// operations within the same capture are joined rather than decoded in isolation.
/// <para>
/// Every ROP decoded here has an operation boundary that is entirely self-contained: its width is a
/// function only of bytes read within the same operation (a count, a declared size, or a
/// success/status gate). ROPs whose width depends on a previous operation, a prior ROP or external
/// session state are not implemented, so no operation boundary is ever guessed.
/// </para>
/// <para>
/// Wire layouts come from MS-OXCROPS 2.2.12/2.2.13 (request and response buffers) and MS-OXCFXICS
/// 2.2.3, cross-checked against the pinned MIT upstream MAPIInspector MSOXCFXICS parsers.
/// </para>
/// </summary>
internal static class RopFastTransferDecoders
{
    private const uint ServerBusy = 0x00000480;

    /// <summary>The request ROPs this family decodes, in ascending RopId order.</summary>
    internal static readonly ImmutableArray<byte> SupportedRequestRopIds =
    [
        0x4B, 0x4C, 0x4D, 0x4E, 0x53, 0x54, 0x69, 0x70, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x80, 0x93, 0x9D,
    ];

    /// <summary>The response ROPs this family decodes, in ascending RopId order.</summary>
    internal static readonly ImmutableArray<byte> SupportedResponseRopIds =
    [
        0x4E, 0x54, 0x72, 0x73, 0x75, 0x76, 0x77, 0x78, 0x7F, 0x9D,
    ];

    private static readonly ImmutableHashSet<byte> RequestSet = [.. SupportedRequestRopIds];
    private static readonly ImmutableHashSet<byte> ResponseSet = [.. SupportedResponseRopIds];

    public static bool Supports(MapiDirection direction, byte ropId) => direction switch
    {
        MapiDirection.Request => RequestSet.Contains(ropId),
        MapiDirection.Response => ResponseSet.Contains(ropId),
        _ => false,
    };

    /// <summary>
    /// The standard family entry point, matching <see cref="RopVariableDispatcher.Parse"/> exactly.
    /// Lexer warnings are surfaced as in-tree Warning nodes because this overload has no sink.
    /// </summary>
    public static MapiNode Parse(
        ref MapiReader reader,
        int operationIndex,
        MapiDirection direction,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken) =>
        Parse(ref reader, operationIndex, direction, handleReferences, budget, cancellationToken, null, null, null, null);

    /// <summary>
    /// The extended entry point. <paramref name="assembler"/> and <paramref name="captureScope"/> are
    /// optional; when both are supplied, transfer buffers are lexed as slices of a capture-local
    /// reconstructed stream so values split across buffers are joined.
    /// </summary>
    public static MapiNode Parse(
        ref MapiReader reader,
        int operationIndex,
        MapiDirection direction,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings,
        FastTransferStreamAssembler? assembler,
        string? captureScope,
        MapiCaptureContext? captureContext = null)
    {
        ArgumentNullException.ThrowIfNull(handleReferences);
        ArgumentNullException.ThrowIfNull(budget);

        var ropId = reader.PeekByte("RopId");
        if (!Supports(direction, ropId))
        {
            throw new MapiParseException(
                reader.Position,
                $"ROP 0x{ropId:X2} is not decoded by the MS-OXCFXICS {direction.ToString().ToLowerInvariant()} family.");
        }

        var context = new FastTransferParseContext
        {
            OperationIndex = operationIndex,
            Direction = direction,
            HandleReferences = handleReferences,
            Budget = budget,
            CancellationToken = cancellationToken,
            Warnings = warnings,
            Assembler = assembler,
            CaptureScope = captureScope,
            CaptureContext = captureContext,
        };

        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        if (direction == MapiDirection.Request)
        {
            ParseRequest(ref reader, ropId, children, context);
        }
        else
        {
            ParseResponse(ref reader, ropId, children, context);
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

    // ---- Requests ---------------------------------------------------------------------------------

    private static void ParseRequest(
        ref MapiReader reader,
        byte ropId,
        ImmutableArray<MapiNode>.Builder children,
        FastTransferParseContext context)
    {
        ReadRopId(ref reader, children, context);
        ReadLogonId(ref reader, children, context);
        var inputHandle = ReadHandle(ref reader, children, "InputHandleIndex", context);

        switch (ropId)
        {
            case 0x4B: // RopFastTransferSourceCopyMessages (MS-OXCROPS 2.2.12.7.1).
            {
                var outputHandle = ReadHandle(ref reader, children, "OutputHandleIndex", context);
                ConfigureRoot(context, outputHandle, FastTransferRootKind.MessageList, "RopFastTransferSourceCopyMessages");
                var countOffset = reader.Position;
                var count = reader.ReadCount16("MessageIdCount");
                AddField(children, "MessageIdCount", countOffset, 2, Count(count), context);
                RequireRemaining(ref reader, checked(count * 8), "MessageIds", countOffset);
                for (var index = 0; index < count; index++)
                {
                    children.Add(ReadMessageOrFolderId(ref reader, $"MessageIds[{index}]", "MessageID", context, 1));
                }
                ReadFlagsByte(ref reader, children, "CopyFlags", CopyFlagsCopyMessages, context);
                ReadFlagsByte(ref reader, children, "SendOptions", SendOptions, context);
                break;
            }
            case 0x4C: // RopFastTransferSourceCopyFolder (MS-OXCROPS 2.2.12.6.1).
            {
                var outputHandle = ReadHandle(ref reader, children, "OutputHandleIndex", context);
                ConfigureRoot(context, outputHandle, FastTransferRootKind.TopFolder, "RopFastTransferSourceCopyFolder");
                ReadFlagsByte(ref reader, children, "CopyFlags", CopyFlagsCopyFolder, context);
                ReadFlagsByte(ref reader, children, "SendOptions", SendOptions, context);
                break;
            }
            case 0x4D: // RopFastTransferSourceCopyTo (MS-OXCROPS 2.2.12.8.1).
            {
                var outputHandle = ReadHandle(ref reader, children, "OutputHandleIndex", context);
                ConfigureRoot(
                    context,
                    outputHandle,
                    FastTransferRootKind.Unknown,
                    "RopFastTransferSourceCopyTo without a recovered Folder/Message/Attachment object type");
                ReadByteField(ref reader, children, "Level", context);
                var flagsOffset = reader.Position;
                var copyFlags = reader.ReadUInt32("CopyFlags");
                AddField(children, "CopyFlags", flagsOffset, 4, Flags(copyFlags, CopyFlagsCopyTo, 8), context);
                ReadFlagsByte(ref reader, children, "SendOptions", SendOptions, context);
                ReadPropertyTags(ref reader, children, context);
                break;
            }
            case 0x69: // RopFastTransferSourceCopyProperties (MS-OXCROPS 2.2.12.9.1).
            {
                var outputHandle = ReadHandle(ref reader, children, "OutputHandleIndex", context);
                ConfigureRoot(
                    context,
                    outputHandle,
                    FastTransferRootKind.Unknown,
                    "RopFastTransferSourceCopyProperties without a recovered Folder/Message/Attachment object type");
                ReadByteField(ref reader, children, "Level", context);
                ReadFlagsByte(ref reader, children, "CopyFlags", CopyFlagsCopyProperties, context);
                ReadFlagsByte(ref reader, children, "SendOptions", SendOptions, context);
                ReadPropertyTags(ref reader, children, context);
                break;
            }
            case 0x4E: // RopFastTransferSourceGetBuffer (MS-OXCROPS 2.2.12.4.1).
            {
                var sizeOffset = reader.Position;
                var bufferSize = reader.ReadUInt16("BufferSize");
                AddField(
                    children,
                    "BufferSize",
                    sizeOffset,
                    2,
                    bufferSize == 0xBABE ? "0xBABE (MaximumBufferSize follows)" : Count(bufferSize),
                    context);
                if (bufferSize == 0xBABE)
                {
                    var maxOffset = reader.Position;
                    var maximum = reader.ReadUInt16("MaximumBufferSize");
                    AddField(children, "MaximumBufferSize", maxOffset, 2, Count(maximum), context);
                }
                break;
            }
            case 0x53: // RopFastTransferDestinationConfigure (MS-OXCROPS 2.2.12.1.1).
            {
                var outputHandle = ReadHandle(ref reader, children, "OutputHandleIndex", context);
                var sourceOperation = ReadEnumByte(ref reader, children, "SourceOperation", SourceOperations, context);
                var root = sourceOperation switch
                {
                    0x03 => FastTransferRootKind.MessageList,
                    0x04 => FastTransferRootKind.TopFolder,
                    _ => FastTransferRootKind.Unknown,
                };
                ConfigureRoot(
                    context,
                    outputHandle,
                    root,
                    root == FastTransferRootKind.Unknown
                        ? $"RopFastTransferDestinationConfigure SourceOperation 0x{sourceOperation:X2} without a recovered Folder/Message/Attachment object type"
                        : $"RopFastTransferDestinationConfigure SourceOperation 0x{sourceOperation:X2}");
                ReadFlagsByte(ref reader, children, "CopyFlags", CopyFlagsDestinationConfigure, context);
                break;
            }
            case 0x54: // RopFastTransferDestinationPutBuffer (MS-OXCROPS 2.2.12.2.1).
            case 0x9D: // RopFastTransferDestinationPutBufferExtended (MS-OXCROPS 2.2.12.3.1).
            {
                var sizeOffset = reader.Position;
                var size = reader.ReadUInt16("TransferDataSize");
                AddField(children, "TransferDataSize", sizeOffset, 2, Count(size), context);
                RequireRemaining(ref reader, size, "TransferData", sizeOffset);
                children.Add(LexTransfer(ref reader, size, "TransferData", context, inputHandle));
                break;
            }
            case 0x70: // RopSynchronizationConfigure (MS-OXCROPS 2.2.13.1.1).
            {
                var outputHandle = ReadHandle(ref reader, children, "OutputHandleIndex", context);
                var synchronizationType = ReadEnumByte(
                    ref reader, children, "SynchronizationType", SynchronizationTypes, context);
                ConfigureRoot(
                    context,
                    outputHandle,
                    synchronizationType switch
                    {
                        0x01 => FastTransferRootKind.ContentsSync,
                        0x02 => FastTransferRootKind.HierarchySync,
                        _ => FastTransferRootKind.Unknown,
                    },
                    $"RopSynchronizationConfigure SynchronizationType 0x{synchronizationType:X2}");
                ReadFlagsByte(ref reader, children, "SendOptions", SendOptions, context);
                var syncFlagsOffset = reader.Position;
                var syncFlags = reader.ReadUInt16("SynchronizationFlags");
                AddField(children, "SynchronizationFlags", syncFlagsOffset, 2, Flags(syncFlags, SynchronizationFlags, 4), context);
                var restrictionSizeOffset = reader.Position;
                var restrictionSize = reader.ReadUInt16("RestrictionDataSize");
                AddField(children, "RestrictionDataSize", restrictionSizeOffset, 2, Count(restrictionSize), context);
                RequireRemaining(ref reader, restrictionSize, "RestrictionData", restrictionSizeOffset);
                if (restrictionSize > 0)
                {
                    children.Add(ParseRestriction(ref reader, restrictionSize, context));
                }
                var extraOffset = reader.Position;
                var extraFlags = reader.ReadUInt32("SynchronizationExtraFlags");
                AddField(
                    children, "SynchronizationExtraFlags", extraOffset, 4, Flags(extraFlags, SynchronizationExtraFlags, 8), context);
                ReadPropertyTags(ref reader, children, context);
                break;
            }
            case 0x72: // RopSynchronizationImportMessageChange (MS-OXCROPS 2.2.13.2.1).
                ReadHandle(ref reader, children, "OutputHandleIndex", context);
                ReadFlagsByte(ref reader, children, "ImportFlag", ImportFlags, context);
                ReadTaggedValues(ref reader, children, "PropertyValueCount", "PropertyValues", context);
                break;
            case 0x73: // RopSynchronizationImportHierarchyChange (MS-OXCROPS 2.2.13.3.1).
                ReadTaggedValues(ref reader, children, "HierarchyValueCount", "HierarchyValues", context);
                ReadTaggedValues(ref reader, children, "PropertyValueCount", "PropertyValues", context);
                break;
            case 0x74: // RopSynchronizationImportDeletes (MS-OXCROPS 2.2.13.4.1).
                ReadFlagsByte(ref reader, children, "ImportDeleteFlags", ImportDeleteFlags, context);
                ReadTaggedValues(ref reader, children, "PropertyValueCount", "PropertyValues", context);
                break;
            case 0x75: // RopSynchronizationUploadStateStreamBegin (MS-OXCROPS 2.2.13.9.1).
            {
                var tagOffset = reader.Position;
                var propertyType = reader.ReadUInt16("StateProperty.PropertyType");
                var propertyId = reader.ReadUInt16("StateProperty.PropertyId");
                var stateProperty = ((uint)propertyId << 16) | propertyType;
                AddField(
                    children,
                    "StateProperty",
                    tagOffset,
                    4,
                    IcsStatePropertyName(stateProperty),
                    context);
                if (!IsLegalIcsStateProperty(stateProperty))
                {
                    Warn(
                        context,
                        $"StateProperty 0x{stateProperty:X8} is not one of the four ICS state properties.");
                }
                var sizeOffset = reader.Position;
                var transferBufferSize = reader.ReadUInt32("TransferBufferSize");
                AddField(
                    children,
                    "TransferBufferSize",
                    sizeOffset,
                    4,
                    transferBufferSize.ToString(CultureInfo.InvariantCulture),
                    context);
                context.CaptureContext?.StageIcsStateBegin(
                    context.CaptureScope,
                    inputHandle,
                    stateProperty,
                    transferBufferSize);
                break;
            }
            case 0x76: // RopSynchronizationUploadStateStreamContinue (MS-OXCROPS 2.2.13.10.1).
            {
                var sizeOffset = reader.Position;
                var size = reader.ReadUInt32("StreamDataSize");
                if (size > MapiParseLimits.MaxPayloadBytes || size > reader.Remaining)
                {
                    throw new MapiParseException(
                        sizeOffset, $"StreamDataSize {size:N0} exceeds the safe or remaining extent.");
                }
                AddField(children, "StreamDataSize", sizeOffset, 4, Count((int)size), context);
                var dataOffset = reader.Position;
                var data = reader.ReadBytes((int)size, "StreamData");
                children.Add(ExtendedBufferParser.RawNode("StreamData", data, dataOffset, context.Budget));
                context.CaptureContext?.StageIcsStateContinue(context.CaptureScope, inputHandle, data);
                break;
            }
            case 0x77: // RopSynchronizationUploadStateStreamEnd (MS-OXCROPS 2.2.13.11.1).
                context.CaptureContext?.StageIcsStateEnd(context.CaptureScope, inputHandle);
                break;
            case 0x78: // RopSynchronizationImportMessageMove (MS-OXCROPS 2.2.13.5.1).
                ReadSizedBinary(ref reader, children, "SourceFolderId", context);
                ReadSizedBinary(ref reader, children, "SourceMessageId", context);
                ReadSizedBinary(ref reader, children, "PredecessorChangeList", context);
                ReadSizedBinary(ref reader, children, "DestinationMessageId", context);
                ReadSizedBinary(ref reader, children, "ChangeNumber", context);
                break;
            case 0x80: // RopSynchronizationImportReadStateChanges (MS-OXCROPS 2.2.13.6.1).
            {
                var sizeOffset = reader.Position;
                var size = reader.ReadUInt16("MessageReadStatesSize");
                AddField(children, "MessageReadStatesSize", sizeOffset, 2, Count(size), context);
                RequireRemaining(ref reader, size, "MessageReadStates", sizeOffset);
                children.Add(ParseMessageReadStates(ref reader, size, context));
                break;
            }
            case 0x93: // RopSetLocalReplicaMidsetDeleted (MS-OXCROPS 2.2.13.12.1).
            {
                var sizeOffset = reader.Position;
                var size = reader.ReadUInt16("DataSize");
                AddField(children, "DataSize", sizeOffset, 2, Count(size), context);
                RequireRemaining(ref reader, size, "LongTermIdRanges", sizeOffset);
                children.Add(ParseLongTermIdRanges(ref reader, size, context));
                break;
            }
            default:
                throw new MapiParseException(
                    reader.Position, $"ROP 0x{ropId:X2} has no MS-OXCFXICS request decoder.");
        }
    }

    // ---- Responses --------------------------------------------------------------------------------

    private static void ParseResponse(
        ref MapiReader reader,
        byte ropId,
        ImmutableArray<MapiNode>.Builder children,
        FastTransferParseContext context)
    {
        var operationOffset = reader.Position;
        ReadRopId(ref reader, children, context);
        var handleIndex = ReadHandle(
            ref reader,
            children,
            ropId == 0x72 ? "OutputHandleIndex" : "InputHandleIndex",
            context);
        var returnValue = ReadReturnValue(ref reader, children, context);

        switch (ropId)
        {
            case 0x4E: // RopFastTransferSourceGetBuffer (MS-OXCROPS 2.2.12.4.2/2.2.12.4.3).
            {
                var transferStatus = ReadEnumUInt16(ref reader, children, "TransferStatus", TransferStatuses, context);
                ReadUInt16Field(ref reader, children, "InProgressCount", context);
                ReadUInt16Field(ref reader, children, "TotalStepCount", context);
                ReadByteField(ref reader, children, "Reserved", context);
                var sizeOffset = reader.Position;
                var bufferSize = reader.ReadUInt16("TransferBufferSize");
                AddField(children, "TransferBufferSize", sizeOffset, 2, Count(bufferSize), context);
                if (returnValue == 0)
                {
                    RequireRemaining(ref reader, bufferSize, "TransferBuffer", sizeOffset);
                    children.Add(LexTransfer(ref reader, bufferSize, "TransferBuffer", context, handleIndex));
                    if (transferStatus == 0x0003
                        && TryResolveStreamKey(context, handleIndex, out var completedKey))
                    {
                        var completedState = context.Assembler!.StateFor(completedKey);
                        if (completedState.Pending is not null || completedState.MarkerDepth != 0)
                        {
                            Warn(
                                context,
                                "RopFastTransferSourceGetBuffer reported Done while the reconstructed stream " +
                                "still has an incomplete value or unclosed syntactical markers.");
                        }
                        if (completedState.CompletionIssue is { } completionIssue)
                        {
                            Warn(
                                context,
                                "RopFastTransferSourceGetBuffer reported Done before the provenance-selected " +
                                $"{completionIssue} completed.");
                        }
                        context.Assembler!.Complete(completedKey);
                    }
                }
                else if (returnValue == ServerBusy)
                {
                    var backoffOffset = reader.Position;
                    var backoff = reader.ReadUInt32("BackoffTime");
                    AddField(
                        children, "BackoffTime", backoffOffset, 4, $"{backoff:N0} ms", context);
                }
                else
                {
                    if (bufferSize != 0)
                    {
                        RequireRemaining(ref reader, bufferSize, "TransferBuffer", sizeOffset);
                        var bufferOffset = reader.Position;
                        var buffer = reader.ReadBytes(bufferSize, "TransferBuffer");
                        children.Add(ExtendedBufferParser.RawNode(
                            "TransferBuffer (failed operation)", buffer, bufferOffset, context.Budget));
                        Warn(
                            context,
                            $"RopFastTransferSourceGetBuffer failed with 0x{returnValue:X8} but carried " +
                            $"{bufferSize:N0} transfer byte(s); they were retained as raw.");
                    }
                    if (TryResolveStreamKey(context, handleIndex, out var failedKey))
                    {
                        context.Assembler!.Forget(failedKey);
                    }
                }
                break;
            }
            case 0x54: // RopFastTransferDestinationPutBuffer (MS-OXCROPS 2.2.12.2.2).
                ReadIgnoredUInt16(ref reader, children, "TransferStatus", context);
                ReadUInt16Field(ref reader, children, "InProgressCount", context);
                ReadUInt16Field(ref reader, children, "TotalStepCount", context);
                ReadByteField(ref reader, children, "Reserved", context);
                var used = ReadUInt16Field(ref reader, children, "BufferUsedSize", context);
                WarnIfPresent(
                    context,
                    context.CaptureContext?.CompleteFastTransferUpload(
                        context.CaptureScope, handleIndex, returnValue == 0, used));
                break;
            case 0x9D: // RopFastTransferDestinationPutBufferExtended (MS-OXCROPS 2.2.12.3.2).
                ReadIgnoredUInt16(ref reader, children, "TransferStatus", context);
                ReadUInt32Field(ref reader, children, "InProgressCount", context);
                ReadUInt32Field(ref reader, children, "TotalStepCount", context);
                ReadByteField(ref reader, children, "Reserved", context);
                var extendedUsed = ReadUInt16Field(ref reader, children, "BufferUsedSize", context);
                WarnIfPresent(
                    context,
                    context.CaptureContext?.CompleteFastTransferUpload(
                        context.CaptureScope, handleIndex, returnValue == 0, extendedUsed));
                break;
            case 0x72: // RopSynchronizationImportMessageChange (MS-OXCROPS 2.2.13.2.2).
                if (returnValue == 0)
                {
                    children.Add(ReadMessageOrFolderId(ref reader, "MessageId", "MessageID", context, 0));
                }
                break;
            case 0x73: // RopSynchronizationImportHierarchyChange (MS-OXCROPS 2.2.13.3.2).
                if (returnValue == 0)
                {
                    children.Add(ReadMessageOrFolderId(ref reader, "FolderId", "FolderID", context, 0));
                }
                break;
            case 0x75: // RopSynchronizationUploadStateStreamBegin (MS-OXCROPS 2.2.13.9.2).
            case 0x76: // RopSynchronizationUploadStateStreamContinue (MS-OXCROPS 2.2.13.10.2).
            {
                if (context.CaptureContext is { } captureContext)
                {
                    captureContext.CompleteIcsStateOperation(
                        context.CaptureScope,
                        ropId,
                        handleIndex,
                        returnValue == 0,
                        out var stateWarning);
                    WarnIfPresent(context, stateWarning);
                }
                break;
            }
            case 0x77: // RopSynchronizationUploadStateStreamEnd (MS-OXCROPS 2.2.13.11.2).
            {
                if (context.CaptureContext is { } captureContext)
                {
                    var completion = captureContext.CompleteIcsStateOperation(
                        context.CaptureScope,
                        ropId,
                        handleIndex,
                        returnValue == 0,
                        out var stateWarning);
                    WarnIfPresent(context, stateWarning);
                    if (completion is not null)
                    {
                        WarnIfPresent(context, completion.Warning);
                        children.Add(ParseCompletedIcsState(completion, operationOffset, context));
                    }
                }
                break;
            }
            case 0x78: // RopSynchronizationImportMessageMove (MS-OXCROPS 2.2.13.5.2).
                if (returnValue == 0)
                {
                    children.Add(ReadMessageOrFolderId(ref reader, "MessageId", "MessageID", context, 0));
                }
                break;
            case 0x7F: // RopGetLocalReplicaIds (MS-OXCROPS 2.2.13.11.2).
                if (returnValue == 0)
                {
                    var guidOffset = reader.Position;
                    var replGuid = reader.ReadGuid("ReplGuid");
                    AddField(children, "ReplGuid", guidOffset, 16, replGuid.ToString(), context);
                    var counterOffset = reader.Position;
                    var counter = reader.ReadBytes(6, "GlobalCount");
                    AddField(children, "GlobalCount", counterOffset, 6, Convert.ToHexString(counter), context);
                }
                break;
            default:
                throw new MapiParseException(
                    reader.Position, $"ROP 0x{ropId:X2} has no MS-OXCFXICS response decoder.");
        }
    }

    // ---- Composite field readers --------------------------------------------------------------------

    private static MapiNode LexTransfer(
        ref MapiReader reader,
        int length,
        string name,
        FastTransferParseContext context,
        byte handleIndex)
    {
        var offset = reader.Position;
        var bytes = reader.ReadBytes(length, name);
        FastTransferLexResult result;
        if (TryResolveStreamKey(context, handleIndex, out var key)
            && context.Direction == MapiDirection.Request
            && context.CaptureContext is { } captureContext
            && context.CaptureScope is { } captureScope)
        {
            result = captureContext.StageFastTransferUpload(
                captureScope,
                handleIndex,
                key,
                bytes,
                offset,
                context.Budget,
                2,
                context.CancellationToken);
        }
        else if (TryResolveStreamKey(context, handleIndex, out key))
        {
            result = context.Assembler!.Continue(
                key,
                bytes,
                offset,
                context.Budget,
                2,
                context.CancellationToken);
        }
        else
        {
            if (context.Assembler is not null && context.CaptureContext is not null)
            {
                Warn(
                    context,
                    $"FastTransfer buffer handle index {handleIndex} could not be resolved to a server object " +
                    "handle; this slice was decoded independently and was not joined to later buffers.");
            }
            result = FastTransferStreamLexer.Lex(
                bytes,
                offset,
                FastTransferStreamState.Initial,
                context.Budget,
                2,
                context.CancellationToken);
        }

        var children = result.Nodes.ToBuilder();
        foreach (var warning in result.Warnings)
        {
            if (context.Warnings is { } sink)
            {
                sink.Add(warning);
            }
            else
            {
                context.Budget.Claim(2);
                children.Add(MapiNode.Leaf("Warning", MapiNodeKind.Warning, offset, 0, warning));
            }
        }

        context.Budget.Claim(1);
        var summary = $"{length:N0} byte(s), {result.ElementCount:N0} lexical element(s)" +
            (result.EndedInsideValue ? ", ends inside a value" : string.Empty);
        return new MapiNode(name, MapiNodeKind.Structure, offset, length, summary, children.ToImmutable());
    }

    private static bool TryResolveStreamKey(
        FastTransferParseContext context,
        byte handleIndex,
        out FastTransferStreamKey key)
    {
        key = default;
        return context.Assembler is not null
            && context.CaptureContext is not null
            && context.CaptureContext.TryGetFastTransferStreamKey(
                context.CaptureScope,
                handleIndex,
                allowProvisional: context.Direction == MapiDirection.Request,
                out key);
    }

    private static void ConfigureRoot(
        FastTransferParseContext context,
        byte outputHandleIndex,
        FastTransferRootKind root,
        string provenance)
    {
        WarnIfPresent(
            context,
            context.CaptureContext?.ConfigureFastTransferRoot(
                context.CaptureScope, outputHandleIndex, root, provenance));
    }

    private static MapiNode ParseCompletedIcsState(
        MapiCaptureContext.IcsStateCompletion completion,
        long offset,
        FastTransferParseContext context)
    {
        // A reconstructed state can contain thousands of IDSET/GLOBSET nodes. Bound it
        // independently so it cannot consume the enclosing response ROP-list budget.
        var stateBudget = new MapiNodeBudget();
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        if (completion.Data.IsEmpty)
        {
            stateBudget.Claim(1);
            children.Add(MapiNode.Leaf(
                "StateData",
                MapiNodeKind.Field,
                offset,
                0,
                "empty initial ICS state"));
        }
        else
        {
            var reader = new MapiReader(completion.Data.AsSpan(), context.CancellationToken);
            try
            {
                var decoded = FastTransferStreamLexer.ParseIdsetReplGuidValue(
                    ref reader,
                    stateBudget,
                    2);
                children.Add(MarkReconstructed(decoded, offset));
            }
            catch (MapiParseException exception)
            {
                Warn(
                    context,
                    $"Completed ICS state stream could not be decoded ({exception.Message}); it was retained as raw.");
                var fallbackBudget = new MapiNodeBudget();
                children.Add(MarkReconstructed(
                    ExtendedBufferParser.RawNode(
                        "StateData",
                        completion.Data.AsSpan(),
                        offset,
                        fallbackBudget),
                    offset));
                fallbackBudget.Claim(1);
                return new MapiNode(
                    "CompletedStateStream",
                    MapiNodeKind.Structure,
                    offset,
                    0,
                    $"{IcsStatePropertyName(completion.StateProperty)}; {completion.Data.Length:N0} byte(s) in " +
                    $"{completion.ChunkCount:N0} chunk(s); reconstructed",
                    children.ToImmutable());
            }
        }

        stateBudget.Claim(1);
        return new MapiNode(
            "CompletedStateStream",
            MapiNodeKind.Structure,
            offset,
            0,
            $"{IcsStatePropertyName(completion.StateProperty)}; {completion.Data.Length:N0} byte(s) in " +
            $"{completion.ChunkCount:N0} chunk(s); reconstructed",
            children.ToImmutable());
    }

    private static MapiNode MarkReconstructed(MapiNode node, long offset) =>
        node with
        {
            Offset = offset,
            Length = 0,
            Children = [.. node.Children.Select(child => MarkReconstructed(child, offset))],
        };

    private static void WarnIfPresent(FastTransferParseContext context, string? warning)
    {
        if (warning is not null)
        {
            Warn(context, warning);
        }
    }

    private static MapiNode ParseRestriction(ref MapiReader reader, int size, FastTransferParseContext context)
    {
        var offset = reader.Position;
        var slice = reader.SliceReader(size, "RestrictionData");
        var attempt = slice;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        try
        {
            children.Add(NspiRestrictionParser.Parse(ref attempt, context.Budget, 2, null, context.Warnings, MapiWireWidthContext.RopBuffer));
            if (!attempt.End)
            {
                var tailOffset = attempt.Position;
                var tail = attempt.ReadRemaining("RestrictionData trailing bytes");
                children.Add(ExtendedBufferParser.RawNode(
                    "RestrictionData trailing bytes", tail, tailOffset, context.Budget));
                Warn(context, $"RestrictionData left {tail.Length:N0} byte(s) unparsed.");
            }
        }
        catch (MapiParseException exception)
        {
            // Transactional: discard the partial decode entirely and keep the declared extent raw.
            children.Clear();
            var raw = slice.ReadRemaining("RestrictionData");
            children.Add(ExtendedBufferParser.RawNode("RestrictionData", raw, offset, context.Budget));
            Warn(context, $"RestrictionData could not be decoded ({exception.Message}); it was retained as raw.");
        }

        context.Budget.Claim(1);
        return new MapiNode(
            "RestrictionData", MapiNodeKind.Structure, offset, size, $"{size:N0} byte(s)", children.ToImmutable());
    }

    private static MapiNode ParseMessageReadStates(ref MapiReader reader, int size, FastTransferParseContext context)
    {
        var offset = reader.Position;
        var slice = reader.SliceReader(size, "MessageReadStates");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var index = 0;
        while (!slice.End)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (index >= MapiParseLimits.MaxCollectionCount)
            {
                throw new MapiParseException(slice.Position, "MessageReadStates exceeds the safe element limit.");
            }
            context.Budget.Claim(2);
            var entryStart = slice.Position;
            var entryChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var idSizeOffset = slice.Position;
            var idSize = slice.ReadUInt16("MessageIdSize");
            AddField(entryChildren, "MessageIdSize", idSizeOffset, 2, Count(idSize), context);
            if (idSize > slice.Remaining - 1)
            {
                throw new MapiParseException(
                    idSizeOffset,
                    $"MessageIdSize {idSize:N0} exceeds the {slice.Remaining:N0} remaining byte(s) of MessageReadStates.");
            }
            var idOffset = slice.Position;
            var id = slice.ReadBytes(idSize, "MessageId");
            AddField(entryChildren, "MessageId", idOffset, idSize, Convert.ToHexString(id), context);
            var markOffset = slice.Position;
            var mark = slice.ReadByte("MarkAsRead");
            AddField(entryChildren, "MarkAsRead", markOffset, 1, mark != 0 ? "true" : "false", context);
            children.Add(new MapiNode(
                $"MessageReadState[{index}]",
                MapiNodeKind.Structure,
                entryStart,
                slice.Position - entryStart,
                null,
                entryChildren.ToImmutable()));
            index++;
        }

        context.Budget.Claim(1);
        return new MapiNode(
            "MessageReadStates",
            MapiNodeKind.Array,
            offset,
            size,
            $"{index:N0} state(s)",
            children.ToImmutable());
    }

    private static MapiNode ParseLongTermIdRanges(ref MapiReader reader, int size, FastTransferParseContext context)
    {
        var offset = reader.Position;
        var slice = reader.SliceReader(size, "LongTermIdRangeData");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var countOffset = slice.Position;
        var count = slice.ReadCount32("LongTermIdRangeCount");
        AddField(children, "LongTermIdRangeCount", countOffset, 4, Count(count), context);
        if (checked(count * 48) > slice.Remaining)
        {
            throw new MapiParseException(
                countOffset,
                $"LongTermIdRangeCount {count:N0} needs {count * 48:N0} byte(s) but only {slice.Remaining:N0} remain.");
        }
        for (var index = 0; index < count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.Budget.Claim(2);
            var entryStart = slice.Position;
            var entryChildren = ImmutableArray.CreateBuilder<MapiNode>();
            entryChildren.Add(FastTransferStreamLexer.ParseLongTermIdValue(ref slice, "MinLongTermId", context.Budget, 3));
            entryChildren.Add(FastTransferStreamLexer.ParseLongTermIdValue(ref slice, "MaxLongTermId", context.Budget, 3));
            children.Add(new MapiNode(
                $"LongTermIdRanges[{index}]",
                MapiNodeKind.Structure,
                entryStart,
                slice.Position - entryStart,
                null,
                entryChildren.ToImmutable()));
        }
        if (!slice.End)
        {
            var tailOffset = slice.Position;
            var tail = slice.ReadRemaining("LongTermIdRange trailing bytes");
            children.Add(ExtendedBufferParser.RawNode(
                "LongTermIdRange trailing bytes", tail, tailOffset, context.Budget));
            Warn(context, $"RopSetLocalReplicaMidsetDeleted left {tail.Length:N0} declared byte(s) unparsed.");
        }

        context.Budget.Claim(1);
        return new MapiNode(
            "LongTermIdRanges",
            MapiNodeKind.Array,
            offset,
            size,
            $"{count:N0} range(s)",
            children.ToImmutable());
    }

    private static void ReadSizedBinary(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        FastTransferParseContext context)
    {
        var sizeOffset = reader.Position;
        var size = NspiPropertyParser.ReadLength32(ref reader, $"{name}Size");
        AddField(children, $"{name}Size", sizeOffset, 4, Count(size), context);
        var valueOffset = reader.Position;
        var slice = reader.SliceReader(size, name);
        var entryChildren = ImmutableArray.CreateBuilder<MapiNode>();
        string summary;
        if (name == "PredecessorChangeList")
        {
            var attempt = slice;
            try
            {
                entryChildren.Add(
                    FastTransferStreamLexer.ParsePredecessorChangeListValue(ref attempt, context.Budget, 2));
                summary = "PredecessorChangeList";
            }
            catch (MapiParseException exception)
            {
                entryChildren.Clear();
                entryChildren.Add(ExtendedBufferParser.RawNode(
                    "Bytes", slice.ReadRemaining(name), valueOffset, context.Budget));
                summary = $"{size:N0} byte(s)";
                Warn(context, $"{name} could not be decoded ({exception.Message}); it was retained as raw.");
            }
        }
        else if (size == 22)
        {
            // GUID(16) + GlobalCounter(6): the unpadded long-term form used by the ICS import ROPs.
            var guidOffset = slice.Position;
            var guid = slice.ReadGuid($"{name}.DatabaseGuid");
            AddField(entryChildren, "DatabaseGuid", guidOffset, 16, guid.ToString(), context);
            var counterOffset = slice.Position;
            var counter = slice.ReadBytes(6, $"{name}.GlobalCounter");
            var counterHex = Convert.ToHexString(counter);
            AddField(entryChildren, "GlobalCounter", counterOffset, 6, counterHex, context);
            summary = $"{guid}-{counterHex}";
        }
        else
        {
            var bytes = slice.ReadRemaining(name);
            entryChildren.Add(ExtendedBufferParser.RawNode("Bytes", bytes, valueOffset, context.Budget));
            summary = $"{size:N0} byte(s)";
        }

        context.Budget.Claim(1);
        children.Add(new MapiNode(
            name, MapiNodeKind.Structure, valueOffset, size, summary, entryChildren.ToImmutable()));
    }

    private static void ReadTaggedValues(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string countName,
        string arrayName,
        FastTransferParseContext context)
    {
        // Each TaggedPropertyValue is parsed with MapiWireWidthContext.RopBuffer, so an embedded
        // PtypBinary value (e.g. PidTagChangeKey/PidTagPredecessorChangeList entries) reads its
        // 16-bit ROP-buffer byte count rather than NSPI/extended-rule's 32-bit form.
        var countOffset = reader.Position;
        var count = reader.ReadCount16(countName);
        AddField(children, countName, countOffset, 2, Count(count), context);
        var arrayStart = reader.Position;
        var values = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < count; index++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            values.Add(NspiPropertyParser.ParseStandardTaggedValue(
                ref reader,
                $"PropertyValue[{index}]",
                context.Budget,
                2,
                codePage: null,
                warnings: context.Warnings,
                context: MapiWireWidthContext.RopBuffer));
        }
        context.Budget.Claim(1);
        children.Add(new MapiNode(
            arrayName,
            MapiNodeKind.Array,
            arrayStart,
            reader.Position - arrayStart,
            $"{count:N0} value(s)",
            values.ToImmutable()));
    }

    private static void ReadPropertyTags(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        FastTransferParseContext context)
    {
        var countOffset = reader.Position;
        var count = reader.ReadCount16("PropertyTagCount");
        AddField(children, "PropertyTagCount", countOffset, 2, Count(count), context);
        RequireRemaining(ref reader, checked(count * 4), "PropertyTags", countOffset);
        var arrayStart = reader.Position;
        var tags = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < count; index++)
        {
            context.Budget.Claim(2);
            var tagStart = reader.Position;
            var tagChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var typeOffset = reader.Position;
            var propertyType = reader.ReadUInt16("PropertyType");
            AddField(children: tagChildren, "PropertyType", typeOffset, 2,
                NspiPropertyParser.PropertyTypeName(propertyType), context);
            var idOffset = reader.Position;
            var propertyId = reader.ReadUInt16("PropertyId");
            AddField(tagChildren, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(propertyId), context);
            tags.Add(new MapiNode(
                $"PropertyTags[{index}]",
                MapiNodeKind.Structure,
                tagStart,
                4,
                $"{MapiPropertyNames.FormatPidTag(propertyId)}:{propertyType:X4}",
                tagChildren.ToImmutable()));
        }
        context.Budget.Claim(1);
        children.Add(new MapiNode(
            "PropertyTags",
            MapiNodeKind.Array,
            arrayStart,
            reader.Position - arrayStart,
            $"{count:N0} tag(s)",
            tags.ToImmutable()));
    }

    private static MapiNode ReadMessageOrFolderId(
        ref MapiReader reader,
        string name,
        string label,
        FastTransferParseContext context,
        int depth)
    {
        context.Budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var replicaOffset = reader.Position;
        var replicaId = reader.ReadUInt16($"{name}.ReplicaId");
        AddField(children, "ReplicaId", replicaOffset, 2, $"{replicaId} (0x{replicaId:X4})", context);
        var counterOffset = reader.Position;
        var counter = reader.ReadBytes(6, $"{name}.GlobalCounter");
        var counterHex = Convert.ToHexString(counter);
        AddField(children, "GlobalCounter", counterOffset, 6, counterHex, context);
        return new MapiNode(
            name, MapiNodeKind.Structure, start, 8, $"{label} {replicaId}-{counterHex}", children.ToImmutable());
    }

    // ---- Primitive field readers --------------------------------------------------------------------

    private static void ReadRopId(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadByte("RopId");
        AddField(children, "RopId", offset, 1, $"0x{value:X2} ({RopBufferParser.Name(value)})", context);
    }

    private static byte ReadLogonId(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadByte("LogonId");
        AddField(children, "LogonId", offset, 1, value.ToString(CultureInfo.InvariantCulture), context);
        return value;
    }

    private static byte ReadHandle(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        AddField(children, name, offset, 1, value.ToString(CultureInfo.InvariantCulture), context);
        context.HandleReferences.Add(new RopHandleReference(context.OperationIndex, name, offset, value));
        return value;
    }

    private static uint ReadReturnValue(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32("ReturnValue");
        var rendered = value == 0
            ? "0x00000000 (Success)"
            : value == ServerBusy ? "0x00000480 (ecServerBusy)" : $"0x{value:X8}";
        AddField(children, "ReturnValue", offset, 4, rendered, context);
        return value;
    }

    private static void ReadByteField(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        AddField(children, name, offset, 1, $"0x{value:X2}", context);
    }

    private static ushort ReadUInt16Field(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        AddField(children, name, offset, 2, value.ToString(CultureInfo.InvariantCulture), context);
        return value;
    }

    private static void ReadUInt32Field(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        AddField(children, name, offset, 4, value.ToString(CultureInfo.InvariantCulture), context);
    }

    private static void ReadFlagsByte(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        ImmutableArray<(uint Value, string Name)> table,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        AddField(children, name, offset, 1, Flags(value, table, 2), context);
    }

    private static byte ReadEnumByte(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        ImmutableDictionary<uint, string> table,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        AddField(children, name, offset, 1, Enumerated(value, table, 2), context);
        return value;
    }

    private static ushort ReadEnumUInt16(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        ImmutableDictionary<uint, string> table,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        AddField(children, name, offset, 2, Enumerated(value, table, 4), context);
        return value;
    }

    private static void ReadIgnoredUInt16(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        FastTransferParseContext context)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        AddField(children, name, offset, 2, $"0x{value:X4} (clients MUST ignore)", context);
    }

    private static void AddField(
        ImmutableArray<MapiNode>.Builder children,
        string name,
        long offset,
        long length,
        string value,
        FastTransferParseContext context) =>
        ExtendedBufferParser.AddField(children, name, offset, length, value, context.Budget);

    private static void RequireRemaining(ref MapiReader reader, int needed, string what, long offset)
    {
        if (needed < 0 || needed > reader.Remaining)
        {
            throw new MapiParseException(
                offset, $"{what} needs {needed:N0} byte(s) but only {reader.Remaining:N0} remain.");
        }
    }

    private static void Warn(FastTransferParseContext context, string message) => context.Warnings?.Add(message);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool IsLegalIcsStateProperty(uint tag) => tag is
        0x40170003 or 0x40170102 or 0x67960102 or 0x67DA0102 or 0x67D20102;

    private static string IcsStatePropertyName(uint tag) => tag switch
    {
        0x40170003 => "0x40170003 MetaTagIdsetGiven (PtypInteger32)",
        0x40170102 => "0x40170102 MetaTagIdsetGiven (PtypBinary)",
        0x67960102 => "0x67960102 MetaTagCnsetSeen",
        0x67DA0102 => "0x67DA0102 MetaTagCnsetSeenFAI",
        0x67D20102 => "0x67D20102 MetaTagCnsetRead",
        _ => $"0x{tag:X8}",
    };

    private static string Enumerated(uint value, ImmutableDictionary<uint, string> table, int digits)
    {
        var hex = value.ToString($"X{digits}", CultureInfo.InvariantCulture);
        return table.TryGetValue(value, out var name) ? $"0x{hex} {name}" : $"0x{hex}";
    }

    private static string Flags(uint value, ImmutableArray<(uint Value, string Name)> table, int digits)
    {
        var hex = value.ToString($"X{digits}", CultureInfo.InvariantCulture);
        if (value == 0)
        {
            return $"0x{hex} (none)";
        }
        var names = new List<string>();
        var residual = value;
        foreach (var (bit, name) in table)
        {
            if ((value & bit) == bit)
            {
                names.Add(name);
                residual &= ~bit;
            }
        }
        if (residual != 0)
        {
            names.Add($"0x{residual:X}");
        }
        return $"0x{hex} ({string.Join(" | ", names)})";
    }

    // ---- Enumerations (MS-OXCFXICS 2.2.3 / MS-OXCROPS) ------------------------------------------------

    private static readonly ImmutableArray<(uint Value, string Name)> SendOptions =
    [
        (0x01, "Unicode"),
        (0x02, "UseCpid"),
        (0x04, "RecoverMode"),
        (0x08, "ForceUnicode"),
        (0x10, "PartialItem"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> CopyFlagsCopyTo =
    [
        (0x00000001, "Move"),
        (0x00002000, "BestBody"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> CopyFlagsCopyProperties =
    [
        (0x01, "Move"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> CopyFlagsCopyMessages =
    [
        (0x01, "Move"),
        (0x10, "BestBody"),
        (0x20, "SendEntryId"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> CopyFlagsCopyFolder =
    [
        (0x01, "Move"),
        (0x10, "CopySubfolders"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> CopyFlagsDestinationConfigure =
    [
        (0x01, "Move"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> SynchronizationFlags =
    [
        (0x0001, "Unicode"),
        (0x0002, "NoDeletions"),
        (0x0004, "IgnoreNoLongerInScope"),
        (0x0008, "ReadState"),
        (0x0010, "FAI"),
        (0x0020, "Normal"),
        (0x0080, "OnlySpecifiedProperties"),
        (0x0100, "NoForeignIdentifiers"),
        (0x1000, "Reserved"),
        (0x2000, "BestBody"),
        (0x4000, "IgnoreSpecifiedOnFAI"),
        (0x8000, "Progress"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> SynchronizationExtraFlags =
    [
        (0x00000001, "Eid"),
        (0x00000002, "MessageSize"),
        (0x00000004, "CN"),
        (0x00000008, "OrderByDeliveryTime"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> ImportFlags =
    [
        (0x10, "Associated"),
        (0x40, "FailOnConflict"),
    ];

    private static readonly ImmutableArray<(uint Value, string Name)> ImportDeleteFlags =
    [
        (0x01, "Hierarchy"),
        (0x02, "HardDelete"),
    ];

    private static readonly ImmutableDictionary<uint, string> SourceOperations =
        new Dictionary<uint, string>
        {
            [0x01] = "CopyTo",
            [0x02] = "CopyProperties",
            [0x03] = "CopyMessages",
            [0x04] = "CopyFolder",
        }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<uint, string> SynchronizationTypes =
        new Dictionary<uint, string>
        {
            [0x01] = "Contents",
            [0x02] = "Hierarchy",
        }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<uint, string> TransferStatuses =
        new Dictionary<uint, string>
        {
            [0x0000] = "Error",
            [0x0001] = "Partial",
            [0x0002] = "NoRoom",
            [0x0003] = "Done",
        }.ToImmutableDictionary();
}
