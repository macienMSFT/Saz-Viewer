using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace SazViewer.App.Model;

/// <summary>Outcome of a bounded image decode: a frozen bitmap, or a user-facing error.</summary>
internal sealed record ImageDecodeResult(BitmapSource? Image, int Width, int Height, string? Error);

/// <summary>
/// Safe decoding for the Image tab. Only PNG, JPEG, GIF, WebP, BMP and ICO are accepted (never SVG or any
/// scriptable format); dimensions are read from the container header and checked against
/// <see cref="MaxDimension"/> / <see cref="MaxPixels"/> before any pixel data is decoded, so a small
/// compressed body cannot expand into a huge bitmap.
/// </summary>
internal static class ImageSafety
{
    public const int MaxDimension = 16384;
    public const long MaxPixels = 4096L * 4096L;

    public static readonly IReadOnlySet<string> AllowedMimeTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/x-icon"
    };

    /// <summary>Reads the pixel dimensions declared by the container header, or null when it is malformed.</summary>
    public static (int Width, int Height)? ReadDimensions(ReadOnlySpan<byte> bytes, string mime) => mime switch
    {
        "image/png" => Png(bytes),
        "image/gif" => bytes.Length >= 10 && bytes[..3].SequenceEqual("GIF"u8)
            ? (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]))
            : null,
        "image/bmp" => Bmp(bytes),
        "image/jpeg" => Jpeg(bytes),
        "image/webp" => WebP(bytes),
        "image/x-icon" => Ico(bytes),
        _ => null
    };

    /// <summary>Null when the dimensions are acceptable; otherwise why decoding is refused.</summary>
    public static string? CheckLimits(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "the image header declares empty dimensions";
        }
        if (width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
        {
            return $"the declared {width}\u00d7{height} pixel size exceeds the {MaxPixels:N0}-pixel safety limit";
        }
        return null;
    }

    /// <summary>Validates and decodes <paramref name="bytes"/>; safe to call off the UI thread.</summary>
    public static ImageDecodeResult Decode(ReadOnlyMemory<byte> bytes, string mime)
    {
        if (!AllowedMimeTypes.Contains(mime))
        {
            return Failed("image type is unsupported");
        }
        if (ReadDimensions(bytes.Span, mime) is not { } size)
        {
            return Failed("the image header is malformed");
        }
        if (CheckLimits(size.Width, size.Height) is { } limit)
        {
            return new ImageDecodeResult(null, size.Width, size.Height, limit);
        }
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return Failed("the image contains no frames");
            }
            // Animated GIF/WebP show their first frame; icons show their largest image.
            BitmapSource frame = mime == "image/x-icon"
                ? decoder.Frames.MaxBy(candidate => (long)candidate.PixelWidth * candidate.PixelHeight)!
                : decoder.Frames[0];
            if (CheckLimits(frame.PixelWidth, frame.PixelHeight) is { } decodedLimit)
            {
                return new ImageDecodeResult(null, frame.PixelWidth, frame.PixelHeight, decodedLimit);
            }
            frame.Freeze();
            return new ImageDecodeResult(frame, frame.PixelWidth, frame.PixelHeight, null);
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException
            or InvalidOperationException or ArgumentException or COMException or OverflowException)
        {
            return Failed("Windows could not decode the retained image bytes");
        }
    }

    private static ImageDecodeResult Failed(string error) => new(null, 0, 0, error);

    private static (int, int)? Png(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            && bytes[12..16].SequenceEqual("IHDR"u8)
            ? Clamp(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]))
            : null;

    private static (int, int)? Bmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || bytes[0] != 'B' || bytes[1] != 'M')
        {
            return null;
        }
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        if (headerSize == 12)
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]));
        }
        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
        return width <= 0 || height == int.MinValue ? null : (width, Math.Abs(height));
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }
        var position = 2;
        while (position + 4 <= bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                return null;
            }
            var marker = bytes[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }
            if (marker is 0xD9 or 0xDA)
            {
                return null;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 2)..]);
            if (length < 2)
            {
                return null;
            }
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (position + 9 > bytes.Length)
                {
                    return null;
                }
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]));
            }
            position += 2 + length;
        }
        return null;
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return null;
        }
        var chunk = bytes[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
        {
            return bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A
                ? (BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF)
                : null;
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (bytes[20] != 0x2F)
            {
                return null;
            }
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8X"u8))
        {
            var width = bytes[24] | (bytes[25] << 8) | (bytes[26] << 16);
            var height = bytes[27] | (bytes[28] << 8) | (bytes[29] << 16);
            return (width + 1, height + 1);
        }
        return null;
    }

    /// <summary>The largest directory entry; embedded PNG entries use their own IHDR dimensions.</summary>
    private static (int, int)? Ico(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 1)
        {
            return null;
        }
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (count == 0 || 6 + (16 * count) > bytes.Length)
        {
            return null;
        }
        (int Width, int Height)? largest = null;
        for (var index = 0; index < count; index++)
        {
            var entry = bytes.Slice(6 + (16 * index), 16);
            (int Width, int Height)? size = (entry[0] == 0 ? 256 : entry[0], entry[1] == 0 ? 256 : entry[1]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (offset < bytes.Length && bytes[(int)offset..] is { Length: >= 8 } data && data[0] == 0x89 && data[1] == 0x50)
            {
                size = Png(data);
                if (size is null)
                {
                    return null;
                }
            }
            if (largest is null || (long)size.Value.Width * size.Value.Height > (long)largest.Value.Width * largest.Value.Height)
            {
                largest = size;
            }
        }
        return largest;
    }

    private static (int, int)? Clamp(uint width, uint height) =>
        width > int.MaxValue || height > int.MaxValue ? (int.MaxValue, int.MaxValue) : ((int)width, (int)height);
}
