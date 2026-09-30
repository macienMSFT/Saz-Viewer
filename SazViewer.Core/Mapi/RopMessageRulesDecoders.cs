using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

/// <summary>
/// Self-contained, variable-width semantic decoders for exactly four MS-OXCROPS families:
/// MS-OXCMSG (messages/recipients/attachments/read state), MS-OXORULE (rules), MS-OXCPERM
/// (folder permissions) and MS-OXCNOTIF (notifications).
///
/// This module mirrors the shape of <see cref="RopVariableDispatcher"/> (same
/// <c>Supports</c>/<c>Parse</c> signatures) and is wired directly into
/// <see cref="RopVariableDispatcher"/>'s dispatch (see <c>RopVariableDispatcher.Parse</c>), which
/// consults it after <see cref="RopSemanticParser"/>'s fixed schema catalog declines a RopId. It is
/// also exercised directly by <c>RopMessageRulesDecodersTests</c>.
///
/// Only RopIds whose exact byte width is fully determined by bytes already read within the same
/// operation (never earlier operations, other ROPs in the same list, or external session/logon
/// state) are implemented, with one deliberate exception: RopSetMessageReadFlag's request (0x11)
/// needs its originating logon's LogonFlags.Private bit, which the optional trailing
/// <see cref="MapiCaptureContext"/> parameter on <see cref="Parse"/> supplies (recorded by
/// <see cref="RopPropertyStoreDecoders"/>'s RopLogon request case, transactionally, in the same
/// capture). When that state is unavailable or ambiguous, the case throws
/// <see cref="MapiParseException"/>, which <c>RopSemanticParser</c> turns into a raw+warning
/// fallback rather than guessing. Every RopId already covered by <see cref="RopSemanticParser"/>'s
/// fixed schemas is intentionally excluded here (duplicating it would be dead code, since the
/// semantic parser only ever consults this module for a RopId it does not already have a fixed
/// schema for).
///
/// Sources: pinned upstream MAPIInspector ROP parsers
/// (https://github.com/OfficeDev/Office-Inspectors-for-Fiddler @ c18dd66c99f3b5a96c2e1d31698c5cf2deb828e7,
/// MIT licensed) cross-checked against [MS-OXCROPS], [MS-OXCDATA], [MS-OXCMSG], [MS-OXORULE],
/// [MS-OXCPERM] and [MS-OXCNOTIF].
///
/// Explicitly UNSUPPORTED (named on purpose, never guessed):
///  - RopNotify response (0x2A) "TableRowData" payload content when no unambiguous successful prior
///    RopSetColumns can be correlated to NotificationHandle. Its size-prefixed boundary remains raw.
///  - ReadRecipientRow (used by RopReadRecipients response, 0x0F): unlike RopModifyRecipients and
///    RopOpenMessage/RopReloadCachedInformation/RopOpenEmbeddedMessage, this response carries no
///    RecipientColumns array of its own, so there is no deterministic column list to decode each
///    RecipientRow against; the row bytes are retained as bounded raw (matching upstream's own
///    incomplete/raw treatment of this exact structure).
///  - Generic EntryID/ServerEid payloads inside RopUpdateDeferredActionMessages remain bounded raw:
///    their outer size-prefixed boundary is exact, but their client-defined internal shape is not.
/// </summary>
internal static class RopMessageRulesDecoders
{
    // Bits of RecipientFlags byte 0 ([MS-OXCDATA] 2.8.3.1).
    private const byte RecipientFlagR = 0x80;
    private const byte RecipientFlagS = 0x40;
    private const byte RecipientFlagT = 0x20;
    private const byte RecipientFlagD = 0x10;
    private const byte RecipientFlagE = 0x08;
    private const byte RecipientAddressTypeMask = 0x07;

    // Bits of RecipientFlags byte 1.
    private const byte RecipientFlagO = 0x80;
    private const byte RecipientFlagI = 0x04;
    private const byte RecipientFlagU = 0x02;
    private const byte RecipientFlagN = 0x01;

    // NotificationFlags bits ([MS-OXCNOTIF] 2.2.1.4.1 / upstream NotificationFlags).
    private const ushort NotificationTypeMask = 0x0FFF;
    private const ushort NfNewMail = 0x0002;
    private const ushort NfObjectCreated = 0x0004;
    private const ushort NfObjectDeleted = 0x0008;
    private const ushort NfObjectModified = 0x0010;
    private const ushort NfObjectMoved = 0x0020;
    private const ushort NfObjectCopied = 0x0040;
    private const ushort NfTableModified = 0x0100;
    private const ushort NfExtended = 0x0400;
    private const ushort NfT = 0x1000;
    private const ushort NfU = 0x2000;
    private const ushort NfS = 0x4000;
    private const ushort NfM = 0x8000;

    public static bool Supports(MapiDirection direction, byte ropId) => direction switch
    {
        MapiDirection.Request => ropId is
            0x03 or // RopOpenMessage
            0x06 or // RopCreateMessage
            0x0C or // RopSaveChangesMessage
            0x0E or // RopModifyRecipients
            0x0F or // RopReadRecipients
            0x11 or // RopSetMessageReadFlag
            0x20 or // RopSetMessageStatus
            0x21 or // RopGetAttachmentTable
            0x22 or // RopOpenAttachment
            0x29 or // RopRegisterNotification
            0x40 or // RopModifyPermissions
            0x41 or // RopModifyRules
            0x46 or // RopOpenEmbeddedMessage
            0x57 or // RopUpdateDeferredActionMessages
            0x66,   // RopSetReadFlags
        MapiDirection.Response => ropId is
            0x03 or // RopOpenMessage
            0x06 or // RopCreateMessage
            0x0C or // RopSaveChangesMessage
            0x0F or // RopReadRecipients
            0x10 or // RopReloadCachedInformation
            0x1F or // RopGetMessageStatus
            0x20 or // RopSetMessageStatus
            0x23 or // RopCreateAttachment
            0x2A or // RopNotify
            0x46 or // RopOpenEmbeddedMessage
            0x52 or // RopGetValidAttachments
            0x66 or // RopSetReadFlags
            0x6E,   // RopPending
        _ => false,
    };

