using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace SazViewer.Core;

internal static class HttpBodyDecoder
{
    private const int MaxCodingLayers = 8;
    private const int MaxDecodedBytes = 4 * 1024 * 1024;
    private const int MinimumDecodedLimit = 1024 * 1024;
    private const int MaxExpansionRatio = 100;
    private const int MaxChunkLineBytes = 8 * 1024;
    private const int MaxTrailerBytes = 64 * 1024;
    private const int MaxTrailerCount = 100;
    private const int MaxGzipMembers = 128;
    private const int CapturedBytesPreviewLimit = 16 * 1024;

    static HttpBodyDecoder()
    {
        AppContext.SetSwitch("System.IO.Compression.UseStrictValidation", true);
    }

    public static BodyPreview CreatePreview(
        ReadOnlySpan<byte> availableBody,
        long capturedLength,
        IReadOnlyList<HttpHeader> headers,
        string label,
        List<string> warnings)
    {
        var contentType = HeaderValues(headers, "Content-Type").FirstOrDefault();
        if (capturedLength == 0)
        {
            return HttpMessageParser.CreateBodyPreview([], 0, 0, contentType);
        }
        var transferResult = ParseCodings(headers, "Transfer-Encoding", allowParameters: true);
        var contentResult = ParseCodings(headers, "Content-Encoding", allowParameters: false);
        var declaredCodings = transferResult.Codings.Where(coding => coding != "identity")
            .Concat(contentResult.Codings.Where(coding => coding != "identity"))
            .ToList();

        if (transferResult.Error is not null || contentResult.Error is not null)
        {
            return Failure(
                availableBody,
                capturedLength,
                $"{transferResult.Error ?? contentResult.Error} Captured bytes are shown as hex.",
                label,
                warnings);
        }

        if (declaredCodings.Count == 0)
        {
            return HttpMessageParser.CreateBodyPreview(
                availableBody[..Math.Min(availableBody.Length, HttpMessageParser.MaxBodyPreview)],
                capturedLength,
                capturedLength,
                contentType);
        }

        var totalCodingCount = transferResult.Codings.Count + contentResult.Codings.Count;
        if (totalCodingCount > MaxCodingLayers)
        {
            return Failure(
                availableBody,
                capturedLength,
                $"Body declares {totalCodingCount} coding layers; the safe limit is {MaxCodingLayers}. Captured bytes are shown as hex.",
                label,
                warnings);
        }

        if (capturedLength != availableBody.Length)
        {
            return Failure(
                availableBody,
                capturedLength,
                $"Encoded body is incomplete in the bounded parser window ({availableBody.Length:N0} of {capturedLength:N0} bytes). Captured bytes are shown as hex.",
                label,
                warnings);
        }

        var declaredTransferCodings = transferResult.Codings;
        var chunkedIndex = declaredTransferCodings.FindIndex(coding => coding == "chunked");
        if (chunkedIndex >= 0 && (chunkedIndex != declaredTransferCodings.Count - 1
            || declaredTransferCodings.Count(coding => coding == "chunked") != 1))
        {
            return Failure(
                availableBody,
                capturedLength,
                "Transfer-Encoding chunked is duplicated or is not the final transfer coding. Captured bytes are shown as hex.",
                label,
                warnings);
        }
        var transferCodings = declaredTransferCodings.Where(coding => coding != "identity").ToList();

        var original = availableBody.ToArray();
        var current = original;
        var removed = new List<string>();

        for (var index = transferCodings.Count - 1; index >= 0; index--)
        {
            var coding = transferCodings[index];
            if (coding == "chunked")
            {
                if (!TryDecodeChunked(current, out var decoded, out var trailerCount, out var error))
                {
                    return Failure(
                        original,
                        capturedLength,
                        $"Transfer-Encoding chunked could not be decoded ({error}); the body may be malformed, incomplete, or already normalized by the SAZ producer. Captured bytes are shown as hex.",
                        label,
                        warnings);
                }
                current = decoded;
                removed.Add(trailerCount == 0 ? "transfer: chunked" : $"transfer: chunked ({trailerCount} trailers)");
                continue;
            }

            if (!TryDecodeCompression(current, coding, out var decodedCompression, out var usedCoding, out var compressionError))
            {
                return Failure(
                    original,
                    capturedLength,
                    $"Transfer-Encoding '{coding}' could not be decoded ({compressionError}). Captured bytes are shown as hex.",
                    label,
                    warnings);
            }
            current = decodedCompression;
            removed.Add($"transfer: {usedCoding}");
        }

        for (var index = contentResult.Codings.Count - 1; index >= 0; index--)
        {
            var coding = contentResult.Codings[index];
            if (coding == "identity")
            {
                continue;
            }
            if (!TryDecodeCompression(current, coding, out var decoded, out var usedCoding, out var error))
            {
                return Failure(
                    original,
                    capturedLength,
                    $"Content-Encoding '{coding}' could not be decoded ({error}). Captured bytes are shown as hex.",
                    label,
                    warnings);
            }
            current = decoded;
            removed.Add($"content: {usedCoding}");
        }

        var preview = HttpMessageParser.CreateBodyPreview(
            current.AsSpan(0, Math.Min(current.Length, HttpMessageParser.MaxBodyPreview)),
            current.Length,
            capturedLength,
            contentType);
        return new BodyPreview
        {
            Length = preview.Length,
            CapturedLength = capturedLength,
            IsBinary = preview.IsBinary,
            IsTruncated = preview.IsTruncated,
            Charset = preview.Charset,
            Preview = preview.Preview,
            CapturedBytesPreview = HttpMessageParser.HexPreview(
                original.AsSpan(0, Math.Min(original.Length, CapturedBytesPreviewLimit))),
            CapturedBytesPreviewTruncated = original.Length > CapturedBytesPreviewLimit,
            RemovedEncodings = removed,
            DecodingStatus = $"Decoded in wire-removal order: {string.Join(" -> ", removed)}."
        };
    }

