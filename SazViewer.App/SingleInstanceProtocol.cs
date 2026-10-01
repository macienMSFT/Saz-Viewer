using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SazViewer.App;

/// <summary>
/// Wire format for handing command-line paths from a second launch to the running instance.
/// <para>Request: ASCII <c>SAZV</c>, version byte <c>1</c>, little-endian <see cref="int"/> payload length,
/// then a UTF-8 JSON payload that is exactly <c>{"paths":["C:\\full\\path.saz", ...]}</c>.</para>
/// <para>Response: one byte, <see cref="Accepted"/> or <see cref="Rejected"/>.</para>
/// The format has no field for passwords or options: unknown properties, non-string entries, too many
/// or overlong paths, a bad header, or a payload over <see cref="MaximumPayloadBytes"/> reject the whole request.
/// </summary>
internal static class SingleInstanceProtocol
{
    public const byte Version = 1;
    public const int HeaderLength = 9;
    public const int MaximumPayloadBytes = 256 * 1024;
    public const byte Accepted = 0x00;
    public const byte Rejected = 0x01;
    public const string PathsProperty = "paths";

    private static readonly byte[] Magic = "SAZV"u8.ToArray();

    public static byte[] EncodeRequest(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > AppArguments.MaximumPaths)
        {
            throw new ArgumentException($"At most {AppArguments.MaximumPaths} paths can be forwarded.", nameof(paths));
        }
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || path.Length > ForwardedPathValidator.MaximumPathLength)
            {
                throw new ArgumentException("A forwarded path is empty or too long.", nameof(paths));
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(PathsProperty);
            foreach (var path in paths)
            {
                writer.WriteStringValue(path);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var payload = buffer.ToArray();
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("The forwarded paths are too large.", nameof(paths));
        }

        var message = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(message, 0);
        message[4] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(5, 4), payload.Length);
        payload.CopyTo(message, HeaderLength);
        return message;
    }

    /// <summary>Reads one request; null when the stream ends early or the request is malformed.</summary>
    public static async Task<IReadOnlyList<string>?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        if (!await ReadExactlyOrFalseAsync(stream, header, cancellationToken))
        {
            return null;
        }
        var length = TryParseHeader(header);
        if (length < 0)
        {
            return null;
        }
        var payload = new byte[length];
        if (!await ReadExactlyOrFalseAsync(stream, payload, cancellationToken))
        {
            return null;
        }
        return DecodePayload(payload);
    }

    /// <summary>Returns the payload length, or -1 for a bad magic, version, or length.</summary>
    public static int TryParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderLength || !header[..4].SequenceEqual(Magic) || header[4] != Version)
        {
            return -1;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(5, 4));
        return length is > 0 and <= MaximumPayloadBytes ? length : -1;
    }

    public static IReadOnlyList<string>? DecodePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes)
        {
            return null;
        }
        try
        {
            // Strict UTF-8: invalid sequences throw instead of being replaced.
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(payload);
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 3, CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject
                || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(PathsProperty)
                || !reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return null;
            }
            var paths = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String || paths.Count == AppArguments.MaximumPaths)
                {
                    return null;
                }
                var path = reader.GetString();
                if (string.IsNullOrEmpty(path) || path.Length > ForwardedPathValidator.MaximumPathLength)
                {
                    return null;
                }
                paths.Add(path);
            }
            if (reader.TokenType != JsonTokenType.EndArray
                || !reader.Read() || reader.TokenType != JsonTokenType.EndObject
                || reader.BytesConsumed != payload.Length
                || payload[^1] != (byte)'}')
            {
                return null;
            }
            return paths;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactlyOrFalseAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
