using System.Collections.Immutable;

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
    private readonly Dictionary<uint, string> logons = [];
    private readonly Dictionary<uint, string> handles = [];

    // [MS-OXCSTOR] (logical MAPI connection, LogonId) -> whether the RopLogon's LogonFlags had
    // Private (0x01) set. The HTTP-session-to-connection map lets later Execute round-trips reuse the
    // state without allowing unrelated mailboxes/clients that reuse a LogonId to overwrite it.
    // A null value records conflicting observations for one logical connection and deliberately makes
    // future shape-dependent operations fall back to raw rather than guessing.
    private readonly Dictionary<string, string> logonCorrelationScopes = [];
    private readonly Dictionary<(string Scope, byte LogonId), bool?> logonPrivacy = [];

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
        sessionHandles.Remove(captureScope);
        logonCorrelationScopes.Remove(captureScope);
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
