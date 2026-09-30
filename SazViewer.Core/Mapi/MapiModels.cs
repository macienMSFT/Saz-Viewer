using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace SazViewer.Core;

public enum MapiEndpoint
{
    Unknown,
    Mailbox,
    AddressBook
}

public enum MapiDirection
{
    Request,
    Response
}

public enum MapiNodeKind
{
    Structure,
    Field,
    Array,
    Operation,
    Property,
    Error,
    Warning,
    Unknown,
    Raw
}

public sealed record MapiNode(
    string Name,
    MapiNodeKind Kind,
    long Offset,
    long Length,
    string? Value,
    ImmutableArray<MapiNode> Children)
{
    public static MapiNode Leaf(
        string name,
        MapiNodeKind kind,
        long offset,
        long length,
        string? value = null) =>
        new(name, kind, offset, length, value, []);
}

public sealed record MapiMessageParse(
    MapiDirection Direction,
    MapiNode Root,
    ImmutableArray<string> Warnings,
    bool Complete,
    long ParsedBytes,
    long TotalBytes);

public sealed record MapiSession(
    string HttpSessionId,
    int ChronologicalIndex,
    MapiEndpoint Endpoint,
    string RequestType,
    string? ResponseCode,
    bool IsProtocolError,
    MapiMessageParse? Request,
    MapiMessageParse? Response,
    ImmutableArray<string> Warnings);

public sealed record MapiCapture(
    ImmutableArray<MapiSession> Sessions,
    ImmutableDictionary<string, MapiSession> ByHttpSessionId,
    MapiCoverage Coverage);

public sealed record MapiCoverage(
    int DetectedSessions,
    int ParsedRequests,
    int ParsedResponses,
    ImmutableArray<string> SupportedRequestTypes,
    ImmutableArray<string> SupportedNspiOperations,
    ImmutableArray<string> SupportedRopFamilies,
    ImmutableArray<string> KnownGaps);

internal sealed class MapiCaptureContext
{
    internal enum ServerObjectType
    {
        Unknown,
        Folder,
        Message,
        Attachment,
    }

    private readonly Dictionary<uint, string> logons = [];
    private readonly Dictionary<uint, string> handles = [];

    // [MS-OXCSTOR] (logical MAPI connection, LogonId) -> whether the RopLogon's LogonFlags had
    // Private (0x01) set. The HTTP-session-to-connection map lets later Execute round-trips reuse the
    // state without allowing unrelated mailboxes/clients that reuse a LogonId to overwrite it.
    // A null value records conflicting observations for one logical connection and deliberately makes
    // future shape-dependent operations fall back to raw rather than guessing.
    private readonly Dictionary<string, string> logonCorrelationScopes = [];
    private readonly Dictionary<string, HashSet<string>> cookieCorrelationScopes = [];
    private readonly Dictionary<string, HashSet<string>> correlationScopeCookies = [];
    private readonly Dictionary<string, HashSet<string>> correlationScopeCookieNames = [];
    private readonly Dictionary<string, HashSet<string>> fallbackCorrelationScopes = [];
    private readonly Dictionary<string, string> correlationScopeFallbacks = [];
    private readonly Dictionary<string, string> pendingFallbackCorrelationScopes = [];
    private readonly HashSet<string> saturatedCookieAliases = [];
    private readonly HashSet<string> saturatedFallbackScopes = [];
    private int cookieCorrelationRelationshipCount;
    private int fallbackCorrelationRelationshipCount;
    private bool cookieCorrelationGloballySaturated;
    private bool fallbackCorrelationGloballySaturated;
    private readonly Dictionary<(string Scope, byte LogonId), bool?> logonPrivacy = [];
    private readonly Dictionary<string, Queue<(byte LogonId, bool IsPrivate)>> pendingLogonPrivacy = [];

    // The handle table is carried after each ROP list, but operation decoders need its values while
    // walking that list. RopBufferParser validates and pre-registers it before semantic dispatch.
    private readonly Dictionary<string, ImmutableArray<uint>> sessionHandles = [];

    // A table's active columns belong to the server object handle within one logical MAPI
    // connection. SetColumns changes are queued from the request and committed only when the matching
    // response succeeds, preventing failed calls from poisoning later row boundaries.
    private readonly Dictionary<(string Scope, uint Handle), ImmutableArray<(ushort Type, ushort Id)>> tableColumns = [];
    private readonly Dictionary<
        string,
        Queue<(
            uint HandleIndex,
            (string Scope, uint Handle)? RequestHandle,
            ImmutableArray<(ushort Type, ushort Id)> Columns)>> pendingSetColumns = [];
    private readonly Dictionary<
        string,
        Queue<(uint HandleIndex, (string Scope, uint Handle)? RequestHandle)>> pendingResetTables = [];
    private readonly Dictionary<string, Queue<PendingFastTransferUpload>> pendingFastTransferUploads = [];
    private readonly Dictionary<string, HashSet<uint>> configuredFastTransferOutputSlots = [];
    private readonly Dictionary<string, Queue<PendingOutputObjectType>> pendingOutputObjectTypes = [];
    private readonly Dictionary<string, Dictionary<uint, ServerObjectType>> successfulOutputObjectTypes = [];
    private readonly Dictionary<string, List<PendingObjectRootDependency>> pendingObjectRootDependencies = [];
    private readonly Dictionary<(string Scope, uint Handle), ServerObjectType> serverObjectTypes = [];
    private readonly Dictionary<string, Queue<PendingIcsStateOperation>> pendingIcsStateOperations = [];
    private readonly Dictionary<IcsStateStreamKey, ActiveIcsStateUpload> activeIcsStateUploads = [];

    // RopGetPropertiesSpecific's request PropertyTags, queued per (HTTP session, handle index) so the
    // matching response - always in the very same Execute round-trip - can look up the tag list its
    // RowData was serialized against. A FIFO queue (not a single slot) lets the same handle index be
    // reused by more than one RopGetPropertiesSpecific call within one ROP list.
    private readonly Dictionary<string, Dictionary<uint, Queue<ImmutableArray<(ushort Type, ushort Id)>>>> propertySpecificTags = [];

    // Per-HTTP-session request ROP list shape: the list's total byte length plus, for j = 0..N, the
    // local byte offset reached once exactly j *response*-producing request operations have been
    // fully parsed (index 0 is initially 0). RopRelease (0x01) requests never produce a response
    // operation ([MS-OXCROPS] 2.2.1), so parsing one replaces the current checkpoint instead of
    // appending another. This is exactly what a same-session RopBufferTooSmall response needs to
    // compute its RequestBuffersSize from its own response-side operationIndex alone.
    private readonly Dictionary<string, (int TotalLength, ImmutableArray<int> ResponseCheckpoints)> requestRopLists = [];

    public IReadOnlyDictionary<uint, string> Logons => logons;
    public IReadOnlyDictionary<uint, string> Handles => handles;

    /// <summary>
    /// The single capture-local FastTransfer (MS-OXCFXICS) stream reassembler for this capture. It
    /// is instance-scoped to this <see cref="MapiCaptureContext"/> - one per <c>MapiCaptureParser</c>
    /// invocation - so streams from one parsed capture can never be joined to another.
    /// </summary>
    public FastTransferStreamAssembler FastTransferAssembler { get; } = new();

