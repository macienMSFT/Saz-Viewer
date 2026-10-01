using System.Security;

namespace SazViewer.App.Tests;

public sealed class ReportWebViewPolicyTests
{
    [Theory]
    [InlineData("https://saz-viewer.invalid/report.html")]
    [InlineData("https://SAZ-VIEWER.invalid/report.html")]
    [InlineData("https://saz-viewer.invalid/report.html#inspector=row-3&filter=abc")]
    [InlineData("https://saz-viewer.invalid:443/report.html")]
    public void ReportDocumentIsAllowed(string uri)
    {
        Assert.True(ReportWebViewPolicy.IsReportDocument(uri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("report.html")]
    [InlineData("http://saz-viewer.invalid/report.html")]
    [InlineData("https://saz-viewer.invalid:8443/report.html")]
    [InlineData("https://saz-viewer.invalid/report.html?x=1")]
    [InlineData("https://saz-viewer.invalid/other.html")]
    [InlineData("https://saz-viewer.invalid/REPORT.html")]
    [InlineData("https://saz-viewer.invalid/")]
    [InlineData("https://user@saz-viewer.invalid/report.html")]
    [InlineData("https://saz-viewer.invalid.example.com/report.html")]
    [InlineData("https://example.com/report.html")]
    [InlineData("file:///C:/report.html")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<p>x</p>")]
    [InlineData("blob:https://saz-viewer.invalid/0f0e")]
    [InlineData("javascript:alert(1)")]
    public void EverythingElseIsNotTheReport(string? uri)
    {
        Assert.False(ReportWebViewPolicy.IsReportDocument(uri));
    }

    [Theory]
    [InlineData("about:srcdoc", true)]
    [InlineData("about:blank", true)]
    [InlineData("https://saz-viewer.invalid/report.html", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("about:srcdoc#x", false)]
    [InlineData(null, false)]
    public void FramesOnlyHostInlineDocuments(string? uri, bool expected)
    {
        Assert.Equal(expected, ReportWebViewPolicy.IsAllowedFrameNavigation(uri));
    }

    [Fact]
    public void UserDroppedCaptureIsRecognized()
    {
        Assert.True(ReportWebViewPolicy.TryGetDroppedCapturePath(
            "file:///C:/captures/my%20capture.SAZ", isUserInitiated: true, out var path));
        Assert.Equal(@"C:\captures\my capture.SAZ", path);
    }

    [Theory]
    [InlineData("file:///C:/captures/a.saz", false)]
    [InlineData("file:///C:/captures/a.html", true)]
    [InlineData("https://example.com/a.saz", true)]
    [InlineData("not a uri", true)]
    public void OtherNavigationsAreNotDrops(string uri, bool isUserInitiated)
    {
        Assert.False(ReportWebViewPolicy.TryGetDroppedCapturePath(uri, isUserInitiated, out var path));
        Assert.Equal("", path);
    }

    [Theory]
    [InlineData("copy", true)]
    [InlineData("selectAll", true)]
    [InlineData("paste", true)]
    [InlineData("inspectElement", false)]
    [InlineData("saveAs", false)]
    [InlineData("print", false)]
    [InlineData("reload", false)]
    [InlineData("back", false)]
    [InlineData("openLinkInNewWindow", false)]
    [InlineData("saveImageAs", false)]
    public void ContextMenuKeepsOnlyEditingCommands(string name, bool expected)
    {
        Assert.Equal(expected, ReportWebViewPolicy.IsAllowedContextMenuItem(name));
    }

    [Fact]
    public void SecurePasswordConvertsWithoutLoss()
    {
        using var secure = new SecureString();
        foreach (var character in " pa\u00DFw\u00F6rd ")
        {
            secure.AppendChar(character);
        }

        Assert.Equal(" pa\u00DFw\u00F6rd ".ToCharArray(), PasswordDialog.ToCharArray(secure));
    }
}
