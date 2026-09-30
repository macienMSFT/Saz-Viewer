using System.Buffers.Binary;
using System.Text;

namespace SazViewer.Core;

internal ref struct MapiReader
{
    private readonly ReadOnlySpan<byte> data;
    private readonly CancellationToken cancellationToken;
    private readonly int baseOffset;
    private int position;

    public MapiReader(
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken = default,
        int baseOffset = 0)
    {
        if (data.Length > MapiParseLimits.MaxPayloadBytes)
        {
            throw new MapiParseException(
                0,
                $"MAPI payload is {data.Length:N0} bytes; the limit is {MapiParseLimits.MaxPayloadBytes:N0} bytes.");
        }
        this.data = data;
        this.cancellationToken = cancellationToken;
        this.baseOffset = baseOffset;
    }

    public int Position => checked(baseOffset + position);
    public int LocalPosition => position;
    public int Remaining => data.Length - position;
    public bool End => position == data.Length;

    public byte PeekByte(string field)
    {
        Ensure(1, field);
        return data[position];
    }

    public byte ReadByte(string field)
    {
        Ensure(1, field);
        return data[position++];
    }

    public ushort ReadUInt16(string field)
    {
        Ensure(sizeof(ushort), field);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
        position += sizeof(ushort);
        return value;
    }

    public short ReadInt16(string field) => unchecked((short)ReadUInt16(field));

    public uint ReadUInt32(string field)
    {
        Ensure(sizeof(uint), field);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        position += sizeof(uint);
        return value;
    }

    public int ReadInt32(string field) => unchecked((int)ReadUInt32(field));

    public long ReadInt64(string field) => unchecked((long)ReadUInt64(field));

    public ulong ReadUInt64(string field)
    {
        Ensure(sizeof(ulong), field);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data[position..]);
        position += sizeof(ulong);
        return value;
    }

    public float ReadSingle(string field)
    {
        var bits = ReadInt32(field);
        return BitConverter.Int32BitsToSingle(bits);
    }

    public double ReadDouble(string field)
    {
        var bits = ReadInt64(field);
        return BitConverter.Int64BitsToDouble(bits);
    }

    public Guid ReadGuid(string field)
    {
        Ensure(16, field);
        var value = new Guid(data.Slice(position, 16));
        position += 16;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count, string field)
    {
        ValidateCount(count, field);
        Ensure(count, field);
        var value = data.Slice(position, count);
        position += count;
        return value;
    }

    public ReadOnlySpan<byte> ReadRemaining(string field) => ReadBytes(Remaining, field);

    public string ReadAscii(int byteCount, string field)
    {
        ValidateStringLength(byteCount, field);
        return Encoding.ASCII.GetString(ReadBytes(byteCount, field));
    }

    public string ReadUtf8(int byteCount, string field)
    {
        ValidateStringLength(byteCount, field);
        try
        {
            return new UTF8Encoding(false, true).GetString(ReadBytes(byteCount, field));
        }
        catch (DecoderFallbackException exception)
        {
            throw new MapiParseException(position - byteCount, $"{field} is not valid UTF-8.", exception);
        }
    }

    public string ReadUnicode(int byteCount, string field)
    {
        ValidateStringLength(byteCount, field);
        if ((byteCount & 1) != 0)
        {
            throw new MapiParseException(position, $"{field} has odd UTF-16LE byte length {byteCount}.");
        }
        try
        {
            return new UnicodeEncoding(false, false, true).GetString(ReadBytes(byteCount, field));
        }
        catch (DecoderFallbackException exception)
        {
            throw new MapiParseException(position - byteCount, $"{field} is not valid UTF-16LE.", exception);
        }
    }

    public string ReadNullTerminatedAscii(string field)
    {
        return Encoding.ASCII.GetString(ReadNullTerminatedBytes(field));
    }

    public ReadOnlySpan<byte> ReadNullTerminatedBytes(string field)
    {
        var start = position;
        var terminator = data[position..].IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new MapiParseException(Position, $"{field} is missing its null terminator.");
        }
        ValidateStringLength(terminator, field);
        var value = ReadBytes(terminator, field);
        position++;
        return value;
    }

    public string ReadNullTerminatedUnicode(string field)
    {
        var start = position;
        var scan = position;
        while (scan + 1 < data.Length)
        {
            if (data[scan] == 0 && data[scan + 1] == 0)
            {
                try
                {
                    var value = new UnicodeEncoding(false, false, true)
                        .GetString(data.Slice(start, scan - start));
                    position = scan + 2;
                    return value;
                }
                catch (DecoderFallbackException exception)
                {
                    throw new MapiParseException(
                        checked(baseOffset + start),
                        $"{field} is not valid UTF-16LE.",
                        exception);
                }
            }
            scan = checked(scan + 2);
            if (scan - start > MapiParseLimits.MaxStringBytes)
            {
                throw new MapiParseException(
                    checked(baseOffset + start),
                    $"{field} exceeds the string limit.");
            }
        }
        throw new MapiParseException(
            checked(baseOffset + start),
            $"{field} is missing its UTF-16 null terminator.");
    }

    public void Align(int alignment, string field)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment));
        }
        var aligned = checked((position + alignment - 1) & ~(alignment - 1));
        Skip(aligned - position, field);
    }

    public void Skip(int count, string field)
    {
        _ = ReadBytes(count, field);
    }

    public MapiReader SliceReader(int count, string field)
    {
        var start = Position;
        var bytes = ReadBytes(count, field);
        return new MapiReader(bytes, cancellationToken, start);
    }

    public int ReadCount16(string field)
    {
        var value = ReadUInt16(field);
        return ValidateCollectionCount(value, field);
    }

    public int ReadCount32(string field)
    {
        var offset = Position;
        var value = ReadUInt32(field);
        if (value > int.MaxValue)
        {
            throw new MapiParseException(offset, $"{field} count {value:N0} exceeds the safe range.");
        }
        return ValidateCollectionCount((int)value, field);
    }

    private void Ensure(int count, string field)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count < 0 || count > Remaining)
        {
            throw new MapiParseException(
                Position,
                $"{field} needs {count:N0} bytes at offset {Position:N0}, but only {Remaining:N0} remain.");
        }
    }

    private static void ValidateCount(int count, string field)
    {
        if (count < 0 || count > MapiParseLimits.MaxPayloadBytes)
        {
            throw new MapiParseException(0, $"{field} count {count:N0} is outside the safe range.");
        }
    }

    private static void ValidateStringLength(int count, string field)
    {
        if (count < 0 || count > MapiParseLimits.MaxStringBytes)
        {
            throw new MapiParseException(0, $"{field} string length {count:N0} exceeds the safe limit.");
        }
    }

    private static int ValidateCollectionCount(int count, string field)
    {
        if (count > MapiParseLimits.MaxCollectionCount)
        {
            throw new MapiParseException(0, $"{field} count {count:N0} exceeds the safe limit.");
        }
        return count;
    }
}

internal sealed class MapiParseException : IOException
{
    public MapiParseException(long offset, string message, Exception? innerException = null)
        : base($"Offset {offset:N0}: {message}", innerException)
    {
        Offset = offset;
    }

    public long Offset { get; }
}

internal sealed class MapiNodeBudget
{
    private int nodes;

    public void Claim(int depth, int count = 1)
    {
        if (depth > MapiParseLimits.MaxDepth)
        {
            throw new MapiParseException(0, $"Protocol tree depth exceeds {MapiParseLimits.MaxDepth}.");
        }
        if (count < 0 || count > MapiParseLimits.MaxCollectionCount)
        {
            throw new MapiParseException(0, $"Collection count {count:N0} exceeds the safe limit.");
        }
        nodes = checked(nodes + count);
        if (nodes > MapiParseLimits.MaxNodes)
        {
            throw new MapiParseException(0, $"Protocol tree exceeds {MapiParseLimits.MaxNodes:N0} nodes.");
        }
    }
}