    private sealed class PendingFastTransferUpload
    {
        public required uint HandleIndex { get; init; }
        public required FastTransferStreamKey Key { get; set; }
        public required FastTransferStreamState BaseState { get; init; }
        public required FastTransferStreamState State { get; init; }
        public required byte[] Buffer { get; init; }
        public required long AbsoluteOffset { get; init; }
        public bool Invalidated { get; set; }
    }

    private readonly record struct PendingOutputObjectType(
        byte RopId,
        uint HandleIndex,
        ServerObjectType Type);

    private readonly record struct PendingObjectRootDependency(
        uint InputHandleIndex,
        uint OutputHandleIndex,
        string Operation);

    private readonly record struct IcsStateStreamKey(string Scope, uint Handle, bool Provisional = false);

    private sealed class PendingIcsStateOperation
    {
        public required byte RopId { get; init; }
        public required uint HandleIndex { get; init; }
        public required IcsStateStreamKey Key { get; set; }
        public uint StateProperty { get; init; }
        public uint DeclaredSize { get; init; }
        public byte[] Data { get; init; } = [];
    }

    private sealed class ActiveIcsStateUpload
    {
        public required uint StateProperty { get; init; }
        public required uint DeclaredSize { get; init; }
        public List<byte> Data { get; } = [];
        public int ChunkCount { get; set; }
    }

    internal sealed record IcsStateCompletion(
        uint StateProperty,
        uint DeclaredSize,
        ImmutableArray<byte> Data,
        int ChunkCount,
        string? Warning);

    public void SetLogon(uint id, string value)
    {
        if (logons.Count < MapiParseLimits.MaxStateEntries)
        {
            logons[id] = value;
        }
    }

    public void RecordSessionHandles(string? captureScope, ImmutableArray<uint> values)
    {
        if (captureScope is not null
            && (sessionHandles.Count < MapiParseLimits.MaxStateEntries || sessionHandles.ContainsKey(captureScope)))
        {
            sessionHandles[captureScope] = values;
        }
    }

    public void ClearSessionHandles(string? captureScope)
    {
        if (captureScope is not null)
        {
            sessionHandles.Remove(captureScope);
        }
    }

    public void EnqueueSetColumns(
        string? captureScope,
        uint handleIndex,
        ImmutableArray<(ushort Type, ushort Id)> columns)
    {
        if (captureScope is null
            || !TryGetServerHandle(captureScope, handleIndex, out var scope, out var handleValue))
        {
            return;
        }
        (string Scope, uint Handle)? requestHandle = handleValue == uint.MaxValue
            ? null
            : (scope, handleValue);

        if (!pendingSetColumns.TryGetValue(captureScope, out var queue))
        {
            if (pendingSetColumns.Count >= MapiParseLimits.MaxStateEntries)
            {
                return;
            }
            queue = [];
            pendingSetColumns[captureScope] = queue;
        }
        if (queue.Count < MapiParseLimits.MaxStateEntries)
        {
            queue.Enqueue((handleIndex, requestHandle, columns));
        }
    }

    public void CompleteSetColumns(string? captureScope, uint responseHandleIndex, bool success)
    {
        if (captureScope is null
            || !pendingSetColumns.TryGetValue(captureScope, out var queue)
            || queue.Count == 0)
        {
            InvalidateTableColumns(captureScope, responseHandleIndex);
            return;
        }

        var pending = queue.Dequeue();
        var hasResponseHandle = TryResolveServerHandle(captureScope, responseHandleIndex, out var responseHandle);
        if (!success)
        {
            if (pending.RequestHandle is { } failedRequestHandle)
            {
                tableColumns.Remove(failedRequestHandle);
            }
            if (hasResponseHandle)
            {
                tableColumns.Remove(responseHandle);
            }
            return;
        }

        if (pending.HandleIndex != responseHandleIndex
            || (pending.RequestHandle is { } requestHandle
                && hasResponseHandle
                && responseHandle != requestHandle))
        {
            if (pending.RequestHandle is { } mismatchedRequestHandle)
            {
                tableColumns.Remove(mismatchedRequestHandle);
            }
            if (hasResponseHandle)
            {
                tableColumns.Remove(responseHandle);
            }
            return;
        }

        var targetHandle = pending.RequestHandle
            ?? (hasResponseHandle ? responseHandle : null);
        if (targetHandle is { } target
            && (tableColumns.Count < MapiParseLimits.MaxStateEntries || tableColumns.ContainsKey(target)))
        {
            tableColumns[target] = pending.Columns;
        }
    }

    public bool TryGetTableColumns(
        string? captureScope,
        uint handleIndex,
        out ImmutableArray<(ushort Type, ushort Id)> columns)
    {
        columns = default;
        return captureScope is not null
            && TryResolveServerHandle(captureScope, handleIndex, out var handle)
            && tableColumns.TryGetValue(handle, out columns);
    }

    public void EnqueueResetTable(string? captureScope, uint handleIndex)
    {
        if (captureScope is null
            || !TryGetServerHandle(captureScope, handleIndex, out var scope, out var handleValue))
        {
            return;
        }

        if (!pendingResetTables.TryGetValue(captureScope, out var queue))
        {
            if (pendingResetTables.Count >= MapiParseLimits.MaxStateEntries)
            {
                return;
            }
            queue = [];
            pendingResetTables[captureScope] = queue;
        }
        if (queue.Count < MapiParseLimits.MaxStateEntries)
        {
            queue.Enqueue((
                handleIndex,
                handleValue == uint.MaxValue ? null : (scope, handleValue)));
        }
    }

    public void CompleteResetTable(string? captureScope, uint responseHandleIndex, bool success)
    {
        (string Scope, uint Handle) responseHandle = default;
        var hasResponseHandle = captureScope is not null
            && TryResolveServerHandle(captureScope, responseHandleIndex, out responseHandle);
        if (captureScope is null
            || !pendingResetTables.TryGetValue(captureScope, out var queue)
            || queue.Count == 0)
        {
            if (success && hasResponseHandle)
            {
                tableColumns.Remove(responseHandle);
            }
            return;
        }

        var pending = queue.Dequeue();
        if (!success)
        {
            return;
        }

        if (pending.RequestHandle is { } requestHandle)
        {
            tableColumns.Remove(requestHandle);
        }
        if (hasResponseHandle)
        {
            tableColumns.Remove(responseHandle);
        }
    }

