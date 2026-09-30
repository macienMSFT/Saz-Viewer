using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

/// <summary>
/// [MS-OXORULE] 2.2.5 RuleAction Structure and 2.2.5.1 ActionBlock Structure, cross-checked against
/// the pinned MIT upstream MAPIInspector MSOXORULE/structs/RuleAction.cs and ActionBlock.cs parsers.
/// <see cref="MapiWireWidthContext.RopBuffer"/> (MS-OXCROPS "standard rule") uses a 16-bit
/// <c>NoOfActions</c> and per-action 16-bit <c>ActionLength</c>, plus distinct OP_MOVE/OP_COPY
/// (2.2.5.1.2.1), OP_REPLY/OP_OOF_REPLY (2.2.5.1.2.2), and OP_FORWARD/OP_DELEGATE (2.2.5.1.2.4)
/// counted-field widths - not merely re-widened fields for the first two, which also have distinct
/// action-data shapes. <see cref="MapiWireWidthContext.Extended"/> (the default: NSPI/MAPI-HTTP/
/// address-book "extended rule") retains the pre-existing 32-bit widths and action-data shapes
/// throughout, consistent with how this codebase resolves every other counted/sized field in this
/// family against current MS-OXORULE rather than the pinned upstream parser's narrower (always
/// 16-bit RecipientCount/NoOfProperties) reads for OP_FORWARD/OP_DELEGATE. OP_BOUNCE (2.2.5.1.2.5)
/// is the only action type genuinely identical in both conventions.
/// </summary>
internal static class RuleActionParser
{
    public static MapiNode Parse(
        ref MapiReader reader,
        MapiNodeBudget budget,
        int depth,
        uint? codePage,
        List<string>? warnings,
        MapiWireWidthContext context = MapiWireWidthContext.Extended)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var countOffset = reader.Position;
        int count;
        int countLength;
        if (context == MapiWireWidthContext.RopBuffer)
        {
            count = reader.ReadCount16("NoOfActions");
            countLength = 2;
        }
        else
        {
            count = reader.ReadCount32("NoOfActions");
            countLength = 4;
        }
        if (count == 0)
        {
            throw new MapiParseException(countOffset, "NoOfActions must be greater than zero.");
        }
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(
            children,
            "NoOfActions",
            countOffset,
            countLength,
            count.ToString(CultureInfo.InvariantCulture),
            budget);
        for (var index = 0; index < count; index++)
        {
            children.Add(ParseAction(ref reader, index, budget, depth + 1, codePage, warnings, context));
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
        List<string>? warnings,
        MapiWireWidthContext context)
    {
        budget.Claim(depth);
        var start = reader.Position;
        int actionLength;
        int actionLengthFieldSize;
        if (context == MapiWireWidthContext.RopBuffer)
        {
            actionLength = NspiPropertyParser.ReadLength16(ref reader, $"ActionBlock[{index}].ActionLength");
            actionLengthFieldSize = 2;
        }
        else
        {
            actionLength = NspiPropertyParser.ReadLength32(ref reader, $"ActionBlock[{index}].ActionLength");
            actionLengthFieldSize = 4;
        }
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
            actionLengthFieldSize,
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
            ParseActionData(ref actionData, type, actionDataChildren, budget, depth + 1, codePage, warnings, context);
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
            actionLengthFieldSize + actionLength,
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
        List<string>? warnings,
        MapiWireWidthContext context)
    {
        switch (type)
        {
            case 0x01:
            case 0x02:
                if (context == MapiWireWidthContext.RopBuffer)
                {
                    ParseMoveOrCopyStandard(ref reader, nodes, budget, warnings);
                }
                else
                {
                    ParseMoveOrCopyExtended(ref reader, nodes, budget, warnings);
                }
                break;
            case 0x03:
            case 0x04:
                if (context == MapiWireWidthContext.RopBuffer)
                {
                    ParseReplyStandard(ref reader, nodes, budget);
                }
                else
                {
                    ParseReplyExtended(ref reader, nodes, budget, warnings);
                }
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
                ParseRecipients(ref reader, nodes, budget, depth, codePage, warnings, context);
                break;
            case 0x09:
                nodes.Add(NspiPropertyParser.ParseStandardTaggedValue(
                    ref reader,
                    "TaggedPropertyValue",
                    budget,
                    depth,
                    codePage: codePage,
                    warnings: warnings,
                    context: context));
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

    // [MS-OXORULE] 2.2.5.1.2.1 "OP_MOVE and OP_COPY ActionData Structure for Extended Rules":
    // StoreEIDSize/FolderEIDSize are 32-bit, StoreEID is opaque, and FolderEID is always a
    // Folder EntryID ([MS-OXCDATA] 2.2.4.1).
    private static void ParseMoveOrCopyExtended(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        ParseSizedBytes(ref reader, nodes, "StoreEID", EntryIdKind.Opaque, budget, warnings);
        ParseSizedBytes(ref reader, nodes, "FolderEID", EntryIdKind.Folder, budget, warnings);
    }

    // [MS-OXORULE] 2.2.5.1.2.1 "... for Standard Rules": adds a FolderInThisStore boolean, uses
    // 16-bit StoreEIDSize/FolderEIDSize, and FolderEID is either a fixed 2.2.5.1.2.1.1 ServerEid
    // structure (same-store destination) or an opaque Folder EntryID (different-store destination).
    private static void ParseMoveOrCopyStandard(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        var folderInThisStoreOffset = reader.Position;
        var folderInThisStore = reader.ReadByte("FolderInThisStore") != 0;
        ExtendedBufferParser.AddField(nodes, "FolderInThisStore", folderInThisStoreOffset, 1, folderInThisStore ? "true" : "false", budget);

        var storeSizeOffset = reader.Position;
        var storeSize = NspiPropertyParser.ReadLength16(ref reader, "StoreEIDSize");
        ExtendedBufferParser.AddField(nodes, "StoreEIDSize", storeSizeOffset, 2, storeSize.ToString(CultureInfo.InvariantCulture), budget);
        var storeOffset = reader.Position;
        var storeEid = reader.ReadBytes(storeSize, "StoreEID");
        AddOpaqueField(nodes, "StoreEID", storeEid, storeOffset, budget);

        var folderSizeOffset = reader.Position;
        var folderSize = NspiPropertyParser.ReadLength16(ref reader, "FolderEIDSize");
        ExtendedBufferParser.AddField(nodes, "FolderEIDSize", folderSizeOffset, 2, folderSize.ToString(CultureInfo.InvariantCulture), budget);

        var folderOffset = reader.Position;
        var folderBytes = reader.ReadBytes(folderSize, "FolderEID");
        if (folderInThisStore)
        {
            try
            {
                nodes.Add(ParseServerEid(folderBytes, folderOffset, budget, warnings));
            }
            catch (MapiParseException ex)
            {
                nodes.Add(ExtendedBufferParser.RawNode("FolderEID", folderBytes, folderOffset, budget));
                warnings?.Add($"FolderEID (ServerEid) semantic fields are retained as {folderBytes.Length:N0} raw bytes: {ex.Message}");
            }
        }
        else
        {
            try
            {
                nodes.Add(MapiEntryIdParser.ParseFolderEntryId(folderBytes, folderOffset, budget, warnings));
            }
            catch (MapiParseException ex)
            {
                nodes.Add(ExtendedBufferParser.RawNode("FolderEID", folderBytes, folderOffset, budget));
                warnings?.Add($"FolderEID semantic fields are retained as {folderBytes.Length:N0} raw bytes: {ex.Message}");
            }
        }
    }

    private const int ServerEidSize = 21;

    // [MS-OXORULE] 2.2.5.1.2.1.1 ServerEid Structure: Ours(1) + FolderId(8, [MS-OXCDATA] 2.2.1.1) +
    // MessageId(8, reserved) + Instance(4, reserved).
    private static MapiNode ParseServerEid(ReadOnlySpan<byte> bytes, long offset, MapiNodeBudget budget, List<string>? warnings)
    {
        if (bytes.Length != ServerEidSize)
        {
            throw new MapiParseException(checked((int)offset), $"ServerEid is {bytes.Length:N0} bytes; {ServerEidSize} are required.");
        }
        var reader = new MapiReader(bytes, baseOffset: checked((int)offset));
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var oursOffset = reader.Position;
        var ours = reader.ReadByte("Ours");
        ExtendedBufferParser.AddField(children, "Ours", oursOffset, 1, ours == 0 ? "false" : "true", budget);
        if (ours == 0)
        {
            warnings?.Add("ServerEid.Ours is expected to be non-zero (0x01).");
        }
        children.Add(ParseFixedId(ref reader, "FolderId", budget));
        var messageIdOffset = reader.Position;
        var messageId = reader.ReadUInt64("MessageId");
        ExtendedBufferParser.AddField(children, "MessageId", messageIdOffset, 8, $"0x{messageId:X16}", budget);
        var instanceOffset = reader.Position;
        var instance = reader.ReadInt32("Instance");
        ExtendedBufferParser.AddField(children, "Instance", instanceOffset, 4, instance.ToString(CultureInfo.InvariantCulture), budget);
        return new MapiNode("FolderEID (ServerEid)", MapiNodeKind.Structure, offset, bytes.Length, null, children.ToImmutable());
    }

    // [MS-OXORULE] 2.2.5.1.2.2 "... for Extended Rules": a length-prefixed Message EntryID
    // ([MS-OXCDATA] 2.2.4.2) followed by a client-generated GUID.
    private static void ParseReplyExtended(
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

    // [MS-OXORULE] 2.2.5.1.2.2 "... for Standard Rules": a fixed Folder ID ([MS-OXCDATA] 2.2.1.1)
    // and Message ID ([MS-OXCDATA] 2.2.1.2) pair identifying the reply template, plus the GUID -
    // no length-prefixed EntryID at all.
    private static void ParseReplyStandard(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget)
    {
        nodes.Add(ParseFixedId(ref reader, "ReplyTemplateFID", budget));
        nodes.Add(ParseFixedId(ref reader, "ReplyTemplateMID", budget));
        var guidOffset = reader.Position;
        var guid = reader.ReadGuid("ReplyTemplateGUID");
        ExtendedBufferParser.AddField(nodes, "ReplyTemplateGUID", guidOffset, 16, guid.ToString(), budget);
    }

    // [MS-OXCDATA] 2.2.1.1/2.2.1.2: FolderID and MessageID share an identical 8-byte
    // ReplicaId(2)+GlobalCounter(6) layout.
    private static MapiNode ParseFixedId(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var replicaOffset = reader.Position;
        var replicaId = reader.ReadUInt16("ReplicaId");
        ExtendedBufferParser.AddField(children, "ReplicaId", replicaOffset, 2, $"0x{replicaId:X4}", budget);
        var counterOffset = reader.Position;
        var counter = reader.ReadBytes(6, "GlobalCounter");
        children.Add(ExtendedBufferParser.RawNode("GlobalCounter", counter, counterOffset, budget));
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, children.ToImmutable());
    }

    // [MS-OXORULE] 2.2.5.1.2.4 OP_FORWARD and OP_DELEGATE ActionData Structure / 2.2.5.1.2.4.1
    // RecipientBlockData Structure: RecipientCount and each recipient's NoOfProperties use the same
    // context-dependent width split as every other counted field in this structure family - 16-bit
    // for MS-OXCROPS ROP buffers (standard rules), 32-bit for NSPI/MAPI-HTTP/address-book (extended
    // rules) - consistent with how this codebase already resolves AndRestriction/OrRestriction's
    // RestrictCount and PtypBinary's byte-count prefix against current MS-OXORULE/MS-OXCDATA rather
    // than the pinned upstream parser's narrower (always-16-bit) reads for this one structure. Each
    // recipient's nested TaggedPropertyValue uses the same ambient context, so an embedded
    // PtypBinary property's byte count matches the surrounding rule's convention.
    private static void ParseRecipients(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder nodes,
        MapiNodeBudget budget,
        int depth,
        uint? codePage,
        List<string>? warnings,
        MapiWireWidthContext context)
    {
        var start = reader.Position;
        int count;
        int countLength;
        if (context == MapiWireWidthContext.RopBuffer)
        {
            count = reader.ReadCount16("RecipientCount");
            countLength = 2;
        }
        else
        {
            count = reader.ReadCount32("RecipientCount");
            countLength = 4;
        }
        if (count == 0)
        {
            throw new MapiParseException(start, "RecipientCount must be greater than zero.");
        }
        var recipients = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(
            recipients,
            "RecipientCount",
            start,
            countLength,
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
            int propertyCount;
            int propertyCountLength;
            if (context == MapiWireWidthContext.RopBuffer)
            {
                propertyCount = reader.ReadCount16("Recipient.NoOfProperties");
                propertyCountLength = 2;
            }
            else
            {
                propertyCount = reader.ReadCount32("Recipient.NoOfProperties");
                propertyCountLength = 4;
            }
            if (propertyCount == 0)
            {
                throw new MapiParseException(propertyCountOffset, "Recipient.NoOfProperties must be greater than zero.");
            }
            ExtendedBufferParser.AddField(
                fields,
                "NoOfProperties",
                propertyCountOffset,
                propertyCountLength,
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
                    warnings: warnings,
                    context: context));
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
