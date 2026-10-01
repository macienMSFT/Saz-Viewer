namespace SazViewer.App;

/// <summary>Identity of a file's content as seen through metadata: length plus last-write time.</summary>
internal readonly record struct FileFingerprint(long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>Null when the file does not exist (or cannot be inspected).</summary>
    public static FileFingerprint? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileFingerprint(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

internal enum FileChangeNotice
{
    None,
    Changed,
    Deleted
}

/// <summary>
/// Decides whether a tab should tell the user its capture changed on disk. The baseline is the
/// fingerprint observed just before the loaded report was parsed, so the app's own reads never count as
/// changes. Dismissing remembers the current state so the same change is not reported twice.
/// </summary>
internal sealed class FileChangeTracker(FileFingerprint? baseline)
{
    private FileFingerprint? dismissed;
    private bool dismissedDeletion;

    public FileFingerprint? Baseline { get; private set; } = baseline;

    public FileChangeNotice Evaluate(FileFingerprint? current)
    {
        if (current is null)
        {
            return dismissedDeletion ? FileChangeNotice.None : FileChangeNotice.Deleted;
        }
        dismissedDeletion = false;
        if (current == Baseline || current == dismissed)
        {
            return FileChangeNotice.None;
        }
        return FileChangeNotice.Changed;
    }

    public void Dismiss(FileFingerprint? current)
    {
        if (current is null)
        {
            dismissedDeletion = true;
        }
        else
        {
            dismissed = current;
        }
    }

    /// <summary>Called after a successful reload with the fingerprint read before that parse.</summary>
    public void Reset(FileFingerprint? baseline)
    {
        Baseline = baseline;
        dismissed = null;
        dismissedDeletion = false;
    }
}

/// <summary>Trailing-edge debouncer: <see cref="Signal"/> bursts produce one callback after a quiet period.</summary>
internal sealed class Debouncer : IDisposable
{
    private readonly ITimer timer;
    private readonly TimeSpan quietPeriod;
    private readonly object gate = new();
    private bool disposed;

    public Debouncer(TimeSpan quietPeriod, Action callback, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.quietPeriod = quietPeriod;
        timer = timeProvider.CreateTimer(_ =>
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
            }
            callback();
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Signal()
    {
        lock (gate)
        {
            if (!disposed)
            {
                timer.Change(quietPeriod, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
        timer.Dispose();
    }
}

/// <summary>
/// Watches one capture's directory for changes to its file name, including editors that save by writing
/// a temporary file and renaming it over the original, then reports the debounced current fingerprint.
/// Only name, size, and last-write notifications are requested, so reads by the app do not trigger it.
/// </summary>
internal sealed class CaptureFileWatcher : IDisposable
{
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(400);

    private readonly string path;
    private readonly string fileName;
    private readonly FileSystemWatcher? watcher;
    private readonly Debouncer debouncer;

    public CaptureFileWatcher(string path, TimeSpan quietPeriod, TimeProvider timeProvider)
    {
        this.path = Path.GetFullPath(path);
        fileName = Path.GetFileName(this.path);
        debouncer = new Debouncer(quietPeriod, () => Settled?.Invoke(this, FileFingerprint.TryRead(this.path)), timeProvider);
        var directory = Path.GetDirectoryName(this.path);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                InternalBufferSize = 16 * 1024
            };
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnRenamed;
            // Overflow or a vanished directory: re-check the file rather than guess.
            watcher.Error += (_, _) => debouncer.Signal();
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
            watcher = null;
        }
    }

    /// <summary>Raised on a worker thread with the file's fingerprint (null if missing) after changes settle.</summary>
    public event EventHandler<FileFingerprint?>? Settled;

    public bool IsWatching => watcher is not null;

    /// <summary>True when a watcher notification for <paramref name="name"/> concerns the watched file.</summary>
    public static bool IsRelevant(string? name, string fileName) =>
        name is not null && string.Equals(Path.GetFileName(name), fileName, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        debouncer.Dispose();
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.Name, fileName))
        {
            debouncer.Signal();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsRelevant(e.Name, fileName) || IsRelevant(e.OldName, fileName))
        {
            debouncer.Signal();
        }
    }
}
