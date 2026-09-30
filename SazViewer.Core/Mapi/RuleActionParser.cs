using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

internal static class RuleActionParser
{
    public static MapiNode Parse(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth,
        uint? codePage,
        List<string>? warnings)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var countOffset = reader.Position;
        var count = reader.ReadCount32("NoOfActions");
        if (count == 0)
        {
            throw new MapiParseException(countOffset, "NoOfActions must be greater than zero.");
        }
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(
            children,
            "NoOfActions",
            countOffset,
            4,
            count.ToString(CultureInfo.InvariantCulture),
            budget);
        for (var index = 0; index < count; index++)
        {
            children.Add(ParseAction(ref reader, index, budget, depth + 1, codePage, warnings));
        }
        return new MapiNode(
            "RuleAction",
            MapiNodeKind.Array,
            start,
            reader.Position - start,
            $"{count:N0} action(s)",
            children.ToImmutable());
    }

    private static MapiNode ParseAction(
        ref MapiReader reader,
        int index,
        MapiNodeBudget budget,
        int depth,
        uint? codePage,
        List<string>? warnings)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var actionLength = NspiPropertyParser.ReadLength32(ref reader, $"ActionBlock[{index}].ActionLength");
        if (actionLength < 9)
        {
            throw new MapiParseException(start, $"ActionBlock[{index}].ActionLength {actionLength} is smaller than the 9-byte action header.");
        }
        var action = reader.SliceReader(actionLength, $"ActionBlock[{index}]");
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(
            children,
            "ActionLength",
            start,
            4,
            actionLength.ToString(CultureInfo.InvariantCulture),
            budget);
        var typeOffset = action.Position;
        var type = action.ReadByte("ActionType");
        ExtendedBufferParser.AddField(children, "ActionType", typeOffset, 1, ActionTypeName(type), budget);
        var flavorOffset = action.Position;
        var flavor = action.ReadUInt32("ActionFlavor");
        ExtendedBufferParser.AddField(children, "ActionFlavor", flavorOffset, 4, FormatFlavor(type, flavor), budget);
        ValidateFlavor(type, flavor, index, warnings);
        var flagsOffset = action.Position;
        var flags = action.ReadUInt32("ActionFlags");
        ExtendedBufferParser.AddField(children, "ActionFlags", flagsOffset, 4, $"0x{flags:X8}", budget);

        var actionData = action;
        var actionDataChildren = ImmutableArray.CreateBuilder<MapiNode>();
        try
        {
            ParseActionData(ref actionData, type, actionDataChildren, budget, depth + 1, codePage, warnings);
            action = actionData;
            children.AddRange(actionDataChildren);
        }
        catch (MapiParseException ex)
        {
            var rawOffset = action.Position;
            var raw = action.ReadRemaining("malformed action data");
            children.Add(ExtendedBufferParser.RawNode("Malformed action data", raw, rawOffset, budget));
            warnings?.Add($"ActionBlock[{index}] {ActionTypeName(type)} is malformed: {ex.Message}");
        }
        if (!action.End)
        {
            var rawOffset = action.Position;
            var raw = action.ReadRemaining("trailing action data");
            children.Add(ExtendedBufferParser.RawNode("Trailing action data", raw, rawOffset, budget));
            warnings?.Add($"ActionBlock[{index}] {ActionTypeName(type)} left {raw.Length:N0} unparsed bytes.");
        }
        return new MapiNode(
            $"ActionBlock[{index}]",
            MapiNodeKind.Structure,
            start,
            4L + actionLength,
            ActionTypeName(type),
            children.ToImmutable());
    }

    private static void ParseActionData(
        ref MapiReader reader,
        byte type,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget,
        int depth,
        uint? codePage,
        List<string>? warnings)
    {
        switch (type)
        {
            case 0x01:
            case 0x02:
                ParseMoveOrCopy(ref reader, nodes, budget, warnings);
                break;
            case 0x03:
            case 0x04:
                ParseReply(ref reader, nodes, budget, warnings);
                break;
            case 0x05:
                AddOpaqueField(ref reader, nodes, "DeferActionData", budget);
                break;
            case 0x06:
            {
                var offset = reader.Position;
                var code = reader.ReadUInt32("BounceCode");
                var name = code switch
                {
                    0x0000000D => "RejectedMessageTooLarge",
                    0x0000001F => "RejectedMessageNotDisplayed",
                    0x00000026 => "DeliveryMessageDenied",
                    _ => "Unknown"
                };
                ExtendedBufferParser.AddField(nodes, "BounceCode", offset, 4, $"0x{code:X8} {name}", budget);
                if (name == "Unknown")
                {
                    warnings?.Add($"OP_BOUNCE has invalid BounceCode 0x{code:X8}.");
                }
                break;
            }
            case 0x07:
            case 0x08:
                ParseRecipients(ref reader, nodes, budget, depth, codePage, warnings);
                break;
            case 0x09:
                nodes.Add(NspiPropertyParser.ParseStandardTaggedValue(
                    ref reader,
                    "TaggedPropertyValue",
                    budget,
                    depth,
                    codePage: codePage,
                    warnings: warnings));
                break;
            case 0x0A:
            case 0x0B:
                break;
            default:
            {
                var rawOffset = reader.Position;
                var raw = reader.ReadRemaining("unknown action data");
                nodes.Add(ExtendedBufferParser.RawNode("Unknown action data", raw, rawOffset, budget));
                warnings?.Add($"Unknown rule action type 0x{type:X2}; {raw.Length:N0} action bytes were retained raw.");
                break;
            }
        }
    }

    private static void ParseMoveOrCopy(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        ParseSizedBytes(ref reader, nodes, "StoreEID", EntryIdKind.Opaque, budget, warnings);
        ParseSizedBytes(ref reader, nodes, "FolderEID", EntryIdKind.Folder, budget, warnings);
    }

    private static void ParseReply(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        ParseSizedBytes(ref reader, nodes, "ReplyTemplateMessageEID", EntryIdKind.Message, budget, warnings);
        var guidOffset = reader.Position;
        var guid = reader.ReadGuid("ReplyTemplateGUID");
        ExtendedBufferParser.AddField(nodes, "ReplyTemplateGUID", guidOffset, 16, guid.ToString(), budget);
    }

    private static void ParseRecipients(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget,
        int depth,
        uint? codePage,
        List<string>? warnings)
    {
        var start = reader.Position;
        var count = reader.ReadCount32("RecipientCount");
        if (count == 0)
        {
            throw new MapiParseException(start, "RecipientCount must be greater than zero.");
        }
        var recipients = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(
            recipients,
            "RecipientCount",
            start,
            4,
            count.ToString(CultureInfo.InvariantCulture),
            budget);
        for (var recipientIndex = 0; recipientIndex < count; recipientIndex++)
        {
            budget.Claim(depth);
            var recipientStart = reader.Position;
            var fields = ImmutableArray.CreateBuilder<MapiNode>();
            var reservedOffset = reader.Position;
            var reserved = reader.ReadByte("Recipient.Reserved");
            ExtendedBufferParser.AddField(fields, "Reserved", reservedOffset, 1, $"0x{reserved:X2}", budget);
            var propertyCountOffset = reader.Position;
            var propertyCount = reader.ReadCount32("Recipient.NoOfProperties");
            if (propertyCount == 0)
            {
                throw new MapiParseException(propertyCountOffset, "Recipient.NoOfProperties must be greater than zero.");
            }
            ExtendedBufferParser.AddField(
                fields,
                "NoOfProperties",
                propertyCountOffset,
                4,
                propertyCount.ToString(CultureInfo.InvariantCulture),
                budget);
            for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                fields.Add(NspiPropertyParser.ParseStandardTaggedValue(
                    ref reader,
                    $"PropertyValue[{propertyIndex}]",
                    budget,
                    depth + 1,
                    codePage: codePage,
                    warnings: warnings));
            }
            recipients.Add(new MapiNode(
                $"RecipientBlock[{recipientIndex}]",
                MapiNodeKind.Structure,
                recipientStart,
                reader.Position - recipientStart,
                null,
                fields.ToImmutable()));
        }
        budget.Claim(depth);
        nodes.Add(new MapiNode(
            "Recipients",
            MapiNodeKind.Array,
            start,
            reader.Position - start,
            null,
            recipients.ToImmutable()));
    }

    private static void ParseSizedBytes(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        EntryIdKind kind,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        var sizeOffset = reader.Position;
        var size = NspiPropertyParser.ReadLength32(ref reader, $"{name}Size");
        ExtendedBufferParser.AddField(
            nodes,
            $"{name}Size",
            sizeOffset,
            4,
            size.ToString(CultureInfo.InvariantCulture),
            budget);
        var valueOffset = reader.Position;
        var value = reader.ReadBytes(size, name);
        if (kind == EntryIdKind.Opaque)
        {
            AddOpaqueField(nodes, name, value, valueOffset, budget);
        }
        else
        {
            try
            {
                nodes.Add(kind == EntryIdKind.Folder
                    ? MapiEntryIdParser.ParseFolderEntryId(value, valueOffset, budget, warnings)
                    : MapiEntryIdParser.ParseMessageEntryId(value, valueOffset, budget, warnings));
            }
            catch (MapiParseException ex)
            {
                nodes.Add(ExtendedBufferParser.RawNode(name, value, valueOffset, budget));
                warnings?.Add($"{name} semantic fields are retained as {value.Length:N0} raw bytes: {ex.Message}");
            }
        }
    }

    private enum EntryIdKind
    {
        Opaque,
        Folder,
        Message
    }

    private static void AddOpaqueField(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadRemaining(name);
        AddOpaqueField(nodes, name, value, offset, budget);
    }

    private static void AddOpaqueField(
        ImmutableArray<MapiNode>.Builder nodes,
        string name,
        ReadOnlySpan<byte> value,
        long offset,
        MapiNodeBudget budget)
    {
        budget.Claim(0);
        var shown = value[..Math.Min(value.Length, MapiParseLimits.MaxRawNodeBytes)];
        var text = Convert.ToHexString(shown);
        if (shown.Length != value.Length)
        {
            text += $" ... [{value.Length - shown.Length:N0} more bytes]";
        }
        nodes.Add(MapiNode.Leaf(name, MapiNodeKind.Field, offset, value.Length, text));
    }

    private static string ActionTypeName(byte type) => type switch
    {
        0x01 => "OP_MOVE",
        0x02 => "OP_COPY",
        0x03 => "OP_REPLY",
        0x04 => "OP_OOF_REPLY",
        0x05 => "OP_DEFER_ACTION",
        0x06 => "OP_BOUNCE",
        0x07 => "OP_FORWARD",
        0x08 => "OP_DELEGATE",
        0x09 => "OP_TAG",
        0x0A => "OP_DELETE",
        0x0B => "OP_MARK_AS_READ",
        _ => $"Unknown 0x{type:X2}"
    };

    private static string FormatFlavor(byte type, uint flavor)
    {
        if (type is 0x03 or 0x04)
        {
            return $"0x{flavor:X8} (ST={((flavor & 0x02) != 0 ? 1 : 0)}, NS={((flavor & 0x01) != 0 ? 1 : 0)})";
        }
        if (type == 0x07)
        {
            return $"0x{flavor:X8} (TM={((flavor & 0x08) != 0 ? 1 : 0)}, AT={((flavor & 0x04) != 0 ? 1 : 0)}, NC={((flavor & 0x02) != 0 ? 1 : 0)}, PR={((flavor & 0x01) != 0 ? 1 : 0)})";
        }
        return $"0x{flavor:X8}";
    }

    private static void ValidateFlavor(byte type, uint flavor, int index, List<string>? warnings)
    {
        var valid = type switch
        {
            0x03 or 0x04 => flavor is 0 or 0x01 or 0x02,
            0x07 => flavor is 0 or 0x01 or 0x02 or 0x03 or 0x04 or 0x08,
            _ => flavor == 0
        };
        if (!valid)
        {
            warnings?.Add(
                $"ActionBlock[{index}] {ActionTypeName(type)} has invalid ActionFlavor 0x{flavor:X8}.");
        }
    }
}
