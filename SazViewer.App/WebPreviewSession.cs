using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SazViewer.App.Model;

namespace SazViewer.App;

/// <summary>
/// Applies <see cref="WebPreviewPolicy"/> to one <see cref="CoreWebView2"/> for the WebView tab: scripts,
/// host objects, web messages, context menus and (in Release) dev tools are off; the sanitized inert document
/// is served once from memory and every other request gets 403 so no network request is ever made; frame
/// navigation, popups, downloads, permissions, authentication prompts and external schemes are refused.
/// Any top-level navigation after the initial load reports <see cref="NavigationBlocked"/>, and the owner
/// closes the preview.
/// </summary>
internal sealed class WebPreviewSession
{
    private readonly CoreWebView2 core;
    private readonly byte[] document;
    private readonly WebPreviewPolicy policy = new();
    private bool closed;

    public WebPreviewSession(CoreWebView2 core, string html)
    {
        this.core = core;
        document = System.Text.Encoding.UTF8.GetBytes(html);
        WebPreviewPolicy.Settings.ApplyTo(core.Settings);

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
        };
        core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
        };
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.ContextMenuRequested += (_, e) => e.Handled = true;
        core.ProcessFailed += (_, e) => Raise(Failed, $"the preview process stopped ({e.ProcessFailedKind})");
    }

    public event EventHandler? Loaded;

    public event EventHandler? NavigationBlocked;

    public event EventHandler<string>? Failed;

    public void Navigate() => core.Navigate(WebPreviewPolicy.PreviewUri.AbsoluteUri);

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        e.Response = policy.ServeDocument(e.Request.Method, e.Request.Uri)
            ? core.Environment.CreateWebResourceResponse(new MemoryStream(document, writable: false), 200, "OK", WebPreviewPolicy.ResponseHeaders)
            : core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (policy.AllowNavigation(e.Uri))
        {
            return;
        }
        e.Cancel = true;
        RaiseOnce(NavigationBlocked);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            Raise(Loaded);
        }
        else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            Raise(Failed, $"navigation failed ({e.WebErrorStatus})");
        }
    }

    private void RaiseOnce(EventHandler? handler)
    {
        if (!closed)
        {
            closed = true;
            Raise(handler);
        }
    }

    // Deferred so the owner may dispose the WebView2 without doing so inside its own event callback.
    private void Raise(EventHandler? handler) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(() => handler?.Invoke(this, EventArgs.Empty));

    private void Raise(EventHandler<string>? handler, string message) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(() => handler?.Invoke(this, message));
}