    private static BodyPreview Failure(
        ReadOnlySpan<byte> availableBody,
        long capturedLength,
        string status,
        string label,
        List<string> warnings)
    {
        warnings.Add($"{label}: {status}");
        var previewLength = Math.Min(availableBody.Length, CapturedBytesPreviewLimit);
        return new BodyPreview
        {
            Length = capturedLength,
            CapturedLength = capturedLength,
            IsBinary = true,
            IsTruncated = capturedLength > previewLength,
            Preview = HttpMessageParser.HexPreview(availableBody[..previewLength]),
            DecodingStatus = status
        };
    }

    private static CodingParseResult ParseCodings(
        IReadOnlyList<HttpHeader> headers,
        string headerName,
        bool allowParameters)
    {
        var codings = new List<string>();
        foreach (var value in HeaderValues(headers, headerName))
        {
            if (!TrySplitHttpList(value, out var items, out var listError))
            {
                return new CodingParseResult([], $"{headerName} {listError}");
            }
            foreach (var item in items)
            {
                var token = item.Trim();
                if (token.Length == 0)
                {
                    return new CodingParseResult([], $"{headerName} contains an empty coding token.");
                }

                var semicolon = token.IndexOf(';');
                if (semicolon >= 0)
                {
                    if (!allowParameters)
                    {
                        return new CodingParseResult([], $"{headerName} coding '{token}' has unsupported parameters.");
                    }
                    if (!ValidateTransferParameters(token.AsSpan(semicolon), out var parameterError))
                    {
                        return new CodingParseResult([], $"{headerName} coding '{token}' has {parameterError}.");
                    }
                    token = token[..semicolon].Trim();
                }

                if (token.Length == 0 || token.Any(character => !IsTokenCharacter(character)))
                {
                    return new CodingParseResult([], $"{headerName} contains invalid coding token '{item.Trim()}'.");
                }
                codings.Add(token.ToLowerInvariant());
            }
        }
        return new CodingParseResult(codings, null);
    }

