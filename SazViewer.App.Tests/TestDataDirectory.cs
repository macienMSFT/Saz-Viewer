using System.Runtime.CompilerServices;

namespace SazViewer.App.Tests;

/// <summary>
/// Points <see cref="AppPaths.DataDirectory"/> (e.g. the WebView2 user data folder used by in-process view
/// tests) at a test-only folder so tests never share state with a real SazViewer instance.
/// </summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryOverrideVariable)))
        {
            Environment.SetEnvironmentVariable(
                AppPaths.DataDirectoryOverrideVariable,
                Path.Combine(Path.GetTempPath(), "SazViewer.App.Tests", "data"));
        }
    }
}
