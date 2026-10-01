namespace SazViewer.App.Tests;

public sealed class AppPathsTests
{
    private const string LocalAppData = @"C:\Users\someone\AppData\Local";

    [Fact]
    public void DefaultsToPerUserLocalAppData()
    {
        Assert.Equal(@"C:\Users\someone\AppData\Local\SazViewer", AppPaths.ResolveDataDirectory(null, LocalAppData));
        Assert.Equal(@"C:\Users\someone\AppData\Local\SazViewer", AppPaths.ResolveDataDirectory("  ", LocalAppData));
    }

    [Fact]
    public void AcceptsOnlyFullyQualifiedOverride()
    {
        Assert.Equal(@"D:\isolated\data", AppPaths.ResolveDataDirectory(@"D:\isolated\data", LocalAppData));
        Assert.Equal(@"C:\Users\someone\AppData\Local\SazViewer", AppPaths.ResolveDataDirectory(@"relative\data", LocalAppData));
    }
}