    public void CompleteHttpSession(string captureScope)
    {
        if (pendingSetColumns.Remove(captureScope, out var pendingColumns))
        {
            while (pendingColumns.Count > 0)
            {
                var pending = pendingColumns.Dequeue();
                if (pending.RequestHandle is { } requestHandle)
                {
                    tableColumns.Remove(requestHandle);
                }
                if (TryResolveServerHandle(captureScope, pending.HandleIndex, out var responseHandle))
                {
                    tableColumns.Remove(responseHandle);
                }
            }
        }

        if (pendingResetTables.Remove(captureScope, out var pendingResets))
        {
            while (pendingResets.Count > 0)
            {
                var pending = pendingResets.Dequeue();
                if (pending.RequestHandle is { } requestHandle)
                {
                    tableColumns.Remove(requestHandle);
                }
                if (TryResolveServerHandle(captureScope, pending.HandleIndex, out var responseHandle))
                {
                    tableColumns.Remove(responseHandle);
                }
            }
        }

        propertySpecificTags.Remove(captureScope);
        requestRopLists.Remove(captureScope);
        pendingLogonPrivacy.Remove(captureScope);
        pendingFastTransferUploads.Remove(captureScope);
        configuredFastTransferOutputSlots.Remove(captureScope);
        pendingOutputObjectTypes.Remove(captureScope);
        successfulOutputObjectTypes.Remove(captureScope);
        pendingObjectRootDependencies.Remove(captureScope);
        if (pendingIcsStateOperations.Remove(captureScope, out var abandonedStateOperations))
        {
            foreach (var operation in abandonedStateOperations)
            {
                activeIcsStateUploads.Remove(operation.Key);
            }
        }
        foreach (var key in activeIcsStateUploads.Keys
                     .Where(key => key.Provisional && key.Scope == captureScope)
                     .ToArray())
        {
            activeIcsStateUploads.Remove(key);
        }
        FastTransferAssembler.ForgetProvisional(captureScope);
        sessionHandles.Remove(captureScope);
        logonCorrelationScopes.Remove(captureScope);
        pendingFallbackCorrelationScopes.Remove(captureScope);
    }

    public bool TryGetTableColumnsByHandleValue(
        string? captureScope,
        uint serverHandle,
        out ImmutableArray<(ushort Type, ushort Id)> columns)
    {
        columns = default;
        return TryResolveConnectionScope(captureScope, out var scope)
            && serverHandle != uint.MaxValue
            && tableColumns.TryGetValue((scope, serverHandle), out columns);
    }

    public void InvalidateTableColumns(string? captureScope, uint handleIndex)
    {
        if (captureScope is not null && TryResolveServerHandle(captureScope, handleIndex, out var handle))
        {
            tableColumns.Remove(handle);
        }
    }

    public bool TryGetFastTransferStreamKey(
        string? captureScope,
        uint handleIndex,
        bool allowProvisional,
        out FastTransferStreamKey key)
    {
        key = default;
        if (captureScope is null)
        {
            return false;
        }

        if (TryResolveServerHandle(captureScope, handleIndex, out var handle))
        {
            key = new FastTransferStreamKey(handle.Scope, handle.Handle);
            return true;
        }

        if (allowProvisional)
        {
            key = new FastTransferStreamKey(captureScope, handleIndex, Provisional: true);
            return true;
        }

        return false;
    }

    public string? ConfigureFastTransferRoot(
        string? captureScope,
        uint outputHandleIndex,
        FastTransferRootKind root,
        string provenance)
    {
        if (captureScope is null)
        {
            return null;
        }

        if (!configuredFastTransferOutputSlots.TryGetValue(captureScope, out var configuredSlots))
        {
            if (configuredFastTransferOutputSlots.Count >= MapiParseLimits.MaxStateEntries)
            {
                return "FastTransfer root provenance was not retained because the capture-local state limit was reached.";
            }
            configuredSlots = [];
            configuredFastTransferOutputSlots[captureScope] = configuredSlots;
        }

        var key = new FastTransferStreamKey(captureScope, outputHandleIndex, Provisional: true);
        if (!configuredSlots.Add(outputHandleIndex))
        {
            FastTransferAssembler.Forget(key);
            if (pendingFastTransferUploads.TryGetValue(captureScope, out var pendingUploads))
            {
                foreach (var upload in pendingUploads.Where(upload => upload.Key == key))
                {
                    upload.Invalidated = true;
                }
            }
            return $"FastTransfer output handle slot {outputHandleIndex} is configured more than once in the same " +
                "HTTP session; intermediate server handles are not recoverable from the final handle table, so " +
                "root provenance and staged upload state for that slot were discarded rather than misapplied.";
        }

        FastTransferAssembler.Configure(key, root, provenance);
        return null;
    }

    public void StageOutputObjectType(string? captureScope, byte ropId, uint outputHandleIndex)
    {
        if (captureScope is null)
        {
            return;
        }

        if (!pendingOutputObjectTypes.TryGetValue(captureScope, out var queue))
        {
            if (pendingOutputObjectTypes.Count >= MapiParseLimits.MaxStateEntries)
            {
                return;
            }
            queue = [];
            pendingOutputObjectTypes[captureScope] = queue;
        }
        if (queue.Count >= MapiParseLimits.MaxStateEntries)
        {
            return;
        }

        queue.Enqueue(new PendingOutputObjectType(
            ropId,
            outputHandleIndex,
            ropId switch
            {
                0x02 or 0x1C => ServerObjectType.Folder,
                0x03 or 0x06 or 0x46 => ServerObjectType.Message,
                0x22 or 0x23 => ServerObjectType.Attachment,
                _ => ServerObjectType.Unknown,
            }));
    }

    public string? CompleteOutputObjectType(
        string? captureScope,
        byte ropId,
        uint outputHandleIndex,
        bool success)
    {
        if (success)
        {
            CompleteSuccessfulOutputHandle(captureScope, outputHandleIndex);
        }
        if (captureScope is null
            || !pendingOutputObjectTypes.TryGetValue(captureScope, out var queue)
            || queue.Count == 0)
        {
            return null;
        }

        var pending = queue.Dequeue();
        if (pending.RopId != ropId || pending.HandleIndex != outputHandleIndex)
        {
            pendingOutputObjectTypes.Remove(captureScope);
            successfulOutputObjectTypes.Remove(captureScope);
            if (success && TryResolveServerHandle(captureScope, outputHandleIndex, out var mismatched))
            {
                serverObjectTypes.Remove(mismatched);
            }
            return "Output server-object type provenance did not match the response operation; " +
                "pending object types for this HTTP session were discarded.";
        }

        if (success)
        {
            if (!successfulOutputObjectTypes.TryGetValue(captureScope, out var bySlot))
            {
                bySlot = [];
                successfulOutputObjectTypes[captureScope] = bySlot;
            }
            bySlot[outputHandleIndex] = pending.Type;
        }

        if (queue.Any(candidate => candidate.HandleIndex == outputHandleIndex))
        {
            return null;
        }

        if (successfulOutputObjectTypes.TryGetValue(captureScope, out var completed)
            && completed.Remove(outputHandleIndex, out var type)
            && TryResolveServerHandle(captureScope, outputHandleIndex, out var handle))
        {
            serverObjectTypes.Remove(handle);
            if (type != ServerObjectType.Unknown
                && (serverObjectTypes.Count < MapiParseLimits.MaxStateEntries
                    || serverObjectTypes.ContainsKey(handle)))
            {
                serverObjectTypes[handle] = type;
            }
            if (completed.Count == 0)
            {
                successfulOutputObjectTypes.Remove(captureScope);
            }
        }

        if (queue.Count == 0)
        {
            pendingOutputObjectTypes.Remove(captureScope);
        }
        RefreshObjectRootDependencies(captureScope, outputHandleIndex);
        return null;
    }

