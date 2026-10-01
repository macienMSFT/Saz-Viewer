using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SazViewer.App.Themes;

namespace SazViewer.App.Controls;

/// <summary>
/// The report's light-bulb theme toggle: switches between light and dark (remembered), with the bulb lit in the
/// dark theme. Disabled while Windows high contrast is on, which always wins.
/// </summary>
internal sealed class ThemeToggleButton : Button
{
    private static readonly Geometry Bulb = Geometry.Parse(
        "M9 18h6M10 21h4M8.5 15.5A6 6 0 1 1 15.5 15.5C14.6 16.2 14 17 14 18h-4c0-1-.6-1.8-1.5-2.5Z");

    private readonly Ellipse core;

    public ThemeToggleButton()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        var outline = new System.Windows.Shapes.Path
        {
            Data = Bulb,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        outline.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        core = new Ellipse { Width = 4.8, Height = 4.8 };
        Canvas.SetLeft(core, 12 - 2.4);
        Canvas.SetTop(core, 11 - 2.4);
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(outline);
        canvas.Children.Add(core);
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
        core.Fill = ThemeManager.IsDark ? (Brush)FindResourceOrDefault("Saz.SynNumber") : Brushes.Transparent;
    }

    private object FindResourceOrDefault(string key) => TryFindResource(key) ?? Foreground;
}
