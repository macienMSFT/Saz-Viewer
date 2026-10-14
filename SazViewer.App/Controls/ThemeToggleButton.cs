using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SazViewer.App.Themes;

namespace SazViewer.App.Controls;

/// <summary>
/// Sun/moon theme toggle: switches between light and dark (remembered), showing the icon of the theme it will
/// switch to (sun in dark, moon in light). Disabled while Windows high contrast is on, which always wins.
/// </summary>
internal sealed class ThemeToggleButton : Button
{
    private static readonly Geometry Sun = Geometry.Parse(
        "M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8Z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4");

    private static readonly Geometry Moon = Geometry.Parse("M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z");

    private readonly System.Windows.Shapes.Path icon;

    public ThemeToggleButton()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        icon = new System.Windows.Shapes.Path
        {
            Data = Moon,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        icon.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(icon);
        Content = new Viewbox { Width = 18, Height = 18, Child = canvas };
        Padding = new Thickness(6, 3, 6, 3);
        Click += (_, _) => ThemeManager.Toggle();
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Sync();
        };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        Sync();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        var label = ThemeManager.ToggleLabel;
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetItemStatus(this, ThemeManager.IsDark ? "Dark theme" : ThemeManager.IsHighContrast ? "High contrast" : "Light theme");
        ToolTip = label;
        IsEnabled = !ThemeManager.IsHighContrast;
        icon.Data = ThemeManager.IsDark ? Sun : Moon;
    }
}
