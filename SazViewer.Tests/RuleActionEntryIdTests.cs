using System.Buffers.Binary;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class RuleActionEntryIdTests
{
    [Fact]
    public void DecodesExtendedMoveFolderEntryIdFields()
    {
        using var payload = new MemoryStream();
        WriteUInt32(payload, 0);
        WriteUInt32(payload, MapiEntryIdParser.FolderEntryIdSize);
        payload.Write(BuildFolderEntryId());
        var warnings = new List<string>();
        var node = ParseAction(0x01, payload.ToArray(), warnings);

        Assert.Empty(warnings);
        Assert.Equal("000102030405", Find(node, "GlobalCounter").Value);
        Assert.Equal("00000000-0000-0000-0000-000000000002", Find(node, "DatabaseGuid").Value);
        Assert.Equal("PrivateFolder = 0x0001", Find(node, "FolderType").Value);
        Assert.Equal(MapiNodeKind.Structure, Find(node, "FolderEID").Kind);
    }

    [Fact]
    public void DecodesExtendedReplyMessageEntryIdFields()
    {
        using var payload = new MemoryStream();
        WriteUInt32(payload, MapiEntryIdParser.MessageEntryIdSize);
        payload.Write(BuildMessageEntryId());
        payload.Write(Guid.Parse("00000000-0000-0000-0000-000000000005").ToByteArray());
        var warnings = new List<string>();
        var node = ParseAction(0x03, payload.ToArray(), warnings);

        Assert.Empty(warnings);
        Assert.Equal("101112131415", Find(node, "FolderGlobalCounter").Value);
        Assert.Equal("202122232425", Find(node, "MessageGlobalCounter").Value);
        Assert.Equal("PrivateMessage = 0x0007", Find(node, "MessageType").Value);
        Assert.Equal("00000000-0000-0000-0000-000000000005", Find(node, "ReplyTemplateGUID").Value);
        Assert.Equal(MapiNodeKind.Structure, Find(node, "ReplyTemplateMessageEID").Kind);
    }

    [Fact]
    public void RetainsWrongSizedEntryIdAsRawWithWarning()
    {
        using var payload = new MemoryStream();
        WriteUInt32(payload, 0);
        WriteUInt32(payload, 3);
        payload.Write([1, 2, 3]);
        var warnings = new List<string>();
        var node = ParseAction(0x02, payload.ToArray(), warnings);

        Assert.Contains(warnings, warning => warning.Contains("46 are required", StringComparison.Ordinal));
        Assert.Equal(MapiNodeKind.Raw, Find(node, "FolderEID").Kind);
    }

    [Fact]
    public void WarnsWhenStoreObjectTypeDoesNotMatchEntryIdKind()
    {
        var warnings = new List<string>();
        var node = MapiEntryIdParser.ParseFolderEntryId(
            BuildFolderEntryId(0x0007),
            0,
            new MapiNodeBudget(),
            warnings);

        Assert.Equal("PrivateMessage = 0x0007", Find(node, "FolderType").Value);
        Assert.Contains(warnings, warning =>
            warning.Contains("not valid for this field", StringComparison.Ordinal));
    }

    private static MapiNode ParseAction(byte type, byte[] payload, List<string> warnings)
    {
        using var data = new MemoryStream();
        WriteUInt32(data, 1);
        WriteUInt32(data, checked((uint)(9 + payload.Length)));
        data.WriteByte(type);
        WriteUInt32(data, 0);
        WriteUInt32(data, 0);
        data.Write(payload);
        var reader = new MapiReader(data.ToArray());

        var node = RuleActionParser.Parse(ref reader, new MapiNodeBudget(), 0, null, warnings);

        Assert.True(reader.End);
        return node;
    }

    private static byte[] BuildFolderEntryId(ushort folderType = 0x0001)
    {
        using var data = new MemoryStream();
        WriteUInt32(data, 0);
        data.Write(Guid.Parse("00000000-0000-0000-0000-000000000001").ToByteArray());
        WriteUInt16(data, folderType);
        data.Write(Guid.Parse("00000000-0000-0000-0000-000000000002").ToByteArray());
        data.Write([0, 1, 2, 3, 4, 5]);
        WriteUInt16(data, 0);
        return data.ToArray();
    }

    private static byte[] BuildMessageEntryId()
    {
        using var data = new MemoryStream();
        WriteUInt32(data, 0);
        data.Write(Guid.Parse("00000000-0000-0000-0000-000000000001").ToByteArray());
        WriteUInt16(data, 7);
        data.Write(Guid.Parse("00000000-0000-0000-0000-000000000002").ToByteArray());
        data.Write([0x10, 0x11, 0x12, 0x13, 0x14, 0x15]);
        WriteUInt16(data, 0);
        data.Write(Guid.Parse("00000000-0000-0000-0000-000000000003").ToByteArray());
        data.Write([0x20, 0x21, 0x22, 0x23, 0x24, 0x25]);
        WriteUInt16(data, 0);
        return data.ToArray();
    }

    private static MapiNode Find(MapiNode node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }
        foreach (var child in node.Children)
        {
            try
            {
                return Find(child, name);
            }
            catch (Xunit.Sdk.XunitException)
            {
            }
        }
        throw new Xunit.Sdk.XunitException($"Node '{name}' was not found.");
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
