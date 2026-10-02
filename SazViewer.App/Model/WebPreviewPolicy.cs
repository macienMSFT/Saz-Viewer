using Microsoft.Web.WebView2.Core;

namespace SazViewer.App.Model;

/// <summary>
/// The WebView tab's sandbox policy, kept free of WebView2 objects so it is unit-testable. The preview is
/// served once from memory at a synthetic, unresolvable URI; every other request is answered with 403, and
/// any navigation other than that single initial load closes the preview.
/// </summary>
internal sealed class WebPreviewPolicy
{
    public static readonly Uri PreviewUri = new("https://webview-preview.sazviewer.invalid/document.html");

    public const string ResponseHeaders =
        "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer";

    private bool navigated;
    private bool served;

    /// <summary>The WebView2 settings the preview runs with. Dev tools exist only in Debug builds.</summary>
    public static WebPreviewSettings Settings { get; } = new(
        IsScriptEnabled: false,
#if DEBUG
        AreDevToolsEnabled: true,
#else
        AreDevToolsEnabled: false,
#endif
        AreHostObjectsAllowed: false,
        IsWebMessageEnabled: false,
        AreDefaultContextMenusEnabled: false,
        AreBrowserAcceleratorKeysEnabled: false,
        IsStatusBarEnabled: false,
        IsGeneralAutofillEnabled: false,
        IsPasswordAutosaveEnabled: false,
        IsSwipeNavigationEnabled: false,
        IsBuiltInErrorPageEnabled: false,
        IsReputationCheckingRequired: false,
        AllowExternalDrop: false);

    public static bool IsPreviewDocument(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && string.IsNullOrEmpty(parsed.Query)
        && string.IsNullOrEmpty(parsed.Fragment)
        && Uri.Compare(parsed, PreviewUri, UriComponents.AbsoluteUri, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Top-level navigation: only the first navigation, to the preview URI, is allowed.</summary>
    public bool AllowNavigation(string? uri)
    {
        if (navigated || !IsPreviewDocument(uri))
        {
            navigated = true;
            return false;
        }
        navigated = true;
        return true;
    }

    /// <summary>Resource requests: the document is served exactly once (a GET for the preview URI).</summary>
    public bool ServeDocument(string? method, string? uri)
    {
        if (served || method != "GET" || !IsPreviewDocument(uri))
        {
            return false;
        }
        served = true;
        return true;
    }
}

internal sealed record WebPreviewSettings(
    bool IsScriptEnabled,
    bool AreDevToolsEnabled,
    bool AreHostObjectsAllowed,
    bool IsWebMessageEnabled,
    bool AreDefaultContextMenusEnabled,
    bool AreBrowserAcceleratorKeysEnabled,
    bool IsStatusBarEnabled,
    bool IsGeneralAutofillEnabled,
    bool IsPasswordAutosaveEnabled,
    bool IsSwipeNavigationEnabled,
    bool IsBuiltInErrorPageEnabled,
    bool IsReputationCheckingRequired,
    bool AllowExternalDrop)
{
    public void ApplyTo(CoreWebView2Settings settings)
    {
        settings.IsScriptEnabled = IsScriptEnabled;
        settings.AreDevToolsEnabled = AreDevToolsEnabled;
        settings.AreHostObjectsAllowed = AreHostObjectsAllowed;
        settings.IsWebMessageEnabled = IsWebMessageEnabled;
        settings.AreDefaultContextMenusEnabled = AreDefaultContextMenusEnabled;
        settings.AreBrowserAcceleratorKeysEnabled = AreBrowserAcceleratorKeysEnabled;
        settings.IsStatusBarEnabled = IsStatusBarEnabled;
        settings.IsGeneralAutofillEnabled = IsGeneralAutofillEnabled;
        settings.IsPasswordAutosaveEnabled = IsPasswordAutosaveEnabled;
        settings.IsSwipeNavigationEnabled = IsSwipeNavigationEnabled;
        settings.IsBuiltInErrorPageEnabled = IsBuiltInErrorPageEnabled;
        settings.IsReputationCheckingRequired = IsReputationCheckingRequired;
    }
}
