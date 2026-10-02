using System.Buffers.Binary;
using System.Text;

namespace SazViewer.App.Tests;

public sealed class SingleInstanceProtocolTests
{
    [Fact]
    public async Task RequestRoundTrips()
    {
        string[] paths = [@"C:\a.saz", @"\\server\share\ünïcode capture.saz"];
        var message = SingleInstanceProtocol.EncodeRequest(paths);

        Assert.Equal("SAZV"u8.ToArray(), message[..4]);
        Assert.Equal(SingleInstanceProtocol.Version, message[4]);
        Assert.Equal(message.Length - SingleInstanceProtocol.HeaderLength, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(5)));
        Assert.Equal("""{"paths":["C:\\a.saz","\\\\server\\share\\\u00FCn\u00EFcode capture.saz"]}""",
            Encoding.UTF8.GetString(message, SingleInstanceProtocol.HeaderLength, message.Length - SingleInstanceProtocol.HeaderLength));

        using var stream = new MemoryStream(message);
        Assert.Equal(paths, await SingleInstanceProtocol.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyPathListIsValid()
    {
        using var stream = new MemoryStream(SingleInstanceProtocol.EncodeRequest([]));
        Assert.Empty((await SingleInstanceProtocol.ReadRequestAsync(stream, CancellationToken.None))!);
    }

    [Fact]
    public void EncodeRejectsOutOfRangeInput()
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceProtocol.EncodeRequest(
            Enumerable.Range(0, AppArguments.MaximumPaths + 1).Select(i => $@"C:\{i}.saz").ToArray()));
        Assert.Throws<ArgumentException>(() => SingleInstanceProtocol.EncodeRequest([""]));
        Assert.Throws<ArgumentException>(() => SingleInstanceProtocol.EncodeRequest(
            [@"C:\" + new string('a', ForwardedPathValidator.MaximumPathLength) + ".saz"]));
    }

    [Theory]
    [InlineData("XAZV", 1, 10)]
    [InlineData("SAZV", 0, 10)]
    [InlineData("SAZV", 2, 10)]
    [InlineData("SAZV", 1, 0)]
    [InlineData("SAZV", 1, -1)]
    [InlineData("SAZV", 1, SingleInstanceProtocol.MaximumPayloadBytes + 1)]
    public void BadHeadersAreRejected(string magic, byte version, int length)
    {
        var header = new byte[SingleInstanceProtocol.HeaderLength];
        Encoding.ASCII.GetBytes(magic).CopyTo(header, 0);
        header[4] = version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), length);
        Assert.Equal(-1, SingleInstanceProtocol.TryParseHeader(header));
    }

    [Fact]
    public void HeaderOfWrongSizeIsRejected()
    {
        Assert.Equal(-1, SingleInstanceProtocol.TryParseHeader(SingleInstanceProtocol.EncodeRequest([])[..8]));
    }

    [Theory]
    [InlineData("""{"paths":["C:\\a.saz"],"password":"hunter2"}""")]
    [InlineData("""{"password":"hunter2","paths":["C:\\a.saz"]}""")]
    [InlineData("""{"paths":["C:\\a.saz", 1]}""")]
    [InlineData("""{"paths":["C:\\a.saz", null]}""")]
    [InlineData("""{"paths":[["C:\\a.saz"]]}""")]
    [InlineData("""{"paths":[{"path":"C:\\a.saz"}]}""")]
    [InlineData("""{"paths":[""]}""")]
    [InlineData("""{"paths":"C:\\a.saz"}""")]
    [InlineData("""{"Paths":["C:\\a.saz"]}""")]
    [InlineData("""["C:\\a.saz"]""")]
    [InlineData("""{"paths":["C:\\a.saz"]} """)]
    [InlineData("""{"paths":["C:\\a.saz"]}{}""")]
    [InlineData("""{"paths":["C:\\a.saz"]""")]
    [InlineData("""{"paths":["C:\\a.saz",]}""")]
    [InlineData("""{"paths":[/* c */"C:\\a.saz"]}""")]
    [InlineData("")]
    public void MalformedPayloadsAreRejected(string json)
    {
        Assert.Null(SingleInstanceProtocol.DecodePayload(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void TooManyOrOverlongPathsAreRejected()
    {
        var tooMany = "{\"paths\":[" + string.Join(",", Enumerable.Range(0, AppArguments.MaximumPaths + 1).Select(i => $"\"C:\\\\{i}.saz\"")) + "]}";
        Assert.Null(SingleInstanceProtocol.DecodePayload(Encoding.UTF8.GetBytes(tooMany)));

        var overlong = "{\"paths\":[\"" + new string('a', ForwardedPathValidator.MaximumPathLength + 1) + "\"]}";
        Assert.Null(SingleInstanceProtocol.DecodePayload(Encoding.UTF8.GetBytes(overlong)));
    }

    [Fact]
    public void InvalidUtf8IsRejected()
    {
        byte[] payload = [.. "{\"paths\":[\"C:\\\\"u8, 0xC3, 0x28, .. ".saz\"]}"u8];
        Assert.Null(SingleInstanceProtocol.DecodePayload(payload));
    }

    [Fact]
    public async Task TruncatedStreamsAreRejected()
    {
        var message = SingleInstanceProtocol.EncodeRequest([@"C:\a.saz"]);
        foreach (var length in new[] { 0, 4, SingleInstanceProtocol.HeaderLength, message.Length - 1 })
        {
            using var stream = new MemoryStream(message[..length]);
            Assert.Null(await SingleInstanceProtocol.ReadRequestAsync(stream, CancellationToken.None));
        }
    }

    [Fact]
    public async Task PayloadIsReadOnlyToDeclaredLength()
    {
        var message = SingleInstanceProtocol.EncodeRequest([@"C:\a.saz"]);
        using var stream = new MemoryStream([.. message, .. "garbage"u8]);
        Assert.Equal([@"C:\a.saz"], await SingleInstanceProtocol.ReadRequestAsync(stream, CancellationToken.None));
    }
}
