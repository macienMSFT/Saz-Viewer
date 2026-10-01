namespace SazViewer.App.Tests;

public sealed class CaptureTabCollectionTests
{
    private sealed class FakeTab(string path) : ICaptureTab
    {
        public string SourcePath { get; } = Path.GetFullPath(path);

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private readonly CaptureTabCollection<FakeTab> tabs = new();
    private readonly List<FakeTab?> activations = [];

    public CaptureTabCollectionTests()
    {
        tabs.ActiveChanged += (_, tab) => activations.Add(tab);
    }

    [Fact]
    public void AddActivatesNewTab()
    {
        var a = Add(@"C:\caps\a.saz");
        var b = Add(@"C:\caps\b.saz");
        Assert.Same(b, tabs.Active);
        Assert.Equal([a, b], tabs.Tabs);
        Assert.Equal([a, b], activations);
    }

    [Theory]
    [InlineData(@"C:\caps\a.saz")]
    [InlineData(@"c:\CAPS\A.SAZ")]
    [InlineData(@"C:\caps\sub\..\a.saz")]
    public void SamePathFocusesExistingTabInsteadOfDuplicating(string path)
    {
        var a = Add(@"C:\caps\a.saz");
        Add(@"C:\caps\b.saz");

        Assert.Same(a, tabs.FocusExisting(path));
        Assert.Same(a, tabs.Active);
        Assert.Equal(2, tabs.Count);
        Assert.Throws<InvalidOperationException>(() => tabs.Add(new FakeTab(path)));
        Assert.Equal(2, tabs.Count);
    }

    [Fact]
    public void FocusExistingReturnsNullForUnknownPath()
    {
        Add(@"C:\caps\a.saz");
        activations.Clear();
        Assert.Null(tabs.FocusExisting(@"C:\caps\z.saz"));
        Assert.Empty(activations);
    }

    [Fact]
    public void ClosingActiveTabDisposesItAndSelectsRightNeighbor()
    {
        var a = Add(@"C:\caps\a.saz");
        var b = Add(@"C:\caps\b.saz");
        var c = Add(@"C:\caps\c.saz");
        tabs.Activate(b);

        Assert.True(tabs.Close(b));
        Assert.Equal(1, b.DisposeCount);
        Assert.Same(c, tabs.Active);

        Assert.True(tabs.Close(c));
        Assert.Same(a, tabs.Active);

        Assert.True(tabs.Close(a));
        Assert.Null(tabs.Active);
        Assert.Null(activations[^1]);
        Assert.False(tabs.Close(a));
        Assert.Equal(1, a.DisposeCount);
    }

    [Fact]
    public void ClosingInactiveTabKeepsActiveTab()
    {
        var a = Add(@"C:\caps\a.saz");
        var b = Add(@"C:\caps\b.saz");
        activations.Clear();

        tabs.Close(a);
        Assert.Same(b, tabs.Active);
        Assert.Empty(activations);
        Assert.Equal(1, a.DisposeCount);
    }

    [Fact]
    public void ClosedPathCanBeReopened()
    {
        var a = Add(@"C:\caps\a.saz");
        tabs.Close(a);
        Assert.Null(tabs.Find(a.SourcePath));
        var again = Add(@"C:\caps\a.saz");
        Assert.Same(again, tabs.Active);
    }

    [Fact]
    public void CycleWrapsInBothDirections()
    {
        var a = Add(@"C:\caps\a.saz");
        var b = Add(@"C:\caps\b.saz");
        var c = Add(@"C:\caps\c.saz");

        tabs.Cycle(forward: true);
        Assert.Same(a, tabs.Active);
        tabs.Cycle(forward: true);
        Assert.Same(b, tabs.Active);
        tabs.Cycle(forward: false);
        tabs.Cycle(forward: false);
        Assert.Same(c, tabs.Active);
    }

    [Fact]
    public void CycleWithOneTabIsNoOp()
    {
        var a = Add(@"C:\caps\a.saz");
        activations.Clear();
        tabs.Cycle(forward: true);
        Assert.Same(a, tabs.Active);
        Assert.Empty(activations);
    }

    [Fact]
    public void CloseAllDisposesEveryTab()
    {
        var all = new[] { Add(@"C:\caps\a.saz"), Add(@"C:\caps\b.saz"), Add(@"C:\caps\c.saz") };
        tabs.CloseAll();
        Assert.Equal(0, tabs.Count);
        Assert.Null(tabs.Active);
        Assert.All(all, tab => Assert.Equal(1, tab.DisposeCount));
    }

    [Fact]
    public void ActivatingUnknownTabThrows()
    {
        Assert.Throws<ArgumentException>(() => tabs.Activate(new FakeTab(@"C:\caps\x.saz")));
    }

    private FakeTab Add(string path)
    {
        var tab = new FakeTab(path);
        tabs.Add(tab);
        return tab;
    }
}
