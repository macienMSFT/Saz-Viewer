using System.Windows;
using SazViewer.App.Themes;
using SazViewer.App.ViewModels;

namespace SazViewer.App;

internal partial class AdvancedFilterWindow : Window
{
    private readonly UiPreferences preferences;

    public AdvancedFilterWindow(
        string captureName,
        AdvancedFilterViewModel model,
        UiPreferences preferences,
        Window anchor)
    {
        InitializeComponent();
        this.preferences = preferences;
        Title = $"Filter sessions \u2013 {captureName}";
        FilterView.DataContext = model;
        RestorePosition(anchor);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closed += (_, _) => SavePosition();
    }

    private void RestorePosition(Window anchor)
    {
        if (preferences.FilterWindowBounds is { } saved)
        {
            var work = InspectorWindow.WorkAreaFor(anchor);
            Width = Math.Clamp(saved.Width, MinWidth, work.Width);
            Height = Math.Clamp(saved.Height, MinHeight, work.Height);
            Left = Math.Clamp(saved.Left, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(saved.Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
            return;
        }

        var area = InspectorWindow.WorkAreaFor(anchor);
        var anchorWidth = anchor.ActualWidth > 0 ? anchor.ActualWidth : anchor.Width;
        var anchorHeight = anchor.ActualHeight > 0 ? anchor.ActualHeight : anchor.Height;
        Left = Math.Clamp(anchor.Left + ((anchorWidth - Width) / 2), area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(anchor.Top + ((anchorHeight - Height) / 2), area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void SavePosition()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            preferences.FilterWindowBounds = bounds;
        }
    }
}
