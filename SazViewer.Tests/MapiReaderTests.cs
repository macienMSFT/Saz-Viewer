using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class MapiReaderTests
{
    [Fact]
    public void ReadsLittleEndianPrimitivesStringsAndAlignment()
    {
        byte[] bytes =
        [
            0x7F,
            0x34, 0x12,
            0x78, 0x56, 0x34, 0x12,
            (byte)'o', (byte)'k', 0,
            0x00, 0x00,
            (byte)'A', 0, 0, 0
        ];
        var reader = new MapiReader(bytes);

        Assert.Equal(0x7F, reader.ReadByte("byte"));
        Assert.Equal(0x1234, reader.ReadUInt16("word"));
        Assert.Equal(0x12345678u, reader.ReadUInt32("dword"));
        Assert.Equal("ok", reader.ReadNullTerminatedAscii("ascii"));
        reader.Align(4, "padding");
        Assert.Equal("A", reader.ReadNullTerminatedUnicode("unicode"));
        Assert.True(reader.End);
    }

    [Fact]
    public void RejectsTruncatedAndOversizedReadsWithOffsets()
    {
        var truncated = Assert.Throws<MapiParseException>(ReadTruncatedUInt32);

        Assert.Equal(0, truncated.Offset);
        Assert.Contains("only 2 remain", truncated.Message, StringComparison.Ordinal);
        Assert.Throws<MapiParseException>(ReadOversizedBlob);
    }

    [Fact]
    public void EnforcesPayloadAndNodeBudgets()
    {
        Assert.Throws<MapiParseException>(
            () => new MapiReader(new byte[MapiParseLimits.MaxPayloadBytes + 1]));

        var budget = new MapiNodeBudget();
        Assert.Throws<MapiParseException>(() => budget.Claim(MapiParseLimits.MaxDepth + 1));
        Assert.Throws<MapiParseException>(() => budget.Claim(0, MapiParseLimits.MaxCollectionCount + 1));
    }

    [Fact]
    public void HonorsCancellationBeforeReading()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ReadCancelled(cancellation.Token));
    }

    [Fact]
    public void PreservesAbsoluteOffsetsAcrossBoundedSlices()
    {
        var reader = new MapiReader(new byte[] { 1, 2, 3, 4 });
        reader.Skip(1, "prefix");
        var nested = reader.SliceReader(2, "nested");

        Assert.Equal(1, nested.Position);
        Assert.Equal(2, nested.ReadByte("value"));
        Assert.Equal(2, nested.Position);
    }

    private static void ReadTruncatedUInt32()
    {
        var reader = new MapiReader(new byte[] { 1, 2 });
        reader.ReadUInt32("value");
    }

    private static void ReadOversizedBlob()
    {
        var reader = new MapiReader([]);
        reader.ReadBytes(MapiParseLimits.MaxPayloadBytes + 1, "blob");
    }

    private static void ReadCancelled(CancellationToken cancellationToken)
    {
        var reader = new MapiReader(new byte[] { 1 }, cancellationToken);
        reader.ReadByte("cancelled");
    }
}
