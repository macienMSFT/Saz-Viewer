using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SazViewer.App.Model;
using SazViewer.App.Themes;
using SazViewer.App.ViewModels;
using SazViewer.Core;

namespace SazViewer.App.Tests;

/// <summary>Binary-capable captures for the M3 views (Hex, Image, Auth, WebView).</summary>
internal static class MediaCaptures
{
    public const string BearerValue = "Bearer test-token-value-123";

    public static SazReport Parse(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                output.Write(content);
            }
        }
        stream.Position = 0;
        return new SazParser { DeferBodyDecoding = true }.Parse(stream);
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    public static byte[] Response(string headers, byte[] body) =>
        [.. Ascii($"HTTP/1.1 200 OK\r\n{headers}Content-Length: {body.Length}\r\n\r\n"), .. body];

    public static byte[] Gzip(string text)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        return output.ToArray();
    }

    public static byte[] Png(int width = 3, int height = 2)
    {
        byte[]? result = null;
        StaRunner.Run(() =>
        {
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)0x80);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            result = stream.ToArray();
        });
        return result!;
    }

    /// <summary>1: gzip JSON, 2: PNG labelled image/jpeg, 3: HTML page, 4: bearer-authenticated request.</summary>
    public static SazReport Media() => Parse(
        ("raw/1_c.txt", Ascii("GET https://a.test/data HTTP/1.1\r\nHost: a.test\r\n\r\n")),
        ("raw/1_s.txt", Response("Content-Type: application/json\r\nContent-Encoding: gzip\r\n", Gzip("{\"a\":1}"))),
        ("raw/2_c.txt", Ascii("GET https://a.test/logo HTTP/1.1\r\nHost: a.test\r\n\r\n")),
        ("raw/2_s.txt", Response("Content-Type: image/jpeg\r\n", Png())),
        ("raw/3_c.txt", Ascii("GET https://a.test/page HTTP/1.1\r\nHost: a.test\r\n\r\n")),
        ("raw/3_s.txt", Response("Content-Type: text/html; charset=utf-8\r\n",
            Ascii("<!doctype html><html><head><title>T</title><script>alert(1)</script></head><body><h1>Hello</h1><img src=\"https://evil.test/x.png\"></body></html>"))),
        ("raw/4_c.txt", Ascii($"GET https://a.test/me HTTP/1.1\r\nHost: a.test\r\nAuthorization: {BearerValue}\r\n\r\n")),
        ("raw/4_s.txt", Response("WWW-Authenticate: Basic realm=\"x\"\r\nContent-Type: text/plain\r\n", Ascii("ok"))));

    public static CaptureViewModel Open(out FakeClipboard clipboard, UiPreferences? preferences = null)
    {
        clipboard = new FakeClipboard();
        return new CaptureViewModel(Media(), clipboard, preferences ?? new UiPreferences(null));
    }
}

public sealed class HexViewModelTests
{
    [Fact]
    public void FormatMatchesTheReportsHexDump()
    {
        var text = HexViewModel.Format("ABCDEFGHIJKLMNOPQ\n"u8, "Captured", 18, null);

        Assert.Equal(
            "Captured body bytes\n18 total; 18 retained\n\n" + HexViewModel.HeaderRow + "\n" +
            "00000000  41 42 43 44 45 46 47 48  49 4A 4B 4C 4D 4E 4F 50  |ABCDEFGHIJKLMNOP|\n" +
            "00000010  51 0A                                             |Q.              |",
            text);
    }

    [Fact]
    public void FormatLabelsTruncationAndRemovedEncodings()
    {
        var text = HexViewModel.Format(new byte[1024], "Decoded", 5000, "gzip");

        Assert.StartsWith("Decoded body bytes\n5,000 total; 1,024 retained (truncated)\nRemoved encodings: gzip\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n[HexView truncated: 1,024 of 5,000 bytes retained.]", text, StringComparison.Ordinal);
        Assert.Equal(64, text.Split('\n').Count(line => line.StartsWith('0')));
    }

    [Fact]
    public void CapturedAndDecodedSourcesToggle()
    {
        var model = MediaCaptures.Open(out _);
        model.Inspector.Load(model.Sessions.VisibleRows[0]);
        var tab = model.Inspector.Response!.Tab("hex");
        var hex = Assert.IsType<HexViewModel>(tab.Content);

        Assert.True(hex.CanShowDecoded);
        Assert.Equal("Captured body bytes", hex.SourceLabel);
        Assert.Contains("00000000  1F 8B 08", hex.Text, StringComparison.Ordinal);
        Assert.Equal("Copy captured body hex view", tab.CopyAccessibleName);
        var searchChanges = 0;
        hex.SearchTargetChanged += (_, _) => searchChanges++;

        hex.IsDecoded = true;

        Assert.Equal("Decoded body bytes", hex.SourceLabel);
        Assert.Contains("\nRemoved encodings: content: gzip\n", hex.Text, StringComparison.Ordinal);
        Assert.Contains("|{\"a\":1}", hex.Text, StringComparison.Ordinal);
        Assert.Equal("Showing all 7 bytes.", hex.Status);
        Assert.Equal("Copy decoded body hex view", tab.CopyAccessibleName);
        Assert.Equal(1, searchChanges);
        Assert.Same(hex.Document.Document, hex.SearchTarget);
    }

