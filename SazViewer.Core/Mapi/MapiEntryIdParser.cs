using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SazViewer.Core;

internal static class MapiEntryIdParser
{
    private static readonly Guid MuidEmsab = new("C840A7DC-42C0-1A10-B4B9-08002B2FE182");

    public const int FolderEntryIdSize = 46;
    public const int MessageEntryIdSize = 70;
    public const int EphemeralEntryIdSize = 32;
    private const int MinimumAddressBookEntryIdSize = 29;

    public static MapiNode ParseFolderEntryId(
        ReadOnlySpan<byte> bytes,
        long offset,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        if (bytes.Length != FolderEntryIdSize)
        {
            throw new MapiParseException(
                checked((int)offset),
                $"Folder EntryID is {bytes.Length:N0} bytes; {FolderEntryIdSize} are required.");
        }

        var reader = new MapiReader(bytes, baseOffset: checked((int)offset));
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddUInt32(ref reader, children, "Flags", budget, warnings, mustBeZero: true);
        AddGuid(ref reader, children, "ProviderUID", budget);
        AddStoreObjectType(ref reader, children, "FolderType", budget, warnings, folderType: true);
        AddGuid(ref reader, children, "DatabaseGuid", budget);
        AddBytes(ref reader, children, "GlobalCounter", 6, budget);
        AddUInt16(ref reader, children, "Pad", budget, warnings, mustBeZero: true);
        return new MapiNode(
            "FolderEID",
            MapiNodeKind.Structure,
            offset,
            bytes.Length,
            null,
            children.ToImmutable());
    }

    public static MapiNode ParseMessageEntryId(
        ReadOnlySpan<byte> bytes,
        long offset,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        if (bytes.Length != MessageEntryIdSize)
        {
            throw new MapiParseException(
                checked((int)offset),
                $"Message EntryID is {bytes.Length:N0} bytes; {MessageEntryIdSize} are required.");
        }

        var reader = new MapiReader(bytes, baseOffset: checked((int)offset));
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddUInt32(ref reader, children, "Flags", budget, warnings, mustBeZero: true);
        AddGuid(ref reader, children, "ProviderUID", budget);
        AddStoreObjectType(ref reader, children, "MessageType", budget, warnings, folderType: false);
        AddGuid(ref reader, children, "FolderDatabaseGuid", budget);
        AddBytes(ref reader, children, "FolderGlobalCounter", 6, budget);
        AddUInt16(ref reader, children, "Pad1", budget, warnings, mustBeZero: true);
        AddGuid(ref reader, children, "MessageDatabaseGuid", budget);
        AddBytes(ref reader, children, "MessageGlobalCounter", 6, budget);
        AddUInt16(ref reader, children, "Pad2", budget, warnings, mustBeZero: true);
        return new MapiNode(
            "ReplyTemplateMessageEID",
            MapiNodeKind.Structure,
            offset,
            bytes.Length,
            null,
            children.ToImmutable());
    }

    public static MapiNode ParseAddressBookEntryId(
        ReadOnlySpan<byte> bytes,
        long offset,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        if (bytes.Length < MinimumAddressBookEntryIdSize)
        {
            throw new MapiParseException(
                checked((int)offset),
                $"AddressBookEntryID is {bytes.Length:N0} bytes; at least {MinimumAddressBookEntryIdSize} are required.");
        }
        ValidatePermanentShape(bytes, offset, "AddressBookEntryID", "X500DN");

        var localWarnings = new List<string>();
        var reader = new MapiReader(bytes, baseOffset: checked((int)offset));
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddUInt32(ref reader, children, "Flags", budget, localWarnings, requiredValue: 0);
        AddRequiredProviderGuid(ref reader, children, budget);
        AddUInt32(ref reader, children, "Version", budget, localWarnings, requiredValue: 1);
        AddDisplayType(ref reader, children, "Type", budget, localWarnings, addressBookType: true);
        AddNullTerminatedAscii(ref reader, children, "X500DN", budget);
        EnsureEnd(ref reader, "AddressBookEntryID");
        warnings?.AddRange(localWarnings);
        budget.Claim(0);
        return new MapiNode(
            "AddressBookEntryID",
            MapiNodeKind.Structure,
            offset,
            bytes.Length,
            null,
            children.ToImmutable());
    }

    public static MapiNode ParseNspiEntryId(
        ReadOnlySpan<byte> bytes,
        long offset,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        if (bytes.IsEmpty)
        {
            throw new MapiParseException(checked((int)offset), "NSPI EntryID is empty.");
        }

        var localWarnings = new List<string>();
        var node = bytes[0] switch
        {
            0x00 => ParsePermanentEntryId(bytes, offset, budget, localWarnings),
            0x87 => ParseEphemeralEntryId(bytes, offset, budget, localWarnings),
            _ => throw new MapiParseException(
                checked((int)offset),
                $"NSPI EntryID has unknown IDType 0x{bytes[0]:X2}."),
        };
        warnings?.AddRange(localWarnings);
        return node;
    }