    public bool TryGetServerObjectType(
        string? captureScope,
        uint handleIndex,
        out ServerObjectType type)
    {
        return TryGetServerObjectType(captureScope, handleIndex, out type, out _);
    }

    public bool TryGetServerObjectType(
        string? captureScope,
        uint handleIndex,
        out ServerObjectType type,
        out bool provisional)
    {
        type = ServerObjectType.Unknown;
        provisional = false;
        if (captureScope is null)
        {
            return false;
        }

        if (pendingOutputObjectTypes.TryGetValue(captureScope, out var pending))
        {
            foreach (var candidate in pending.Reverse())
            {
                if (candidate.HandleIndex == handleIndex)
                {
                    type = candidate.Type;
                    provisional = true;
                    return type != ServerObjectType.Unknown;
                }
            }
        }

        return TryResolveServerHandle(captureScope, handleIndex, out var handle)
            && serverObjectTypes.TryGetValue(handle, out type);
    }

    public void RegisterObjectRootDependency(
        string? captureScope,
        uint inputHandleIndex,
        uint outputHandleIndex,
        string operation)
    {
        if (captureScope is null)
        {
            return;
        }

        if (!pendingObjectRootDependencies.TryGetValue(captureScope, out var dependencies))
        {
            if (pendingObjectRootDependencies.Count >= MapiParseLimits.MaxStateEntries)
            {
                return;
            }
            dependencies = [];
            pendingObjectRootDependencies[captureScope] = dependencies;
        }
        if (dependencies.Count < MapiParseLimits.MaxStateEntries)
        {
            dependencies.Add(new PendingObjectRootDependency(
                inputHandleIndex,
                outputHandleIndex,
                operation));
        }
    }

    public void DiscardPendingOutputObjectTypes(string? captureScope)
    {
        if (captureScope is null)
        {
            return;
        }

        pendingOutputObjectTypes.Remove(captureScope);
        successfulOutputObjectTypes.Remove(captureScope);
        pendingObjectRootDependencies.Remove(captureScope);
        configuredFastTransferOutputSlots.Remove(captureScope);
        FastTransferAssembler.ForgetProvisional(captureScope);
    }

    private void RefreshObjectRootDependencies(string captureScope, uint inputHandleIndex)
    {
        if (!pendingObjectRootDependencies.TryGetValue(captureScope, out var dependencies))
        {
            return;
        }

        var matching = dependencies
            .Where(dependency => dependency.InputHandleIndex == inputHandleIndex)
            .ToArray();
        foreach (var dependency in matching)
        {
            var root = TryGetServerObjectType(
                captureScope,
                inputHandleIndex,
                out var type)
                ? type switch
                {
                    ServerObjectType.Folder => FastTransferRootKind.FolderContent,
                    ServerObjectType.Message => FastTransferRootKind.MessageContent,
                    ServerObjectType.Attachment => FastTransferRootKind.AttachmentContent,
                    _ => FastTransferRootKind.Unknown,
                }
                : FastTransferRootKind.Unknown;
            var key = new FastTransferStreamKey(
                captureScope,
                dependency.OutputHandleIndex,
                Provisional: true);
            FastTransferAssembler.Configure(
                key,
                root,
                root == FastTransferRootKind.Unknown
                    ? $"{dependency.Operation} after unresolved same-session object creation"
                    : $"{dependency.Operation} after resolved same-session {type} output state");
            if (pendingFastTransferUploads.TryGetValue(captureScope, out var uploads))
            {
                foreach (var upload in uploads.Where(upload => upload.Key == key))
                {
                    upload.Invalidated = true;
                }
            }
            dependencies.Remove(dependency);
        }

        if (dependencies.Count == 0)
        {
            pendingObjectRootDependencies.Remove(captureScope);
        }
    }

    public void InvalidateHandleState(string? captureScope, uint handleIndex)
    {
        if (captureScope is null)
        {
            return;
        }

        FastTransferAssembler.Forget(new FastTransferStreamKey(captureScope, handleIndex, Provisional: true));
        activeIcsStateUploads.Remove(new IcsStateStreamKey(captureScope, handleIndex, Provisional: true));
        if (TryResolveServerHandle(captureScope, handleIndex, out var handle))
        {
            tableColumns.Remove(handle);
            serverObjectTypes.Remove(handle);
            FastTransferAssembler.Forget(new FastTransferStreamKey(handle.Scope, handle.Handle));
            activeIcsStateUploads.Remove(new IcsStateStreamKey(handle.Scope, handle.Handle));
        }
    }

    public void CompleteSuccessfulOutputHandle(string? captureScope, uint handleIndex)
    {
        if (captureScope is null)
        {
            return;
        }

        var provisional = new FastTransferStreamKey(captureScope, handleIndex, Provisional: true);
        if (!TryResolveServerHandle(captureScope, handleIndex, out var handle))
        {
            FastTransferAssembler.Forget(provisional);
            return;
        }

        tableColumns.Remove(handle);
        serverObjectTypes.Remove(handle);
        var resolved = new FastTransferStreamKey(handle.Scope, handle.Handle);
        FastTransferAssembler.ResolveProvisional(provisional, resolved);
        if (pendingFastTransferUploads.TryGetValue(captureScope, out var pending))
        {
            foreach (var upload in pending)
            {
                if (upload.Key == provisional)
                {
                    upload.Key = resolved;
                }
            }
        }
        var provisionalIcs = new IcsStateStreamKey(captureScope, handleIndex, Provisional: true);
        var resolvedIcs = new IcsStateStreamKey(handle.Scope, handle.Handle);
        activeIcsStateUploads.Remove(resolvedIcs);
        if (pendingIcsStateOperations.TryGetValue(captureScope, out var pendingIcs))
        {
            foreach (var operation in pendingIcs)
            {
                if (operation.Key == provisionalIcs)
                {
                    operation.Key = resolvedIcs;
                }
            }
        }
    }

    public FastTransferLexResult StageFastTransferUpload(
        string captureScope,
        uint handleIndex,
        FastTransferStreamKey key,
        ReadOnlySpan<byte> buffer,
        long absoluteOffset,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        if (!pendingFastTransferUploads.TryGetValue(captureScope, out var queue))
        {
            queue = [];
            pendingFastTransferUploads[captureScope] = queue;
        }
        if (queue.Count >= MapiParseLimits.MaxStateEntries)
        {
            throw new MapiParseException(
                absoluteOffset,
                $"FastTransfer upload staging exceeds the safe limit of {MapiParseLimits.MaxStateEntries:N0} operations.");
        }

        var baseState = queue.LastOrDefault(upload => upload.Key == key)?.State
            ?? FastTransferAssembler.StateFor(key);
        var result = FastTransferStreamLexer.Lex(
            buffer,
            absoluteOffset,
            baseState,
            budget,
            depth,
            cancellationToken);
        queue.Enqueue(new PendingFastTransferUpload
        {
            HandleIndex = handleIndex,
            Key = key,
            BaseState = baseState,
            State = result.State,
            Buffer = buffer.ToArray(),
            AbsoluteOffset = absoluteOffset,
        });
        return result;
    }

