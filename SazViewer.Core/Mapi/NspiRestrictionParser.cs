using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

internal static class NspiRestrictionParser
{
    public static MapiNode Parse(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth = 0,
        uint? codePage = null,
        List<string>? warnings = null,
        MapiWireWidthContext context = MapiWireWidthContext.Extended)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var type = reader.ReadByte("RestrictionType");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddField(children, "RestrictionType", start, 1, TypeName(type), budget, depth);
        switch (type)
        {
            case 0x00:
            case 0x01:
            {
                // [MS-OXCDATA] 2.11.3/2.11.4: AndRestriction/OrRestriction's RestrictCount is 16-bit
                // in an MS-OXCROPS ROP buffer, but 32-bit in NSPI ([MS-OXNSPI]) and MS-OXORULE
                // extended-rule buffers - the one field in this structure whose width is not
                // identical between the two wire conventions.
                var countOffset = reader.Position;
                int count;
                int countLength;
                if (context == MapiWireWidthContext.RopBuffer)
                {
                    count = reader.ReadCount16("RestrictionCount");
                    countLength = 2;
                }
                else
                {
                    count = reader.ReadCount32("RestrictionCount");
                    countLength = 4;
                }
                AddField(children, "RestrictionCount", countOffset, countLength, count.ToString(CultureInfo.InvariantCulture), budget, depth);
                for (var index = 0; index < count; index++)
                {
                    children.Add(Parse(ref reader, budget, depth + 1, codePage, warnings, context) with { Name = $"Restriction[{index}]" });
                }
                break;
            }
            case 0x02:
                children.Add(Parse(ref reader, budget, depth + 1, codePage, warnings, context) with { Name = "Restriction" });
                break;
            case 0x03:
                ReadUInt16(ref reader, children, "FuzzyLevelLow", budget, depth, true);
                ReadUInt16(ref reader, children, "FuzzyLevelHigh", budget, depth, true);
                var contentTag = ReadPropertyTag(ref reader, children, "PropertyTag", budget, depth);
                children.Add(NspiPropertyParser.ParseStandardTaggedValue(
                    ref reader,
                    "TaggedValue",
                    budget,
                    depth + 1,
                    RestrictionValueType(contentTag),
                    codePage,
                    warnings,
                    context));
                break;
            case 0x04:
                ReadByte(ref reader, children, "RelOp", budget, depth, true);
                var propertyTag = ReadPropertyTag(ref reader, children, "PropertyTag", budget, depth);
                children.Add(NspiPropertyParser.ParseStandardTaggedValue(
                    ref reader,
                    "TaggedValue",
                    budget,
                    depth + 1,
                    RestrictionValueType(propertyTag),
                    codePage,
                    warnings,
                    context));
                break;
            case 0x05:
                ReadByte(ref reader, children, "RelOp", budget, depth, true);
                ReadPropertyTag(ref reader, children, "PropertyTag1", budget, depth);
                ReadPropertyTag(ref reader, children, "PropertyTag2", budget, depth);
                break;
            case 0x06:
                ReadByte(ref reader, children, "BitmapRelOp", budget, depth, true);
                ReadPropertyTag(ref reader, children, "PropertyTag", budget, depth);
                ReadUInt32(ref reader, children, "Mask", budget, depth, true);
                break;
            case 0x07:
                ReadByte(ref reader, children, "RelOp", budget, depth, true);
                ReadPropertyTag(ref reader, children, "PropertyTag", budget, depth);
                ReadUInt32(ref reader, children, "Size", budget, depth, false);
                break;
            case 0x08:
                ReadPropertyTag(ref reader, children, "PropertyTag", budget, depth);
                break;
            case 0x09:
                ReadPropertyTag(ref reader, children, "Subobject", budget, depth);
                children.Add(Parse(ref reader, budget, depth + 1, codePage, warnings, context) with { Name = "Restriction" });
                break;
            case 0x0A:
            {
                var countOffset = reader.Position;
                var count = reader.ReadByte("TaggedValuesCount");
                AddField(children, "TaggedValuesCount", countOffset, 1, count.ToString(CultureInfo.InvariantCulture), budget, depth);
                for (var index = 0; index < count; index++)
                {
                    children.Add(NspiPropertyParser.ParseStandardTaggedValue(
                        ref reader,
                        $"TaggedValue[{index}]",
                        budget,
                        depth + 1,
                        codePage: codePage,
                        warnings: warnings,
                        context: context));
                }
                var presentOffset = reader.Position;
                var present = reader.ReadByte("RestrictionPresent");
                AddField(children, "RestrictionPresent", presentOffset, 1, present == 0 ? "false" : "true", budget, depth);
                if (present != 0)
                {
                    children.Add(Parse(ref reader, budget, depth + 1, codePage, warnings, context) with { Name = "Restriction" });
                }
                break;
            }
            case 0x0B:
                ReadUInt32(ref reader, children, "Count", budget, depth, false);
                children.Add(Parse(ref reader, budget, depth + 1, codePage, warnings, context) with { Name = "SubRestriction" });
                break;
            default:
                throw new MapiParseException(start, $"Unknown restriction type 0x{type:X2}; its extent cannot be determined safely.");
        }

        return new MapiNode(
            TypeName(type),
            MapiNodeKind.Structure,
            start,
            reader.Position - start,
            null,
            children.ToImmutable());
    }

    private static ushort RestrictionValueType(uint propertyTag) =>
        (ushort)((ushort)propertyTag & ~0x1000);

    private static uint ReadPropertyTag(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget,
        int depth)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        AddField(
            nodes,
            name,
            offset,
            4,
            $"0x{value:X8} (id {MapiPropertyNames.FormatPidTag((ushort)(value >> 16))}, {NspiPropertyParser.PropertyTypeName((ushort)value)})",
            budget,
            depth);
        return value;
    }

    private static void ReadUInt32(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget,
        int depth,
        bool hex)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        AddField(nodes, name, offset, 4, hex ? $"0x{value:X8}" : value.ToString(CultureInfo.InvariantCulture), budget, depth);
    }

    private static void ReadUInt16(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget,
        int depth,
        bool hex)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        AddField(nodes, name, offset, 2, hex ? $"0x{value:X4}" : value.ToString(CultureInfo.InvariantCulture), budget, depth);
    }

    private static void ReadByte(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget,
        int depth,
        bool hex)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        AddField(nodes, name, offset, 1, hex ? $"0x{value:X2}" : value.ToString(CultureInfo.InvariantCulture), budget, depth);
    }

    private static void AddField(
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        long offset,
        long length,
        string value,
        MapiNodeBudget budget,
        int depth)
    {
        budget.Claim(depth);
        nodes.Add(MapiNode.Leaf(name, MapiNodeKind.Field, offset, length, value));
    }

    private static string TypeName(byte type) => type switch
    {
        0x00 => "AndRestriction",
        0x01 => "OrRestriction",
        0x02 => "NotRestriction",
        0x03 => "ContentRestriction",
        0x04 => "PropertyRestriction",
        0x05 => "ComparePropertiesRestriction",
        0x06 => "BitMaskRestriction",
        0x07 => "SizeRestriction",
        0x08 => "ExistRestriction",
        0x09 => "SubObjectRestriction",
        0x0A => "CommentRestriction",
        0x0B => "CountRestriction",
        _ => $"UnknownRestriction 0x{type:X2}"
    };
}
