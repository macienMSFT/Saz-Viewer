using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SazViewer.Core;

internal static class ExtendedBufferParser
{
    private const ushort Compressed = 0x0001;
    private const ushort XorMagic = 0x0002;
    private const ushort Last = 0x0004;

    public static ImmutableArray<MapiNode> ParseSequence(
        ReadOnlySpan<byte> bytes,
        long absoluteOffset,
        List<string> warnings,
        bool parseRops,
        MapiDirection direction,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        var nodes = ImmutableArray.CreateBuilder<MapiNode>();
        var reader = new MapiReader(bytes, cancellationToken, checked((int)absoluteOffset));
        var index = 0;
        while (!reader.End)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = reader.Position;
            if (reader.Remaining < 8)
            {
                warnings.Add($"Extended buffer {index} has only {reader.Remaining} bytes remaining; 8 are required.");
                nodes.Add(RawNode("Truncated extended buffer", reader.ReadRemaining("truncated buffer"), start, budget));
                break;
            }

            var version = reader.ReadUInt16("RPC_HEADER_EXT.Version");
            var flags = reader.ReadUInt16("RPC_HEADER_EXT.Flags");
            var size = reader.ReadUInt16("RPC_HEADER_EXT.Size");
            var sizeActual = reader.ReadUInt16("RPC_HEADER_EXT.SizeActual");
            if (size > reader.Remaining)
            {
                warnings.Add($"Extended buffer {index} declares {size} payload bytes but only {reader.Remaining} remain.");
                nodes.Add(RawNode("Truncated extended buffer", reader.ReadRemaining("truncated payload"), start, budget));
                break;
            }

            var transmittedOffset = reader.Position;
            var transmitted = reader.ReadBytes(size, "RPC_HEADER_EXT.Payload").ToArray();
            var children = ImmutableArray.CreateBuilder<MapiNode>();
            AddField(children, "Version", start, 2, version.ToString(), budget);
            AddField(children, "Flags", start + 2, 2, FormatFlags(flags), budget);
            AddField(children, "Size", start + 4, 2, size.ToString(), budget);
            AddField(children, "SizeActual", start + 6, 2, sizeActual.ToString(), budget);

            if (version != 0)
            {
                warnings.Add($"Extended buffer {index} uses unsupported version {version}; payload retained as raw.");
                children.Add(RawNode("Payload", transmitted, transmittedOffset, budget));
            }
            else if ((flags & ~(Compressed | XorMagic | Last)) != 0)
            {
                warnings.Add($"Extended buffer {index} has unknown flags 0x{flags:X4}; known transforms were not applied.");
                children.Add(RawNode("Payload", transmitted, transmittedOffset, budget));
            }
            else if (!TryDecode(transmitted, flags, sizeActual, out var decoded, out var error))
            {
                warnings.Add($"Extended buffer {index} could not be decoded: {error}");
                children.Add(RawNode("Encoded payload", transmitted, transmittedOffset, budget));
            }
            else
            {
                var decodedChildren = parseRops
                    ? RopBufferParser.Parse(decoded, transmittedOffset, direction, warnings, budget, cancellationToken)
                    : ParseAuxiliary(decoded, transmittedOffset, warnings, budget, cancellationToken);
                children.Add(new MapiNode(
                    parseRops ? "ROP payload" : "Auxiliary payload",
                    MapiNodeKind.Structure,
                    transmittedOffset,
                    decoded.Length,
                    flags == 0 ? null : $"Decoded from {size} transmitted bytes",
                    decodedChildren));
            }

            budget.Claim(0);
            nodes.Add(new MapiNode(
                $"Extended buffer {index}",
                MapiNodeKind.Structure,
                start,
                reader.Position - start,
                (flags & Last) != 0 ? "Last" : null,
                children.ToImmutable()));
            index++;
        }
        return nodes.ToImmutable();
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> transmitted,
        ushort flags,
        int sizeActual,
        out byte[] decoded,
        out string? error)
    {
        decoded = transmitted.ToArray();
        error = null;
        if ((flags & XorMagic) != 0)
        {
            for (var index = 0; index < decoded.Length; index++)
            {
                decoded[index] ^= 0xA5;
            }
        }

        if ((flags & Compressed) != 0)
        {
            if (!TryDecompressLz77(decoded, sizeActual, out decoded, out error))
            {
                return false;
            }
        }
        else if (sizeActual != decoded.Length)
        {
            error = $"uncompressed SizeActual {sizeActual} does not match payload length {decoded.Length}";
            decoded = [];
            return false;
        }
        return true;
    }

    internal static bool TryDecompressLz77(
        ReadOnlySpan<byte> input,
        int expectedLength,
        out byte[] output,
        out string? error)
    {
        output = [];
        error = null;
        if (expectedLength < 0 || expectedLength > MapiParseLimits.MaxPayloadBytes)
        {
            error = $"decoded length {expectedLength:N0} exceeds the safe limit";
            return false;
        }

        var result = new byte[expectedLength];
        var inputPosition = 0;
        var outputPosition = 0;
        uint mask = 0;
        var bitsRemaining = 0;
        byte? sharedLengthByte = null;
        try
        {
            while (inputPosition < input.Length)
            {
                if (bitsRemaining == 0)
                {
                    if (input.Length - inputPosition < 4)
                    {
                        error = "truncated 32-bit control mask";
                        return false;
                    }
                    mask = BinaryPrimitives.ReadUInt32LittleEndian(input[inputPosition..]);
                    inputPosition += 4;
                    bitsRemaining = 32;
                }

                var backReference = (mask & 0x80000000) != 0;
                mask <<= 1;
                bitsRemaining--;
                if (!backReference)
                {
                    if (inputPosition >= input.Length || outputPosition >= result.Length)
                    {
                        error = "literal exceeds the declared input or output extent";
                        return false;
                    }
                    result[outputPosition++] = input[inputPosition++];
                    continue;
                }

                if (input.Length - inputPosition < 2)
                {
                    error = "truncated back-reference token";
                    return false;
                }
                var token = BinaryPrimitives.ReadUInt16LittleEndian(input[inputPosition..]);
                inputPosition += 2;
                var offset = (token >> 3) + 1;
                var length = (token & 7) + 3;
                if (length > 9)
                {
                    int additive;
                    if (sharedLengthByte.HasValue)
                    {
                        additive = (sharedLengthByte.Value >> 4) & 0x0F;
                        sharedLengthByte = null;
                    }
                    else
                    {
                        if (inputPosition >= input.Length)
                        {
                            error = "truncated shared length byte";
                            return false;
                        }
                        sharedLengthByte = input[inputPosition++];
                        additive = sharedLengthByte.Value & 0x0F;
                    }
                    length += additive;
                }
                if (length > 24)
                {
                    if (inputPosition >= input.Length)
                    {
                        error = "truncated extended length byte";
                        return false;
                    }
                    length += input[inputPosition++];
                }
                if (length > 279)
                {
                    if (input.Length - inputPosition < 2)
                    {
                        error = "truncated 16-bit extended length";
                        return false;
                    }
                    length = BinaryPrimitives.ReadUInt16LittleEndian(input[inputPosition..]) + 3;
                    inputPosition += 2;
                }
                if (offset > outputPosition)
                {
                    error = $"back-reference offset {offset} precedes the decoded output";
                    return false;
                }
                if (length > result.Length - outputPosition)
                {
                    error = $"back-reference length {length} exceeds declared output size";
                    return false;
                }
                for (var count = 0; count < length; count++)
                {
                    result[outputPosition] = result[outputPosition - offset];
                    outputPosition++;
                }
            }
        }
        catch (OverflowException)
        {
            error = "integer overflow while decoding";
            return false;
        }

        if (outputPosition != expectedLength)
        {
            error = $"decoded {outputPosition} bytes; expected exactly {expectedLength}";
            return false;
        }
        output = result;
        return true;
    }

    private static ImmutableArray<MapiNode> ParseAuxiliary(
        ReadOnlySpan<byte> decoded,
        long offset,
        List<string> warnings,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<MapiNode>();
        var reader = new MapiReader(decoded, cancellationToken, checked((int)offset));
        var index = 0;
        while (!reader.End)
        {
            var start = reader.Position;
            if (reader.Remaining < 4)
            {
                warnings.Add($"Auxiliary payload ends with {reader.Remaining} unframed bytes.");
                result.Add(RawNode("Trailing auxiliary bytes", reader.ReadRemaining("auxiliary tail"), start, budget));
                break;
            }
            var size = reader.ReadUInt16("AUX_HEADER.Size");
            var version = reader.ReadByte("AUX_HEADER.Version");
            var type = reader.ReadByte("AUX_HEADER.Type");
            if (size < 4 || size - 4 > reader.Remaining)
            {
                warnings.Add($"Auxiliary block {index} has invalid size {size}.");
                result.Add(RawNode("Malformed auxiliary block", reader.ReadRemaining("malformed auxiliary"), start, budget));
                break;
            }
            var payload = reader.ReadBytes(size - 4, "Auxiliary payload");
            var children = ImmutableArray.CreateBuilder<MapiNode>();
            AddField(children, "Version", start + 2, 1, version.ToString(), budget);
            AddField(children, "Type", start + 3, 1, AuxiliaryTypeName(version, type), budget);
            children.Add(RawNode("Payload", payload, start + 4, budget));
            budget.Claim(0);
            result.Add(new MapiNode(
                $"Auxiliary block {index}",
                MapiNodeKind.Structure,
                start,
                size,
                null,
                children.ToImmutable()));
            index++;
        }
        return result.ToImmutable();
    }

    internal static MapiNode RawNode(
        string name,
        ReadOnlySpan<byte> bytes,
        long offset,
        MapiNodeBudget budget)
    {
        budget.Claim(0);
        var shown = bytes[..Math.Min(bytes.Length, MapiParseLimits.MaxRawNodeBytes)];
        var value = Convert.ToHexString(shown);
        if (shown.Length < bytes.Length)
        {
            value += $" ... [{bytes.Length - shown.Length:N0} more bytes]";
        }
        return MapiNode.Leaf(name, MapiNodeKind.Raw, offset, bytes.Length, value);
    }

    internal static void AddField(
        ImmutableArray<MapiNode>.Builder target,
        string name,
        long offset,
        long length,
        string value,
        MapiNodeBudget budget)
    {
        budget.Claim(0);
        target.Add(MapiNode.Leaf(name, MapiNodeKind.Field, offset, length, value));
    }

    private static string FormatFlags(ushort flags)
    {
        var names = new List<string>();
        if ((flags & Compressed) != 0) names.Add("Compressed");
        if ((flags & XorMagic) != 0) names.Add("XorMagic");
        if ((flags & Last) != 0) names.Add("Last");
        return $"0x{flags:X4}" + (names.Count == 0 ? string.Empty : $" ({string.Join(", ", names)})");
    }

    private static string AuxiliaryTypeName(byte version, byte type) =>
        (version, type) switch
        {
            (1, 0x01) => "0x01 PERF_REQUESTID",
            (1, 0x02) => "0x02 PERF_CLIENTINFO",
            (1, 0x03) => "0x03 PERF_SERVERINFO",
            (1, 0x04) => "0x04 PERF_SESSIONINFO",
            (1, 0x0A) => "0x0A CLIENT_CONTROL",
            (1, 0x16) => "0x16 OSVERSIONINFO",
            (1, 0x17) => "0x17 EXORGINFO",
            (1, 0x18) => "0x18 PERF_ACCOUNTINFO",
            (1, 0x48) => "0x48 ENDPOINT_CAPABILITIES",
            (1, 0x49) => "0x49 EXCEPTION_TRACE",
            (1, 0x4A) => "0x4A CLIENT_CONNECTION_INFO",
            (1, 0x4B) => "0x4B SERVER_SESSION_INFO",
            (1, 0x4E) => "0x4E PROTOCOL_DEVICE_IDENTIFICATION",
            _ => $"0x{type:X2} (version {version})"
        };
}
