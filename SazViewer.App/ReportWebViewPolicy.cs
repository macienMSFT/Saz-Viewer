namespace SazViewer.App;

/// <summary>
/// Pure URI decisions for the hosted report. The report is served from memory at a synthetic HTTPS
/// origin (a reserved <c>.invalid</c> name that can never resolve); every other request, navigation,
/// frame, window, and download is refused by <see cref="SecureReportSession"/>.
/// </summary>
internal static class ReportWebViewPolicy
{
    public const string ReportHost = "saz-viewer.invalid";
    public const string ReportPath = "/report.html";
    public static readonly Uri ReportUri = new($"https://{ReportHost}{ReportPath}");

    private static readonly HashSet<string> AllowedContextMenuItems = new(StringComparer.Ordinal)
    {
        "copy", "cut", "paste", "pasteAndMatchStyle", "selectAll", "undo", "redo"
    };

    /// <summary>True only for the report document itself; a fragment (inspector state) is allowed, a query is not.</summary>
    public static bool IsReportDocument(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && parsed.IsDefaultPort
        && string.Equals(parsed.Host, ReportHost, StringComparison.OrdinalIgnoreCase)
        && string.Equals(parsed.AbsolutePath, ReportPath, StringComparison.Ordinal)
        && string.IsNullOrEmpty(parsed.Query)
        && string.IsNullOrEmpty(parsed.UserInfo);

    /// <summary>Sub-frames may only host inline documents (the WebView tab's sandboxed <c>srcdoc</c>).</summary>
    public static bool IsAllowedFrameNavigation(string? uri) =>
        string.Equals(uri, "about:srcdoc", StringComparison.Ordinal)
        || string.Equals(uri, "about:blank", StringComparison.Ordinal);

    /// <summary>
    /// A user dropping a file on WebView2 surfaces as a user-initiated navigation to a <c>file:</c> URI.
    /// Returns the local path when that file is a .saz capture so the app can open it instead.
    /// </summary>
    public static bool TryGetDroppedCapturePath(string? uri, bool isUserInitiated, out string path)
    {
        path = "";
        if (!isUserInitiated
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !parsed.IsFile)
        {
            return false;
        }
        var localPath = parsed.LocalPath;
        if (!localPath.EndsWith(".saz", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(localPath))
        {
            return false;
        }
        path = localPath;
        return true;
    }

    public static bool IsAllowedContextMenuItem(string name) => AllowedContextMenuItems.Contains(name);
}
