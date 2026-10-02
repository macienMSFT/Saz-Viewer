using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>
/// WebSocket inspector: resizable traffic / detail panes (stacked below 900 px or whenever hosted in the
/// right pane), the content-fit message list and the payload filter (Enter / Shift+Enter select matches).
/// </summary>
internal partial class WebSocketInspectorView : UserControl
{
    public const double NarrowWidth = 900;

    private bool? narrow;
    private bool forceStacked;

    public WebSocketInspectorView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is WebSocketInspectorViewModel old)
            {
                old.PropertyChanged -= OnModelPropertyChanged;
            }
            if (e.NewValue is WebSocketInspectorViewModel model)
            {
                model.PropertyChanged += OnModelPropertyChanged;
                FitColumns(model.Items);
                Dispatcher.BeginInvoke(ScrollToSelection);
            }
        };
    }

    public bool IsNarrow => narrow == true;

    internal bool ForceStacked
    {
        get => forceStacked;
        set
        {
            if (forceStacked == value)
            {
                return;
            }
            forceStacked = value;
            ApplyLayout(forceStacked || ActualWidth < NarrowWidth);
        }
    }

    public void FocusSearch()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebSocketInspectorViewModel.SelectedItem))
        {
            Dispatcher.BeginInvoke(ScrollToSelection);
        }
    }

    private void ScrollToSelection()
    {
        if (MessageGrid.SelectedItem is { } item)
        {
            MessageGrid.ScrollIntoView(item);
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not WebSocketInspectorViewModel model)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            var pending = FilterBox.GetBindingExpression(TextBox.TextProperty);
            if (model.Query != FilterBox.Text)
            {
                pending?.UpdateSource();
            }
            else
            {
                model.SelectRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            }
            e.Handled = true;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyLayout(ForceStacked || e.NewSize.Width < NarrowWidth);

    /// <summary>Side by side with a splitter on wide layouts; stacked (traffic above detail) on narrow ones.</summary>
    private void ApplyLayout(bool isNarrow)
    {
        if (narrow == isNarrow)
        {
            return;
        }
        narrow = isNarrow;
        if (isNarrow)
        {
            Grid.SetColumnSpan(TrafficPane, 3);
            Grid.SetRow(DetailPane, 2);
            Grid.SetColumn(DetailPane, 0);
            Grid.SetColumnSpan(DetailPane, 3);
            Splitter.Visibility = Visibility.Collapsed;
            TrafficColumn.MinWidth = 0;
            DetailColumn.MinWidth = 0;
            TrafficRow.Height = new GridLength(38, GridUnitType.Star);
            TrafficRow.MinHeight = 180;
            GapRow.Height = new GridLength(10);
            DetailRow.Height = new GridLength(62, GridUnitType.Star);
        }
        else
        {
            Grid.SetColumnSpan(TrafficPane, 1);
            Grid.SetRow(DetailPane, 0);
            Grid.SetColumn(DetailPane, 2);
            Grid.SetColumnSpan(DetailPane, 1);
            Splitter.Visibility = Visibility.Visible;
            TrafficColumn.MinWidth = 280;
            DetailColumn.MinWidth = 320;
            var ratio = Math.Clamp(WebSocketInspectorViewModel.SplitRatio, .05, .95);
            TrafficColumn.Width = new GridLength(ratio, GridUnitType.Star);
            DetailColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
            TrafficRow.Height = new GridLength(1, GridUnitType.Star);
            TrafficRow.MinHeight = 0;
            GapRow.Height = new GridLength(0);
            DetailRow.Height = new GridLength(0);
        }
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => RememberRatio();

    private void OnSplitterKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            RememberRatio();
        }
    }

    private void RememberRatio()
    {
        var total = TrafficColumn.ActualWidth + DetailColumn.ActualWidth;
        if (total > 0)
        {
            WebSocketInspectorViewModel.SplitRatio = TrafficColumn.ActualWidth / total;
        }
    }

    /// <summary>ID, Type and Body fit their widest content; Preview takes the remaining width.</summary>
    private void FitColumns(IReadOnlyList<WebSocketMessageItem> items)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mono = new Typeface((FontFamily)FindResource("Saz.MonoFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var monoBold = new Typeface((FontFamily)FindResource("Saz.MonoFont"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var header = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        const double HeaderChrome = 16 + 1 + 18;
        const double CellChrome = 10 + 6;

        double Measure(string text, Typeface face, double size) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        double Fit(DataGridColumn column, Func<WebSocketMessageItem, string> value, Typeface face, double extra = 0)
        {
            var headerWidth = Measure((string)column.Header, header, 13) + HeaderChrome;
            var widest = items.Select(value).Distinct().OrderByDescending(text => text.Length).Take(8)
                .Select(text => Measure(text, face, 12) + CellChrome + extra).DefaultIfEmpty(0).Max();
            return Math.Ceiling(Math.Max(headerWidth, widest));
        }

        IdColumn.Width = Fit(IdColumn, item => item.IdText, mono, Measure("\u2194", monoBold, 14) + 4);
        TypeColumn.Width = Fit(TypeColumn, item => item.ListType, mono);
        BodyColumn.Width = Fit(BodyColumn, item => item.Body, monoBold);
    }
}
