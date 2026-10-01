namespace SazViewer.App;

/// <summary>
/// Per-user locations under %LOCALAPPDATA%\SazViewer (inherits the user-only profile ACL).
/// <c>SAZVIEWER_DATA_DIR</c> (a fully qualified path) overrides the root for isolated test runs.
/// </summary>
internal static class AppPaths
{
    public const string DataDirectoryOverrideVariable = "SAZVIEWER_DATA_DIR";

    public static string DataDirectory { get; } = ResolveDataDirectory(
        Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create));

    public static string WebViewUserDataDirectory => Path.Combine(DataDirectory, "WebView2");

    public static string RecentFilesPath => Path.Combine(DataDirectory, "recent.json");

    public static string PreferencesPath => Path.Combine(DataDirectory, "preferences.json");

    /// <summary>The app's own <c>SazViewer.App.exe</c> (apphost), or null when it cannot be found.</summary>
    public static string? ExecutablePath { get; } = ResolveExecutablePath(AppContext.BaseDirectory);

    internal static string? ResolveExecutablePath(string baseDirectory)
    {
        var candidate = Path.Combine(baseDirectory, FileAssociationService.ExecutableName);
        return Path.IsPathFullyQualified(candidate) && File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    internal static string ResolveDataDirectory(string? overridePath, string localApplicationData) =>
        !string.IsNullOrWhiteSpace(overridePath) && Path.IsPathFullyQualified(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(localApplicationData, "SazViewer");
}
