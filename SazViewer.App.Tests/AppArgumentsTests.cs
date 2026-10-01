namespace SazViewer.App.Tests;

public sealed class AppArgumentsTests
{
    [Fact]
    public void NoArgumentsOpensEmptyWindow()
    {
        Assert.Equal(new AppArguments(null, false, null), AppArguments.Parse([]));
    }

    [Theory]
    [InlineData("capture.saz")]
    [InlineData(@"C:\captures\my capture.saz")]
    [InlineData(@"\\server\share\capture.SAZ")]
    public void SinglePathIsAccepted(string path)
    {
        var parsed = AppArguments.Parse([path]);
        Assert.Equal(path, parsed.CapturePath);
        Assert.Null(parsed.Error);
        Assert.False(parsed.ShowHelp);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-H")]
    [InlineData("/?")]
    public void HelpIsRecognized(string option)
    {
        Assert.True(AppArguments.Parse([option]).ShowHelp);
        Assert.True(AppArguments.Parse(["capture.saz", option]).ShowHelp);
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--password-stdin")]
    [InlineData("--scrub-auth")]
    [InlineData("-x")]
    public void UnknownOptionsAreRejected(string option)
    {
        var parsed = AppArguments.Parse([option, "capture.saz"]);
        Assert.Null(parsed.CapturePath);
        Assert.Contains(option, parsed.Error);
    }

    [Fact]
    public void MultiplePathsAreRejected()
    {
        var parsed = AppArguments.Parse(["a.saz", "b.saz"]);
        Assert.Null(parsed.CapturePath);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void BlankPathIsRejected()
    {
        Assert.NotNull(AppArguments.Parse(["  "]).Error);
    }

    [Fact]
    public void DoubleDashAllowsPathsThatLookLikeOptions()
    {
        var parsed = AppArguments.Parse(["--", "-odd-name.saz"]);
        Assert.Equal("-odd-name.saz", parsed.CapturePath);
        Assert.Null(parsed.Error);
        Assert.Equal("--help", AppArguments.Parse(["--", "--help"]).CapturePath);
    }
}