    private static MapiNode ParsePermanentEntryId(
        ReadOnlySpan<byte> bytes,
        long offset,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        if (bytes.Length < MinimumAddressBookEntryIdSize)
        {
            throw new MapiParseException(
                checked((int)offset),
                $"PermanentEntryID is {bytes.Length:N0} bytes; at least {MinimumAddressBookEntryIdSize} are required.");
        }
        ValidatePermanentShape(bytes, offset, "PermanentEntryID", "DistinguishedName");

        var reader = new MapiReader(bytes, baseOffset: checked((int)offset));
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddByte(ref reader, children, "IDType", budget, warnings, requiredValue: 0);
        AddByte(ref reader, children, "R1", budget, warnings, requiredValue: 0);
        AddByte(ref reader, children, "R2", budget, warnings, requiredValue: 0);
        AddByte(ref reader, children, "R3", budget, warnings, requiredValue: 0);
        AddRequiredProviderGuid(ref reader, children, budget);
        AddUInt32(ref reader, children, "R4", budget, warnings, requiredValue: 1);
        AddDisplayType(ref reader, children, "DisplayType", budget, warnings, addressBookType: false);
        AddNullTerminatedAscii(ref reader, children, "DistinguishedName", budget);
        EnsureEnd(ref reader, "PermanentEntryID");
        budget.Claim(0);
        return new MapiNode(
            "PermanentEntryID",
            MapiNodeKind.Structure,
            offset,
            bytes.Length,
            null,
            children.ToImmutable());
    }

    private static MapiNode ParseEphemeralEntryId(
        ReadOnlySpan<byte> bytes,
        long offset,
        MapiNodeBudget budget,
        List<string>? warnings)
    {
        if (bytes.Length != EphemeralEntryIdSize)
        {
            throw new MapiParseException(
                checked((int)offset),
                $"EphemeralEntryID is {bytes.Length:N0} bytes; {EphemeralEntryIdSize} are required.");
        }

        var reader = new MapiReader(bytes, baseOffset: checked((int)offset));
        var children = ImmutableArray.CreateBuilder<MapiNode>();
        AddByte(ref reader, children, "Type", budget, warnings, requiredValue: 0x87);
        AddByte(ref reader, children, "R1", budget, warnings, requiredValue: 0);
        AddByte(ref reader, children, "R2", budget, warnings, requiredValue: 0);
        AddByte(ref reader, children, "R3", budget, warnings, requiredValue: 0);
        AddGuid(ref reader, children, "ProviderUID", budget);
        AddUInt32(ref reader, children, "R4", budget, warnings, requiredValue: 1);
        AddDisplayType(ref reader, children, "DisplayType", budget, warnings, addressBookType: false);
        var midOffset = reader.Position;
        var mid = reader.ReadUInt32("Mid");
        ExtendedBufferParser.AddField(
            children,
            "Mid",
            midOffset,
            4,
            $"0x{mid:X8} ({mid:N0})",
            budget);
        budget.Claim(0);
        return new MapiNode(
            "EphemeralEntryID",
            MapiNodeKind.Structure,
            offset,
            bytes.Length,
            null,
            children.ToImmutable());
    }

