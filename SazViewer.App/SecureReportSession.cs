using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace SazViewer.App;

/// <summary>Single shared WebView2 environment with a per-user data folder under %LOCALAPPDATA%\SazViewer.</summary>
internal static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? environment;

    public static Task<CoreWebView2Environment> GetAsync()
    {
        Dispatcher.CurrentDispatcher.VerifyAccess();
        return environment ??= CreateAsync();
    }

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(AppPaths.WebViewUserDataDirectory);
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = false,
            // Keeps renderer crash dumps (which could contain capture data) local instead of uploading them.
            IsCustomCrashReportingEnabled = true
        };
        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.WebViewUserDataDirectory,
            options: options);
    }
}

/// <summary>
/// Locks a <see cref="CoreWebView2"/> down to the in-memory report: serves the report document from
/// memory, answers every other request with 403 (so no network request is ever made), and refuses
/// navigation, external schemes, downloads, permissions, authentication prompts, and unmanaged windows.
/// </summary>
internal sealed class SecureReportSession
{
    private readonly CoreWebView2 core;
    private readonly Func<ReportDocument?> documentProvider;

    public SecureReportSession(CoreWebView2 core, Func<ReportDocument?> documentProvider)
    {
        this.core = core;
        this.documentProvider = documentProvider;
        ApplySettings(core.Settings);

        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.ContextMenuRequested += OnContextMenuRequested;
    }

    /// <summary>Raised when the user drops a .saz file onto the report surface.</summary>
    public event EventHandler<string>? CaptureDropped;

    /// <summary>
    /// Creates an app-controlled window for the report's "Open in new tab" action. The returned
    /// <see cref="CoreWebView2"/> must already be wrapped by its own <see cref="SecureReportSession"/>.
    /// When unset, new windows are refused and the report shows its built-in "blocked" status.
    /// </summary>
    public Func<Task<CoreWebView2?>>? CreatePopupAsync { get; set; }

    public void NavigateToReport() => core.Navigate(ReportWebViewPolicy.ReportUri.AbsoluteUri);

    private static void ApplySettings(CoreWebView2Settings settings)
    {
#if DEBUG
        const bool developerFeatures = true;
#else
        const bool developerFeatures = false;
#endif
        settings.AreDevToolsEnabled = developerFeatures;
        settings.AreBrowserAcceleratorKeysEnabled = developerFeatures;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.IsScriptEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        // Only app-generated, in-memory content is ever loaded, so there is nothing for SmartScreen to
        // check and the synthetic report URL should not be sent to a reputation service.
        settings.IsReputationCheckingRequired = false;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var document = documentProvider();
        if (document is not null
            && e.Request.Method == "GET"
            && ReportWebViewPolicy.IsReportDocument(e.Request.Uri))
        {
            e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(document.Utf8, writable: false),
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer");
            return;
        }

        e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (ReportWebViewPolicy.IsReportDocument(e.Uri))
        {
            return;
        }
        e.Cancel = true;
        if (ReportWebViewPolicy.TryGetDroppedCapturePath(e.Uri, e.IsUserInitiated, out var path))
        {
            RaiseCaptureDropped(path);
        }
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!ReportWebViewPolicy.IsAllowedFrameNavigation(e.Uri))
        {
            e.Cancel = true;
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (ReportWebViewPolicy.TryGetDroppedCapturePath(e.Uri, e.IsUserInitiated, out var path))
        {
            RaiseCaptureDropped(path);
            return;
        }
        if (CreatePopupAsync is null || !ReportWebViewPolicy.IsReportDocument(e.Uri))
        {
            return;
        }

        using var deferral = e.GetDeferral();
        try
        {
            var popup = await CreatePopupAsync();
            if (popup is not null)
            {
                e.NewWindow = popup;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Leaving NewWindow unset makes window.open return null; the report shows its blocked-tab status.
        }
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var items = e.MenuItems;
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            if (item.Kind != CoreWebView2ContextMenuItemKind.Separator
                && !ReportWebViewPolicy.IsAllowedContextMenuItem(item.Name))
            {
                items.RemoveAt(index);
            }
        }
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var isSeparator = items[index].Kind == CoreWebView2ContextMenuItemKind.Separator;
            if (isSeparator
                && (index == 0
                    || index == items.Count - 1
                    || items[index - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
            {
                items.RemoveAt(index);
            }
        }
        if (items.Count == 0)
        {
            e.Handled = true;
        }
    }

    private void RaiseCaptureDropped(string path) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(() => CaptureDropped?.Invoke(this, path));
}
