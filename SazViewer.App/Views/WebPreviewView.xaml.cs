using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SazViewer.App.Themes;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>
/// WebView tab. The sandboxed WebView2 exists only while the tab is shown in Rendered mode: it is created on
/// demand, disposed when the tab is left, the mode switches to Source, or the preview tries to navigate, and
/// recreated when the app theme changes.
/// </summary>
internal partial class WebPreviewView : UserControl
{
    private WebPreviewViewModel? model;
    private WebView2? webView;
    private int generation;

    public WebPreviewView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>The live WebView2, if any (UI tests check that it is released).</summary>
    internal WebView2? LiveWebView => webView;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        ThemeManager.ThemeChanged += OnThemeChanged;
        Attach(DataContext as WebPreviewViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
        Attach(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Attach(e.NewValue as WebPreviewViewModel);
        }
    }

    private void Attach(WebPreviewViewModel? next)
    {
        if (ReferenceEquals(model, next))
        {
            return;
        }
        if (model is not null)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            model.Deactivate();
        }
        Close();
        model = next;
        if (model is not null)
        {
            model.PropertyChanged += OnModelPropertyChanged;
            model.Activate();
            Sync();
        }
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebPreviewViewModel.ShouldRender))
        {
            Sync();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (webView is not null)
        {
            Close();
            Sync();
        }
    }

    private void Sync()
    {
        if (model?.ShouldRender == true)
        {
            if (webView is null)
            {
                _ = OpenAsync(model);
            }
        }
        else
        {
            Close();
        }
    }

    private async Task OpenAsync(WebPreviewViewModel target)
    {
        var token = ++generation;
        var view = new WebView2
        {
            AllowExternalDrop = false,
            DefaultBackgroundColor = BackgroundColor(),
            Focusable = true
        };
        System.Windows.Automation.AutomationProperties.SetName(view, "Inert HTML preview");
        webView = view;
        Host.Child = view;
        try
        {
            var environment = await WebViewEnvironment.GetAsync();
            await view.EnsureCoreWebView2Async(environment);
            if (token != generation)
            {
                return;
            }
            var session = new WebPreviewSession(view.CoreWebView2, target.RenderedDocument(ThemeManager.Current));
            session.Loaded += (_, _) =>
            {
                if (token == generation)
                {
                    target.ReportLoaded();
                }
            };
            session.NavigationBlocked += (_, _) =>
            {
                if (token == generation)
                {
                    target.ReportBlockedNavigation();
                }
            };
            session.Failed += (_, message) =>
            {
                if (token == generation)
                {
                    target.ReportFailure(message);
                }
            };
            session.Navigate();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or WebView2RuntimeNotFoundException or ArgumentException)
        {
            if (token == generation)
            {
                Close();
                target.ReportFailure(error is WebView2RuntimeNotFoundException ? "the WebView2 Runtime is not installed" : error.Message);
            }
        }
    }

    private void Close()
    {
        generation++;
        if (webView is null)
        {
            return;
        }
        Host.Child = null;
        webView.Dispose();
        webView = null;
    }

    private System.Drawing.Color BackgroundColor() =>
        TryFindResource("Saz.Bg") is SolidColorBrush { Color: var color }
            ? System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B)
            : System.Drawing.Color.White;
}
