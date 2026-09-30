using System.Collections.Immutable;
using System.Globalization;

namespace SazViewer.Core;

internal static class MapiEntryIdParser
{
    public const int FolderEntryIdSize = 46;
    public const int MessageEntryIdSize = 70;

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
        AddUInt16(ref reader, children, "FolderType", budget);
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
        AddUInt16(ref reader, children, "MessageType", budget);
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
        bool mustBeZero = false)
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
        if (mustBeZero && value != 0)
        {
            warnings?.Add($"{name} in the EntryID is 0x{value:X8}; zero is required.");
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
