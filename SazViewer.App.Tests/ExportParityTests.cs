using System.Text;
using SazViewer.Cli;
using SazViewer.Core;

namespace SazViewer.App.Tests;

public sealed class ExportParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainCaptureExportIsByteIdenticalToCli(bool scrub)
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("capture.saz"));
        var cliOutput = temp.File("cli.html");
        var appOutput = temp.File("app.html");

        string[] args = scrub ? ["--scrub-auth", capture, cliOutput] : [capture, cliOutput];
        Assert.Equal(0, new CliApplication(new FakeConsole()).Run(args));

        var document = ReportBuilder.Build(capture, scrub, new QueuePasswordProvider());
        ReportBuilder.WriteHtml(document, appOutput);

        var expected = File.ReadAllBytes(cliOutput);
        Assert.Equal(expected, File.ReadAllBytes(appOutput));
        Assert.Equal(expected, document.Utf8);
        Assert.False(expected.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(scrub, document.ScrubbedValueCount is > 0);
        if (scrub)
        {
            Assert.DoesNotContain("topsecretcookie", Encoding.UTF8.GetString(expected));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncryptedCaptureExportIsByteIdenticalToCli(bool scrub)
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WriteEncrypted(temp.File("encrypted.saz"));
        var cliOutput = temp.File("cli.html");
        var appOutput = temp.File("app.html");

        var console = new FakeConsole(TestCaptures.Password + "\n");
        string[] args = scrub
            ? ["--password-stdin", "--scrub-auth", capture, cliOutput]
            : ["--password-stdin", capture, cliOutput];
        Assert.Equal(0, new CliApplication(console).Run(args));

        var provider = new QueuePasswordProvider("wrong", TestCaptures.Password);
        var document = ReportBuilder.Build(capture, scrub, provider);
        ReportBuilder.WriteHtml(document, appOutput);

        Assert.Equal(File.ReadAllBytes(cliOutput), File.ReadAllBytes(appOutput));
        Assert.Equal(2, provider.Requests.Count);
        Assert.True(provider.Requests[1].PreviousPasswordRejected);
        Assert.Equal(3, provider.Requests[1].MaximumAttempts);
        Assert.All(provider.Returned, buffer => Assert.All(buffer, character => Assert.Equal('\0', character)));
        Assert.DoesNotContain(TestCaptures.Password, document.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeWrongPasswordsFailWithAuthenticationError()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WriteEncrypted(temp.File("encrypted.saz"));
        var provider = new QueuePasswordProvider("a", "b", "c", "d");

        var exception = Assert.Throws<SazAuthenticationException>(() => ReportBuilder.Build(capture, false, provider));
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal("The password is incorrect.", ReportBuilder.DescribeFailure(exception));
    }

    [Fact]
    public void CancelledPasswordPromptThrowsCancelled()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WriteEncrypted(temp.File("encrypted.saz"));

        Assert.Throws<SazPasswordCancelledException>(
            () => ReportBuilder.Build(capture, false, new QueuePasswordProvider()));
    }

    [Fact]
    public void ExportRefusesToOverwriteCapture()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("capture.saz"));
        var document = ReportBuilder.Build(capture, false, new QueuePasswordProvider());

        Assert.Throws<ArgumentException>(() => ReportBuilder.WriteHtml(document, capture.ToUpperInvariant()));
    }

    [Fact]
    public void ExportCreatesMissingDirectories()
    {
        using var temp = new TempDirectory();
        var capture = TestCaptures.WritePlain(temp.File("capture.saz"));
        var document = ReportBuilder.Build(capture, false, new QueuePasswordProvider());
        var output = temp.File(@"a\b\report.html");

        ReportBuilder.WriteHtml(document, output);
        Assert.Equal(document.Utf8, File.ReadAllBytes(output));
    }

    [Fact]
    public void FailuresUseCliWording()
    {
        Assert.StartsWith("I/O failure: ", ReportBuilder.DescribeFailure(new IOException("x")));
        Assert.StartsWith("Access denied: ", ReportBuilder.DescribeFailure(new UnauthorizedAccessException("x")));
        Assert.Equal("missing", ReportBuilder.DescribeFailure(new FileNotFoundException("missing")));
        Assert.Null(ReportBuilder.DescribeFailure(new InvalidOperationException()));
    }

    private sealed class FakeConsole(string input = "") : ICliConsole
    {
        public bool IsInputRedirected => true;
        public TextReader In { get; } = new StringReader(input);
        public void Write(string value) { }
        public void WriteLine(string value = "") { }
        public void WriteErrorLine(string value) { }
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
