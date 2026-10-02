namespace SazViewer.App.Tests;

public sealed class AppArgumentsTests
{
    [Fact]
    public void NoArgumentsOpensEmptyWindow()
    {
        var parsed = AppArguments.Parse([]);
        Assert.Empty(parsed.CapturePaths);
        Assert.False(parsed.ShowHelp);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("capture.saz")]
    [InlineData(@"C:\captures\my capture.saz")]
    [InlineData(@"\\server\share\capture.SAZ")]
    public void SinglePathIsAccepted(string path)
    {
        var parsed = AppArguments.Parse([path]);
        Assert.Equal([path], parsed.CapturePaths);
        Assert.Null(parsed.Error);
        Assert.False(parsed.ShowHelp);
    }

    [Fact]
    public void MultiplePathsAreAcceptedInOrder()
    {
        var parsed = AppArguments.Parse(["b.saz", "a.saz", @"C:\c.saz"]);
        Assert.Equal(["b.saz", "a.saz", @"C:\c.saz"], parsed.CapturePaths);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void TooManyPathsAreRejected()
    {
        var atLimit = Enumerable.Range(0, AppArguments.MaximumPaths).Select(i => $"{i}.saz").ToArray();
        Assert.Equal(AppArguments.MaximumPaths, AppArguments.Parse(atLimit).CapturePaths.Count);

        var parsed = AppArguments.Parse([.. atLimit, "extra.saz"]);
        Assert.Empty(parsed.CapturePaths);
        Assert.NotNull(parsed.Error);
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
        Assert.Empty(parsed.CapturePaths);
        Assert.Contains(option, parsed.Error);
    }

    [Fact]
    public void PasswordAfterPathIsRejectedNotForwarded()
    {
        var parsed = AppArguments.Parse(["capture.saz", "--password", "secret"]);
        Assert.Empty(parsed.CapturePaths);
        Assert.NotNull(parsed.Error);
        Assert.DoesNotContain("secret", parsed.Error);
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
        Assert.Equal(["-odd-name.saz"], parsed.CapturePaths);
        Assert.Null(parsed.Error);
        Assert.Equal(["--help"], AppArguments.Parse(["--", "--help"]).CapturePaths);
    }
}
