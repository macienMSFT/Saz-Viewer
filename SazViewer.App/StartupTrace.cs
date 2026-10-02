using System.Diagnostics;

namespace SazViewer.App;

/// <summary>
/// Opt-in startup timing for performance checks: when <c>SAZVIEWER_STARTUP_TRACE</c> names a fully qualified file,
/// each <see cref="Mark"/> appends "milliseconds since process start, phase" to it. Off (and free) otherwise.
/// </summary>
internal static class StartupTrace
{
    private static readonly string? Path = ResolvePath();
    private static readonly object Gate = new();

    public static bool IsEnabled => Path is not null;

    public static void Mark(string phase)
    {
        if (Path is null)
        {
            return;
        }
        var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
        lock (Gate)
        {
            File.AppendAllText(Path, $"{elapsed.TotalMilliseconds:F0}\t{phase}{Environment.NewLine}");
        }
    }

    private static string? ResolvePath()
    {
        var value = Environment.GetEnvironmentVariable("SAZVIEWER_STARTUP_TRACE");
        return !string.IsNullOrWhiteSpace(value) && System.IO.Path.IsPathFullyQualified(value) ? value : null;
    }
}