    public string? CompleteFastTransferUpload(
        string? captureScope,
        uint handleIndex,
        bool success,
        uint bufferUsedSize)
    {
        if (captureScope is null
            || !pendingFastTransferUploads.TryGetValue(captureScope, out var queue)
            || queue.Count == 0)
        {
            return null;
        }

        var pending = queue.Dequeue();
        var matchingHandle = pending.HandleIndex == handleIndex;
        if (!matchingHandle || pending.Invalidated)
        {
            InvalidateLaterUploads(queue, pending.Key);
            return "FastTransfer upload response could not be paired safely with its staged request; " +
                "the staged stream state was discarded.";
        }

        if (!success)
        {
            FastTransferAssembler.Forget(pending.Key);
            InvalidateLaterUploads(queue, pending.Key);
            return "FastTransfer upload failed; BufferUsedSize is not reliable on failure, so the staged " +
                "request and prior reconstructed state for this upload context were discarded.";
        }

        if (bufferUsedSize > pending.Buffer.Length)
        {
            InvalidateLaterUploads(queue, pending.Key);
            return $"FastTransfer upload response reports BufferUsedSize {bufferUsedSize:N0}, which exceeds " +
                $"the staged {pending.Buffer.Length:N0}-byte request buffer; the staged stream state was discarded.";
        }

        if (bufferUsedSize == pending.Buffer.Length)
        {
            FastTransferAssembler.Commit(pending.Key, pending.State);
            return null;
        }

        if (bufferUsedSize > 0)
        {
            var accepted = FastTransferStreamLexer.Lex(
                pending.Buffer.AsSpan(0, checked((int)bufferUsedSize)),
                pending.AbsoluteOffset,
                pending.BaseState,
                new MapiNodeBudget(),
                0,
                CancellationToken.None);
            FastTransferAssembler.Commit(pending.Key, accepted.State);
        }
        InvalidateLaterUploads(queue, pending.Key);
        return $"FastTransfer upload accepted {bufferUsedSize:N0} of {pending.Buffer.Length:N0} staged byte(s); " +
            "only the accepted prefix was committed and later same-round-trip state was discarded.";
    }

    private static void InvalidateLaterUploads(
        Queue<PendingFastTransferUpload> queue,
        FastTransferStreamKey key)
    {
        foreach (var later in queue.Where(upload => upload.Key == key))
        {
            later.Invalidated = true;
        }
    }

    public void StageIcsStateBegin(
        string? captureScope,
        uint handleIndex,
        uint stateProperty,
        uint declaredSize)
    {
        if (TryCreateIcsStateKey(captureScope, handleIndex, out var key))
        {
            EnqueueIcsStateOperation(
                captureScope!,
                new PendingIcsStateOperation
                {
                    RopId = 0x75,
                    HandleIndex = handleIndex,
                    Key = key,
                    StateProperty = stateProperty,
                    DeclaredSize = declaredSize,
                });
        }
    }

    public void StageIcsStateContinue(
        string? captureScope,
        uint handleIndex,
        ReadOnlySpan<byte> data)
    {
        if (TryCreateIcsStateKey(captureScope, handleIndex, out var key))
        {
            EnqueueIcsStateOperation(
                captureScope!,
                new PendingIcsStateOperation
                {
                    RopId = 0x76,
                    HandleIndex = handleIndex,
                    Key = key,
                    Data = data.ToArray(),
                });
        }
    }

    public void StageIcsStateEnd(string? captureScope, uint handleIndex)
    {
        if (TryCreateIcsStateKey(captureScope, handleIndex, out var key))
        {
            EnqueueIcsStateOperation(
                captureScope!,
                new PendingIcsStateOperation
                {
                    RopId = 0x77,
                    HandleIndex = handleIndex,
                    Key = key,
                });
        }
    }

    public IcsStateCompletion? CompleteIcsStateOperation(
        string? captureScope,
        byte ropId,
        uint handleIndex,
        bool success,
        out string? warning)
    {
        warning = null;
        if (captureScope is null
            || !pendingIcsStateOperations.TryGetValue(captureScope, out var queue)
            || queue.Count == 0)
        {
            warning = $"RopSynchronization state-stream response 0x{ropId:X2} has no staged request.";
            return null;
        }

        var pending = queue.Dequeue();
        var key = ResolveIcsStateKey(captureScope, handleIndex, pending.Key);
        if (pending.RopId != ropId || pending.HandleIndex != handleIndex)
        {
            activeIcsStateUploads.Remove(key);
            activeIcsStateUploads.Remove(pending.Key);
            warning =
                $"RopSynchronization state-stream response 0x{ropId:X2}/handle {handleIndex} does not match " +
                $"the staged 0x{pending.RopId:X2}/handle {pending.HandleIndex}; state was discarded.";
            return null;
        }

        if (!success)
        {
            activeIcsStateUploads.Remove(key);
            warning = $"RopSynchronization state-stream operation 0x{ropId:X2} failed; in-progress state was discarded.";
            return null;
        }

        switch (ropId)
        {
            case 0x75:
            {
                if (pending.DeclaredSize > MapiParseLimits.MaxPayloadBytes)
                {
                    warning =
                        $"ICS state stream declares {pending.DeclaredSize:N0} bytes, exceeding the " +
                        $"{MapiParseLimits.MaxPayloadBytes:N0}-byte limit; it will not be accumulated.";
                    activeIcsStateUploads.Remove(key);
                    return null;
                }
                var replaced = activeIcsStateUploads.ContainsKey(key);
                if (!replaced && activeIcsStateUploads.Count >= MapiParseLimits.MaxStateEntries)
                {
                    warning = "ICS state-stream tracking has reached its safe entry limit.";
                    return null;
                }
                activeIcsStateUploads[key] = new ActiveIcsStateUpload
                {
                    StateProperty = pending.StateProperty,
                    DeclaredSize = pending.DeclaredSize,
                };
                if (replaced)
                {
                    warning = "A new ICS state property upload began before the prior property ended; prior state was discarded.";
                }
                return null;
            }
            case 0x76:
            {
                if (!activeIcsStateUploads.TryGetValue(key, out var active))
                {
                    warning = "ICS state-stream Continue has no successful Begin; its bytes were not accumulated.";
                    return null;
                }
                if (active.ChunkCount >= FastTransferLimits.MaxStateStreamChunks
                    || active.Data.Count + (long)pending.Data.Length > MapiParseLimits.MaxPayloadBytes
                    || active.Data.Count + (long)pending.Data.Length > active.DeclaredSize)
                {
                    activeIcsStateUploads.Remove(key);
                    warning = "ICS state-stream chunks exceed the declared size or safe accumulation limit; state was discarded.";
                    return null;
                }
                active.Data.AddRange(pending.Data);
                active.ChunkCount++;
                return null;
            }
            case 0x77:
            {
                if (!activeIcsStateUploads.Remove(key, out var active))
                {
                    warning = "ICS state-stream End has no successful Begin; no state was finalized.";
                    return null;
                }
                var mismatch = active.Data.Count != active.DeclaredSize
                    ? $"Observed {active.Data.Count:N0} byte(s), but Begin declared {active.DeclaredSize:N0}."
                    : null;
                return new IcsStateCompletion(
                    active.StateProperty,
                    active.DeclaredSize,
                    [.. active.Data],
                    active.ChunkCount,
                    mismatch);
            }
            default:
                warning = $"ROP 0x{ropId:X2} is not an ICS state-stream operation.";
                return null;
        }
    }

