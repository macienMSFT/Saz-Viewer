using System.Windows;
using Microsoft.Win32;

namespace SazViewer.App.Themes;

internal enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// Swaps the active palette dictionary (the first merged dictionary of the application resources). All native
/// views reference palette brushes through DynamicResource, so a swap restyles open windows immediately.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event EventHandler? ThemeChanged;

    public static Uri PaletteUri(AppTheme theme) =>
        new($"pack://application:,,,/SazViewer.App;component/Themes/{theme}.xaml", UriKind.Absolute);

    /// <summary>Reads the Windows app mode (light/dark); light when unknown.</summary>
    public static AppTheme SystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return AppTheme.Light;
        }
    }

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }
        var palette = new ResourceDictionary { Source = PaletteUri(theme) };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(palette);
        }
        else
        {
            merged[0] = palette;
        }
        Current = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
