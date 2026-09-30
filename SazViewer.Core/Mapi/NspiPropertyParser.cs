using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SazViewer.Core;

internal static class NspiPropertyParser
{
    private const ushort MultiValue = 0x1000;

    public static MapiNode ParseValueList(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        int depth = 0,
        uint? codePage = null,
        List<string>? warnings = null)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var count = reader.ReadCount32($"{name}.PropertyValueCount");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddField(children, "PropertyValueCount", start, 4, count.ToString(CultureInfo.InvariantCulture), budget, depth);
        for (var index = 0; index < count; index++)
        {
            children.Add(ParseTaggedValue(ref reader, $"PropertyValue[{index}]", budget, depth + 1, codePage, warnings));
        }
        return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, null, children.ToImmutable());
    }

    public static MapiNode ParseTaggedValue(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        int depth = 0,
        uint? codePage = null,
        List<string>? warnings = null)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var typeOffset = reader.Position;
        var type = reader.ReadUInt16($"{name}.PropertyType");
        var idOffset = reader.Position;
        var id = reader.ReadUInt16($"{name}.PropertyId");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddField(children, "PropertyType", typeOffset, 2, PropertyTypeName(type), budget, depth);
        AddField(children, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(id), budget, depth);
        children.Add(ParseValue(
            ref reader,
            type,
            "PropertyValue",
            budget,
            depth + 1,
            includePresence: true,
            codePage,
            warnings: warnings));
        return new MapiNode(
            name,
            MapiNodeKind.Property,
            start,
            reader.Position - start,
            $"0x{id:X4}:{type:X4}",
            children.ToImmutable());
    }

    public static MapiNode ParseRow(
        ref MapiReader reader,
        IReadOnlyList<uint> propertyTags,
        string name,
        MapiNodeBudget budget,
        int depth = 0,
        uint? codePage = null,
        List<string>? warnings = null)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var flagsOffset = reader.Position;
        var flags = reader.ReadByte($"{name}.Flags");
        if (flags is not (0 or 1))
        {
            throw new MapiParseException(flagsOffset, $"{name}.Flags has unsupported value 0x{flags:X2}.");
        }
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddField(children, "Flags", flagsOffset, 1, flags == 0 ? "All values" : "Flagged values", budget, depth);
        for (var index = 0; index < propertyTags.Count; index++)
        {
            var declaredType = (ushort)propertyTags[index];
            var propertyId = (ushort)(propertyTags[index] >> 16);
            var valueStart = reader.Position;
            var valueChildren = ImmutableArray.CreateBuilder<MapiNode>();
            ushort actualType = declaredType;
            if (actualType == 0)
            {
                var actualTypeOffset = reader.Position;
                actualType = reader.ReadUInt16($"{name}.Value[{index}].PropertyType");
                AddField(valueChildren, "PropertyType", actualTypeOffset, 2, PropertyTypeName(actualType), budget, depth);
            }
            if (flags == 1)
            {
                var flagOffset = reader.Position;
                var valueFlag = reader.ReadByte($"{name}.Value[{index}].Flag");
                AddField(valueChildren, "Flag", flagOffset, 1, valueFlag switch
                {
                    0x00 => "Available",
                    0x01 => "Unavailable",
                    0x0A => "Error",
                    _ => $"Unknown 0x{valueFlag:X2}"
                }, budget, depth);
                if (valueFlag == 0x01)
                {
                    children.Add(new MapiNode(
                        $"Value[{index}]",
                        MapiNodeKind.Property,
                        valueStart,
                        reader.Position - valueStart,
                        $"{MapiPropertyNames.FormatPidTag(propertyId)}:{declaredType:X4}",
                        valueChildren.ToImmutable()));
                    continue;
                }
                if (valueFlag == 0x0A)
                {
                    actualType = 0x000A;
                }
                else if (valueFlag != 0)
                {
                    throw new MapiParseException(flagOffset, $"Unknown address-book property flag 0x{valueFlag:X2}.");
                }
            }
            valueChildren.Add(ParseValue(
                ref reader,
                actualType,
                "Value",
                budget,
                depth + 1,
                includePresence: true,
                codePage,
                warnings: warnings));
            children.Add(new MapiNode(
                $"Value[{index}]",
                MapiNodeKind.Property,
                valueStart,
                reader.Position - valueStart,
                $"{MapiPropertyNames.FormatPidTag(propertyId)}:{actualType:X4}",
                valueChildren.ToImmutable()));
        }
        return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, null, children.ToImmutable());
    }

    public static MapiNode ParseValue(
        ref MapiReader reader,
        ushort type,
        string name,
        MapiNodeBudget budget,
        int depth,
        bool includePresence,
        uint? codePage = null,
        bool addressBookSemantics = true,
        List<string>? warnings = null)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        if (includePresence && HasPresenceIndicator(type))
        {
            var presentOffset = reader.Position;
            var present = reader.ReadByte($"{name}.HasValue");
            AddField(children, "HasValue", presentOffset, 1, present == 0 ? "false" : "true", budget, depth);
            if (present == 0)
            {
                return new MapiNode(name, MapiNodeKind.Property, start, reader.Position - start, "null", children.ToImmutable());
            }
        }

        if ((type & MultiValue) != 0)
        {
            var baseType = (ushort)(type & ~MultiValue);
            var countOffset = reader.Position;
            var count = reader.ReadCount32($"{name}.Count");
            AddField(children, "Count", countOffset, 4, count.ToString(CultureInfo.InvariantCulture), budget, depth);
            for (var index = 0; index < count; index++)
            {
                children.Add(ParseValue(
                    ref reader,
                    baseType,
                    $"[{index}]",
                    budget,
                    depth + 1,
                    includePresence: addressBookSemantics && HasPresenceIndicator(baseType),
                    codePage,
                    addressBookSemantics,
                    warnings));
            }
            return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, PropertyTypeName(type), children.ToImmutable());
        }

        string value;
        switch (type)
        {
            case 0x0000:
            case 0x0001:
            case 0x000D:
                value = "null";
                break;
            case 0x0002:
                value = reader.ReadInt16(name).ToString(CultureInfo.InvariantCulture);
                break;
            case 0x0003:
                value = reader.ReadInt32(name).ToString(CultureInfo.InvariantCulture);
                break;
            case 0x0004:
                value = reader.ReadSingle(name).ToString("R", CultureInfo.InvariantCulture);
                break;
            case 0x0005:
            case 0x0007:
                value = reader.ReadDouble(name).ToString("R", CultureInfo.InvariantCulture);
                break;
            case 0x0006:
            case 0x0014:
                value = reader.ReadInt64(name).ToString(CultureInfo.InvariantCulture);
                break;
            case 0x000A:
                value = $"0x{reader.ReadUInt32(name):X8}";
                break;
            case 0x000B:
                value = reader.ReadByte(name) == 0 ? "false" : "true";
                break;
            case 0x001E:
                var stringOffset = reader.Position;
                var stringBytes = reader.ReadNullTerminatedBytes(name);
                if (TryGetString8Encoding(codePage, out var encoding))
                {
                    try
                    {
                        value = encoding.GetString(stringBytes);
                    }
                    catch (DecoderFallbackException exception)
                    {
                        throw new MapiParseException(stringOffset, $"{name} is invalid for code page {codePage}.", exception);
                    }
                }
                else
                {
                    children.Add(ExtendedBufferParser.RawNode("Encoded bytes", stringBytes, stringOffset, budget));
                    value = codePage.HasValue
                        ? $"Unsupported code page {codePage.Value}; bytes retained"
                        : "Code page unavailable; bytes retained";
                    warnings?.Add($"{name} {value.ToLowerInvariant()}.");
                }
                break;
            case 0x001F:
                value = reader.ReadNullTerminatedUnicode(name);
                break;
            case 0x0040:
                value = $"0x{reader.ReadUInt64(name):X16}";
                break;
            case 0x0048:
                value = reader.ReadGuid(name).ToString();
                break;
            case 0x00FB:
                var serverIdLengthOffset = reader.Position;
                var serverIdLength = reader.ReadUInt16($"{name}.Length");
                if (serverIdLength > reader.Remaining)
                {
                    throw new MapiParseException(
                        serverIdLengthOffset,
                        $"{name}.Length {serverIdLength:N0} exceeds the remaining extent.");
                }
                var serverIdOffset = reader.Position;
                var serverId = reader.ReadBytes(serverIdLength, name);
                budget.Claim(depth);
                children.Add(MapiNode.Leaf(
                    "Bytes",
                    MapiNodeKind.Field,
                    serverIdOffset,
                    serverId.Length,
                    BoundedHex(serverId)));
                value = $"{serverIdLength:N0} bytes";
                break;
            case 0x00FD:
                children.Add(NspiRestrictionParser.Parse(ref reader, budget, depth + 1, codePage, warnings));
                value = "Restriction";
                break;
            case 0x0102:
                var length = ReadLength32(ref reader, $"{name}.Length");
                var payloadOffset = reader.Position;
                var payload = reader.ReadBytes(length, name);
                budget.Claim(depth);
                children.Add(MapiNode.Leaf(
                    "Bytes",
                    MapiNodeKind.Field,
                    payloadOffset,
                    payload.Length,
                    BoundedHex(payload)));
                value = $"{length:N0} bytes";
                break;
            default:
                throw new MapiParseException(start, $"Unsupported property type 0x{type:X4}; its length cannot be determined safely.");
        }
        return new MapiNode(name, MapiNodeKind.Property, start, reader.Position - start, value, children.ToImmutable());
    }

    public static int ReadLength32(ref MapiReader reader, string field)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(field);
        if (value > MapiParseLimits.MaxPayloadBytes || value > reader.Remaining)
        {
            throw new MapiParseException(offset, $"{field} length {value:N0} exceeds the safe or remaining extent.");
        }
        return (int)value;
    }

    public static MapiNode ParseStandardTaggedValue(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        int depth = 0,
        ushort? valueTypeOverride = null,
        uint? codePage = null,
        List<string>? warnings = null)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var typeOffset = reader.Position;
        var embeddedType = reader.ReadUInt16($"{name}.PropertyType");
        var idOffset = reader.Position;
        var id = reader.ReadUInt16($"{name}.PropertyId");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddField(children, "PropertyType", typeOffset, 2, PropertyTypeName(embeddedType), budget, depth);
        AddField(children, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(id), budget, depth);
        var valueType = valueTypeOverride ?? embeddedType;
        children.Add(ParseValue(
            ref reader,
            valueType,
            "PropertyValue",
            budget,
            depth + 1,
            includePresence: false,
            codePage,
            addressBookSemantics: false,
            warnings));
        return new MapiNode(
            name,
            MapiNodeKind.Property,
            start,
            reader.Position - start,
            $"0x{id:X4}:{embeddedType:X4}",
            children.ToImmutable());
    }

    public static string PropertyTypeName(ushort type) => type switch
    {
        0x0000 => "0x0000 PtypUnspecified",
        0x0001 => "0x0001 PtypNull",
        0x0002 => "0x0002 PtypInteger16",
        0x0003 => "0x0003 PtypInteger32",
        0x0004 => "0x0004 PtypFloating32",
        0x0005 => "0x0005 PtypFloating64",
        0x0006 => "0x0006 PtypCurrency",
        0x0007 => "0x0007 PtypFloatingTime",
        0x000A => "0x000A PtypErrorCode",
        0x000B => "0x000B PtypBoolean",
        0x000D => "0x000D PtypObject/PtypEmbeddedTable",
        0x0014 => "0x0014 PtypInteger64",
        0x001E => "0x001E PtypString8",
        0x001F => "0x001F PtypString",
        0x0040 => "0x0040 PtypTime",
        0x0048 => "0x0048 PtypGuid",
        0x00FB => "0x00FB PtypServerId",
        0x00FD => "0x00FD PtypRestriction",
        0x00FE => "0x00FE PtypRuleAction",
        0x0102 => "0x0102 PtypBinary",
        _ when ((type & MultiValue) != 0) =>
            $"0x{type:X4} Multiple<{PropertyTypeName((ushort)(type & ~MultiValue))}>",
        _ => $"0x{type:X4} Unknown"
    };

    private static bool HasPresenceIndicator(ushort type) =>
        type is 0x001E or 0x001F or 0x0102 || (type & MultiValue) != 0;

    private static bool TryGetString8Encoding(uint? codePage, out Encoding encoding)
    {
        encoding = null!;
        if (!codePage.HasValue || codePage is 0 or > int.MaxValue)
        {
            return false;
        }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(
                (int)codePage.Value,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static string BoundedHex(ReadOnlySpan<byte> bytes)
    {
        var shown = bytes[..Math.Min(bytes.Length, MapiParseLimits.MaxRawNodeBytes)];
        var value = Convert.ToHexString(shown);
        return shown.Length == bytes.Length
            ? value
            : value + $" ... [{bytes.Length - shown.Length:N0} more bytes]";
    }

    private static void AddField(
        ImmutableArray<MapiNode>.Builder target,
        string name,
        long offset,
        long length,
        string value,
        MapiNodeBudget budget,
        int depth)
    {
        budget.Claim(depth);
        target.Add(MapiNode.Leaf(name, MapiNodeKind.Field, offset, length, value));
    }
}
