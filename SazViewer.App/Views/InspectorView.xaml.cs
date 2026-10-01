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
        SizeChanged += (_, _) => UpdateWidthClass();
        DataContextChanged += (_, _) =>
        {
            UpdateWidthClass();
            ArrangeSplit();
        };
    }

    /// <summary>Minimum width of each side-by-side pane (the report's MIN_PANE).</summary>
    internal const double MinPaneWidth = 320;

    private bool? arrangedNarrow;

    /// <summary>Raised when the inspector asks to return focus to the session grid (after closing).</summary>
    public event EventHandler? FocusGridRequested;

    /// <summary>Focuses the search box of the visible pane (in split view, the pane holding focus; else Request).</summary>
    public void FocusSearch()
    {
        if (DataContext is InspectorViewModel { IsSplit: true })
        {
            (ResponseSplitPane.IsKeyboardFocusWithin ? ResponseSearchBar : RequestSearchBar).FocusQuery();
        }
        else
        {
            SearchBar.FocusQuery();
        }
    }

    /// <summary>Focuses the first useful control: the selected Request/Response tab, or the Request pane when split.</summary>
    public void FocusContent()
    {
        if (SideTabs.IsVisible && SideTabs.ItemContainerGenerator.ContainerFromIndex(SideTabs.SelectedIndex) is TabItem side)
        {
            side.Focus();
        }
        else if (SplitBody.IsVisible && RequestSplitPane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
        }
        else
        {
            CloseButton.Focus();
        }
    }

    private void UpdateWidthClass()
    {
        if (DataContext is not InspectorViewModel inspector || ActualWidth <= 0)
        {
            return;
        }
        // The breakpoint applies to the window (the report uses the viewport); fall back to our own width.
        var width = Window.GetWindow(this)?.ActualWidth ?? ActualWidth;
        inspector.WidthClass = UiPreferences.WidthClassFor(width);
        ArrangeSplit();
    }

    /// <summary>Side by side with a splitter when wide; stacked without a splitter when narrow.</summary>
    private void ArrangeSplit()
    {
        var narrow = DataContext is InspectorViewModel { IsNarrow: true };
        if (arrangedNarrow == narrow)
        {
            return;
        }
        arrangedNarrow = narrow;
        PaneSplitter.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ResponseSplitPane, narrow ? 0 : 2);
        Grid.SetRow(ResponseSplitPane, narrow ? 2 : 0);
        ResponseSplitPane.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        SplitFirstColumn.MinWidth = narrow ? 0 : MinPaneWidth;
        SplitSecondColumn.MinWidth = narrow ? 0 : MinPaneWidth;
        SplitFirstColumn.Width = new GridLength(1, GridUnitType.Star);
        SplitSecondColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SplitSecondRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>Arrow keys move the splitter 12 px (40 with Shift); Home/End jump to the minimum pane widths.</summary>
    private void OnSplitterKeyDown(object sender, KeyEventArgs e)
    {
        var first = SplitFirstColumn.ActualWidth;
        var total = first + SplitSecondColumn.ActualWidth;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 40 : 12;
        double? target = e.Key switch
        {
            Key.Left or Key.Up => first - step,
            Key.Right or Key.Down => first + step,
            Key.Home => MinPaneWidth,
            Key.End => total - MinPaneWidth,
            _ => null
        };
        if (target is not { } wanted || total <= 0)
        {
            return;
        }
        var clamped = total < 2 * MinPaneWidth ? total / 2 : Math.Clamp(wanted, MinPaneWidth, total - MinPaneWidth);
        SplitFirstColumn.Width = new GridLength(clamped, GridUnitType.Star);
        SplitSecondColumn.Width = new GridLength(total - clamped, GridUnitType.Star);
        e.Handled = true;
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
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && (inspector.IsHttp || inspector.IsWebSocket))
        {
            if (inspector.IsWebSocket)
            {
                WebSocketView.FocusSearch();
            }
            else
            {
                FocusSearch();
            }
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
