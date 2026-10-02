namespace SazViewer.App.Tests;

/// <summary>Manually advanced clock whose timers fire synchronously from <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        now += by;
        foreach (var timer in timers.ToList())
        {
            timer.FireIfDue(now);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (due is { } d && d <= now)
            {
                due = null;
                callback(state);
            }
        }

        public void Dispose() => due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class DebouncerTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    [Fact]
    public void BurstProducesOneCallbackAfterQuietPeriod()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        using var debouncer = new Debouncer(Quiet, () => calls++, clock);

        for (var i = 0; i < 5; i++)
        {
            debouncer.Signal();
            clock.Advance(TimeSpan.FromMilliseconds(300));
        }
        Assert.Equal(0, calls);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SeparateBurstsProduceSeparateCallbacks()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        using var debouncer = new Debouncer(Quiet, () => calls++, clock);

        debouncer.Signal();
        clock.Advance(Quiet);
        debouncer.Signal();
        debouncer.Signal();
        clock.Advance(Quiet);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DisposedDebouncerNeverCallsBack()
    {
        var clock = new ManualTimeProvider();
        var calls = 0;
        var debouncer = new Debouncer(Quiet, () => calls++, clock);
        debouncer.Signal();
        debouncer.Dispose();
        debouncer.Signal();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, calls);
    }
}

public sealed class FileChangeTrackerTests
{
    private static readonly FileFingerprint Original = new(100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    private static readonly FileFingerprint Edited = new(120, new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));
    private static readonly FileFingerprint EditedAgain = new(120, new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc));

    [Fact]
    public void UnchangedFileIsNotReported()
    {
        Assert.Equal(FileChangeNotice.None, new FileChangeTracker(Original).Evaluate(Original));
    }

    [Fact]
    public void ChangeAndDeletionAreReported()
    {
        var tracker = new FileChangeTracker(Original);
        Assert.Equal(FileChangeNotice.Changed, tracker.Evaluate(Edited));
        Assert.Equal(FileChangeNotice.Deleted, tracker.Evaluate(null));
        // Restored to exactly the loaded version: nothing to reload.
        Assert.Equal(FileChangeNotice.None, tracker.Evaluate(Original));
    }

    [Fact]
    public void DismissSuppressesOnlyThatState()
    {
        var tracker = new FileChangeTracker(Original);
        tracker.Dismiss(Edited);
        Assert.Equal(FileChangeNotice.None, tracker.Evaluate(Edited));
        Assert.Equal(FileChangeNotice.Changed, tracker.Evaluate(EditedAgain));

        tracker.Dismiss(null);
        Assert.Equal(FileChangeNotice.None, tracker.Evaluate(null));
        // The file comes back changed: report again, and a later deletion is new as well.
        Assert.Equal(FileChangeNotice.Changed, tracker.Evaluate(EditedAgain));
        Assert.Equal(FileChangeNotice.Deleted, tracker.Evaluate(null));
    }

    [Fact]
    public void ResetAdoptsNewBaseline()
    {
        var tracker = new FileChangeTracker(Original);
        tracker.Dismiss(Edited);
        tracker.Reset(EditedAgain);
        Assert.Equal(EditedAgain, tracker.Baseline);
        Assert.Equal(FileChangeNotice.None, tracker.Evaluate(EditedAgain));
        Assert.Equal(FileChangeNotice.Changed, tracker.Evaluate(Edited));
    }

    [Fact]
    public void MissingBaselineTreatsAnyFileAsChanged()
    {
        Assert.Equal(FileChangeNotice.Changed, new FileChangeTracker(null).Evaluate(Original));
    }
}

public sealed class CaptureFileWatcherTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    [Theory]
    [InlineData("capture.saz", true)]
    [InlineData("CAPTURE.SAZ", true)]
    [InlineData("capture.saz.tmp", false)]
    [InlineData("other.saz", false)]
    [InlineData(null, false)]
    public void RelevanceMatchesFileNameOnly(string? name, bool expected)
    {
        Assert.Equal(expected, CaptureFileWatcher.IsRelevant(name, "capture.saz"));
    }

    [Fact]
    public void FingerprintIsNullForMissingFile()
    {
        using var temp = new TempDirectory();
        Assert.Null(FileFingerprint.TryRead(temp.File("missing.saz")));
        File.WriteAllBytes(temp.File("x.saz"), [1, 2, 3]);
        Assert.Equal(3, FileFingerprint.TryRead(temp.File("x.saz"))!.Value.Length);
    }

    [Fact]
    public async Task InPlaceWriteIsReportedOnceSettled()
    {
        using var temp = new TempDirectory();
        var path = TestCaptures.WritePlain(temp.File("capture.saz"));
        using var watcher = new CaptureFileWatcher(path, Quiet, TimeProvider.System);
        Assert.True(watcher.IsWatching);
        var settled = Next(watcher);

        await File.AppendAllTextAsync(path, "more");

        var fingerprint = await settled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new FileInfo(path).Length, fingerprint!.Value.Length);
    }

    [Fact]
    public async Task ReplaceViaRenameIsReported()
    {
        using var temp = new TempDirectory();
        var path = TestCaptures.WritePlain(temp.File("capture.saz"));
        using var watcher = new CaptureFileWatcher(path, Quiet, TimeProvider.System);
        var settled = Next(watcher);

        var staging = TestCaptures.WriteSimple(temp.File("capture.saz.tmp"), 4);
        File.Move(staging, path, overwrite: true);

        var fingerprint = await settled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(fingerprint);
        Assert.Equal(new FileInfo(path).Length, fingerprint.Value.Length);
    }

    [Fact]
    public async Task DeletionReportsMissingFile()
    {
        using var temp = new TempDirectory();
        var path = TestCaptures.WritePlain(temp.File("capture.saz"));
        using var watcher = new CaptureFileWatcher(path, Quiet, TimeProvider.System);
        var settled = Next(watcher);

        File.Delete(path);

        Assert.Null(await settled.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ReadsAndUnrelatedFilesAreIgnored()
    {
        using var temp = new TempDirectory();
        var path = TestCaptures.WritePlain(temp.File("capture.saz"));
        using var watcher = new CaptureFileWatcher(path, Quiet, TimeProvider.System);
        var count = 0;
        watcher.Settled += (_, _) => Interlocked.Increment(ref count);

        _ = await File.ReadAllBytesAsync(path);
        await using (File.OpenRead(path))
        {
        }
        await File.WriteAllTextAsync(temp.File("other.saz"), "x");
        await Task.Delay(Quiet * 6);

        Assert.Equal(0, Volatile.Read(ref count));
    }

    private static Task<FileFingerprint?> Next(CaptureFileWatcher watcher)
    {
        var source = new TaskCompletionSource<FileFingerprint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Settled += (_, fingerprint) => source.TrySetResult(fingerprint);
        return source.Task;
    }
}
