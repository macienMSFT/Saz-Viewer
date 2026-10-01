using System.Text;
using SazViewer.Core;

namespace SazViewer.App;

/// <summary>A generated report held in memory for display and export.</summary>
internal sealed class ReportDocument
{
    public ReportDocument(string sourcePath, string html, SazReport report)
    {
        SourcePath = sourcePath;
        Html = html;
        Utf8 = ReportBuilder.Encoding.GetBytes(html);
        SessionCount = report.Sessions.Count;
        WebSocketMessageCount = report.WebSocketMessages.Count;
        WarningCount = report.Warnings.Count;
        ScrubbedValueCount = report.AuthScrub?.Total;
    }

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    public string Html { get; }

    /// <summary>The exact bytes the CLI would write (UTF-8 without BOM); served to WebView2.</summary>
    public byte[] Utf8 { get; }

    public int SessionCount { get; }

    public int WebSocketMessageCount { get; }

    public int WarningCount { get; }

    public int? ScrubbedValueCount { get; }
}

/// <summary>
/// Parse → optional scrub → generate pipeline. Intentionally mirrors <c>CliApplication</c> step for
/// step so desktop exports are byte-identical to <c>saz-viewer [--scrub-auth] input.saz output.html</c>.
/// </summary>
internal static class ReportBuilder
{
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);

    public static ReportDocument Build(string capturePath, bool scrubAuth, ISazPasswordProvider passwordProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        ArgumentNullException.ThrowIfNull(passwordProvider);
        var inputPath = Path.GetFullPath(capturePath);
        var report = new SazParser().Parse(inputPath, passwordProvider);
        if (scrubAuth)
        {
            AuthScrubber.Scrub(report);
        }
        var html = new HtmlReportGenerator().Generate(report);
        return new ReportDocument(inputPath, html, report);
    }

    public static void WriteHtml(ReportDocument document, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullOutputPath, document.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The export path must be different from the capture path.");
        }
        var outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        File.WriteAllText(fullOutputPath, document.Html, Encoding);
    }

    /// <summary>Maps expected failures to the same user-facing wording the CLI uses; null for unexpected ones.</summary>
    public static string? DescribeFailure(Exception exception) => exception switch
    {
        FileNotFoundException => exception.Message,
        ArgumentException => $"Invalid path or argument: {exception.Message}",
        UnauthorizedAccessException => $"Access denied: {exception.Message}",
        SazArchiveException => exception.Message,
        InvalidDataException => exception.Message,
        IOException => $"I/O failure: {exception.Message}",
        _ => null
    };
}
