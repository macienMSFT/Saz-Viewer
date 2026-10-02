namespace SazViewer.Core;

/// <summary>Detects a supported capture format and parses it into the shared report model.</summary>
public sealed class CaptureParser
{
    public bool DeferBodyDecoding { get; init; }

    public SazReport Parse(string path, ISazPasswordProvider? passwordProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Capture file was not found: {fullPath}", fullPath);
        }

        return DetectFormat(fullPath) switch
        {
            CaptureFormat.Saz => new SazParser { DeferBodyDecoding = DeferBodyDecoding }
                .Parse(fullPath, passwordProvider),
            CaptureFormat.Har => new HarParser().Parse(fullPath),
            _ => throw new InvalidDataException($"'{fullPath}' is not a supported SAZ or HAR capture.")
        };
    }

    public static void CompleteDeferred(
        SazReport report,
        int maximumDegreeOfParallelism = -1) =>
        SazParser.CompleteDeferred(report, maximumDegreeOfParallelism);

    public static CaptureFormat DetectFormat(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Capture file was not found: {fullPath}", fullPath);
        }

        using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> prefix = stackalloc byte[4];
        var count = stream.Read(prefix);
        if (count >= 4 && prefix[0] == (byte)'P' && prefix[1] == (byte)'K'
            && ((prefix[2] == 3 && prefix[3] == 4)
                || (prefix[2] == 5 && prefix[3] == 6)
                || (prefix[2] == 7 && prefix[3] == 8)))
        {
            return CaptureFormat.Saz;
        }

        stream.Position = 0;
        int value;
        do
        {
            value = stream.ReadByte();
        }
        while (value >= 0 && char.IsWhiteSpace((char)value));

        if (value == '{')
        {
            return CaptureFormat.Har;
        }

        var extension = Path.GetExtension(fullPath);
        throw new InvalidDataException(
            $"'{fullPath}' is not a supported capture. Expected a ZIP/SAZ archive or a JSON HAR 1.2 document"
            + (string.IsNullOrEmpty(extension) ? "." : $", not '{extension}'."));
    }
}
