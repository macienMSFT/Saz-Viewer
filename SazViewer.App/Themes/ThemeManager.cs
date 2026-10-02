using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SazViewer.App.Themes;

internal enum AppTheme
{
    Light,
    Dark,
    HighContrast
}

/// <summary>
/// Swaps the active palette dictionary (the first merged dictionary of the application resources). All native
/// views reference palette brushes through DynamicResource, so a swap restyles open windows immediately.
/// Like the report, the theme follows Windows until the user toggles it; the explicit choice is remembered in
/// <see cref="UiPreferences"/>. Windows high contrast overrides both with a palette built from system colors.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static bool listening;

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event EventHandler? ThemeChanged;

    public static bool IsDark => Current == AppTheme.Dark;

    public static bool IsHighContrast => Current == AppTheme.HighContrast;

    /// <summary>The toggle's accessible label: the theme it switches to.</summary>
    public static string ToggleLabel => IsHighContrast
        ? "Theme follows Windows high contrast"
        : IsDark ? "Switch to light theme" : "Switch to dark theme";

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

    /// <summary>The theme to show: high contrast, else the remembered choice, else the Windows app mode.</summary>
    public static AppTheme Resolve(string? preference, bool highContrast, Func<AppTheme> system) =>
        highContrast ? AppTheme.HighContrast
        : preference == "dark" ? AppTheme.Dark
        : preference == "light" ? AppTheme.Light
        : system();

    /// <summary>Applies the effective theme and follows Windows theme / high contrast changes from now on.</summary>
    public static void Initialize()
    {
        Refresh();
        if (listening)
        {
            return;
        }
        listening = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                Application.Current?.Dispatcher.BeginInvoke(Refresh);
            }
        };
        if (Application.Current is { } app)
        {
            app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    /// <summary>Re-applies the effective theme (after a system change or a preference change).</summary>
    public static void Refresh() =>
        Apply(Resolve(UiPreferences.Current.Theme, SystemParameters.HighContrast, SystemTheme));

    /// <summary>The light-bulb toggle: switches between light and dark and remembers the choice.</summary>
    public static void Toggle()
    {
        if (IsHighContrast)
        {
            return;
        }
        UiPreferences.Current.Theme = IsDark ? "light" : "dark";
        Refresh();
    }

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }
        var palette = theme == AppTheme.HighContrast ? HighContrastPalette() : new ResourceDictionary { Source = PaletteUri(theme) };
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
        foreach (Window window in app.Windows)
        {
            ApplyTitleBar(window);
        }
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Matches the window's title bar to the theme (Windows 10 20H1+ immersive dark mode; ignored elsewhere).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    /// <summary>Maps every palette key to a Windows high contrast system color.</summary>
    internal static ResourceDictionary HighContrastPalette()
    {
        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        var highlightText = SystemColors.HighlightTextColor;
        var hot = SystemColors.HotTrackColor;
        var palette = new ResourceDictionary();
        void Set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            palette[key] = brush;
        }
        foreach (var key in new[] { "Saz.Bg", "Saz.Panel", "Saz.Panel2", "Saz.Hover", "Saz.InfoBg", "Saz.WarningBg" })
        {
            Set(key, window);
        }
        foreach (var key in new[]
        {
            "Saz.Text", "Saz.Muted", "Saz.Line", "Saz.Warn", "Saz.DirClient", "Saz.DirServer", "Saz.ProtocolBinary",
            "Saz.ProtocolKind", "Saz.SynBlue", "Saz.SynGreen", "Saz.SynNumber", "Saz.SynPunct", "Saz.SynPurple",
            "Saz.SynRed", "Saz.SynString"
        })
        {
            Set(key, text);
        }
        Set("Saz.Accent", hot);
        Set("Saz.AccentText", window);
        Set("Saz.Selected", highlight);
        Set("Saz.SelectedText", highlightText);
        Set("Saz.MatchBg", highlight);
        Set("Saz.MatchFg", highlightText);
        Set("Saz.CurrentBg", hot);
        Set("Saz.CurrentFg", window);
        return palette;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
        {
            Application.Current?.Dispatcher.BeginInvoke(Refresh);
        }
    }
}