    private bool TryCreateIcsStateKey(
        string? captureScope,
        uint handleIndex,
        out IcsStateStreamKey key)
    {
        key = default;
        if (captureScope is null)
        {
            return false;
        }
        if (TryResolveServerHandle(captureScope, handleIndex, out var resolved))
        {
            key = new IcsStateStreamKey(resolved.Scope, resolved.Handle);
        }
        else
        {
            key = new IcsStateStreamKey(captureScope, handleIndex, Provisional: true);
        }
        return true;
    }

    private IcsStateStreamKey ResolveIcsStateKey(
        string captureScope,
        uint handleIndex,
        IcsStateStreamKey staged) =>
        TryResolveServerHandle(captureScope, handleIndex, out var resolved)
            ? new IcsStateStreamKey(resolved.Scope, resolved.Handle)
            : staged;

    private void EnqueueIcsStateOperation(string captureScope, PendingIcsStateOperation operation)
    {
        if (!pendingIcsStateOperations.TryGetValue(captureScope, out var queue))
        {
            queue = [];
            pendingIcsStateOperations[captureScope] = queue;
        }
        if (queue.Count < MapiParseLimits.MaxStateEntries)
        {
            queue.Enqueue(operation);
        }
    }

    private bool TryResolveServerHandle(
        string captureScope,
        uint handleIndex,
        out (string Scope, uint Handle) handle)
    {
        handle = default;
        if (!TryGetServerHandle(captureScope, handleIndex, out var scope, out var handleValue))
        {
            return false;
        }

        handle = (scope, handleValue);
        return handleValue != uint.MaxValue;
    }

    private bool TryGetServerHandle(
        string captureScope,
        uint handleIndex,
        out string scope,
        out uint handleValue)
    {
        handleValue = uint.MaxValue;
        if (!TryResolveConnectionScope(captureScope, out scope)
            || !sessionHandles.TryGetValue(captureScope, out var values)
            || handleIndex >= values.Length)
        {
            return false;
        }

        handleValue = values[(int)handleIndex];
        return true;
    }

    /// <summary>Associates one HTTP session with the stable logical MAPI connection used for logon correlation.</summary>
    public void RegisterLogonCorrelationScope(string captureScope, string correlationScope)
    {
        if (logonCorrelationScopes.Count < MapiParseLimits.MaxStateEntries || logonCorrelationScopes.ContainsKey(captureScope))
        {
            logonCorrelationScopes[captureScope] = correlationScope;
        }
    }

    public string? ResolveAndRegisterLogonCorrelationScope(
        string captureScope,
        string requestType,
        string fallbackScope,
        IEnumerable<string> cookieHeaders)
    {
        string? warning = null;
        string scope;
        var establishesContext = requestType.Equals("Connect", StringComparison.OrdinalIgnoreCase)
            || requestType.Equals("Bind", StringComparison.OrdinalIgnoreCase);
        if (establishesContext)
        {
            scope = $"context:{HashCorrelationValue($"{fallbackScope}\u001F{captureScope}")}";
            if (pendingFallbackCorrelationScopes.Count < MapiParseLimits.MaxStateEntries
                || pendingFallbackCorrelationScopes.ContainsKey(captureScope))
            {
                pendingFallbackCorrelationScopes[captureScope] = fallbackScope;
            }
            else
            {
                MarkFallbackCorrelationSaturated(fallbackScope);
                warning = "MAPI/HTTP fallback correlation reached its capture-local safety limit; " +
                    "the new session context will remain isolated from fallback-only requests.";
            }
        }
        else
        {
            var scores = new Dictionary<string, int>(StringComparer.Ordinal);
            var requestCookies = ParseRequestCookies(cookieHeaders)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var unmatchedCookieNames = new HashSet<string>(StringComparer.Ordinal);
            var saturated = cookieCorrelationGloballySaturated;
            foreach (var cookie in requestCookies)
            {
                var alias = HashCorrelationValue(cookie);
                if (saturatedCookieAliases.Contains(alias))
                {
                    saturated = true;
                }
                if (!cookieCorrelationScopes.TryGetValue(alias, out var scopes))
                {
                    unmatchedCookieNames.Add(HashCookieName(cookie));
                    continue;
                }
                foreach (var candidate in scopes)
                {
                    scores[candidate] = scores.GetValueOrDefault(candidate) + 1;
                }
            }

            var bestScore = scores.Count == 0 ? 0 : scores.Values.Max();
            var bestScopes = scores
                .Where(pair => pair.Value == bestScore)
                .Select(pair => pair.Key)
                .ToArray();
            if (saturated)
            {
                scope = $"ambiguous:{HashCorrelationValue(captureScope)}";
                warning = "MAPI/HTTP cookie correlation reached its capture-local safety limit; " +
                    "state was isolated for this HTTP session rather than matched against incomplete aliases.";
            }
            else if (bestScopes.Length == 1
                && IsCookieCandidateCompatible(bestScopes[0], fallbackScope, unmatchedCookieNames))
            {
                scope = bestScopes[0];
            }
            else if (bestScopes.Length == 1)
            {
                scope = $"ambiguous:{HashCorrelationValue(captureScope)}";
                warning = "MAPI/HTTP request cookies conflicted with the matched session context; " +
                    "capture-local state was isolated for this HTTP session rather than guessed.";
            }
            else if (bestScopes.Length > 1)
            {
                scope = $"ambiguous:{HashCorrelationValue(captureScope)}";
                warning = "MAPI/HTTP request cookies matched multiple prior session contexts equally; " +
                    "capture-local state was isolated for this HTTP session rather than guessed.";
            }
            else
            {
                scope = ResolveFallbackCorrelationScope(captureScope, fallbackScope, out warning);
            }
        }

        if (!establishesContext
            && warning is null
            && !correlationScopeFallbacks.ContainsKey(scope)
            && !RegisterFallbackCorrelationScope(fallbackScope, scope))
        {
            scope = $"ambiguous:{HashCorrelationValue(captureScope)}";
            warning = "MAPI/HTTP fallback correlation reached its capture-local safety limit; " +
                "capture-local state was isolated for this HTTP session rather than matched against incomplete aliases.";
        }
        RegisterLogonCorrelationScope(captureScope, scope);
        return warning;
    }

    public string? CompleteLogonCorrelationEstablishment(string captureScope, bool success)
    {
        if (!pendingFallbackCorrelationScopes.Remove(captureScope, out var fallbackScope))
        {
            return null;
        }
        if (!success)
        {
            EndLogonCorrelationScope(captureScope);
            return null;
        }
        if (!TryResolveConnectionScope(captureScope, out var scope))
        {
            return null;
        }

        return RegisterFallbackCorrelationScope(fallbackScope, scope)
            ? null
            : "MAPI/HTTP fallback correlation reached its capture-local safety limit; " +
                "the new session context will remain isolated from fallback-only requests.";
    }

