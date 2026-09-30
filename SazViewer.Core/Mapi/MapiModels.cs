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
    private readonly Dictionary<uint, ImmutableArray<uint>> tableColumns = [];
    private readonly Dictionary<uint, string> handles = [];

    // [MS-OXCSTOR] (logical MAPI connection, LogonId) -> whether the RopLogon's LogonFlags had
    // Private (0x01) set. The HTTP-session-to-connection map lets later Execute round-trips reuse the
    // state without allowing unrelated mailboxes/clients that reuse a LogonId to overwrite it.
    // A null value records conflicting observations for one logical connection and deliberately makes
    // future shape-dependent operations fall back to raw rather than guessing.
    private readonly Dictionary<string, string> logonCorrelationScopes = [];
    private readonly Dictionary<(string Scope, byte LogonId), bool?> logonPrivacy = [];

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
    public IReadOnlyDictionary<uint, ImmutableArray<uint>> TableColumns => tableColumns;
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

    public void SetTableColumns(uint handle, ImmutableArray<uint> columns)
    {
        if (tableColumns.Count < MapiParseLimits.MaxStateEntries)
        {
            tableColumns[handle] = columns;
        }
    }

    public void SetHandle(uint index, string value)
    {
        if (handles.Count < MapiParseLimits.MaxStateEntries)
        {
            handles[index] = value;
        }
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
        if (!TryResolveLogonScope(captureScope, out var scope))
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
        if (!TryResolveLogonScope(captureScope, out var scope)
            || !logonPrivacy.TryGetValue((scope, logonId), out var value)
            || value is null)
        {
            return false;
        }

        isPrivate = value.Value;
        return true;
    }

    private bool TryResolveLogonScope(string? captureScope, out string scope)
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
