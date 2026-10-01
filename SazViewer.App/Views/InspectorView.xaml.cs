using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>The session inspector: header navigation, session details and the Request / Response panes.</summary>
internal partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
        // Keep keyboard focus on navigation: when the focused Previous/Next button becomes disabled at an end,
        // move focus to the other one instead of losing it.
        PreviousButton.IsEnabledChanged += (_, e) => HandOff(PreviousButton, NextButton, e);
        NextButton.IsEnabledChanged += (_, e) => HandOff(NextButton, PreviousButton, e);
    }

    /// <summary>Raised when the inspector asks to return focus to the session grid (after closing).</summary>
    public event EventHandler? FocusGridRequested;

    public void FocusSearch() => SearchBar.FocusQuery();

    /// <summary>Focuses the first useful control: the selected Request/Response tab.</summary>
    public void FocusContent()
    {
        if (SideTabs.IsVisible && SideTabs.ItemContainerGenerator.ContainerFromIndex(SideTabs.SelectedIndex) is TabItem side)
        {
            side.Focus();
        }
        else
        {
            CloseButton.Focus();
        }
    }

    internal static bool IsNavigationKey(KeyEventArgs e, out int delta)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        delta = key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0
        };
        return delta != 0 && Keyboard.Modifiers == ModifierKeys.Alt;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not InspectorViewModel inspector)
        {
            return;
        }
        if (IsNavigationKey(e, out var delta))
        {
            inspector.Navigate(delta);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && inspector.IsHttp)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !e.Handled && !(e.OriginalSource is TextBox { Text.Length: > 0 }))
        {
            inspector.Close();
            FocusGridRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private static void HandOff(Button source, Button other, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && source.IsKeyboardFocused && other.IsEnabled)
        {
            other.Focus();
        }
    }
}
