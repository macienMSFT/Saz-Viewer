using System.Windows.Automation.Peers;
using System.Windows.Controls;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;

namespace SazViewer.App.Tests;

public sealed class MediaViewSmokeTests
{
    [Fact]
    public void ImageViewShowsTheDecodedImage()
    {
        var model = MediaCaptures.Open(out _);
        model.Inspector.Load(model.Sessions.VisibleRows[1]);
        var image = (ImageViewModel)model.Inspector.Response!.Tab("image").Content!;
        StaRunner.Run(() =>
        {
            var view = new ImageView { DataContext = image };
            var window = NativeViewSmokeTests.Host(view);
            try
            {
                for (var attempt = 0; attempt < 50 && image.Image is null; attempt++)
                {
                    Thread.Sleep(20);
                    StaRunner.DoEvents();
                }
                StaRunner.DoEvents();
                Assert.NotNull(image.Image);
                view.UpdateLayout();
                Assert.True(view.ActualHeight > 0);
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
        Assert.Null(image.Image);
    }

    [Fact]
    public void HexAndAuthViewsLoad()
    {
        var model = MediaCaptures.Open(out _);
        model.Inspector.Load(model.Sessions.VisibleRows[3]);
        var hex = model.Inspector.Response!.Tab("hex").Content!;
        var auth = model.Inspector.Request!.Tab("auth").Content!;
        StaRunner.Run(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new HexView { DataContext = hex, Height = 300 });
            panel.Children.Add(new AuthView { DataContext = auth, Height = 300 });
            var window = NativeViewSmokeTests.Host(panel);
            try
            {
                Assert.All(panel.Children.Cast<UserControl>(), child => Assert.True(child.ActualHeight > 0));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WebPreviewCreatesTheWebViewOnlyWhileRendered()
    {
        var model = MediaCaptures.Open(out _);
        model.Inspector.Load(model.Sessions.VisibleRows[2]);
        var preview = (WebPreviewViewModel)model.Inspector.Response!.Tab("webview").Content!;
        StaRunner.RunInLoop(() =>
        {
            preview.IsSourceMode = true;
            var view = new WebPreviewView { DataContext = preview };
            var window = NativeViewSmokeTests.Host(view);
            try
            {
                StaRunner.DoEvents();
                Assert.True(view.IsLoaded);
                Assert.Null(view.LiveWebView);

                preview.IsRenderedMode = true;
                Assert.True(view.LiveWebView is not null, preview.Status);
                for (var attempt = 0; attempt < 250 && preview.Status != WebPreviewViewModel.LoadedStatus; attempt++)
                {
                    Thread.Sleep(20);
                    StaRunner.DoEvents();
                }
                Assert.Equal(WebPreviewViewModel.LoadedStatus, preview.Status);
                Assert.False(view.LiveWebView!.CoreWebView2.Settings.IsScriptEnabled);
                Assert.Equal(Model.WebPreviewPolicy.PreviewUri.AbsoluteUri, view.LiveWebView.Source.AbsoluteUri);

                preview.IsSourceMode = true;
                Assert.Null(view.LiveWebView);
                preview.IsRenderedMode = true;
                Assert.NotNull(view.LiveWebView);
                preview.Deactivate();
                Assert.Null(view.LiveWebView);
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void TabContentIsExposedToUiAutomation()
    {
        var model = MediaCaptures.Open(out _);
        model.Inspector.Load(model.Sessions.VisibleRows[3]);
        var pane = model.Inspector.Request!;
        pane.Select("auth");
        StaRunner.Run(() =>
        {
            var view = new MessagePaneView { DataContext = pane };
            var window = NativeViewSmokeTests.Host(view);
            try
            {
                var names = AutomationNames(UIElementAutomationPeer.CreatePeerForElement(view)).ToList();
                Assert.Contains("Reveal full authentication header values", names);
                Assert.Contains(names, name => name.StartsWith("Copy", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }

    internal static IEnumerable<string> AutomationNames(AutomationPeer? peer)
    {
        if (peer is null)
        {
            yield break;
        }
        yield return peer.GetName();
        foreach (var child in peer.GetChildren() ?? new List<AutomationPeer>())
        {
            foreach (var name in AutomationNames(child))
            {
                yield return name;
            }
        }
    }
}