    private static bool TrySplitHttpList(string value, out List<string> items, out string error)
    {
        items = [];
        var start = 0;
        var quoted = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quoted)
            {
                if (character == '"' && !escaped)
                {
                    quoted = false;
                }
                escaped = character == '\\' && !escaped;
                if (character != '\\')
                {
                    escaped = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                items.Add(value[start..index]);
                start = index + 1;
            }
        }
        if (quoted || escaped)
        {
            error = "contains an unterminated quoted string.";
            return false;
        }
        items.Add(value[start..]);
        error = string.Empty;
        return true;
    }

    private static bool ValidateTransferParameters(ReadOnlySpan<char> parameters, out string error)
    {
        var offset = 0;
        while (offset < parameters.Length)
        {
            if (parameters[offset++] != ';')
            {
                error = "invalid transfer parameter delimiter";
                return false;
            }
            SkipOptionalWhitespace(parameters, ref offset);
            if (!ReadToken(parameters, ref offset))
            {
                error = "an invalid transfer parameter name";
                return false;
            }
            SkipOptionalWhitespace(parameters, ref offset);
            if (offset >= parameters.Length || parameters[offset++] != '=')
            {
                error = "a transfer parameter without a value";
                return false;
            }
            SkipOptionalWhitespace(parameters, ref offset);
            if (!ReadTokenOrQuotedString(parameters, ref offset))
            {
                error = "an invalid transfer parameter value";
                return false;
            }
            SkipOptionalWhitespace(parameters, ref offset);
        }
        error = string.Empty;
        return true;
    }

    private static bool ReadTokenOrQuotedString(ReadOnlySpan<char> value, ref int offset)
    {
        if (offset >= value.Length)
        {
            return false;
        }
        if (value[offset] != '"')
        {
            return ReadToken(value, ref offset);
        }

        offset++;
        while (offset < value.Length)
        {
            var character = value[offset++];
            if (character == '"')
            {
                return true;
            }
            if (character == '\\')
            {
                if (offset >= value.Length || !IsQuotedCharacter(value[offset++]))
                {
                    return false;
                }
            }
            else if (!IsQuotedCharacter(character))
            {
                return false;
            }
        }
        return false;
    }

    private static bool ReadToken(ReadOnlySpan<char> value, ref int offset)
    {
        var start = offset;
        while (offset < value.Length && IsTokenCharacter(value[offset]))
        {
            offset++;
        }
        return offset > start;
    }

    private static void SkipOptionalWhitespace(ReadOnlySpan<char> value, ref int offset)
    {
        while (offset < value.Length && value[offset] is ' ' or '\t')
        {
            offset++;
        }
    }

    private static bool IsQuotedCharacter(char character) =>
        character is '\t' or ' '
        || character is >= '\x21' and <= '\x7E'
        || character >= '\x80';

    private static IEnumerable<string> HeaderValues(IReadOnlyList<HttpHeader> headers, string name) =>
        headers.Where(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value);

    private static bool IsTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static bool TryDecodeCompression(
        byte[] input,
        string coding,
        out byte[] output,
        out string usedCoding,
        out string error)
    {
        usedCoding = coding;
        if (coding == "gzip")
        {
            return TryDecompressGzipMembers(input, out output, out error);
        }
        if (coding == "br")
        {
            return TryDecompressBrotli(input, out output, out error);
        }
        if (coding == "deflate")
        {
            var zlib = TryDecompress(
                input,
                stream => new ZLibStream(stream, CompressionMode.Decompress),
                ValidateZlib,
                out output,
                out var zlibError,
                out var limitExceeded);
            if (zlib)
            {
                error = string.Empty;
                return true;
            }
            if (limitExceeded)
            {
                error = zlibError;
                return false;
            }

            var raw = TryDecompressRawDeflate(input, out output, out var rawError);
            if (raw)
            {
                usedCoding = "deflate (raw variant)";
                error = string.Empty;
                return true;
            }

            error = $"neither zlib-wrapped nor raw DEFLATE was valid; zlib: {zlibError}; raw: {rawError}";
            return false;
        }

        output = [];
        error = $"unsupported coding '{coding}'";
        return false;
    }

    private static bool TryDecompressRawDeflate(byte[] input, out byte[] output, out string error)
    {
        var limit = OutputLimit(input.Length);
        try
        {
            using var source = new MemoryStream(input, writable: false);
            using var exactSource = new SingleByteReadStream(source);
            using var decoder = new DeflateStream(exactSource, CompressionMode.Decompress);
            using var destination = new MemoryStream(Math.Min(limit, 64 * 1024));
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = decoder.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (destination.Length + read > limit)
                {
                    output = [];
                    error = $"decoded output exceeded the {limit:N0}-byte safety limit";
                    return false;
                }
                destination.Write(buffer, 0, read);
            }
            if (source.Position != source.Length)
            {
                output = [];
                error = $"raw DEFLATE stream has {source.Length - source.Position:N0} unexpected trailing bytes";
                return false;
            }
            output = destination.ToArray();
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            output = [];
            error = $"corrupt or truncated raw DEFLATE data ({exception.Message})";
            return false;
        }
    }

    private static bool TryDecompress(
        byte[] input,
        Func<Stream, Stream> createDecoder,
        Func<byte[], byte[], string?>? validateContainer,
        out byte[] output,
        out string error,
        out bool limitExceeded)
    {
        limitExceeded = false;
        var limit = OutputLimit(input.Length);
        try
        {
            using var source = new MemoryStream(input, writable: false);
            using var decoder = createDecoder(source);
            using var destination = new MemoryStream(Math.Min(limit, 64 * 1024));
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = decoder.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (destination.Length + read > limit)
                {
                    output = [];
                    error = $"decoded output exceeded the {limit:N0}-byte safety limit";
                    limitExceeded = true;
                    return false;
                }
                destination.Write(buffer, 0, read);
            }
            output = destination.ToArray();
            var validationError = validateContainer?.Invoke(input, output);
            if (validationError is not null)
            {
                output = [];
                error = validationError;
                return false;
            }
            if (validateContainer is not null
                && !HasExactCompressedExtent(input, output, createDecoder))
            {
                output = [];
                error = "compressed stream has unexpected trailing data";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            output = [];
            error = $"corrupt or truncated data ({exception.Message})";
            return false;
        }
    }

    private static bool HasExactCompressedExtent(
        byte[] input,
        byte[] expectedOutput,
        Func<Stream, Stream> createDecoder)
    {
        var low = 1;
        var high = input.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (PrefixDecodesTo(input, middle, expectedOutput, createDecoder))
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        return low == input.Length;
    }

    private static bool PrefixDecodesTo(
        byte[] input,
        int length,
        byte[] expectedOutput,
        Func<Stream, Stream> createDecoder)
    {
        try
        {
            using var source = new MemoryStream(input, 0, length, writable: false);
            using var decoder = createDecoder(source);
            var buffer = new byte[16 * 1024];
            var outputOffset = 0;
            while (true)
            {
                var read = decoder.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return outputOffset == expectedOutput.Length;
                }
                if (outputOffset + read > expectedOutput.Length
                    || !buffer.AsSpan(0, read).SequenceEqual(expectedOutput.AsSpan(outputOffset, read)))
                {
                    return false;
                }
                outputOffset += read;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return false;
        }
    }

    private static bool TryDecompressBrotli(byte[] input, out byte[] output, out string error)
    {
        var limit = OutputLimit(input.Length);
        var destination = new byte[limit + 1];
        var decoder = new BrotliDecoder();
        try
        {
            var status = decoder.Decompress(input, destination, out var consumed, out var written);
            if (status == OperationStatus.DestinationTooSmall)
            {
                output = [];
                error = $"decoded output exceeded the {limit:N0}-byte safety limit";
                return false;
            }
            if (status == OperationStatus.NeedMoreData)
            {
                output = [];
                error = "corrupt or truncated Brotli data";
                return false;
            }
            if (status == OperationStatus.InvalidData)
            {
                output = [];
                error = "invalid Brotli data";
                return false;
            }
            if (consumed != input.Length)
            {
                output = [];
                error = $"Brotli stream has {input.Length - consumed:N0} unexpected trailing bytes";
                return false;
            }
            output = destination.AsSpan(0, written).ToArray();
            error = string.Empty;
            return true;
        }
        finally
        {
            decoder.Dispose();
        }
    }

    private static bool TryDecompressGzipMembers(byte[] input, out byte[] output, out string error)
    {
        var limit = OutputLimit(input.Length);
        using var destination = new MemoryStream(Math.Min(limit, 64 * 1024));
        var offset = 0;
        var memberCount = 0;
        var buffer = new byte[16 * 1024];
        try
        {
            while (offset < input.Length)
            {
                if (memberCount >= MaxGzipMembers)
                {
                    output = [];
                    error = $"gzip content exceeds the {MaxGzipMembers}-member safety limit";
                    return false;
                }
                if (!TryReadGzipHeader(input, ref offset, out error))
                {
                    output = [];
                    return false;
                }

                using var source = new MemoryStream(input, writable: false);
                source.Position = offset;
                using var exactSource = new SingleByteReadStream(source);
                using var decoder = new DeflateStream(exactSource, CompressionMode.Decompress, leaveOpen: true);
                var crc = uint.MaxValue;
                uint memberSize = 0;
                while (true)
                {
                    var read = decoder.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }
                    if (destination.Length + read > limit)
                    {
                        output = [];
                        error = $"decoded output exceeded the {limit:N0}-byte safety limit";
                        return false;
                    }
                    UpdateCrc32(ref crc, buffer.AsSpan(0, read));
                    memberSize = unchecked(memberSize + (uint)read);
                    destination.Write(buffer, 0, read);
                }

                offset = checked((int)source.Position);
                if (offset + 8 > input.Length)
                {
                    output = [];
                    error = "gzip member is missing its checksum footer";
                    return false;
                }
                var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset, 4));
                var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset + 4, 4));
                if (expectedCrc != ~crc || expectedSize != memberSize)
                {
                    output = [];
                    error = "gzip member checksum or size does not match";
                    return false;
                }
                offset += 8;
                memberCount++;
            }

            if (memberCount == 0)
            {
                output = [];
                error = "gzip content contains no members";
                return false;
            }
            output = destination.ToArray();
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            output = [];
            error = $"corrupt or truncated gzip data ({exception.Message})";
            return false;
        }
    }

    private static bool TryReadGzipHeader(byte[] input, ref int offset, out string error)
    {
        var headerStart = offset;
        if (offset + 10 > input.Length
            || input[offset] != 0x1F
            || input[offset + 1] != 0x8B
            || input[offset + 2] != 8)
        {
            error = "invalid gzip member header or unexpected trailing data";
            return false;
        }
        var flags = input[offset + 3];
        if ((flags & 0xE0) != 0)
        {
            error = "gzip member uses reserved header flags";
            return false;
        }
        offset += 10;
        if ((flags & 0x04) != 0)
        {
            if (offset + 2 > input.Length)
            {
                error = "gzip extra-field length is truncated";
                return false;
            }
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(offset, 2));
            offset += 2 + extraLength;
            if (offset > input.Length)
            {
                error = "gzip extra field is truncated";
                return false;
            }
        }
        if ((flags & 0x08) != 0 && !TrySkipZeroTerminated(input, ref offset))
        {
            error = "gzip file name is unterminated";
            return false;
        }
        if ((flags & 0x10) != 0 && !TrySkipZeroTerminated(input, ref offset))
        {
            error = "gzip comment is unterminated";
            return false;
        }
        if ((flags & 0x02) != 0)
        {
            if (offset + 2 > input.Length)
            {
                error = "gzip header checksum is truncated";
                return false;
            }
            var expected = BinaryPrimitives.ReadUInt16LittleEndian(input.AsSpan(offset, 2));
            if ((ushort)ComputeCrc32(input.AsSpan(headerStart, offset - headerStart)) != expected)
            {
                error = "gzip header checksum does not match";
                return false;
            }
            offset += 2;
        }
        error = string.Empty;
        return offset <= input.Length - 8;
    }

    private static bool TrySkipZeroTerminated(byte[] input, ref int offset)
    {
        while (offset < input.Length)
        {
            if (input[offset++] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string? ValidateZlib(byte[] input, byte[] output)
    {
        if (input.Length < 6)
        {
            return "zlib stream is too short";
        }
        if (!HasValidZlibHeader(input))
        {
            return "invalid or unsupported zlib container header";
        }
        var expected = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(input.Length - 4));
        return expected == ComputeAdler32(output)
            ? null
            : "zlib checksum mismatch or unexpected trailing data";
    }

    private static bool HasValidZlibHeader(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return false;
        }
        var header = (input[0] << 8) | input[1];
        return (input[0] & 0x0F) == 8
            && (input[0] >> 4) <= 7
            && header % 31 == 0
            && (input[1] & 0x20) == 0;
    }

    private static uint ComputeAdler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint first = 1;
        uint second = 0;
        foreach (var value in data)
        {
            first = (first + value) % modulus;
            second = (second + first) % modulus;
        }
        return (second << 16) | first;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        UpdateCrc32(ref crc, data);
        return ~crc;
    }

    private static void UpdateCrc32(ref uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }
    }

    private static int OutputLimit(int inputLength)
    {
        var ratioLimit = Math.Max(MinimumDecodedLimit, (long)inputLength * MaxExpansionRatio);
        return (int)Math.Min(MaxDecodedBytes, ratioLimit);
    }

    private static bool TryDecodeChunked(
        byte[] input,
        out byte[] output,
        out int trailerCount,
        out string error)
    {
        var offset = 0;
        trailerCount = 0;
        using var decoded = new MemoryStream(Math.Min(input.Length, 64 * 1024));
        while (true)
        {
            if (!TryReadCrlfLine(input, ref offset, MaxChunkLineBytes, out var line, out error))
            {
                output = [];
                return false;
            }

            var semicolon = line.IndexOf((byte)';');
            var sizeBytes = semicolon >= 0 ? line[..semicolon].TrimBadWhitespace() : line;
            if (sizeBytes.IsEmpty
                || !ulong.TryParse(
                    System.Text.Encoding.ASCII.GetString(sizeBytes),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var chunkSize)
                || chunkSize > int.MaxValue)
            {
                output = [];
                error = "invalid or overflowing chunk size";
                return false;
            }
            if (semicolon >= 0 && !ValidateChunkExtensions(line[semicolon..]))
            {
                output = [];
                error = "invalid chunk extension";
                return false;
            }

            if (chunkSize == 0)
            {
                var trailerBytes = 0;
                while (true)
                {
                    var before = offset;
                    if (!TryReadCrlfLine(input, ref offset, MaxChunkLineBytes, out var trailer, out error))
                    {
                        output = [];
                        return false;
                    }
                    trailerBytes += offset - before;
                    if (trailerBytes > MaxTrailerBytes || trailerCount > MaxTrailerCount)
                    {
                        output = [];
                        error = "chunk trailers exceed safety limits";
                        return false;
                    }
                    if (trailer.IsEmpty)
                    {
                        if (offset != input.Length)
                        {
                            output = [];
                            error = "unexpected bytes follow the final chunk trailers";
                            return false;
                        }
                        output = decoded.ToArray();
                        error = string.Empty;
                        return true;
                    }
                    var colon = trailer.IndexOf((byte)':');
                    if (colon <= 0 || trailer[..colon].IndexOfAnyExceptTokenCharacters() >= 0)
                    {
                        output = [];
                        error = "malformed trailer field";
                        return false;
                    }
                    trailerCount++;
                }
            }

            if (chunkSize > (ulong)(input.Length - offset)
                || decoded.Length + (long)chunkSize > OutputLimit(input.Length))
            {
                output = [];
                error = chunkSize > (ulong)(input.Length - offset)
                    ? "chunk data is truncated"
                    : "decoded chunks exceed the safety limit";
                return false;
            }

            decoded.Write(input, offset, (int)chunkSize);
            offset += (int)chunkSize;
            if (offset + 2 > input.Length || input[offset] != '\r' || input[offset + 1] != '\n')
            {
                output = [];
                error = "chunk data is not followed by CRLF";
                return false;
            }
            offset += 2;
        }
    }

    private static bool TryReadCrlfLine(
        byte[] input,
        ref int offset,
        int maxLength,
        out ReadOnlySpan<byte> line,
        out string error)
    {
        var start = offset;
        var maximum = Math.Min(input.Length - 1, start + maxLength);
        for (var index = start; index < maximum; index++)
        {
            if (input[index] == '\r' && input[index + 1] == '\n')
            {
                line = input.AsSpan(start, index - start);
                offset = index + 2;
                error = string.Empty;
                return true;
            }
        }
        line = default;
        error = input.Length - start > maxLength
            ? $"line exceeds the {maxLength:N0}-byte safety limit"
            : "line is missing CRLF";
        return false;
    }

    private sealed record CodingParseResult(List<string> Codings, string? Error);

    private static bool ValidateChunkExtensions(ReadOnlySpan<byte> extensions)
    {
        var offset = 0;
        while (offset < extensions.Length)
        {
            SkipBadWhitespace(extensions, ref offset);
            if (offset >= extensions.Length)
            {
                return true;
            }
            if (extensions[offset++] != ';')
            {
                return false;
            }
            SkipBadWhitespace(extensions, ref offset);
            if (!ReadByteToken(extensions, ref offset))
            {
                return false;
            }
            SkipBadWhitespace(extensions, ref offset);
            if (offset < extensions.Length && extensions[offset] == '=')
            {
                offset++;
                SkipBadWhitespace(extensions, ref offset);
                if (!ReadByteTokenOrQuotedString(extensions, ref offset))
                {
                    return false;
                }
            }
            SkipBadWhitespace(extensions, ref offset);
        }
        return true;
    }

    private static bool ReadByteTokenOrQuotedString(ReadOnlySpan<byte> value, ref int offset)
    {
        if (offset >= value.Length)
        {
            return false;
        }
        if (value[offset] != '"')
        {
            return ReadByteToken(value, ref offset);
        }
        offset++;
        while (offset < value.Length)
        {
            var character = value[offset++];
            if (character == '"')
            {
                return true;
            }
            if (character == '\\')
            {
                if (offset >= value.Length || !IsQuotedByte(value[offset++]))
                {
                    return false;
                }
            }
            else if (!IsQuotedByte(character))
            {
                return false;
            }
        }
        return false;
    }

    private static bool ReadByteToken(ReadOnlySpan<byte> value, ref int offset)
    {
        var start = offset;
        while (offset < value.Length && IsTokenCharacter((char)value[offset]))
        {
            offset++;
        }
        return offset > start;
    }

    private static bool IsQuotedByte(byte value) =>
        value is (byte)'\t' or (byte)' '
        || value is >= 0x21 and <= 0x7E
        || value >= 0x80;

    private static int IndexOfAnyExceptTokenCharacters(this ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!IsTokenCharacter((char)value[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private static ReadOnlySpan<byte> TrimBadWhitespace(this ReadOnlySpan<byte> value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }
        return value[..end];
    }

    private static void SkipBadWhitespace(ReadOnlySpan<byte> value, ref int offset)
    {
        while (offset < value.Length && value[offset] is (byte)' ' or (byte)'\t')
        {
            offset++;
        }
    }

    private sealed class SingleByteReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            count == 0 ? 0 : inner.Read(buffer, offset, 1);

        public override int Read(Span<byte> buffer) =>
            buffer.IsEmpty ? 0 : inner.Read(buffer[..1]);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