    public string? RegisterResponseCookieAliases(
        string captureScope,
        IEnumerable<string> setCookieHeaders)
    {
        if (!TryResolveConnectionScope(captureScope, out var scope))
        {
            return null;
        }

        string? warning = null;
        foreach (var cookie in ParseResponseCookies(setCookieHeaders).Distinct(StringComparer.Ordinal))
        {
            var alias = HashCorrelationValue(cookie);
            cookieCorrelationScopes.TryGetValue(alias, out var scopes);
            correlationScopeCookies.TryGetValue(scope, out var aliases);
            correlationScopeCookieNames.TryGetValue(scope, out var cookieNames);
            if (scopes?.Contains(scope) == true)
            {
                continue;
            }
            if (cookieCorrelationRelationshipCount >= MapiParseLimits.MaxStateEntries
                || (scopes is null && cookieCorrelationScopes.Count >= MapiParseLimits.MaxStateEntries)
                || (aliases is null && correlationScopeCookies.Count >= MapiParseLimits.MaxStateEntries)
                || (cookieNames is null && correlationScopeCookieNames.Count >= MapiParseLimits.MaxStateEntries)
                || scopes?.Count >= MapiParseLimits.MaxStateEntries
                || aliases?.Count >= MapiParseLimits.MaxStateEntries
                || cookieNames?.Count >= MapiParseLimits.MaxStateEntries)
            {
                if (saturatedCookieAliases.Count < MapiParseLimits.MaxStateEntries)
                {
                    saturatedCookieAliases.Add(alias);
                }
                else
                {
                    cookieCorrelationGloballySaturated = true;
                }
                warning = "MAPI/HTTP cookie correlation reached its capture-local safety limit; " +
                    "affected future requests will be isolated rather than matched against incomplete aliases.";
                continue;
            }

            if (scopes is null)
            {
                scopes = new HashSet<string>(StringComparer.Ordinal);
                cookieCorrelationScopes[alias] = scopes;
            }
            if (aliases is null)
            {
                aliases = new HashSet<string>(StringComparer.Ordinal);
                correlationScopeCookies[scope] = aliases;
            }
            if (cookieNames is null)
            {
                cookieNames = new HashSet<string>(StringComparer.Ordinal);
                correlationScopeCookieNames[scope] = cookieNames;
            }
            scopes.Add(scope);
            aliases.Add(alias);
            cookieNames.Add(HashCookieName(cookie));
            cookieCorrelationRelationshipCount++;
        }
        return warning;
    }

    public void EndLogonCorrelationScope(string captureScope)
    {
        if (!TryResolveConnectionScope(captureScope, out var scope))
        {
            return;
        }

        if (correlationScopeCookies.Remove(scope, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (!cookieCorrelationScopes.TryGetValue(alias, out var scopes))
                {
                    continue;
                }
                if (scopes.Remove(scope))
                {
                    cookieCorrelationRelationshipCount--;
                }
                if (scopes.Count == 0)
                {
                    cookieCorrelationScopes.Remove(alias);
                }
            }
            correlationScopeCookieNames.Remove(scope);
        }
        if (correlationScopeFallbacks.Remove(scope, out var fallback)
            && fallbackCorrelationScopes.TryGetValue(fallback, out var fallbackScopes))
        {
            if (fallbackScopes.Remove(scope))
            {
                fallbackCorrelationRelationshipCount--;
            }
            if (fallbackScopes.Count == 0)
            {
                fallbackCorrelationScopes.Remove(fallback);
            }
        }

        foreach (var key in logonPrivacy.Keys.Where(key => key.Scope == scope).ToArray())
        {
            logonPrivacy.Remove(key);
        }
        foreach (var key in tableColumns.Keys.Where(key => key.Scope == scope).ToArray())
        {
            tableColumns.Remove(key);
        }
        foreach (var key in serverObjectTypes.Keys.Where(key => key.Scope == scope).ToArray())
        {
            serverObjectTypes.Remove(key);
        }
        foreach (var key in activeIcsStateUploads.Keys.Where(key => key.Scope == scope).ToArray())
        {
            activeIcsStateUploads.Remove(key);
        }
        FastTransferAssembler.ForgetConnectionScope(scope);
    }

    /// <summary>Records a RopLogon-established LogonId's Private-vs-public-folders flag.</summary>
    public void RecordLogonPrivacy(string? captureScope, byte logonId, bool isPrivate)
    {
        if (!TryResolveConnectionScope(captureScope, out var scope))
        {
            return;
        }

        var key = (scope, logonId);
        if (logonPrivacy.TryGetValue(key, out var existing))
        {
            if (existing != isPrivate)
            {
                logonPrivacy[key] = null;
            }
            return;
        }
        if (logonPrivacy.Count < MapiParseLimits.MaxStateEntries)
        {
            logonPrivacy[key] = isPrivate;
        }
    }

    public void StageLogonPrivacy(string? captureScope, byte logonId, bool isPrivate)
    {
        if (captureScope is null)
        {
            return;
        }
        if (!pendingLogonPrivacy.TryGetValue(captureScope, out var queue))
        {
            if (pendingLogonPrivacy.Count >= MapiParseLimits.MaxStateEntries)
            {
                return;
            }
            queue = [];
            pendingLogonPrivacy[captureScope] = queue;
        }
        if (queue.Count < MapiParseLimits.MaxStateEntries)
        {
            queue.Enqueue((logonId, isPrivate));
        }
    }

    public void CompleteLogonPrivacy(string? captureScope, bool success, bool? responseIsPrivate)
    {
        if (captureScope is null
            || !pendingLogonPrivacy.TryGetValue(captureScope, out var queue)
            || queue.Count == 0)
        {
            return;
        }

        var pending = queue.Dequeue();
        if (queue.Count == 0)
        {
            pendingLogonPrivacy.Remove(captureScope);
        }
        if (success)
        {
            RecordLogonPrivacy(
                captureScope,
                pending.LogonId,
                responseIsPrivate ?? pending.IsPrivate);
        }
    }

    public bool TryGetLogonPrivacy(string? captureScope, byte logonId, out bool isPrivate)
    {
        isPrivate = false;
        if (!TryResolveConnectionScope(captureScope, out var scope)
            || !logonPrivacy.TryGetValue((scope, logonId), out var value)
            || value is null)
        {
            return false;
        }

        isPrivate = value.Value;
        return true;
    }

    private bool TryResolveConnectionScope(string? captureScope, out string scope)
    {
        scope = string.Empty;
        if (captureScope is null)
        {
            return false;
        }

        scope = logonCorrelationScopes.TryGetValue(captureScope, out var correlationScope)
            ? correlationScope
            : captureScope;
        return true;
    }

