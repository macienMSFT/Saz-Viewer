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

    public IReadOnlyDictionary<uint, string> Logons => logons;
    public IReadOnlyDictionary<uint, ImmutableArray<uint>> TableColumns => tableColumns;
    public IReadOnlyDictionary<uint, string> Handles => handles;

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
