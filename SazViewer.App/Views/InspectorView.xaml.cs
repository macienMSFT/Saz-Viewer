using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            arranged = null;
            UpdateWidthClass();
            ArrangeSplit();
        };
    }

    /// <summary>Minimum width of each side-by-side pane (the report's MIN_PANE).</summary>
    internal const double MinPaneWidth = 320;
    internal const double MinPaneHeight = 120;

    private SplitArrangement? arranged;
    private bool isRightPane;

    /// <summary>True when hosted to the right of the session grid; split HTTP and WebSocket content then stacks.</summary>
    internal bool IsRightPane
    {
        get => isRightPane;
        set
        {
            if (isRightPane == value)
            {
                return;
            }
            isRightPane = value;
            arranged = null;
            WebSocketView.ForceStacked = value;
            ArrangeSplit();
        }
    }

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

    /// <summary>Side by side when wide, stacked without a splitter when narrow, or stacked and resizable in the right pane.</summary>
    private void ArrangeSplit()
    {
        var next = IsRightPane
            ? SplitArrangement.RightPane
            : DataContext is InspectorViewModel { IsNarrow: true }
                ? SplitArrangement.Narrow
                : SplitArrangement.Wide;
        if (arranged == next)
        {
            return;
        }
        arranged = next;
        Grid.SetColumn(RequestSplitPane, 0);
        Grid.SetRow(RequestSplitPane, 0);
        Grid.SetColumnSpan(RequestSplitPane, next == SplitArrangement.RightPane ? 3 : 1);
        Grid.SetColumn(ResponseSplitPane, next == SplitArrangement.Wide ? 2 : 0);
        Grid.SetRow(ResponseSplitPane, next == SplitArrangement.Wide ? 0 : 2);
        Grid.SetColumnSpan(ResponseSplitPane, next == SplitArrangement.RightPane ? 3 : 1);
        ResponseSplitPane.Margin = next == SplitArrangement.Narrow ? new Thickness(0, 8, 0, 0) : new Thickness(0);

        SplitFirstColumn.MinWidth = next == SplitArrangement.Wide ? MinPaneWidth : 0;
        SplitSecondColumn.MinWidth = next == SplitArrangement.Wide ? MinPaneWidth : 0;
        SplitFirstColumn.Width = new GridLength(1, GridUnitType.Star);
        SplitSecondColumn.Width = next == SplitArrangement.Wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitFirstRow.MinHeight = next == SplitArrangement.RightPane ? MinPaneHeight : 0;
        SplitSecondRow.MinHeight = next == SplitArrangement.RightPane ? MinPaneHeight : 0;

        if (next == SplitArrangement.RightPane)
        {
            var ratio = DataContext is InspectorViewModel inspector ? inspector.Preferences.RightPaneHttpSplitFraction : .50;
            SplitFirstRow.Height = new GridLength(ratio, GridUnitType.Star);
            SplitSplitterRow.Height = GridLength.Auto;
            SplitSecondRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
            Grid.SetColumn(PaneSplitter, 0);
            Grid.SetColumnSpan(PaneSplitter, 3);
            Grid.SetRow(PaneSplitter, 1);
            PaneSplitter.Width = double.NaN;
            PaneSplitter.Height = 6;
            PaneSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PaneSplitter.ResizeDirection = GridResizeDirection.Rows;
            PaneSplitter.BorderThickness = new Thickness(0, 1, 0, 0);
            PaneSplitter.Margin = new Thickness(0, 2, 0, 2);
            PaneSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            SplitFirstRow.Height = new GridLength(1, GridUnitType.Star);
            SplitSplitterRow.Height = next == SplitArrangement.Wide ? GridLength.Auto : new GridLength(0);
            SplitSecondRow.Height = next == SplitArrangement.Narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(PaneSplitter, 1);
            Grid.SetColumnSpan(PaneSplitter, 1);
            Grid.SetRow(PaneSplitter, 0);
            PaneSplitter.Width = 6;
            PaneSplitter.Height = double.NaN;
            PaneSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            PaneSplitter.ResizeDirection = GridResizeDirection.Columns;
            PaneSplitter.BorderThickness = new Thickness(1, 0, 0, 0);
            PaneSplitter.Margin = new Thickness(4, 0, 4, 0);
            PaneSplitter.Visibility = next == SplitArrangement.Wide ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Arrow keys move the splitter 12 px (40 with Shift); Home/End jump to the minimum pane widths.</summary>
    private void OnSplitterKeyDown(object sender, KeyEventArgs e)
    {
        var rightPane = arranged == SplitArrangement.RightPane;
        var first = rightPane ? SplitFirstRow.ActualHeight : SplitFirstColumn.ActualWidth;
        var second = rightPane ? SplitSecondRow.ActualHeight : SplitSecondColumn.ActualWidth;
        var total = first + second;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 40 : 12;
        double? target = e.Key switch
        {
            Key.Up when rightPane => first - step,
            Key.Down when rightPane => first + step,
            Key.Left when !rightPane => first - step,
            Key.Right when !rightPane => first + step,
            Key.Home => rightPane ? MinPaneHeight : MinPaneWidth,
            Key.End => total - (rightPane ? MinPaneHeight : MinPaneWidth),
            _ => null
        };
        if (target is not { } wanted || total <= 0)
        {
            return;
        }
        var minimum = rightPane ? MinPaneHeight : MinPaneWidth;
        var clamped = total < 2 * minimum ? total / 2 : Math.Clamp(wanted, minimum, total - minimum);
        if (rightPane)
        {
            SplitFirstRow.Height = new GridLength(clamped, GridUnitType.Star);
            SplitSecondRow.Height = new GridLength(total - clamped, GridUnitType.Star);
            RememberRightPaneRatio();
        }
        else
        {
            SplitFirstColumn.Width = new GridLength(clamped, GridUnitType.Star);
            SplitSecondColumn.Width = new GridLength(total - clamped, GridUnitType.Star);
        }
        e.Handled = true;
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (arranged == SplitArrangement.RightPane)
        {
            RememberRightPaneRatio();
        }
    }

    private void RememberRightPaneRatio()
    {
        var total = SplitFirstRow.ActualHeight + SplitSecondRow.ActualHeight;
        if (total > 0 && DataContext is InspectorViewModel inspector)
        {
            inspector.Preferences.RightPaneHttpSplitFraction = SplitFirstRow.ActualHeight / total;
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

    private enum SplitArrangement
    {
        Wide,
        Narrow,
        RightPane
    }
}
