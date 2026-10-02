using System.Text;
using SazViewer.Core;

namespace SazViewer.App;

/// <summary>
/// A parsed capture held in memory. Native views read <see cref="Report"/> directly; the HTML report is
/// generated only when first needed (Export HTML).
/// </summary>
internal sealed class ReportDocument
{
    private readonly Lazy<string> html;
    private readonly Lazy<byte[]> utf8;

    public ReportDocument(string sourcePath, SazReport report)
    {
        SourcePath = sourcePath;
        Report = report;
        html = new Lazy<string>(() =>
        {
            CaptureParser.CompleteDeferred(report);
            return new HtmlReportGenerator().Generate(report);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        utf8 = new Lazy<byte[]>(() => ReportBuilder.Encoding.GetBytes(html.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        SessionCount = report.Sessions.Count;
        WebSocketMessageCount = report.WebSocketMessages.Count;
        ScrubbedValueCount = report.AuthScrub?.Total;
    }

    /// <summary>
    /// Starts (once) background decoding of the bodies not yet viewed; when it completes, <see cref="WarningCount"/>
    /// includes the per-session warnings. Views decode the bodies they need on demand regardless. Callers start it
    /// after the first render, with half the cores, so it doesn't compete with opening the capture.
    /// </summary>
    public Task StartDeferredWork()
    {
        lock (html)
        {
            return deferredWork ??= Report.HasDeferredWork
                ? Task.Run(() => CaptureParser.CompleteDeferred(Report, Math.Max(1, Environment.ProcessorCount / 2)))
                : Task.CompletedTask;
        }
    }

    private Task? deferredWork;

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>The parsed (and, for scrubbed documents, scrubbed) model. Treat as read-only.</summary>
    public SazReport Report { get; }

    public bool IsScrubbed => Report.AuthScrub is not null;

    /// <summary>The report HTML exactly as the CLI generates it (generated on first access).</summary>
    public string Html => html.Value;

    /// <summary>The exact bytes the CLI would write (UTF-8 without BOM).</summary>
    public byte[] Utf8 => utf8.Value;

    public bool IsHtmlGenerated => html.IsValueCreated;

    public int SessionCount { get; }

    public int WebSocketMessageCount { get; }

    /// <summary>The report warning count, or null while deferred body decoding is still running.</summary>
    public int? WarningCount => Report.HasDeferredWork ? null : Report.Warnings.Count;

    public int? ScrubbedValueCount { get; }
}

/// <summary>
/// Parse → optional scrub → generate pipeline. Intentionally mirrors <c>CliApplication</c> step for
/// step so desktop exports are byte-identical to <c>saz-viewer [--scrub-auth] input.saz|input.har output.html</c>.
/// </summary>
internal static class ReportBuilder
{
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false);

    public static ReportDocument Build(string capturePath, bool scrubAuth, ISazPasswordProvider passwordProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        ArgumentNullException.ThrowIfNull(passwordProvider);
        var inputPath = Path.GetFullPath(capturePath);
        // Bodies are decoded on demand so the session list is available quickly; the completed report is identical
        // to the CLI's eager parse (ReportDocument.Html completes it before generating).
        StartupTrace.Mark("parse-start");
        var report = new CaptureParser { DeferBodyDecoding = true }.Parse(inputPath, passwordProvider);
        StartupTrace.Mark("parse-end");
        if (scrubAuth)
        {
            CaptureParser.CompleteDeferred(report);
            AuthScrubber.Scrub(report);
        }
        return new ReportDocument(inputPath, report);
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