    public static MapiNode Parse(
        ref MapiReader reader,
        int operationIndex,
        MapiDirection direction,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        MapiCaptureContext? context = null,
        string? captureScope = null,
        List<string>? warnings = null)
    {
        var ropId = reader.PeekByte("RopId");
        if (!Supports(direction, ropId))
        {
            throw new MapiParseException(
                reader.Position,
                $"RopMessageRulesDecoders has no {direction.ToString().ToLowerInvariant()} decoder for ROP 0x{ropId:X2}.");
        }

        return (direction, ropId) switch
        {
            (MapiDirection.Request, 0x03) => ParseOpenMessageRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x06) => ParseCreateMessageRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x0C) => ParseSaveChangesMessageRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x0E) => ParseModifyRecipientsRequest(
                ref reader, operationIndex, handleReferences, budget, cancellationToken, warnings),
            (MapiDirection.Request, 0x0F) => ParseReadRecipientsRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x11) => ParseSetMessageReadFlagRequest(
                ref reader, operationIndex, handleReferences, budget, context, captureScope),
            (MapiDirection.Request, 0x20) => ParseSetMessageStatusRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x21) => ParseGetAttachmentTableRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x22) => ParseOpenAttachmentRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x29) => ParseRegisterNotificationRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x40) => ParseModifyPermissionsRequest(ref reader, operationIndex, handleReferences, budget, cancellationToken),
            (MapiDirection.Request, 0x41) => ParseModifyRulesRequest(ref reader, operationIndex, handleReferences, budget, cancellationToken),
            (MapiDirection.Request, 0x46) => ParseOpenEmbeddedMessageRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x57) => ParseUpdateDeferredActionMessagesRequest(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Request, 0x66) => ParseSetReadFlagsRequest(ref reader, operationIndex, handleReferences, budget, cancellationToken),

            (MapiDirection.Response, 0x03) => ParseOpenMessageResponse(
                ref reader, operationIndex, handleReferences, budget, cancellationToken, warnings),
            (MapiDirection.Response, 0x06) => ParseCreateMessageResponse(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Response, 0x0C) => ParseSaveChangesMessageResponse(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Response, 0x0F) => ParseReadRecipientsResponse(ref reader, operationIndex, handleReferences, budget, cancellationToken),
            (MapiDirection.Response, 0x10) => ParseReloadCachedInformationResponse(
                ref reader, operationIndex, handleReferences, budget, cancellationToken, warnings),
            (MapiDirection.Response, 0x1F) => ParseGetMessageStatusResponse(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Response, 0x20) => ParseSetMessageStatusResponse(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Response, 0x23) => ParseCreateAttachmentResponse(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Response, 0x2A) => ParseNotifyResponse(
                ref reader, operationIndex, budget, cancellationToken, context, captureScope, warnings),
            (MapiDirection.Response, 0x46) => ParseOpenEmbeddedMessageResponse(
                ref reader, operationIndex, handleReferences, budget, cancellationToken, warnings),
            (MapiDirection.Response, 0x52) => ParseGetValidAttachmentsResponse(ref reader, operationIndex, handleReferences, budget, cancellationToken),
            (MapiDirection.Response, 0x66) => ParseSetReadFlagsResponse(ref reader, operationIndex, handleReferences, budget),
            (MapiDirection.Response, 0x6E) => ParsePendingResponse(ref reader, operationIndex, budget),

            _ => throw new MapiParseException(reader.Position, $"Unreachable: no decoder wired for approved ROP 0x{ropId:X2}."),
        };
    }

    // ---- Shared header/leaf primitives ---------------------------------------------------------

    private static byte ReadRopId(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte("RopId");
        ExtendedBufferParser.AddField(children, "RopId", offset, 1, $"0x{value:X2} ({RopBufferParser.Name(value)})", budget);
        return value;
    }

    private static byte ReadLogonId(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte("LogonId");
        ExtendedBufferParser.AddField(children, "LogonId", offset, 1, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static byte ReadHandleIndex(
        ref MapiReader reader,
        string name,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, value.ToString(CultureInfo.InvariantCulture), budget);
        handleReferences.Add(new RopHandleReference(operationIndex, name, offset, value));
        return value;
    }

    private static bool ReadReturnValueGate(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32("ReturnValue");
        ExtendedBufferParser.AddField(
            children, "ReturnValue", offset, 4, value == 0 ? "0x00000000 (Success)" : $"0x{value:X8} (Failure)", budget);
        return value == 0;
    }

    private static byte AddByteHex(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, $"0x{value:X2}", budget);
        return value;
    }

    private static byte AddByteDecimal(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static bool AddBool(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, value != 0 ? "true" : "false", budget);
        return value != 0;
    }

    private static ushort AddUInt16(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        ExtendedBufferParser.AddField(children, name, offset, 2, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static int AddCount16(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadCount16(name);
        ExtendedBufferParser.AddField(children, name, offset, 2, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static uint AddUInt32Decimal(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
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

    private static int AddInt32Decimal(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadInt32(name);
        ExtendedBufferParser.AddField(children, name, offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
        return value;
    }

    private static void AddStringField(ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, bool unicode, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = unicode ? reader.ReadNullTerminatedUnicode(name) : reader.ReadNullTerminatedAscii(name);
        ExtendedBufferParser.AddField(children, name, offset, reader.Position - offset, value, budget);
    }

    private static void AddSizePrefixedRaw(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string sizeFieldName,
        string dataFieldName,
        MapiNodeBudget budget)
    {
        var size = AddCount16(ref reader, children, sizeFieldName, budget);
        var dataOffset = reader.Position;
        var data = reader.ReadBytes(size, dataFieldName);
        children.Add(ExtendedBufferParser.RawNode(dataFieldName, data, dataOffset, budget));
    }

    private static void AddSizePrefixedAddressBookEntryId(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        var size = AddCount16(ref reader, children, "EntryIdSize", budget);
        var dataOffset = reader.Position;
        var data = reader.ReadBytes(size, "EntryID");
        try
        {
            children.Add(MapiEntryIdParser.ParseAddressBookEntryId(data, dataOffset, budget, warnings));
        }
        catch (MapiParseException exception)
        {
            children.Add(ExtendedBufferParser.RawNode("EntryID", data, dataOffset, budget));
            warnings?.Add(
                $"AddressBookEntryID semantic fields were retained as {data.Length:N0} raw bytes: {exception.Message}");
        }
    }

    /// <summary>Reads a fixed number of raw bytes (its size already implied by an earlier field, not a preceding size field of its own) as a single raw node.</summary>
    private static void AddFixedRaw(
        ref MapiReader reader, ImmutableArray<MapiNode>.Builder children, string name, int count, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var data = reader.ReadBytes(count, name);
        children.Add(ExtendedBufferParser.RawNode(name, data, offset, budget));
    }

    private static MapiNode BuildOperation(
        ref MapiReader reader,
        int operationIndex,
        byte ropId,
        ImmutableArray<MapiNode>.Builder children,
        long start,
        MapiNodeBudget budget)
    {
        budget.Claim(0);
        return new MapiNode(
            $"Operation {operationIndex}",
            MapiNodeKind.Operation,
            start,
            reader.Position - start,
            $"{RopBufferParser.Name(ropId)} (0x{ropId:X2})",
            children.ToImmutable());
    }

    // Shared compound wire types ([MS-OXCDATA] 2.2.1.1/2.2.1.2: FolderID and MessageID share the
    // same ReplicaId(2)+GlobalCounter(6) layout; [MS-OXCDATA] 2.9: PropertyTag).
    private static MapiNode ParseFolderOrMessageId(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddUInt16(ref reader, children, "ReplicaId", budget);
        var counterOffset = reader.Position;
        var counter = reader.ReadBytes(6, "GlobalCounter");
        children.Add(ExtendedBufferParser.RawNode("GlobalCounter", counter, counterOffset, budget));
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, children.ToImmutable());
    }

    private static MapiNode ParsePropertyTag(ref MapiReader reader, string name, MapiNodeBudget budget, out ushort type, out ushort id)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var typeOffset = reader.Position;
        type = reader.ReadUInt16("PropertyType");
        ExtendedBufferParser.AddField(children, "PropertyType", typeOffset, 2, NspiPropertyParser.PropertyTypeName(type), budget);
        var idOffset = reader.Position;
        id = reader.ReadUInt16("PropertyId");
        ExtendedBufferParser.AddField(children, "PropertyId", idOffset, 2, MapiPropertyNames.FormatPidTag(id), budget);
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, $"0x{id:X4}:{type:X4}", children.ToImmutable());
    }

    private static ImmutableArray<(ushort Type, ushort Id)> ParsePropertyTagArray(
        ref MapiReader reader,
        string name,
        int count,
        ImmutableArray<MapiNode>.Builder parentChildren,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var itemChildren = ImmutableArray.CreateBuilder<MapiNode>();
        var tags = ImmutableArray.CreateBuilder<(ushort, ushort)>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemChildren.Add(ParsePropertyTag(ref reader, $"[{index}]", budget, out var type, out var id));
            tags.Add((type, id));
        }
        budget.Claim(0);
        parentChildren.Add(new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, count.ToString(CultureInfo.InvariantCulture), itemChildren.ToImmutable()));
        return tags.ToImmutable();
    }

    // [MS-OXCDATA] 2.11.7 TypedString.
    private static MapiNode ParseTypedString(ref MapiReader reader, string name, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var typeOffset = reader.Position;
        var stringType = reader.ReadByte("StringType");
        ExtendedBufferParser.AddField(children, "StringType", typeOffset, 1, FormatStringType(stringType), budget);
        string? value = null;
        switch (stringType)
        {
            case 0x00: // NoPresent: no string follows.
            case 0x01: // Empty: no string follows, but is defined as empty.
                break;
            case 0x02: // CharacterString
            case 0x03: // ReducedUnicodeCharacterString
            {
                var stringOffset = reader.Position;
                value = reader.ReadNullTerminatedAscii("String");
                children.Add(MapiNode.Leaf("String", MapiNodeKind.Field, stringOffset, reader.Position - stringOffset, value));
                break;
            }
            case 0x04: // UnicodeCharacterString
            {
                var stringOffset = reader.Position;
                value = reader.ReadNullTerminatedUnicode("String");
                children.Add(MapiNode.Leaf("String", MapiNodeKind.Field, stringOffset, reader.Position - stringOffset, value));
                break;
            }
            default:
                throw new MapiParseException(typeOffset, $"TypedString.StringType has unsupported value 0x{stringType:X2}.");
        }
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, value, children.ToImmutable());
    }

    private static string FormatStringType(byte value) => value switch
    {
        0x00 => "NoPresent",
        0x01 => "Empty",
        0x02 => "CharacterString",
        0x03 => "ReducedUnicodeCharacterString",
        0x04 => "UnicodeCharacterString",
        _ => $"Unknown 0x{value:X2}",
    };

    // ---- RecipientRow / PropertyRow ([MS-OXCDATA] 2.8.3, 2.8.1) --------------------------------

    private static string FormatAddressType(int value) => value switch
    {
        0x0 => "NoType",
        0x1 => "X500DN",
        0x2 => "MsMail",
        0x3 => "SMTP",
        0x4 => "Fax",
        0x5 => "ProfessionalOfficeSystem",
        0x6 => "PersonalDistributionList1",
        0x7 => "PersonalDistributionList2",
        _ => $"Unknown 0x{value:X1}",
    };

    private static string FormatDisplayType(byte value) => value switch
    {
        0 => "MessagingUser",
        1 => "DistributionList",
        2 => "Forum",
        3 => "AutomatedAgent",
        4 => "AddressBookforLargeGroup",
        5 => "Private",
        6 => "AddressBookfromMessagingSystem",
        _ => $"Unknown 0x{value:X2}",
    };

    private static MapiNode ParseRecipientTypeField(ref MapiReader reader, MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadByte("RecipientType");
        var flag = (value & 0xF0) >> 4;
        var type = value & 0x0F;
        budget.Claim(0);
        return MapiNode.Leaf("RecipientType", MapiNodeKind.Field, offset, 1, $"0x{value:X2} (Flag=0x{flag:X1}, Type=0x{type:X1})");
    }

    private static MapiNode ParseRecipientRow(
        ref MapiReader reader,
        string name,
        ImmutableArray<(ushort Type, ushort Id)> columns,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();

        var flagsOffset = reader.Position;
        var byte0 = reader.ReadByte("RecipientFlags[0]");
        var byte1 = reader.ReadByte("RecipientFlags[1]");
        var flagR = (byte0 & RecipientFlagR) != 0;
        var flagS = (byte0 & RecipientFlagS) != 0;
        var flagT = (byte0 & RecipientFlagT) != 0;
        var flagD = (byte0 & RecipientFlagD) != 0;
        var flagE = (byte0 & RecipientFlagE) != 0;
        var addressType = byte0 & RecipientAddressTypeMask;
        var flagO = (byte1 & RecipientFlagO) != 0;
        var flagI = (byte1 & RecipientFlagI) != 0;
        var flagU = (byte1 & RecipientFlagU) != 0;
        var flagN = (byte1 & RecipientFlagN) != 0;

        var flagsChildren = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(flagsChildren, "R", flagsOffset, 1, flagR.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "S", flagsOffset, 1, flagS.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "T", flagsOffset, 1, flagT.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "D", flagsOffset, 1, flagD.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "E", flagsOffset, 1, flagE.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "Type", flagsOffset, 1, FormatAddressType(addressType), budget);
        ExtendedBufferParser.AddField(flagsChildren, "O", flagsOffset + 1, 1, flagO.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "I", flagsOffset + 1, 1, flagI.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "U", flagsOffset + 1, 1, flagU.ToString(), budget);
        ExtendedBufferParser.AddField(flagsChildren, "N", flagsOffset + 1, 1, flagN.ToString(), budget);
        budget.Claim(0);
        children.Add(new MapiNode("RecipientFlags", MapiNodeKind.Structure, flagsOffset, 2, FormatAddressType(addressType), flagsChildren.ToImmutable()));

        switch (addressType)
        {
            case 0x1: // X500DN
                AddByteDecimal(ref reader, children, "AddressPrefixUsed", budget);
                var displayTypeOffset = reader.Position;
                var displayType = reader.ReadByte("DisplayType");
                ExtendedBufferParser.AddField(children, "DisplayType", displayTypeOffset, 1, FormatDisplayType(displayType), budget);
                AddStringField(ref reader, children, "X500DN", unicode: false, budget);
                break;
            case 0x6: // PersonalDistributionList1
            case 0x7: // PersonalDistributionList2
                AddSizePrefixedAddressBookEntryId(ref reader, children, budget, warnings);
                AddSizePrefixedRaw(ref reader, children, "SearchKeySize", "SearchKey", budget);
                break;
            case 0x0 when flagO: // NoType with the "Out of band" flag set carries an AddressType string.
                AddStringField(ref reader, children, "AddressType", unicode: false, budget);
                break;
        }

        if (flagE)
        {
            AddStringField(ref reader, children, "EmailAddress", flagU, budget);
        }
        if (flagD)
        {
            AddStringField(ref reader, children, "DisplayName", flagU, budget);
        }
        if (flagI)
        {
            AddStringField(ref reader, children, "SimpleDisplayName", flagU, budget);
        }
        if (flagT)
        {
            AddStringField(ref reader, children, "TransmittableDisplayName", flagU, budget);
        }

        var columnCount = AddCount16(ref reader, children, "RecipientColumnCount", budget);
        if (columnCount > columns.Length)
        {
            throw new MapiParseException(
                reader.Position,
                $"{name}.RecipientColumnCount {columnCount} exceeds the {columns.Length} column(s) declared earlier in this operation.");
        }
        children.Add(ParseRopPropertyRow(
            ref reader,
            "RecipientProperties",
            columns,
            columnCount,
            budget,
            depth: 1,
            cancellationToken));

        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, children.ToImmutable());
    }

    /// <summary>
    /// [MS-OXCDATA] 2.8.1 PropertyRow, decoded with ROP semantics (no per-value presence byte -
    /// unlike <c>NspiPropertyParser.ParseRow</c>, whose <c>includePresence:true</c> behavior is
    /// specific to NSPI's row format and is not reusable here). Each value is parsed with
    /// <see cref="MapiWireWidthContext.RopBuffer"/>, so an embedded PtypBinary value reads its
    /// 16-bit ROP-buffer byte count rather than NSPI/extended-rule's 32-bit form. Internal (rather
    /// than private) because <see cref="RopPropertyStoreDecoders"/> reuses it verbatim for
    /// RopGetReceiveFolderTable's fixed-column response rows, which share this exact StandardPropertyRow/
    /// FlaggedPropertyRow shape against a different, but equally protocol-fixed, column list.
    /// </summary>
    internal static MapiNode ParseRopPropertyRow(
        ref MapiReader reader,
        string name,
        ImmutableArray<(ushort Type, ushort Id)> columns,
        int columnCount,
        MapiNodeBudget budget,
        int depth,
        CancellationToken cancellationToken,
        uint? codePage = null)
    {
        budget.Claim(depth);
        var start = reader.Position;
        var flagOffset = reader.Position;
        var flag = reader.ReadByte("PropertyRow.Flag");
        if (flag is not (0x00 or 0x01))
        {
            throw new MapiParseException(flagOffset, $"{name}.Flag has unsupported value 0x{flag:X2}.");
        }
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        ExtendedBufferParser.AddField(children, "Flag", flagOffset, 1, flag == 0 ? "All values present" : "Flagged values", budget);

        for (var index = 0; index < columnCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (declaredTypeWithFlags, propertyId) = columns[index];
            var declaredType = (ushort)(
                (declaredTypeWithFlags & 0x2000) != 0
                    ? declaredTypeWithFlags & ~0x3000
                    : declaredTypeWithFlags);
            var valueStart = reader.Position;
            var valueChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var actualType = declaredType;

            if (actualType == 0x0000) // PtypUnspecified: the runtime type precedes the value.
            {
                var actualTypeOffset = reader.Position;
                actualType = reader.ReadUInt16("PropertyType");
                ExtendedBufferParser.AddField(valueChildren, "PropertyType", actualTypeOffset, 2, NspiPropertyParser.PropertyTypeName(actualType), budget);
            }

            var skip = false;
            if (flag == 0x01)
            {
                var valueFlagOffset = reader.Position;
                var valueFlag = reader.ReadByte("Value.Flag");
                ExtendedBufferParser.AddField(
                    valueChildren,
                    "Flag",
                    valueFlagOffset,
                    1,
                    valueFlag switch
                    {
                        0x00 => "Present",
                        0x01 => "Unavailable",
                        0x0A => "Error",
                        _ => $"Unknown 0x{valueFlag:X2}",
                    },
                    budget);
                switch (valueFlag)
                {
                    case 0x00:
                        break;
                    case 0x01:
                        skip = true;
                        break;
                    case 0x0A:
                        actualType = 0x000A; // PtypErrorCode
                        break;
                    default:
                        throw new MapiParseException(valueFlagOffset, $"{name}.Value[{index}].Flag has unsupported value 0x{valueFlag:X2}.");
                }
            }

            if (!skip)
            {
                valueChildren.Add(NspiPropertyParser.ParseValue(
                    ref reader,
                    actualType,
                    "Value",
                    budget,
                    depth + 1,
                    includePresence: false,
                    codePage: codePage,
                    addressBookSemantics: false,
                    warnings: null,
                    context: MapiWireWidthContext.RopBuffer));
            }

            budget.Claim(depth);
            children.Add(new MapiNode(
                $"Value[{index}]",
                MapiNodeKind.Property,
                valueStart,
                reader.Position - valueStart,
                $"{MapiPropertyNames.FormatPidTag(propertyId)}:{actualType:X4}",
                valueChildren.ToImmutable()));
        }

        budget.Claim(depth);
        return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, columnCount.ToString(CultureInfo.InvariantCulture), children.ToImmutable());
    }

    // OpenRecipientRow ([MS-OXCROPS] 2.2.5.x responses): no RowId; a fixed-shape header followed
    // by a RecipientRow slice bounded exactly by RecipientRowSize.
    private static MapiNode ParseOpenRecipientRows(
        ref MapiReader reader,
        string name,
        int rowCount,
        ImmutableArray<(ushort Type, ushort Id)> columns,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < rowCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = reader.Position;
            var rowChildren = ImmutableArray.CreateBuilder<MapiNode>();
            rowChildren.Add(ParseRecipientTypeField(ref reader, budget));
            AddUInt16(ref reader, rowChildren, "CodePageId", budget);
            AddUInt16(ref reader, rowChildren, "Reserved", budget);
            var size = AddCount16(ref reader, rowChildren, "RecipientRowSize", budget);
            var rowSlice = reader.SliceReader(size, "RecipientRow");
            rowChildren.Add(ParseRecipientRow(ref rowSlice, "RecipientRow", columns, budget, cancellationToken, warnings));
            if (!rowSlice.End)
            {
                throw new MapiParseException(rowSlice.Position, $"{name}[{index}].RecipientRow did not consume its declared RecipientRowSize.");
            }
            budget.Claim(0);
            children.Add(new MapiNode($"{name}[{index}]", MapiNodeKind.Structure, rowStart, reader.Position - rowStart, null, rowChildren.ToImmutable()));
        }
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, rowCount.ToString(CultureInfo.InvariantCulture), children.ToImmutable());
    }

    // ModifyRecipientRow ([MS-OXCROPS] 2.2.5.7 RopModifyRecipients request): RowId, RecipientType,
    // then an optional RecipientRow (only present when RecipientRowSize > 0).
    private static MapiNode ParseModifyRecipientRows(
        ref MapiReader reader,
        string name,
        int rowCount,
        ImmutableArray<(ushort Type, ushort Id)> columns,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < rowCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = reader.Position;
            var rowChildren = ImmutableArray.CreateBuilder<MapiNode>();
            AddUInt32Decimal(ref reader, rowChildren, "RowId", budget);
            rowChildren.Add(ParseRecipientTypeField(ref reader, budget));
            var size = AddCount16(ref reader, rowChildren, "RecipientRowSize", budget);
            if (size > 0)
            {
                var rowSlice = reader.SliceReader(size, "RecipientRow");
                rowChildren.Add(ParseRecipientRow(ref rowSlice, "RecipientRow", columns, budget, cancellationToken, warnings));
                if (!rowSlice.End)
                {
                    throw new MapiParseException(rowSlice.Position, $"{name}[{index}].RecipientRow did not consume its declared RecipientRowSize.");
                }
            }
            budget.Claim(0);
            children.Add(new MapiNode($"{name}[{index}]", MapiNodeKind.Structure, rowStart, reader.Position - rowStart, null, rowChildren.ToImmutable()));
        }
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, rowCount.ToString(CultureInfo.InvariantCulture), children.ToImmutable());
    }

    // ReadRecipientRow ([MS-OXCROPS] 2.2.5.8 RopReadRecipients response): unlike the three row kinds
    // above, this response carries no RecipientColumns array of its own, so the RecipientRow bytes
    // cannot be deterministically decoded here; they are retained as bounded raw bytes.
    private static MapiNode ParseReadRecipientRows(ref MapiReader reader, string name, int rowCount, MapiNodeBudget budget, CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < rowCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = reader.Position;
            var rowChildren = ImmutableArray.CreateBuilder<MapiNode>();
            AddUInt32Decimal(ref reader, rowChildren, "RowId", budget);
            rowChildren.Add(ParseRecipientTypeField(ref reader, budget));
            AddUInt16(ref reader, rowChildren, "CodePageId", budget);
            AddUInt16(ref reader, rowChildren, "Reserved", budget);
            var size = AddCount16(ref reader, rowChildren, "RecipientRowSize", budget);
            var dataOffset = reader.Position;
            var data = reader.ReadBytes(size, "RecipientRow");
            rowChildren.Add(ExtendedBufferParser.RawNode(
                "RecipientRow (no RecipientColumns are carried by this response; retained as raw)", data, dataOffset, budget));
            budget.Claim(0);
            children.Add(new MapiNode($"{name}[{index}]", MapiNodeKind.Structure, rowStart, reader.Position - rowStart, null, rowChildren.ToImmutable()));
        }
        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Array, start, reader.Position - start, rowCount.ToString(CultureInfo.InvariantCulture), children.ToImmutable());
    }

    // Shared tail shape of RopOpenMessage/RopReloadCachedInformation/RopOpenEmbeddedMessage
    // responses ([MS-OXCROPS] 2.2.5.x): named-property flag, subject strings, then a
    // RecipientColumns-driven array of OpenRecipientRow.
    private static void ParseNamedPropsAndRecipients(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        AddBool(ref reader, children, "HasNamedProperties", budget);
        children.Add(ParseTypedString(ref reader, "SubjectPrefix", budget));
        children.Add(ParseTypedString(ref reader, "NormalizedSubject", budget));
        AddUInt16(ref reader, children, "RecipientCount", budget);
        var columnCount = AddCount16(ref reader, children, "ColumnCount", budget);
        var columns = ParsePropertyTagArray(ref reader, "RecipientColumns", columnCount, children, budget, cancellationToken);
        var rowCount = AddByteDecimal(ref reader, children, "RowCount", budget);
        children.Add(ParseOpenRecipientRows(
            ref reader,
            "RecipientRows",
            rowCount,
            columns,
            budget,
            cancellationToken,
            warnings));
    }

    // ---- RuleData / PermissionData ([MS-OXORULE] 2.2.3.1, [MS-OXCPERM] 2.2.3) ------------------
    // Each PropertyValue is parsed with MapiWireWidthContext.RopBuffer, so an embedded PtypBinary
    // value reads its 16-bit ROP-buffer byte count rather than NSPI/extended-rule's 32-bit form.

    private static MapiNode ParseRuleOrPermissionDataArray(
        ref MapiReader reader,
        string arrayName,
        string itemFlagsFieldName,
        int count,
        MapiNodeBudget budget,
        CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemStart = reader.Position;
            var itemChildren = ImmutableArray.CreateBuilder<MapiNode>();
            var itemFlags = AddByteHex(ref reader, itemChildren, itemFlagsFieldName, budget);
            var valueCount = AddCount16(ref reader, itemChildren, "PropertyValueCount", budget);

            var valuesStart = reader.Position;
            var valuesChildren = ImmutableArray.CreateBuilder<MapiNode>();
            for (var valueIndex = 0; valueIndex < valueCount; valueIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                valuesChildren.Add(NspiPropertyParser.ParseStandardTaggedValue(
                    ref reader, $"PropertyValues[{valueIndex}]", budget, depth: 1, context: MapiWireWidthContext.RopBuffer));
            }
            budget.Claim(0);
            itemChildren.Add(new MapiNode(
                "PropertyValues", MapiNodeKind.Array, valuesStart, reader.Position - valuesStart, valueCount.ToString(CultureInfo.InvariantCulture), valuesChildren.ToImmutable()));

            budget.Claim(0);
            children.Add(new MapiNode($"{arrayName}[{index}]", MapiNodeKind.Structure, itemStart, reader.Position - itemStart, $"0x{itemFlags:X2}", itemChildren.ToImmutable()));
        }
        budget.Claim(0);
        return new MapiNode(arrayName, MapiNodeKind.Array, start, reader.Position - start, count.ToString(CultureInfo.InvariantCulture), children.ToImmutable());
    }

    private static string FormatModifyRulesFlags(byte value) => $"0x{value:X2} ({((value & 0x01) != 0 ? "REPLACE" : "no flags")})";

    private static string FormatModifyPermissionsFlags(byte value)
    {
        var names = new List<string>();
        if ((value & 0x01) != 0) names.Add("ReplaceRows");
        if ((value & 0x02) != 0) names.Add("IncludeFreeBusy");
        return $"0x{value:X2}" + (names.Count == 0 ? string.Empty : $" ({string.Join(", ", names)})");
    }

    // ---- NotificationData ([MS-OXCNOTIF] 2.2.1.4.1, upstream NotificationData.Parse) -----------

    private static string FormatNotificationFlags(ushort flags)
    {
        var type = flags & NotificationTypeMask;
        var names = new List<string>();
        if ((flags & NfT) != 0) names.Add("T");
        if ((flags & NfU) != 0) names.Add("U");
        if ((flags & NfS) != 0) names.Add("S");
        if ((flags & NfM) != 0) names.Add("M");
        var suffix = names.Count == 0 ? string.Empty : $" [{string.Join(",", names)}]";
        return $"0x{flags:X4} (Type=0x{type:X3}{suffix})";
    }

    private static string FormatTableEventType(ushort value) => value switch
    {
        1 => "TableChanged",
        3 => "TableRowAdded",
        4 => "TableRowDeleted",
        5 => "TableRowModified",
        7 => "TableRestrictionChanged",
        _ => $"Unknown 0x{value:X4}",
    };

    private static MapiNode ParseNotificationData(
        ref MapiReader reader,
        string name,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        uint notificationHandle,
        MapiCaptureContext? context,
        string? captureScope,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();

        var flagsOffset = reader.Position;
        var flags = reader.ReadUInt16("NotificationFlags");
        ExtendedBufferParser.AddField(children, "NotificationFlags", flagsOffset, 2, FormatNotificationFlags(flags), budget);
        bool Has(ushort bit) => (flags & bit) != 0;
        var notificationType = (ushort)(flags & NotificationTypeMask);
        bool IsType(ushort type) => notificationType == type;

        ushort? tableEventType = null;
        if (IsType(NfTableModified))
        {
            var tableEventOffset = reader.Position;
            var value = reader.ReadUInt16("TableEventType");
            tableEventType = value;
            ExtendedBufferParser.AddField(children, "TableEventType", tableEventOffset, 2, FormatTableEventType(value), budget);
        }

        var isMessage = Has(NfM);
        var notModifiedExtended = !IsType(NfTableModified) && !IsType(NfExtended);
        var isCreateDeleteMovedCopied = IsType(NfObjectCreated) || IsType(NfObjectDeleted) || IsType(NfObjectMoved) || IsType(NfObjectCopied);
        var isSearchFolderMessage = Has(NfS) && Has(NfM);
        var isFolderEvent = !Has(NfS) && !Has(NfM);
        var isMovedCopied = IsType(NfObjectMoved) || IsType(NfObjectCopied);
        var isCreateModify = IsType(NfObjectCreated) || IsType(NfObjectModified);

        if (tableEventType is { } te)
        {
            var isAdm = te is 0x0003 or 0x0004 or 0x0005;
            var isAm = te is 0x0003 or 0x0005;
            if (isAdm)
            {
                children.Add(ParseFolderOrMessageId(ref reader, "TableRowFolderID", budget));
                if (isMessage)
                {
                    children.Add(ParseFolderOrMessageId(ref reader, "TableRowMessageID", budget));
                    AddUInt32Decimal(ref reader, children, "TableRowInstance", budget);
                }
            }
            if (isAm)
            {
                children.Add(ParseFolderOrMessageId(ref reader, "InsertAfterTableRowFolderID", budget));
            }
            if (isAm && isMessage)
            {
                children.Add(ParseFolderOrMessageId(ref reader, "InsertAfterTableRowID", budget));
                AddUInt32Decimal(ref reader, children, "InsertAfterTableRowInstance", budget);
            }
            if (isAm)
            {
                var size = AddCount16(ref reader, children, "TableRowDataSize", budget);
                var dataOffset = reader.Position;
                var data = reader.ReadBytes(size, "TableRowData");
                if (context is not null
                    && context.TryGetTableColumnsByHandleValue(captureScope, notificationHandle, out var columns))
                {
                    var rowReader = new MapiReader(data, cancellationToken, dataOffset);
                    try
                    {
                        var row = ParseRopPropertyRow(
                            ref rowReader,
                            "TableRowData",
                            columns,
                            columns.Length,
                            budget,
                            depth: 1,
                            cancellationToken);
                        if (!rowReader.End)
                        {
                            throw new MapiParseException(
                                rowReader.Position,
                                $"TableRowData has {rowReader.Remaining:N0} trailing byte(s) beyond its correlated PropertyRow.");
                        }
                        children.Add(row);
                    }
                    catch (MapiParseException exception)
                    {
                        warnings?.Add(
                            $"RopNotify TableRowData could not be decoded against the correlated RopSetColumns list: {exception.Message}");
                        children.Add(ExtendedBufferParser.RawNode(
                            $"TableRowData (correlated columns did not consume the declared size: {exception.Message}; retained raw)",
                            data,
                            dataOffset,
                            new MapiNodeBudget()));
                    }
                }
                else
                {
                    warnings?.Add(
                        $"RopNotify TableRowData for NotificationHandle 0x{notificationHandle:X8} has no unambiguous prior RopSetColumns list; bytes were retained raw.");
                    children.Add(ExtendedBufferParser.RawNode(
                        "TableRowData (no unambiguous prior RopSetColumns for NotificationHandle; retained raw)",
                        data,
                        dataOffset,
                        budget));
                }
            }
        }

        if (notModifiedExtended)
        {
            children.Add(ParseFolderOrMessageId(ref reader, "FolderId", budget));
            if (isMessage)
            {
                children.Add(ParseFolderOrMessageId(ref reader, "MessageId", budget));
            }
        }

        if (isCreateDeleteMovedCopied && (isSearchFolderMessage || isFolderEvent))
        {
            children.Add(ParseFolderOrMessageId(ref reader, "ParentFolderId", budget));
        }

        if (isMovedCopied)
        {
            children.Add(ParseFolderOrMessageId(ref reader, "OldFolderId", budget));
            if (isMessage)
            {
                children.Add(ParseFolderOrMessageId(ref reader, "OldMessageId", budget));
            }
            else
            {
                children.Add(ParseFolderOrMessageId(ref reader, "OldParentFolderId", budget));
            }
        }

        if (isCreateModify)
        {
            var tagCount = AddUInt16(ref reader, children, "TagCount", budget);
            if (tagCount != 0x0000 && tagCount != 0xFFFF)
            {
                ParsePropertyTagArray(ref reader, "Tags", tagCount, children, budget, cancellationToken);
            }
        }

        if (Has(NfT))
        {
            AddUInt32Decimal(ref reader, children, "TotalMessageCount", budget);
        }
        if (Has(NfU))
        {
            AddUInt32Decimal(ref reader, children, "UnreadMessageCount", budget);
        }

        if (Has(NfNewMail))
        {
            AddUInt32Hex(ref reader, children, "MessageFlags", budget);
            var unicodeFlagOffset = reader.Position;
            var unicodeFlag = reader.ReadByte("UnicodeFlag");
            ExtendedBufferParser.AddField(children, "UnicodeFlag", unicodeFlagOffset, 1, unicodeFlag.ToString(CultureInfo.InvariantCulture), budget);
            if (unicodeFlag == 0x00)
            {
                AddStringField(ref reader, children, "MessageClass", unicode: false, budget);
            }
            else if (unicodeFlag == 0x01)
            {
                AddStringField(ref reader, children, "MessageClass", unicode: true, budget);
            }
            // Any other UnicodeFlag value: no further bytes follow, matching upstream exactly.
        }

        budget.Claim(0);
        return new MapiNode(name, MapiNodeKind.Structure, start, reader.Position - start, null, children.ToImmutable());
    }

    // ---- MSOXCMSG request decoders --------------------------------------------------------------

    private static MapiNode ParseOpenMessageRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        AddUInt16(ref reader, children, "CodePageId", budget);
        children.Add(ParseFolderOrMessageId(ref reader, "FolderId", budget));
        AddByteHex(ref reader, children, "OpenModeFlags", budget);
        children.Add(ParseFolderOrMessageId(ref reader, "MessageId", budget));
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseCreateMessageRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        AddUInt16(ref reader, children, "CodePageId", budget);
        children.Add(ParseFolderOrMessageId(ref reader, "FolderId", budget));
        AddBool(ref reader, children, "AssociatedFlag", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseSaveChangesMessageRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "ResponseHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        AddByteHex(ref reader, children, "SaveFlags", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseModifyRecipientsRequest(
        ref MapiReader reader,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var columnCount = AddCount16(ref reader, children, "ColumnCount", budget);
        var columns = ParsePropertyTagArray(ref reader, "RecipientColumns", columnCount, children, budget, cancellationToken);
        var rowCount = AddCount16(ref reader, children, "RowCount", budget);
        children.Add(ParseModifyRecipientRows(
            ref reader,
            "RecipientRows",
            rowCount,
            columns,
            budget,
            cancellationToken,
            warnings));
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseReadRecipientsRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        AddUInt32Decimal(ref reader, children, "RowId", budget);
        AddUInt16(ref reader, children, "Reserved", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    /// <summary>
    /// [MS-OXCMSG] 2.2.3.9.1 / upstream RopSetMessageReadFlagRequest.Parse(): the trailing 24-byte
    /// ClientData block is present exactly when the object was opened against a NON-private (public
    /// folders) logon - state this operation's own bytes cannot reveal, so it is consulted via
    /// <see cref="MapiCaptureContext.TryGetLogonPrivacy"/>, recorded transactionally by
    /// <see cref="RopPropertyStoreDecoders"/>'s RopLogon request case earlier in the same capture.
    /// When that state is unavailable or ambiguous, this throws rather than guessing, and
    /// <c>RopSemanticParser</c> retains the operation as raw with an explanatory warning.
    /// </summary>
    private static MapiNode ParseSetMessageReadFlagRequest(
        ref MapiReader reader,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        MapiCaptureContext? context,
        string? captureScope)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        var logonId = ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "ResponseHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        AddByteHex(ref reader, children, "ReadFlags", budget);
        if (context is null || !context.TryGetLogonPrivacy(captureScope, logonId, out var isPrivate))
        {
            throw new MapiParseException(
                reader.Position,
                $"RopSetMessageReadFlag request: cannot determine whether ClientData follows without a prior same-capture RopLogon request recording LogonId {logonId}'s LogonFlags.Private state.");
        }

        if (!isPrivate)
        {
            AddFixedRaw(ref reader, children, "ClientData", 24, budget);
        }

        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseSetMessageStatusRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        children.Add(ParseFolderOrMessageId(ref reader, "MessageId", budget));
        AddUInt32Hex(ref reader, children, "MessageStatusFlags", budget);
        AddUInt32Hex(ref reader, children, "MessageStatusMask", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseGetAttachmentTableRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        AddByteHex(ref reader, children, "TableFlags", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseOpenAttachmentRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        AddByteHex(ref reader, children, "OpenAttachmentFlags", budget);
        AddUInt32Decimal(ref reader, children, "AttachmentID", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseOpenEmbeddedMessageRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        AddUInt16(ref reader, children, "CodePageId", budget);
        AddByteHex(ref reader, children, "OpenModeFlags", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseSetReadFlagsRequest(
        ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget, CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        AddBool(ref reader, children, "WantAsynchronous", budget);
        AddByteHex(ref reader, children, "ReadFlags", budget);
        var count = AddCount16(ref reader, children, "MessageIdCount", budget);

        var arrayStart = reader.Position;
        var idsChildren = ImmutableArray.CreateBuilder<MapiNode>();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            idsChildren.Add(ParseFolderOrMessageId(ref reader, $"[{index}]", budget));
        }
        budget.Claim(0);
        children.Add(new MapiNode("MessageIds", MapiNodeKind.Array, arrayStart, reader.Position - arrayStart, count.ToString(CultureInfo.InvariantCulture), idsChildren.ToImmutable()));

        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    // ---- MSOXORULE / MSOXCPERM request decoders -------------------------------------------------

    private static MapiNode ParseModifyRulesRequest(
        ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget, CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var flagsOffset = reader.Position;
        var flags = reader.ReadByte("ModifyRulesFlags");
        ExtendedBufferParser.AddField(children, "ModifyRulesFlags", flagsOffset, 1, FormatModifyRulesFlags(flags), budget);
        var count = AddCount16(ref reader, children, "RulesCount", budget);
        children.Add(ParseRuleOrPermissionDataArray(ref reader, "RulesData", "RuleDataFlags", count, budget, cancellationToken));
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseUpdateDeferredActionMessagesRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        AddSizePrefixedRaw(ref reader, children, "ServerEntryIdSize", "ServerEntryId", budget);
        AddSizePrefixedRaw(ref reader, children, "ClientEntryIdSize", "ClientEntryId", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseModifyPermissionsRequest(
        ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget, CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var flagsOffset = reader.Position;
        var flags = reader.ReadByte("ModifyFlags");
        ExtendedBufferParser.AddField(children, "ModifyFlags", flagsOffset, 1, FormatModifyPermissionsFlags(flags), budget);
        var count = AddCount16(ref reader, children, "ModifyCount", budget);
        children.Add(ParseRuleOrPermissionDataArray(ref reader, "PermissionsData", "PermissionDataFlags", count, budget, cancellationToken));
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    // ---- MSOXCNOTIF request decoder --------------------------------------------------------------

    private static MapiNode ParseRegisterNotificationRequest(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadLogonId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);

        var flagsOffset = reader.Position;
        var flags = reader.ReadUInt16("NotificationTypes");
        ExtendedBufferParser.AddField(children, "NotificationTypes", flagsOffset, 2, FormatNotificationFlags(flags), budget);

        if ((flags & NfExtended) != 0)
        {
            AddByteHex(ref reader, children, "Reserved", budget);
        }

        var wantWholeStore = AddBool(ref reader, children, "WantWholeStore", budget);
        if (!wantWholeStore)
        {
            children.Add(ParseFolderOrMessageId(ref reader, "FolderId", budget));
            children.Add(ParseFolderOrMessageId(ref reader, "MessageId", budget));
        }

        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    // ---- MSOXCMSG response decoders ---------------------------------------------------------------

    private static MapiNode ParseOpenMessageResponse(
        ref MapiReader reader,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            ParseNamedPropsAndRecipients(ref reader, children, budget, cancellationToken, warnings);
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseCreateMessageResponse(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            var hasMessageId = AddBool(ref reader, children, "HasMessageId", budget);
            if (hasMessageId)
            {
                children.Add(ParseFolderOrMessageId(ref reader, "MessageId", budget));
            }
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseSaveChangesMessageResponse(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "ResponseHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
            children.Add(ParseFolderOrMessageId(ref reader, "MessageId", budget));
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseReadRecipientsResponse(
        ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget, CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            var rowCount = AddByteDecimal(ref reader, children, "RowCount", budget);
            children.Add(ParseReadRecipientRows(ref reader, "RecipientRows", rowCount, budget, cancellationToken));
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseReloadCachedInformationResponse(
        ref MapiReader reader,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            ParseNamedPropsAndRecipients(ref reader, children, budget, cancellationToken, warnings);
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseGetMessageStatusResponse(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            AddUInt32Hex(ref reader, children, "MessageStatusFlags", budget);
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseSetMessageStatusResponse(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            AddUInt32Hex(ref reader, children, "MessageStatusFlags", budget);
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseCreateAttachmentResponse(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            AddUInt32Decimal(ref reader, children, "AttachmentID", budget);
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseOpenEmbeddedMessageResponse(
        ref MapiReader reader,
        int operationIndex,
        List<RopHandleReference> handleReferences,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "OutputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            AddByteHex(ref reader, children, "Reserved", budget);
            children.Add(ParseFolderOrMessageId(ref reader, "MessageId", budget));
            ParseNamedPropsAndRecipients(ref reader, children, budget, cancellationToken, warnings);
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseGetValidAttachmentsResponse(
        ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget, CancellationToken cancellationToken)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        var success = ReadReturnValueGate(ref reader, children, budget);
        if (success)
        {
            var count = AddCount16(ref reader, children, "AttachmentIdCount", budget);
            var arrayStart = reader.Position;
            var idsChildren = ImmutableArray.CreateBuilder<MapiNode>();
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = reader.Position;
                var value = reader.ReadInt32($"[{index}]");
                ExtendedBufferParser.AddField(idsChildren, $"[{index}]", offset, 4, value.ToString(CultureInfo.InvariantCulture), budget);
            }
            budget.Claim(0);
            children.Add(new MapiNode("AttachmentIdArray", MapiNodeKind.Array, arrayStart, reader.Position - arrayStart, count.ToString(CultureInfo.InvariantCulture), idsChildren.ToImmutable()));
        }
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParseSetReadFlagsResponse(ref MapiReader reader, int operationIndex, List<RopHandleReference> handleReferences, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        ReadHandleIndex(ref reader, "InputHandleIndex", operationIndex, handleReferences, children, budget);
        ReadReturnValueGate(ref reader, children, budget);
        // Unlike almost every other response in this file, PartialCompletion is unconditional -
        // it is always present regardless of ReturnValue, per [MS-OXCROPS] 2.2.5.10.2.
        AddBool(ref reader, children, "PartialCompletion", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    // ---- MSOXCNOTIF response decoders -------------------------------------------------------------

    private static MapiNode ParseNotifyResponse(
        ref MapiReader reader,
        int operationIndex,
        MapiNodeBudget budget,
        CancellationToken cancellationToken,
        MapiCaptureContext? context,
        string? captureScope,
        List<string>? warnings)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);

        // NotificationHandle is an opaque server object handle value, not a byte-sized index into
        // the trailing handle table, so it is intentionally not added to handleReferences.
        var notificationHandle = AddUInt32Hex(ref reader, children, "NotificationHandle", budget);
        ReadLogonId(ref reader, children, budget);

        children.Add(ParseNotificationData(
            ref reader,
            "NotificationData",
            budget,
            cancellationToken,
            notificationHandle,
            context,
            captureScope,
            warnings));

        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }

    private static MapiNode ParsePendingResponse(ref MapiReader reader, int operationIndex, MapiNodeBudget budget)
    {
        var start = reader.Position;
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        var ropId = ReadRopId(ref reader, children, budget);
        AddUInt16(ref reader, children, "SessionIndex", budget);
        return BuildOperation(ref reader, operationIndex, ropId, children, start, budget);
    }
}
