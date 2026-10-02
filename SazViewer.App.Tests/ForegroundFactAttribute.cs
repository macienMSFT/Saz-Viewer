using System.Runtime.InteropServices;

namespace SazViewer.App.Tests;

internal sealed class ForegroundFactAttribute : FactAttribute
{
    public ForegroundFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || GetForegroundWindow() == IntPtr.Zero)
        {
            Skip = "Requires an interactive Windows desktop with a foreground window.";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
