using System.Windows.Threading;

namespace SazViewer.App.Tests;

public sealed class CapturePrefetchTests
{
    [Fact]
    public async Task PrefetchProducesTheSameReportAsADirectBuild()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("capture.saz"));

        var prefetch = CapturePrefetch.Start(capture, Dispatcher.CurrentDispatcher);

        Assert.NotNull(prefetch);
        Assert.Equal(Path.GetFullPath(capture), prefetch.FullPath);
        Assert.NotNull(prefetch.Fingerprint);
        var document = await prefetch.Report;
        var direct = ReportBuilder.Build(capture, scrubAuth: false, new QueuePasswordProvider());
        Assert.Equal(direct.Utf8, document.Utf8);
    }

    [Fact]
    public void MatchesOnlyTheSamePathIgnoringCase()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("capture.saz"));
        var prefetch = CapturePrefetch.Start(capture, Dispatcher.CurrentDispatcher)!;

        Assert.True(prefetch.Matches(Path.GetFullPath(capture).ToUpperInvariant()));
        Assert.False(prefetch.Matches(temp.File("other.saz")));
    }

    [Fact]
    public async Task MissingCaptureFaultsTheReportWithoutThrowingFromStart()
    {
        using var temp = new TempDirectory();

        var prefetch = CapturePrefetch.Start(temp.File("missing.saz"), Dispatcher.CurrentDispatcher);

        Assert.NotNull(prefetch);
        Assert.Null(prefetch.Fingerprint);
        await Assert.ThrowsAnyAsync<IOException>(() => prefetch.Report);
    }

    [Fact]
    public void InvalidPathReturnsNull()
    {
        Assert.Null(CapturePrefetch.Start("", Dispatcher.CurrentDispatcher));
    }
}