    [Fact]
    public void DecodedIsUnavailableWithoutContentCodings()
    {
        var model = MediaCaptures.Open(out _);
        model.Inspector.Load(model.Sessions.VisibleRows[3]);
        var hex = Assert.IsType<HexViewModel>(model.Inspector.Response!.Tab("hex").Content);

        hex.IsDecoded = true;

        Assert.False(hex.CanShowDecoded);
        Assert.False(hex.IsDecoded);
        Assert.Null(hex.RemovedEncodingsText);
        Assert.Equal("Captured", hex.SelectedSource);
    }

    [Fact]
    public void LargeBodiesShowTheTruncatedPrefix()
    {
        var report = MediaCaptures.Parse(
            ("raw/1_c.txt", MediaCaptures.Ascii("GET https://a.test/ HTTP/1.1\r\nHost: a.test\r\n\r\n")),
            ("raw/1_s.txt", MediaCaptures.Response("Content-Type: application/octet-stream\r\n", new byte[3000])));
        var model = new CaptureViewModel(report, new FakeClipboard());
        model.Inspector.Load(model.Sessions.VisibleRows[0]);
        var hex = Assert.IsType<HexViewModel>(model.Inspector.Response!.Tab("hex").Content);

        Assert.True(hex.IsTruncated);
        Assert.Equal("Showing a truncated retained prefix (1,024 of 3,000 bytes).", hex.Status);
    }
}

public sealed class AuthViewModelTests
{
    private static (CaptureViewModel Model, FakeClipboard Clipboard, MessageTabViewModel Tab) OpenAuth(InspectorLayout layout = InspectorLayout.Split)
    {
        var preferences = new UiPreferences(null);
        preferences.SetLayout(LayoutWidthClass.Wide, layout);
        var model = MediaCaptures.Open(out var clipboard, preferences);
        model.Inspector.Load(model.Sessions.VisibleRows[3]);
        model.Inspector.SelectedSide = InspectorViewModel.RequestSide;
        var request = model.Inspector.Request!;
        Assert.True(request.Select("auth"));
        return (model, clipboard, request.Tab("auth"));
    }

    [Fact]
    public void RedactedByDefaultAndCopyRevealsOnlyAfterReveal()
    {
        var (_, clipboard, tab) = OpenAuth();
        var auth = Assert.IsType<AuthViewModel>(tab.Content);

        Assert.False(auth.IsRevealed);
        Assert.Equal("Reveal values", auth.ButtonText);
        Assert.Equal("Copy redacted authentication headers", tab.CopyAccessibleName);
        tab.Copy();
        Assert.Equal("Authorization: Bearer [redacted]\n", clipboard.Text);
        Assert.DoesNotContain("test-token", LineDocument.JoinRows(auth.Document.Document.AllRows), StringComparison.Ordinal);

        auth.ToggleCommand.Execute(null);

        Assert.True(auth.IsRevealed);
        Assert.Equal("Hide values", auth.ButtonText);
        Assert.Equal("Copy revealed authentication headers", tab.CopyAccessibleName);
        Assert.Contains(MediaCaptures.BearerValue, LineDocument.JoinRows(auth.Document.Document.AllRows), StringComparison.Ordinal);
        tab.Copy();
        Assert.Equal($"Authorization: {MediaCaptures.BearerValue}\n", clipboard.Text);
    }

    [Fact]
    public void RevealResetsOnTabSwitch()
    {
        var (model, _, tab) = OpenAuth();
        var auth = (AuthViewModel)tab.Content!;
        auth.IsRevealed = true;

        model.Inspector.Request!.Select("headers");

        Assert.False(auth.IsRevealed);
    }

    [Fact]
    public void RevealResetsOnSideSwitch()
    {
        // Single view: switching sides hides (and resets) the other side.
        var (model, _, tab) = OpenAuth(InspectorLayout.Single);
        var auth = (AuthViewModel)tab.Content!;
        auth.IsRevealed = true;

        model.Inspector.SelectedSide = InspectorViewModel.ResponseSide;

        Assert.False(auth.IsRevealed);
    }