    private static void AddUInt16(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget,
        List<string>? warnings = null,
        bool mustBeZero = false)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        ExtendedBufferParser.AddField(
            children,
            name,
            offset,
            2,
            value.ToString(CultureInfo.InvariantCulture),
            budget);
        if (mustBeZero && value != 0)
        {
            warnings?.Add($"{name} in the EntryID is 0x{value:X4}; zero is required.");
        }
    }

    private static void AddUInt32(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget,
        List<string>? warnings = null,
        bool mustBeZero = false,
        uint? requiredValue = null)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        ExtendedBufferParser.AddField(
            children,
            name,
            offset,
            4,
            $"0x{value:X8}",
            budget);
        var expected = requiredValue ?? (mustBeZero ? 0u : null);
        if (expected.HasValue && value != expected.Value)
        {
            warnings?.Add(
                $"{name} in the EntryID is 0x{value:X8}; 0x{expected.Value:X8} is required.");
        }
    }

    private static void AddStoreObjectType(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget,
        List<string>? warnings,
        bool folderType)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt16(name);
        var semanticName = value switch
        {
            0x0001 => "PrivateFolder",
            0x0003 => "PublicFolder",
            0x0005 => "MappedPublicFolder",
            0x0007 => "PrivateMessage",
            0x0009 => "PublicMessage",
            0x000B => "MappedPublicMessage",
            0x000C => "PublicNewsgroupFolder",
            _ => "Unknown",
        };
        ExtendedBufferParser.AddField(
            children,
            name,
            offset,
            2,
            $"{semanticName} = 0x{value:X4}",
            budget);
        var validForField = folderType
            ? value is 0x0001 or 0x0003 or 0x0005 or 0x000C
            : value is 0x0007 or 0x0009 or 0x000B;
        if (!validForField)
        {
            warnings?.Add(
                semanticName == "Unknown"
                    ? $"{name} in the EntryID has unknown StoreObjectType 0x{value:X4}."
                    : $"{name} in the EntryID has StoreObjectType {semanticName} (0x{value:X4}), which is not valid for this field.");
        }
    }

    private static void AddByte(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget,
        List<string>? warnings,
        byte requiredValue)
    {
        var offset = reader.Position;
        var value = reader.ReadByte(name);
        ExtendedBufferParser.AddField(children, name, offset, 1, $"0x{value:X2}", budget);
        if (value != requiredValue)
        {
            warnings?.Add(
                $"{name} in the EntryID is 0x{value:X2}; 0x{requiredValue:X2} is required.");
        }
    }

    private static void AddDisplayType(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget,
        List<string>? warnings,
        bool addressBookType)
    {
        var offset = reader.Position;
        var value = reader.ReadUInt32(name);
        var semanticName = value switch
        {
            0x00000000 => addressBookType ? "LocalMailUser" : "DT_MAILUSER",
            0x00000001 => addressBookType ? "DistributionList" : "DT_DISTLIST",
            0x00000002 => addressBookType ? "BulletinBoardOrPublicFolder" : "DT_FORUM",
            0x00000003 => addressBookType ? "AutomatedMailbox" : "DT_AGENT",
            0x00000004 => addressBookType ? "OrganizationalMailbox" : "DT_ORGANIZATION",
            0x00000005 => addressBookType ? "PrivateDistributionList" : "DT_PRIVATE_DISTLIST",
            0x00000006 => addressBookType ? "RemoteMailUser" : "DT_REMOTE_MAILUSER",
            0x00000100 => addressBookType ? "Container" : "DT_CONTAINER",
            0x00000101 => addressBookType ? "Template" : "DT_TEMPLATE",
            0x00000102 => addressBookType ? "OneOffUser" : "DT_ADDRESS_TEMPLATE",
            0x00000200 => addressBookType ? "Search" : "DT_SEARCH",
            _ => "Unknown",
        };
        ExtendedBufferParser.AddField(
            children,
            name,
            offset,
            4,
            $"{semanticName} = 0x{value:X8}",
            budget);
        if (semanticName == "Unknown")
        {
            warnings?.Add($"{name} in the EntryID has unknown value 0x{value:X8}.");
        }
    }

    private static void AddRequiredProviderGuid(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadGuid("ProviderUID");
        ExtendedBufferParser.AddField(children, "ProviderUID", offset, 16, value.ToString(), budget);
        if (value != MuidEmsab)
        {
            throw new MapiParseException(
                offset,
                $"ProviderUID is {value}; required MUIDEMSAB is {MuidEmsab}.");
        }
    }

    private static void ValidatePermanentShape(
        ReadOnlySpan<byte> bytes,
        long offset,
        string structureName,
        string stringFieldName)
    {
        var provider = new Guid(bytes.Slice(4, 16));
        if (provider != MuidEmsab)
        {
            throw new MapiParseException(
                checked((int)offset + 4),
                $"ProviderUID is {provider}; required MUIDEMSAB is {MuidEmsab}.");
        }

        var stringBytes = bytes[28..];
        var terminator = stringBytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new MapiParseException(
                checked((int)offset + 28),
                $"{stringFieldName} is missing its null terminator.");
        }
        if (terminator != stringBytes.Length - 1)
        {
            throw new MapiParseException(
                checked((int)offset + 28 + terminator + 1),
                $"{structureName} has {stringBytes.Length - terminator - 1:N0} trailing byte(s) after {stringFieldName}.");
        }
        if (stringBytes[..terminator].IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
        {
            throw new MapiParseException(
                checked((int)offset + 28),
                $"{stringFieldName} contains non-ASCII bytes.");
        }
    }

    private static void AddNullTerminatedAscii(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var bytes = reader.ReadNullTerminatedBytes(name);
        if (bytes.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
        {
            throw new MapiParseException(offset, $"{name} contains non-ASCII bytes.");
        }
        ExtendedBufferParser.AddField(
            children,
            name,
            offset,
            bytes.Length + 1,
            Encoding.ASCII.GetString(bytes),
            budget);
    }

    private static void EnsureEnd(ref MapiReader reader, string name)
    {
        if (!reader.End)
        {
            throw new MapiParseException(
                reader.Position,
                $"{name} has {reader.Remaining:N0} trailing byte(s) after its final field.");
        }
    }

    private static void AddGuid(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        var value = reader.ReadGuid(name);
        ExtendedBufferParser.AddField(children, name, offset, 16, value.ToString(), budget);
    }

    private static void AddBytes(
        ref MapiReader reader,
        ImmutableArray<MapiNode>.Builder children,
        string name,
        int length,
        MapiNodeBudget budget)
    {
        var offset = reader.Position;
        children.Add(ExtendedBufferParser.RawNode(name, reader.ReadBytes(length, name), offset, budget));
    }
}
