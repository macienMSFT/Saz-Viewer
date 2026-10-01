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
