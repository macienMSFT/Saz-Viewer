using System.Windows;
using System.Windows.Threading;

namespace SazViewer.App;

/// <summary>
/// The parse of the capture named on the command line, started before the main window is built so it overlaps
/// WPF start-up. <see cref="MainWindow.OpenCaptureAsync"/> consumes it in place of starting its own parse.
/// </summary>
internal sealed class CapturePrefetch
{
    private CapturePrefetch(string fullPath, FileFingerprint? fingerprint, Task<ReportDocument> report)
    {
        FullPath = fullPath;
        Fingerprint = fingerprint;
        Report = report;
    }

    public string FullPath { get; }

    /// <summary>Read before the parse started, as <see cref="MainWindow.OpenCaptureAsync"/> does.</summary>
    public FileFingerprint? Fingerprint { get; }

    public Task<ReportDocument> Report { get; }

    /// <summary>Whether this parse is for <paramref name="fullPath"/> (a normalized path; Windows paths ignore case).</summary>
    public bool Matches(string fullPath) => string.Equals(FullPath, fullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts parsing <paramref name="path"/>; null when the path can't be normalized.</summary>
    public static CapturePrefetch? Start(string path, Dispatcher dispatcher)
    {
        string fullPath;
        try
        {
            fullPath = CaptureTabCollection<CaptureTab>.NormalizePath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var fingerprint = FileFingerprint.TryRead(fullPath);
        // The prompt (encrypted captures) is owned by the main window once it is shown.
        var passwords = new DialogPasswordProvider(dispatcher, () => Application.Current?.MainWindow, Path.GetFileName(fullPath), "to open it");
        var report = Task.Run(() => ReportBuilder.Build(fullPath, scrubAuth: false, passwords));
        // Observed here too, in case the window never consumes it (for example, when the file is missing).
        _ = report.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return new CapturePrefetch(fullPath, fingerprint, report);
    }
}