    [Fact]
    public void RevealResetsOnSessionNavigation()
    {
        var (model, _, tab) = OpenAuth();
        var auth = (AuthViewModel)tab.Content!;
        auth.IsRevealed = true;

        model.Inspector.Load(model.Sessions.VisibleRows[0]);
        model.Inspector.Load(model.Sessions.VisibleRows[3]);

        Assert.False(auth.IsRevealed);
        Assert.False(((AuthViewModel)model.Inspector.Request!.Tab("auth").Content!).IsRevealed);
    }

    [Fact]
    public void RevealChangesTheSearchTarget()
    {
        var (_, _, tab) = OpenAuth();
        var auth = (AuthViewModel)tab.Content!;
        var changes = 0;
        auth.SearchTargetChanged += (_, _) => changes++;
        var redacted = auth.SearchTarget;

        auth.IsRevealed = true;

        Assert.Equal(1, changes);
        Assert.NotSame(redacted, auth.SearchTarget);
    }
}

public sealed class ImageViewTests
{
    [Fact]
    public async Task PngLabelledJpegShowsMismatchWarningAndDecodes()
    {
        var model = MediaCaptures.Open(out var clipboard);
        model.Inspector.Load(model.Sessions.VisibleRows[1]);
        var response = model.Inspector.Response!;
        Assert.True(response.Tab("image").IsEnabled);
        var image = Assert.IsType<ImageViewModel>(response.Tab("image").Content);

        Assert.Equal("image/png", image.MimeType);
        Assert.Contains("does not match", image.Warning, StringComparison.Ordinal);
        Assert.Equal(ImageViewModel.PendingDimensions, image.Dimensions);

        await image.EnsureLoaded();

        Assert.NotNull(image.Image);
        Assert.Equal("3\u00d72 pixels", image.Dimensions);
        Assert.Equal("Image decoded locally from retained body bytes.", image.Status);
        response.Tab("image").Copy();
        Assert.StartsWith("image/png\n", clipboard.Text, StringComparison.Ordinal);
        Assert.Contains("3\u00d72 pixels", clipboard.Text, StringComparison.Ordinal);
        Assert.EndsWith("rendering as 'image/png'.", clipboard.Text, StringComparison.Ordinal);

        image.Deactivate();
        Assert.Null(image.Image);
    }