    private bool RegisterFallbackCorrelationScope(string fallbackScope, string scope)
    {
        fallbackCorrelationScopes.TryGetValue(fallbackScope, out var scopes);
        if (scopes?.Contains(scope) == true)
        {
            return true;
        }
        if (fallbackCorrelationRelationshipCount >= MapiParseLimits.MaxStateEntries
            || (scopes is null && fallbackCorrelationScopes.Count >= MapiParseLimits.MaxStateEntries)
            || (!correlationScopeFallbacks.ContainsKey(scope)
                && correlationScopeFallbacks.Count >= MapiParseLimits.MaxStateEntries)
            || scopes?.Count >= MapiParseLimits.MaxStateEntries)
        {
            MarkFallbackCorrelationSaturated(fallbackScope);
            return false;
        }
        if (scopes is null)
        {
            scopes = new HashSet<string>(StringComparer.Ordinal);
            fallbackCorrelationScopes[fallbackScope] = scopes;
        }
        scopes.Add(scope);
        correlationScopeFallbacks[scope] = fallbackScope;
        fallbackCorrelationRelationshipCount++;
        return true;
    }

    private string ResolveFallbackCorrelationScope(
        string captureScope,
        string fallbackScope,
        out string? warning)
    {
        warning = null;
        if (fallbackCorrelationGloballySaturated
            || saturatedFallbackScopes.Contains(HashCorrelationValue(fallbackScope)))
        {
            warning = "MAPI/HTTP fallback correlation reached its capture-local safety limit; " +
                "capture-local state was isolated for this HTTP session rather than matched against incomplete aliases.";
            return $"ambiguous:{HashCorrelationValue(captureScope)}";
        }
        if (!fallbackCorrelationScopes.TryGetValue(fallbackScope, out var scopes)
            || scopes.Count == 0)
        {
            return fallbackScope;
        }
        if (scopes.Count == 1)
        {
            return scopes.Single();
        }

        warning = "MAPI/HTTP fallback identity matched multiple active session contexts; " +
            "capture-local state was isolated for this HTTP session rather than guessed.";
        return $"ambiguous:{HashCorrelationValue(captureScope)}";
    }

    private void MarkFallbackCorrelationSaturated(string fallbackScope)
    {
        if (saturatedFallbackScopes.Count < MapiParseLimits.MaxStateEntries)
        {
            saturatedFallbackScopes.Add(HashCorrelationValue(fallbackScope));
        }
        else
        {
            fallbackCorrelationGloballySaturated = true;
        }
    }

    private bool IsCookieCandidateCompatible(
        string candidateScope,
        string fallbackScope,
        HashSet<string> unmatchedCookieNames)
    {
        if (correlationScopeFallbacks.TryGetValue(candidateScope, out var candidateFallback)
            && !candidateFallback.Equals(fallbackScope, StringComparison.Ordinal))
        {
            return false;
        }
        return !correlationScopeCookieNames.TryGetValue(candidateScope, out var knownNames)
            || !knownNames.Overlaps(unmatchedCookieNames);
    }

    private static IEnumerable<string> ParseRequestCookies(IEnumerable<string> headers)
    {
        foreach (var header in headers)
        {
            foreach (var segment in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = segment.IndexOf('=');
                if (separator > 0)
                {
                    yield return $"{segment[..separator].Trim().ToLowerInvariant()}={segment[(separator + 1)..].Trim()}";
                }
            }
        }
    }

    private static IEnumerable<string> ParseResponseCookies(IEnumerable<string> headers)
    {
        foreach (var header in headers)
        {
            var cookie = header.Split(';', 2, StringSplitOptions.TrimEntries)[0];
            var separator = cookie.IndexOf('=');
            if (separator > 0)
            {
                yield return $"{cookie[..separator].Trim().ToLowerInvariant()}={cookie[(separator + 1)..].Trim()}";
            }
        }
    }

    private static string HashCorrelationValue(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string HashCookieName(string cookie)
    {
        var separator = cookie.IndexOf('=');
        return HashCorrelationValue(separator < 0 ? cookie : cookie[..separator]);
    }

    /// <summary>Enqueues a RopGetPropertiesSpecific request's PropertyTags for its matching same-session response.</summary>
    public void EnqueuePropertySpecificTags(string? captureScope, uint handleIndex, ImmutableArray<(ushort Type, ushort Id)> tags)
    {
        if (captureScope is null)
        {
            return;
        }
        if (!propertySpecificTags.TryGetValue(captureScope, out var byHandle))
        {
            if (propertySpecificTags.Count >= MapiParseLimits.MaxStateEntries)
            {
                return;
            }
            byHandle = [];
            propertySpecificTags[captureScope] = byHandle;
        }
        if (!byHandle.TryGetValue(handleIndex, out var queue))
        {
            if (byHandle.Count >= MapiParseLimits.MaxStateEntries)
            {
                return;
            }
            queue = new Queue<ImmutableArray<(ushort Type, ushort Id)>>();
            byHandle[handleIndex] = queue;
        }
        if (queue.Count < MapiParseLimits.MaxStateEntries)
        {
            queue.Enqueue(tags);
        }
    }

    /// <summary>Dequeues (FIFO) the PropertyTags a same-session RopGetPropertiesSpecific request enqueued for this handle index.</summary>
    public bool TryDequeuePropertySpecificTags(string? captureScope, uint handleIndex, out ImmutableArray<(ushort Type, ushort Id)> tags)
    {
        tags = default;
        if (captureScope is not null
            && propertySpecificTags.TryGetValue(captureScope, out var byHandle)
            && byHandle.TryGetValue(handleIndex, out var queue)
            && queue.Count > 0)
        {
            tags = queue.Dequeue();
            return true;
        }
        return false;
    }

    /// <summary>Records a request ROP list's total length and its response-operation-count checkpoints, for this HTTP session.</summary>
    public void RecordRequestRopList(string? captureScope, int totalLength, ImmutableArray<int> responseCheckpoints)
    {
        if (captureScope is null)
        {
            return;
        }
        if (requestRopLists.Count < MapiParseLimits.MaxStateEntries || requestRopLists.ContainsKey(captureScope))
        {
            requestRopLists[captureScope] = (totalLength, responseCheckpoints);
        }
    }

    /// <summary>Looks up this HTTP session's recorded request ROP list shape for a same-session RopBufferTooSmall response.</summary>
    public bool TryGetRequestRopList(string? captureScope, out int totalLength, out ImmutableArray<int> responseCheckpoints)
    {
        if (captureScope is not null && requestRopLists.TryGetValue(captureScope, out var entry))
        {
            totalLength = entry.TotalLength;
            responseCheckpoints = entry.ResponseCheckpoints;
            return true;
        }
        totalLength = 0;
        responseCheckpoints = ImmutableArray<int>.Empty;
        return false;
    }
}

internal static class MapiParseLimits
{
    public const int MaxPayloadBytes = 4 * 1024 * 1024;
    public const int MaxNodes = 25_000;
    public const int MaxDepth = 64;
    public const int MaxCollectionCount = 100_000;
    public const int MaxStringBytes = 1024 * 1024;
    public const int MaxRawNodeBytes = 16 * 1024;
    public const int MaxStateEntries = 16_384;
    public const int MaxWarnings = 1_000;
}
