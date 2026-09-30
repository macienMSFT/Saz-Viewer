using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SazViewer.Core;

/// <summary>
/// Parses the AUX_HEADER-framed auxiliary block sequence carried inside a decoded MAPI extended
/// buffer, per [MS-OXCRPC] 2.2.2.2 (AUX_HEADER), including the 40 version/type mappings represented
/// upstream and AUX_TYPE_SERVER_CAPABILITIES from section 2.2.2.2.19. Every block is parsed
/// transactionally: a per-block failure falls back to a bounded raw node with a warning instead of
/// aborting the whole sequence or the whole capture.
/// </summary>
internal static class AuxiliaryPayloadParser
{
    private const int HeaderSize = 4;

    public static ImmutableArray<MapiNode> Parse(
        ReadOnlySpan<byte> decoded,
        long absoluteOffset,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<MapiNode>();
        var reader = new MapiReader(decoded, cancellationToken, checked((int)absoluteOffset));
        var index = 0;
        while (!reader.End)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = reader.Position;
            var localStart = reader.LocalPosition;
            if (reader.Remaining < HeaderSize)
            {
                warnings.Add($"Auxiliary payload ends with {reader.Remaining} unframed bytes.");
                result.Add(ExtendedBufferParser.RawNode("Trailing auxiliary bytes", reader.ReadRemaining("auxiliary tail"), start, budget));
                break;
            }

            var size = reader.ReadUInt16("AUX_HEADER.Size");
            var version = reader.ReadByte("AUX_HEADER.Version");
            var type = reader.ReadByte("AUX_HEADER.Type");
            if (size < HeaderSize || size - HeaderSize > reader.Remaining)
            {
                warnings.Add($"Auxiliary block {index} has invalid size {size}.");
                result.Add(ExtendedBufferParser.RawNode("Malformed auxiliary block", decoded[localStart..], start, budget));
                break;
            }

            // Capture the whole block (header + payload) so fields that reference other fields by
            // offset can be read from validated, deterministic positions rather than the upstream
            // parser's sequential reads (see the per-struct "TODO: actually read from the offset"
            // markers this corrects).
            var blockBytes = decoded.Slice(localStart, size);
            var label = AuxiliaryTypeName(version, type);
            var children = ImmutableArray.CreateBuilder<MapiNode>();
            ExtendedBufferParser.AddField(children, "Size", start, 2, size.ToString(CultureInfo.InvariantCulture), budget);
            ExtendedBufferParser.AddField(children, "Version", start + 2, 1, FormatVersion(version), budget);
            ExtendedBufferParser.AddField(children, "Type", start + 3, 1, label, budget);

            var payloadOffset = start + HeaderSize;
            var payloadLength = size - HeaderSize;
            var blockChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var trailerTracker = new OffsetTrailerTracker();
            try
            {
                var payloadReader = new MapiReader(blockBytes[HeaderSize..], cancellationToken, checked((int)payloadOffset));
                DispatchBlock(version, type, ref payloadReader, blockBytes, start, blockChildren, budget, index, label, warnings, cancellationToken, trailerTracker);

                // Fields read at validated offsets (e.g. MachineNameOffset) are random-access reads
                // against blockBytes, not sequential reads through payloadReader, so the true "high
                // water mark" of consumed bytes is the larger of the sequential cursor and the
                // furthest offset-based read recorded in trailerTracker.
                var consumedLocalEnd = Math.Max(HeaderSize + payloadReader.LocalPosition, trailerTracker.MaxLocalEnd);
                if (consumedLocalEnd < blockBytes.Length)
                {
                    var trailing = blockBytes[consumedLocalEnd..];
                    if (trailing.Length > 0)
                    {
                        blockChildren.Add(ExtendedBufferParser.RawNode("Trailing block bytes", trailing, start + consumedLocalEnd, budget));
                        warnings.Add($"Auxiliary block {index} ({label}) left {trailing.Length:N0} unparsed bytes.");
                    }
                }
            }
            catch (MapiParseException ex)
            {
                // Transactional per-block fallback: discard any partially built semantic fields and
                // show the entire undecoded block payload as raw bytes instead.
                blockChildren.Clear();
                warnings.Add($"Auxiliary block {index} ({label}) could not be decoded: {ex.Message}");
                blockChildren.Add(ExtendedBufferParser.RawNode("Malformed auxiliary block payload", blockBytes[HeaderSize..], payloadOffset, budget));
            }
            children.Add(new MapiNode("AuxiliaryBlock", MapiNodeKind.Structure, payloadOffset, payloadLength, null, blockChildren.ToImmutable()));

            // Advance the outer cursor past the whole declared block regardless of dispatch outcome.
            _ = reader.ReadBytes(payloadLength, "Auxiliary payload");

            budget.Claim(0);
            result.Add(new MapiNode($"Auxiliary block {index}", MapiNodeKind.Structure, start, size, null, children.ToImmutable()));
            index++;
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Tracks the furthest byte (relative to the start of the AUX_HEADER block, i.e. an index into
    /// <c>blockBytes</c>) consumed by a validated offset-based read, so genuinely unparsed trailing
    /// bytes can still be detected even though offset-referenced fields are read out of sequence.
    /// </summary>
    private sealed class OffsetTrailerTracker
    {
        public int MaxLocalEnd;
    }

    private static void DispatchBlock(
        byte version,
        byte type,
        ref MapiReader reader,
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        int blockIndex,
        string label,
        List<string> warnings,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        if (version == 1)
        {
            switch (type)
            {
                case 0x01: // AUX_TYPE_PERF_REQUESTID
                    ParsePerfRequestId(ref reader, children, budget);
                    return;
                case 0x02: // AUX_TYPE_PERF_CLIENTINFO
                    ParsePerfClientInfo(ref reader, blockBytes, blockStart, children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
                    return;
                case 0x03: // AUX_TYPE_PERF_SERVERINFO
                    ParsePerfServerInfo(ref reader, blockBytes, blockStart, children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
                    return;
                case 0x04: // AUX_TYPE_PERF_SESSIONINFO
                    ParsePerfSessionInfoV1(ref reader, children, budget);
                    return;
                case 0x05: // AUX_TYPE_PERF_DEFMDB_SUCCESS
                case 0x0C: // AUX_TYPE_PERF_BG_DEFMDB_SUCCESS
                case 0x11: // AUX_TYPE_PERF_FG_DEFMDB_SUCCESS
                    ParsePerfDefMdbSuccess(ref reader, children, budget);
                    return;
                case 0x06: // AUX_TYPE_PERF_DEFGC_SUCCESS
                case 0x0D: // AUX_TYPE_PERF_BG_DEFGC_SUCCESS
                case 0x12: // AUX_TYPE_PERF_FG_DEFGC_SUCCESS
                    ParsePerfDefGcSuccess(ref reader, children, budget);
                    return;
                case 0x07: // AUX_TYPE_PERF_MDB_SUCCESS
                case 0x0E: // AUX_TYPE_PERF_BG_MDB_SUCCESS
                case 0x13: // AUX_TYPE_PERF_FG_MDB_SUCCESS
                    ParsePerfMdbSuccess(ref reader, children, budget);
                    return;
                case 0x08: // AUX_TYPE_PERF_GC_SUCCESS
                case 0x0F: // AUX_TYPE_PERF_BG_GC_SUCCESS
                case 0x14: // AUX_TYPE_PERF_FG_GC_SUCCESS
                    ParsePerfGcSuccess(ref reader, children, budget);
                    return;
                case 0x09: // AUX_TYPE_PERF_FAILURE
                case 0x10: // AUX_TYPE_PERF_BG_FAILURE
                case 0x15: // AUX_TYPE_PERF_FG_FAILURE
                    ParsePerfFailure(ref reader, children, budget);
                    return;
                case 0x0A: // AUX_TYPE_CLIENT_CONTROL
                    ParseClientControl(ref reader, children, budget);
                    return;
                case 0x0B: // AUX_TYPE_PERF_PROCESSINFO
                    ParsePerfProcessInfo(ref reader, blockBytes, blockStart, children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
                    return;
                case 0x16: // AUX_TYPE_OSVERSIONINFO
                    ParseOsVersionInfo(ref reader, children, budget);
                    return;
                case 0x17: // AUX_TYPE_EXORGINFO
                    ParseExOrgInfo(ref reader, children, budget);
                    return;
                case 0x18: // AUX_TYPE_PERF_ACCOUNTINFO
                    ParsePerfAccountInfo(ref reader, children, budget);
                    return;
                case 0x46: // AUX_TYPE_SERVER_CAPABILITIES
                    ParseServerCapabilities(ref reader, children, budget);
                    return;
                case 0x48: // AUX_TYPE_ENDPOINT_CAPABILITIES
                    ParseEndpointCapabilities(ref reader, children, budget);
                    return;
                case 0x49: // AUX_TYPE_EXCEPTION_TRACE
                    ParseExceptionTrace(ref reader, children, budget, cancellationToken);
                    return;
                case 0x4A: // AUX_CLIENT_CONNECTION_INFO
                    ParseClientConnectionInfo(ref reader, blockBytes, blockStart, children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
                    return;
                case 0x4B: // AUX_SERVER_SESSION_INFO
                    ParseServerSessionInfo(ref reader, blockBytes, blockStart, children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
                    return;
                case 0x4E: // AUX_PROTOCOL_DEVICE_IDENTIFICATION
                    ParseProtocolDeviceIdentification(ref reader, blockBytes, blockStart, children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
                    return;
                default:
                    warnings.Add($"Auxiliary block {blockIndex} has unrecognized version-1 type 0x{type:X2}; payload retained as raw.");
                    AddRemainingRaw(ref reader, children, "unrecognized auxiliary payload", budget);
                    return;
            }
        }

        if (version == 2)
        {
            switch (type)
            {
                case 0x04: // AUX_TYPE_PERF_SESSIONINFO (V2)
                    ParsePerfSessionInfoV2(ref reader, children, budget);
                    return;
                case 0x07: // AUX_TYPE_PERF_MDB_SUCCESS (V2)
                case 0x0E: // AUX_TYPE_PERF_BG_MDB_SUCCESS (V2)
                case 0x13: // AUX_TYPE_PERF_FG_MDB_SUCCESS (V2)
                    ParsePerfMdbSuccessV2(ref reader, children, budget);
                    return;
                case 0x08: // AUX_TYPE_PERF_GC_SUCCESS (V2)
                case 0x0F: // AUX_TYPE_PERF_BG_GC_SUCCESS (V2)
                case 0x14: // AUX_TYPE_PERF_FG_GC_SUCCESS (V2)
                    ParsePerfGcSuccessV2(ref reader, children, budget);
                    return;
                case 0x09: // AUX_TYPE_PERF_FAILURE (V2)
                case 0x10: // AUX_TYPE_PERF_BG_FAILURE (V2)
                case 0x15: // AUX_TYPE_PERF_FG_FAILURE (V2)
                    ParsePerfFailureV2(ref reader, children, budget);
                    return;
                case 0x0B: // AUX_TYPE_PERF_PROCESSINFO (shared with V1's layout)
                    ParsePerfProcessInfo(ref reader, blockBytes, blockStart, children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
                    return;
                default:
                    warnings.Add($"Auxiliary block {blockIndex} has unrecognized version-2 type 0x{type:X2}; payload retained as raw.");
                    AddRemainingRaw(ref reader, children, "unrecognized auxiliary payload", budget);
                    return;
            }
        }

        warnings.Add($"Auxiliary block {blockIndex} uses unsupported version {version}; payload retained as raw.");
        AddRemainingRaw(ref reader, children, "unsupported auxiliary version", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.1 AUX_PERF_REQUESTID -------------------------------------------------

    private static void ParsePerfRequestId(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "RequestID", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.4 AUX_PERF_CLIENTINFO ------------------------------------------------

    private static void ParsePerfClientInfo(
        ref MapiReader reader,
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string label,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        AddUInt32(ref reader, children, "AdapterSpeed", budget);
        AddUInt16(ref reader, children, "ClientID", budget);
        var machineNameOffset = AddUInt16(ref reader, children, "MachineNameOffset", budget);
        var userNameOffset = AddUInt16(ref reader, children, "UserNameOffset", budget);
        var clientIpSize = AddUInt16(ref reader, children, "ClientIPSize", budget);
        var clientIpOffset = AddUInt16(ref reader, children, "ClientIPOffset", budget);
        var clientIpMaskSize = AddUInt16(ref reader, children, "ClientIPMaskSize", budget);
        var clientIpMaskOffset = AddUInt16(ref reader, children, "ClientIPMaskOffset", budget);
        var adapterNameOffset = AddUInt16(ref reader, children, "AdapterNameOffset", budget);
        var macAddressSize = AddUInt16(ref reader, children, "MacAddressSize", budget);
        var macAddressOffset = AddUInt16(ref reader, children, "MacAddressOffset", budget);
        AddUInt16Formatted(ref reader, children, "ClientMode", FormatClientMode, budget);
        AddUInt16(ref reader, children, "Reserved", budget);

        AddOffsetString(blockBytes, blockStart, machineNameOffset, "MachineName", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetString(blockBytes, blockStart, userNameOffset, "UserName", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetBytes(blockBytes, blockStart, clientIpOffset, clientIpSize, "ClientIP", children, budget, warnings, blockIndex, label, trailerTracker);
        AddOffsetBytes(blockBytes, blockStart, clientIpMaskOffset, clientIpMaskSize, "ClientIPMask", children, budget, warnings, blockIndex, label, trailerTracker);
        AddOffsetString(blockBytes, blockStart, adapterNameOffset, "AdapterName", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetBytes(blockBytes, blockStart, macAddressOffset, macAddressSize, "MacAddress", children, budget, warnings, blockIndex, label, trailerTracker);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.5 AUX_PERF_SERVERINFO ------------------------------------------------

    private static void ParsePerfServerInfo(
        ref MapiReader reader,
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string label,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16Formatted(ref reader, children, "ServerType", FormatServerType, budget);
        var serverDnOffset = AddUInt16(ref reader, children, "ServerDNOffset", budget);
        var serverNameOffset = AddUInt16(ref reader, children, "ServerNameOffset", budget);
        AddOffsetString(blockBytes, blockStart, serverDnOffset, "ServerDN", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetString(blockBytes, blockStart, serverNameOffset, "ServerName", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.2 AUX_PERF_SESSIONINFO -----------------------------------------------

    private static void ParsePerfSessionInfoV1(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "Reserved", budget);
        AddGuid(ref reader, children, "SessionGuid", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.3 AUX_PERF_SESSIONINFO_V2 --------------------------------------------

    private static void ParsePerfSessionInfoV2(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "Reserved", budget);
        AddGuid(ref reader, children, "SessionGuid", budget);
        AddUInt32(ref reader, children, "ConnectionID", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.7 AUX_PERF_DEFMDB_SUCCESS --------------------------------------------

    private static void ParsePerfDefMdbSuccess(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToCompleteRequest", budget);
        AddUInt16(ref reader, children, "RequestID", budget);
        AddUInt16(ref reader, children, "Reserved", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.8 AUX_PERF_DEFGC_SUCCESS ---------------------------------------------

    private static void ParsePerfDefGcSuccess(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToCompleteRequest", budget);
        AddByte(ref reader, children, "RequestOperation", budget);
        AddReservedBytes(ref reader, children, "Reserved", 3, budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.9 AUX_PERF_MDB_SUCCESS -----------------------------------------------

    private static void ParsePerfMdbSuccess(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ClientID", budget);
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "RequestID", budget);
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToCompleteRequest", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.10 AUX_PERF_MDB_SUCCESS_V2 -------------------------------------------

    private static void ParsePerfMdbSuccessV2(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ProcessID", budget);
        AddUInt16(ref reader, children, "ClientID", budget);
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "RequestID", budget);
        AddUInt16(ref reader, children, "Reserved", budget);
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToCompleteRequest", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.11 AUX_PERF_GC_SUCCESS -----------------------------------------------

    private static void ParsePerfGcSuccess(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ClientID", budget);
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "Reserved1", budget);
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToCompleteRequest", budget);
        AddByte(ref reader, children, "RequestOperation", budget);
        AddReservedBytes(ref reader, children, "Reserved2", 3, budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.12 AUX_PERF_GC_SUCCESS_V2 --------------------------------------------

    private static void ParsePerfGcSuccessV2(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ProcessID", budget);
        AddUInt16(ref reader, children, "ClientID", budget);
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToCompleteRequest", budget);
        AddByte(ref reader, children, "RequestOperation", budget);
        AddReservedBytes(ref reader, children, "Reserved", 3, budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.13 AUX_PERF_FAILURE --------------------------------------------------

    private static void ParsePerfFailure(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ClientID", budget);
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "RequestID", budget);
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToFailRequest", budget);
        AddUInt32Hex(ref reader, children, "ResultCode", budget);
        AddByte(ref reader, children, "RequestOperation", budget);
        AddReservedBytes(ref reader, children, "Reserved", 3, budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.14 AUX_PERF_FAILURE_V2 -----------------------------------------------

    private static void ParsePerfFailureV2(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ProcessID", budget);
        AddUInt16(ref reader, children, "ClientID", budget);
        AddUInt16(ref reader, children, "ServerID", budget);
        AddUInt16(ref reader, children, "SessionID", budget);
        AddUInt16(ref reader, children, "RequestID", budget);
        AddUInt16(ref reader, children, "Reserved1", budget);
        AddUInt32(ref reader, children, "TimeSinceRequest", budget);
        AddUInt32(ref reader, children, "TimeToFailRequest", budget);
        AddUInt32Hex(ref reader, children, "ResultCode", budget);
        AddByte(ref reader, children, "RequestOperation", budget);
        AddReservedBytes(ref reader, children, "Reserved2", 3, budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.15 AUX_CLIENT_CONTROL ------------------------------------------------

    private static void ParseClientControl(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt32Formatted(ref reader, children, "EnableFlags", FormatEnableFlags, budget);
        AddUInt32(ref reader, children, "ExpiryTime", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.6 AUX_PERF_PROCESSINFO (shared by AUX_VERSION_1 and AUX_VERSION_2) ---

    private static void ParsePerfProcessInfo(
        ref MapiReader reader,
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string label,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        AddUInt16(ref reader, children, "ProcessID", budget);
        AddUInt16(ref reader, children, "Reserved1", budget);
        AddGuid(ref reader, children, "ProcessGuid", budget);
        var processNameOffset = AddUInt16(ref reader, children, "ProcessNameOffset", budget);
        AddUInt16(ref reader, children, "Reserved2", budget);
        AddOffsetString(blockBytes, blockStart, processNameOffset, "ProcessName", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.16 AUX_OSVERSIONINFO -------------------------------------------------

    private static void ParseOsVersionInfo(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt32(ref reader, children, "OSVersionInfoSize", budget);
        AddUInt32(ref reader, children, "MajorVersion", budget);
        AddUInt32(ref reader, children, "MinorVersion", budget);
        AddUInt32(ref reader, children, "BuildNumber", budget);
        AddReservedBytes(ref reader, children, "Reserved1", 132, budget);
        AddUInt16(ref reader, children, "ServicePackMajor", budget);
        AddUInt16(ref reader, children, "ServicePackMinor", budget);
        AddUInt32(ref reader, children, "Reserved2", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.17 AUX_EXORGINFO -------------------------------------------------------

    private static void ParseExOrgInfo(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt32Formatted(ref reader, children, "OrgFlags", FormatOrgFlags, budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.18 AUX_PERF_ACCOUNTINFO ----------------------------------------------

    private static void ParsePerfAccountInfo(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt16(ref reader, children, "ClientID", budget);
        AddUInt16(ref reader, children, "Reserved", budget);
        AddGuid(ref reader, children, "Account", budget);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.19 AUX_ENDPOINT_CAPABILITIES -----------------------------------------

    private static void ParseEndpointCapabilities(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt32Formatted(ref reader, children, "EndpointCapabilityFlag", FormatEndpointCapabilityFlag, budget);
    }

    private static void ParseServerCapabilities(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        AddUInt32Formatted(ref reader, children, "ServerCapabilityFlags", FormatServerCapabilityFlags, budget);
    }

    // ---- AUX_TYPE_EXCEPTION_TRACE ------------------------------------------------------------------
    // Not individually numbered in MS-OXCRPC; carries a RopIndex followed by a sequence of
    // newline-delimited ASCII trace lines that fill the rest of the declared block.

    private static void ParseExceptionTrace(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        AddUInt32(ref reader, children, "RopIndex", budget);
        var offset = reader.Position;
        var raw = reader.ReadRemaining("ExceptionMessage");
        if (raw.Length == 0)
        {
            return;
        }

        var lines = ImmutableArray.CreateBuilder<MapiNode>();
        var cursor = 0;
        var lineIndex = 0;
        while (cursor < raw.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = raw[cursor..];
            var newlineIndex = remaining.IndexOf((byte)'\n');
            int contentLength;
            int consumed;
            if (newlineIndex < 0)
            {
                contentLength = remaining.Length;
                consumed = remaining.Length;
            }
            else
            {
                contentLength = newlineIndex > 0 && remaining[newlineIndex - 1] == (byte)'\r' ? newlineIndex - 1 : newlineIndex;
                consumed = newlineIndex + 1;
            }
            var lineText = Encoding.ASCII.GetString(remaining[..contentLength]);
            ExtendedBufferParser.AddField(lines, $"ExceptionMessage[{lineIndex}]", offset + cursor, contentLength, lineText, budget);
            lineIndex++;
            cursor += consumed;
            if (lineIndex >= MapiParseLimits.MaxCollectionCount)
            {
                break;
            }
        }
        children.Add(new MapiNode(
            "ExceptionMessage",
            MapiNodeKind.Array,
            offset,
            raw.Length,
            $"{lineIndex:N0} line(s)",
            lines.ToImmutable()));
    }

    // ---- [MS-OXCRPC] 2.2.2.2.20 AUX_CLIENT_CONNECTION_INFO ----------------------------------------

    private static void ParseClientConnectionInfo(
        ref MapiReader reader,
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string label,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        AddGuid(ref reader, children, "ConnectionGUID", budget);
        var contextOffset = AddUInt16(ref reader, children, "OffsetConnectionContextInfo", budget);
        AddUInt16(ref reader, children, "Reserved", budget);
        AddUInt32(ref reader, children, "ConnectionAttempts", budget);
        AddUInt32Formatted(ref reader, children, "ConnectionFlags", FormatConnectionFlags, budget);
        AddOffsetString(blockBytes, blockStart, contextOffset, "ConnectionContextInfo", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.21 AUX_SERVER_SESSION_INFO -------------------------------------------

    private static void ParseServerSessionInfo(
        ref MapiReader reader,
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string label,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        var contextOffset = AddUInt16(ref reader, children, "OffsetServerSessionContextInfo", budget);
        AddOffsetString(blockBytes, blockStart, contextOffset, "ServerSessionContextInfo", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
    }

    // ---- [MS-OXCRPC] 2.2.2.2.22 AUX_PROTOCOL_DEVICE_IDENTIFICATION -------------------------------

    private static void ParseProtocolDeviceIdentification(
        ref MapiReader reader,
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string label,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        var manufacturerOffset = AddUInt16(ref reader, children, "DeviceManufacturerOffset", budget);
        var modelOffset = AddUInt16(ref reader, children, "DeviceModelOffset", budget);
        var serialOffset = AddUInt16(ref reader, children, "DeviceSerialNumberOffset", budget);
        var versionOffset = AddUInt16(ref reader, children, "DeviceVersionOffset", budget);
        var firmwareOffset = AddUInt16(ref reader, children, "DeviceFirmwareVersionOffset", budget);
        AddOffsetString(blockBytes, blockStart, manufacturerOffset, "DeviceManufacturer", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetString(blockBytes, blockStart, modelOffset, "DeviceModel", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetString(blockBytes, blockStart, serialOffset, "DeviceSerialNumber", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetString(blockBytes, blockStart, versionOffset, "DeviceVersion", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
        AddOffsetString(blockBytes, blockStart, firmwareOffset, "DeviceFirmwareVersion", children, budget, warnings, blockIndex, label, cancellationToken, trailerTracker);
    }

    // ---- Shared field helpers ----------------------------------------------------------------------

    private static ushort AddUInt16(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        ExtendedBufferParser.AddField(children, name, offset, 2, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static ushort AddUInt16Formatted(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        Func<ushort, string> format,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        ExtendedBufferParser.AddField(children, name, offset, 2, format(value), budget);
        return value;
    }

    private static uint AddUInt32(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(children, name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static uint AddUInt32Hex(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(children, name, offset, 4, $"0x{value:X8}", budget);
        return value;
    }

    private static uint AddUInt32Formatted(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        Func<uint, string> format,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(children, name, offset, 4, format(value), budget);
        return value;
    }

    private static byte AddByte(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, $"0x{value:X2}", budget);
        return value;
    }

    private static Guid AddGuid(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadGuid(name);
        ExtendedBufferParser.AddField(children, name, offset, 16, value.ToString(), budget);
        return value;
    }

    private static void AddReservedBytes(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, int count, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var bytes = reader.ReadBytes(count, name);
        children.Add(ExtendedBufferParser.RawNode(name, bytes, offset, budget));
    }

    /// <summary>
    /// Reads a null-terminated UTF-16LE string located at <paramref name="offset"/> bytes from the
    /// beginning of the AUX_HEADER structure, matching [MS-OXCRPC]'s offset semantics. This replaces
    /// the pinned upstream parser's "TODO: actually read from the offset, not from the current
    /// position" behavior with a validated, bounded, deterministic random-access read.
    /// </summary>
    private static void AddOffsetString(
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ushort offset,
        string fieldName,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string blockLabel,
        CancellationToken cancellationToken,
        OffsetTrailerTracker trailerTracker)
    {
        if (offset == 0)
        {
            return;
        }
        if (offset >= blockBytes.Length)
        {
            warnings.Add(
                $"Auxiliary block {blockIndex} ({blockLabel}) {fieldName}Offset {offset} is outside the {blockBytes.Length}-byte block; {fieldName} was not read.");
            return;
        }

        var absolute = checked(blockStart + offset);
        var slice = blockBytes[offset..];
        try
        {
            var stringReader = new MapiReader(slice, cancellationToken, checked((int)absolute));
            var value = stringReader.ReadNullTerminatedUnicode(fieldName);
            ExtendedBufferParser.AddField(children, fieldName, absolute, stringReader.LocalPosition, value, budget);
            trailerTracker.MaxLocalEnd = Math.Max(trailerTracker.MaxLocalEnd, offset + stringReader.LocalPosition);
        }
        catch (MapiParseException ex)
        {
            warnings.Add(
                $"Auxiliary block {blockIndex} ({blockLabel}) {fieldName} at offset {offset} could not be read: {ex.Message}");
            children.Add(ExtendedBufferParser.RawNode($"{fieldName} (malformed)", slice, absolute, budget));
            // The raw fallback above already surfaces every byte from the offset to the end of the
            // block, so none of it should also be reported as unexplained "trailing" bytes.
            trailerTracker.MaxLocalEnd = blockBytes.Length;
        }
    }

    /// <summary>
    /// Reads a fixed-size byte array located at <paramref name="offset"/> bytes from the beginning
    /// of the AUX_HEADER structure, with the declared <paramref name="count"/> validated against the
    /// block's bounds before any bytes are read.
    /// </summary>
    private static void AddOffsetBytes(
        ReadOnlySpan<byte> blockBytes,
        long blockStart,
        ushort offset,
        ushort count,
        string fieldName,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string> warnings,
        int blockIndex,
        string blockLabel,
        OffsetTrailerTracker trailerTracker)
    {
        if (offset == 0 || count == 0)
        {
            return;
        }
        if (offset >= blockBytes.Length || count > blockBytes.Length - offset)
        {
            warnings.Add(
                $"Auxiliary block {blockIndex} ({blockLabel}) {fieldName}Offset {offset} with size {count} is outside the {blockBytes.Length}-byte block; {fieldName} was not read.");
            return;
        }

        var absolute = checked(blockStart + offset);
        children.Add(ExtendedBufferParser.RawNode(fieldName, blockBytes.Slice(offset, count), absolute, budget));
        trailerTracker.MaxLocalEnd = Math.Max(trailerTracker.MaxLocalEnd, offset + count);
    }

    // ---- Enum/flag formatting ------------------------------------------------------------------------

    private static string FormatVersion(byte version) => version switch
    {
        1 => "1 (AUX_VERSION_1)",
        2 => "2 (AUX_VERSION_2)",
        _ => $"{version} (unknown)"
    };

    private static string FormatClientMode(ushort value) => value switch
    {
        0x00 => "0x0000 (CLIENTMODE_UNKNOWN)",
        0x01 => "0x0001 (CLIENTMODE_CLASSIC)",
        0x02 => "0x0002 (CLIENTMODE_CACHED)",
        _ => $"0x{value:X4} (unknown)"
    };

    private static string FormatServerType(ushort value) => value switch
    {
        0x00 => "0x0000 (SERVERTYPE_UNKNOWN)",
        0x01 => "0x0001 (SERVERTYPE_PRIVATE)",
        0x02 => "0x0002 (SERVERTYPE_PUBLIC)",
        0x03 => "0x0003 (SERVERTYPE_DIRECTORY)",
        0x04 => "0x0004 (SERVERTYPE_REFERRAL)",
        _ => $"0x{value:X4} (unknown)"
    };

    private static string FormatEnableFlags(uint value) => FormatFlagBits(
        value,
        0x1D,
        (0x00000001u, "ENABLE_PERF_SENDTOSERVER"),
        (0x00000004u, "ENABLE_COMPRESSION"),
        (0x00000008u, "ENABLE_HTTP_TUNNELING"),
        (0x00000010u, "ENABLE_PERF_SENDGCDATA"));

    private static string FormatOrgFlags(uint value) => FormatFlagBits(
        value,
        0x3,
        (0x00000001u, "PUBLIC_FOLDERS_ENABLED"),
        (0x00000002u, "USE_AUTODISCOVER_FOR_PUBLIC_FOLDER_CONFIGURATION"));

    private static string FormatEndpointCapabilityFlag(uint value) => FormatFlagBits(
        value,
        0x1,
        (0x00000001u, "ENDPOINT_CAPABILITIES_SINGLE_ENDPOINT"));

    private static string FormatServerCapabilityFlags(uint value) => FormatFlagBits(
        value,
        0xB,
        (0x00000001u, "PACKED_FAST_TRANSFER_UPLOAD_BUFFERS"),
        (0x00000002u, "PACKED_WRITE_STREAM_UPLOAD_BUFFERS"),
        (0x00000008u, "ULTRA_LARGE_PACKED_DOWNLOAD_BUFFERS"));

    private static void AddRemainingRaw(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string field,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        children.Add(ExtendedBufferParser.RawNode("Payload", reader.ReadRemaining(field), offset, budget));
    }

    private static string FormatConnectionFlags(uint value) => value == 0
        ? "0x00000000 (Clientisnotdesignatingamodeofoperation)"
        : FormatFlagBits(value, 0x1, (0x00000001u, "Clientisrunningincachedmode"));

    private static string FormatFlagBits(uint value, uint knownMask, params (uint Bit, string Name)[] bits)
    {
        var names = new List<string>();
        foreach (var (bit, name) in bits)
        {
            if ((value & bit) != 0)
            {
                names.Add(name);
            }
        }
        var unknown = value & ~knownMask;
        if (unknown != 0)
        {
            names.Add($"unknown 0x{unknown:X8}");
        }
        return names.Count == 0 ? $"0x{value:X8}" : $"0x{value:X8} ({string.Join(", ", names)})";
    }

    internal static string AuxiliaryTypeName(byte version, byte type)
    {
        if (version == 1)
        {
            return type switch
            {
                0x01 => "0x01 AUX_TYPE_PERF_REQUESTID",
                0x02 => "0x02 AUX_TYPE_PERF_CLIENTINFO",
                0x03 => "0x03 AUX_TYPE_PERF_SERVERINFO",
                0x04 => "0x04 AUX_TYPE_PERF_SESSIONINFO",
                0x05 => "0x05 AUX_TYPE_PERF_DEFMDB_SUCCESS",
                0x06 => "0x06 AUX_TYPE_PERF_DEFGC_SUCCESS",
                0x07 => "0x07 AUX_TYPE_PERF_MDB_SUCCESS",
                0x08 => "0x08 AUX_TYPE_PERF_GC_SUCCESS",
                0x09 => "0x09 AUX_TYPE_PERF_FAILURE",
                0x0A => "0x0A AUX_TYPE_CLIENT_CONTROL",
                0x0B => "0x0B AUX_TYPE_PERF_PROCESSINFO",
                0x0C => "0x0C AUX_TYPE_PERF_BG_DEFMDB_SUCCESS",
                0x0D => "0x0D AUX_TYPE_PERF_BG_DEFGC_SUCCESS",
                0x0E => "0x0E AUX_TYPE_PERF_BG_MDB_SUCCESS",
                0x0F => "0x0F AUX_TYPE_PERF_BG_GC_SUCCESS",
                0x10 => "0x10 AUX_TYPE_PERF_BG_FAILURE",
                0x11 => "0x11 AUX_TYPE_PERF_FG_DEFMDB_SUCCESS",
                0x12 => "0x12 AUX_TYPE_PERF_FG_DEFGC_SUCCESS",
                0x13 => "0x13 AUX_TYPE_PERF_FG_MDB_SUCCESS",
                0x14 => "0x14 AUX_TYPE_PERF_FG_GC_SUCCESS",
                0x15 => "0x15 AUX_TYPE_PERF_FG_FAILURE",
                0x16 => "0x16 AUX_TYPE_OSVERSIONINFO",
                0x17 => "0x17 AUX_TYPE_EXORGINFO",
                0x18 => "0x18 AUX_TYPE_PERF_ACCOUNTINFO",
                0x46 => "0x46 AUX_TYPE_SERVER_CAPABILITIES",
                0x48 => "0x48 AUX_TYPE_ENDPOINT_CAPABILITIES",
                0x49 => "0x49 AUX_TYPE_EXCEPTION_TRACE",
                0x4A => "0x4A AUX_CLIENT_CONNECTION_INFO",
                0x4B => "0x4B AUX_SERVER_SESSION_INFO",
                0x4E => "0x4E AUX_PROTOCOL_DEVICE_IDENTIFICATION",
                _ => $"0x{type:X2} (unrecognized version-1 type)"
            };
        }
        if (version == 2)
        {
            return type switch
            {
                0x04 => "0x04 AUX_TYPE_PERF_SESSIONINFO (V2)",
                0x07 => "0x07 AUX_TYPE_PERF_MDB_SUCCESS (V2)",
                0x08 => "0x08 AUX_TYPE_PERF_GC_SUCCESS (V2)",
                0x09 => "0x09 AUX_TYPE_PERF_FAILURE (V2)",
                0x0B => "0x0B AUX_TYPE_PERF_PROCESSINFO",
                0x0E => "0x0E AUX_TYPE_PERF_BG_MDB_SUCCESS (V2)",
                0x0F => "0x0F AUX_TYPE_PERF_BG_GC_SUCCESS (V2)",
                0x10 => "0x10 AUX_TYPE_PERF_BG_FAILURE (V2)",
                0x13 => "0x13 AUX_TYPE_PERF_FG_MDB_SUCCESS (V2)",
                0x14 => "0x14 AUX_TYPE_PERF_FG_GC_SUCCESS (V2)",
                0x15 => "0x15 AUX_TYPE_PERF_FG_FAILURE (V2)",
                _ => $"0x{type:X2} (unrecognized version-2 type)"
            };
        }
        return $"0x{type:X2} (unsupported version {version})";
    }
}