    [Fact]
    public async Task FailedDecodeIsReportedAndCopied()
    {
        var info = new HtmlReportGenerator.ImageViewInfo("image/png", "Detected image/png.", "Static image", null);
        var image = new ImageViewModel(info, new byte[] { 1 }, 1, (_, _) => new ImageDecodeResult(null, 0, 0, "boom"));

        await image.EnsureLoaded();

        Assert.True(image.IsWarning);
        Assert.Equal("Image could not be loaded: boom.", image.Status);
        Assert.EndsWith("\nImage decode failed.", image.MetadataText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AllowListExcludesSvg()
    {
        Assert.DoesNotContain("image/svg+xml", ImageSafety.AllowedMimeTypes);
        Assert.Equal("image type is unsupported", ImageSafety.Decode("<svg/>"u8.ToArray(), "image/svg+xml").Error);
    }

    [Fact]
    public void ReadsHeaderDimensions()
    {
        Assert.Equal((3, 2), ImageSafety.ReadDimensions(MediaCaptures.Png(), "image/png"));
        byte[] gif = [.. "GIF89a"u8, 0x10, 0x00, 0x20, 0x00, 0, 0, 0];
        Assert.Equal((16, 32), ImageSafety.ReadDimensions(gif, "image/gif"));
        Assert.Null(ImageSafety.ReadDimensions("GIF89"u8, "image/gif"));
        Assert.Null(ImageSafety.ReadDimensions(MediaCaptures.Png(), "image/gif"));
    }

    [Fact]
    public void LimitsAreCheckedBeforeDecoding()
    {
        Assert.Null(ImageSafety.CheckLimits(4096, 4096));
        Assert.NotNull(ImageSafety.CheckLimits(4097, 4096));
        Assert.NotNull(ImageSafety.CheckLimits(ImageSafety.MaxDimension + 1, 1));
        Assert.NotNull(ImageSafety.CheckLimits(0, 10));

        var huge = MediaCaptures.Png();
        huge[16] = 0x00; huge[17] = 0x01; huge[18] = 0x00; huge[19] = 0x00; // IHDR width = 65,536
        var result = ImageSafety.Decode(huge, "image/png");
        Assert.Null(result.Image);
        Assert.Equal(65536, result.Width);
    }
}

public sealed class WebPreviewTests
{
    private static (MessageTabViewModel Tab, WebPreviewViewModel Preview, FakeClipboard Clipboard) Open()
    {
        var model = MediaCaptures.Open(out var clipboard);
        model.Inspector.Load(model.Sessions.VisibleRows[2]);
        var tab = model.Inspector.Response!.Tab("webview");
        Assert.True(tab.IsEnabled);
        return (tab, Assert.IsType<WebPreviewViewModel>(tab.Content), clipboard);
    }

    [Fact]
    public void RendersOnlyWhileActiveInRenderedMode()
    {
        var (_, preview, _) = Open();
        Assert.False(preview.ShouldRender);
        Assert.Equal(WebPreviewViewModel.IdleStatus, preview.Status);

        preview.Activate();
        Assert.True(preview.ShouldRender);
        Assert.Null(preview.SearchTarget);
        preview.ReportLoaded();
        Assert.Equal(WebPreviewViewModel.LoadedStatus, preview.Status);

        preview.IsSourceMode = true;
        Assert.False(preview.ShouldRender);
        Assert.Equal(WebPreviewViewModel.SourceStatus, preview.Status);
        Assert.Same(preview.SourceDocument!.Document, preview.SearchTarget);
        Assert.Contains("<script>alert(1)</script>", LineDocument.JoinRows(preview.SourceDocument.Document.AllRows), StringComparison.Ordinal);

        preview.IsRenderedMode = true;
        Assert.True(preview.ShouldRender);
        preview.Deactivate();
        Assert.False(preview.ShouldRender);
    }

    [Fact]
    public void UnexpectedNavigationClosesThePreview()
    {
        var (_, preview, _) = Open();
        preview.Activate();

        preview.ReportBlockedNavigation();

        Assert.False(preview.ShouldRender);
        Assert.True(preview.IsWarning);
        Assert.Equal(WebPreviewViewModel.BlockedStatus, preview.Status);
        preview.Deactivate();
        preview.Activate();
        Assert.True(preview.ShouldRender);
    }

    [Fact]
    public void RenderedDocumentIsSanitizedInertAndThemed()
    {
        var (_, preview, _) = Open();

        var dark = preview.RenderedDocument(AppTheme.Dark);

        Assert.Contains("<html data-theme=\"dark\">", dark, StringComparison.Ordinal);
        Assert.Contains("<html data-theme=\"light\">", preview.RenderedDocument(AppTheme.Light), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", dark, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.test", dark, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", dark, StringComparison.Ordinal);
        Assert.Contains("Hello", dark, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyUsesTheDecodedSource()
    {
        var (tab, _, clipboard) = Open();

        tab.Copy();

        Assert.StartsWith("<!doctype html>", clipboard.Text, StringComparison.Ordinal);
        Assert.Equal("Copy response HTML source", tab.CopyAccessibleName);
    }

    [Fact]
    public void PolicyServesTheDocumentOnceAndAllowsOneNavigation()
    {
        var policy = new WebPreviewPolicy();
        var uri = WebPreviewPolicy.PreviewUri.AbsoluteUri;

        Assert.False(policy.ServeDocument("POST", uri));
        Assert.False(policy.ServeDocument("GET", "https://evil.test/x.png"));
        Assert.False(policy.ServeDocument("GET", uri + "?x=1"));
        Assert.True(policy.ServeDocument("GET", uri));
        Assert.False(policy.ServeDocument("GET", uri));

        Assert.True(policy.AllowNavigation(uri));
        Assert.False(policy.AllowNavigation(uri));
        Assert.False(new WebPreviewPolicy().AllowNavigation("https://evil.test/"));
        Assert.False(WebPreviewPolicy.IsPreviewDocument("http://webview-preview.sazviewer.invalid/document.html"));
        Assert.False(WebPreviewPolicy.IsPreviewDocument(uri + "#top"));
        Assert.True(WebPreviewPolicy.IsPreviewDocument("HTTPS://WEBVIEW-PREVIEW.SAZVIEWER.INVALID/document.html"));
    }

    [Fact]
    public void SandboxSettingsDisableScriptsAndHostAccess()
    {
        var settings = WebPreviewPolicy.Settings;

        Assert.False(settings.IsScriptEnabled);
        Assert.False(settings.AreHostObjectsAllowed);
        Assert.False(settings.IsWebMessageEnabled);
        Assert.False(settings.AreDefaultContextMenusEnabled);
        Assert.False(settings.AreBrowserAcceleratorKeysEnabled);
        Assert.False(settings.IsGeneralAutofillEnabled);
        Assert.False(settings.IsPasswordAutosaveEnabled);
        Assert.False(settings.IsReputationCheckingRequired);
        Assert.False(settings.AllowExternalDrop);
#if !DEBUG
        Assert.False(settings.AreDevToolsEnabled);
#endif
    }
}
