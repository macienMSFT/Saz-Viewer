namespace SazViewer.App.Tests;

public sealed class ForwardedPathValidatorTests
{
    [Theory]
    [InlineData("capture.saz", @"C:\work", @"C:\work\capture.saz")]
    [InlineData(@"..\up.saz", @"C:\work\sub", @"C:\work\up.saz")]
    [InlineData(@"D:\abs.saz", @"C:\work", @"D:\abs.saz")]
    [InlineData(@"\\server\share\x.saz", @"C:\work", @"\\server\share\x.saz")]
    public void ResolvesAgainstSenderWorkingDirectory(string path, string workingDirectory, string expected)
    {
        Assert.Equal(expected, ForwardedPathValidator.TryResolve(path, workingDirectory));
    }

    [Fact]
    public void ResolveRejectsBlankAndOverlongPaths()
    {
        Assert.Null(ForwardedPathValidator.TryResolve(" ", @"C:\work"));
        Assert.Null(ForwardedPathValidator.TryResolve(new string('a', ForwardedPathValidator.MaximumPathLength + 1) + ".saz", @"C:\work"));
    }

    [Theory]
    [InlineData(@"C:\captures\a.saz")]
    [InlineData(@"C:\captures\A.SAZ")]
    [InlineData(@"C:\captures\a.har")]
    [InlineData(@"C:\captures\A.HAR")]
    [InlineData(@"\\server\share\a.saz")]
    public void WellFormedPaths(string path)
    {
        Assert.True(ForwardedPathValidator.IsWellFormed(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.saz")]
    [InlineData(@"captures\a.saz")]
    [InlineData(@"\captures\a.saz")]
    [InlineData(@"C:captures\a.saz")]
    [InlineData(@"C:\captures\a.zip")]
    [InlineData(@"C:\captures\a.saz.exe")]
    [InlineData(@"C:\captures\*.saz")]
    [InlineData(@"C:\captures\a?.saz")]
    [InlineData(@"C:\captures\..\a.saz")]
    [InlineData(@"C:\captures\.\a.saz")]
    [InlineData(@"\\?\C:\captures\a.saz")]
    [InlineData(@"\\.\C:\captures\a.saz")]
    [InlineData(@"C:\captures\a.txt:hidden.saz")]
    [InlineData("C:\\captures\\a\0.saz")]
    public void MalformedPathsAreRefused(string? path)
    {
        Assert.False(ForwardedPathValidator.IsWellFormed(path));
        Assert.False(ForwardedPathValidator.IsAcceptable(path));
    }

    [Fact]
    public void OverlongPathIsRefused()
    {
        var path = @"C:\" + new string('a', ForwardedPathValidator.MaximumPathLength) + ".saz";
        Assert.False(ForwardedPathValidator.IsWellFormed(path));
    }

    [Fact]
    public void AcceptableRequiresAnExistingFile()
    {
        using var temp = new TempDirectory();
        var existing = TestCaptures.WritePlain(temp.File("present.saz"));
        var har = temp.File("present.har");
        File.WriteAllText(har, """{"log":{"version":"1.2","entries":[]}}""");
        Directory.CreateDirectory(temp.File("folder.saz"));

        Assert.True(ForwardedPathValidator.IsAcceptable(existing));
        Assert.True(ForwardedPathValidator.IsAcceptable(har));
        Assert.False(ForwardedPathValidator.IsAcceptable(temp.File("missing.saz")));
        Assert.False(ForwardedPathValidator.IsAcceptable(temp.File("folder.saz")));
    }
}
