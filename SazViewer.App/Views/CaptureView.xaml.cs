using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>
/// Native view of one capture: the session grid with its toolbar and an optional bottom/right inspector.
/// </summary>
internal partial class CaptureView : UserControl
{
    internal const double MinimumGridWidth = 280;
    internal const double MinimumInspectorWidth = 320;
    internal const double MinimumGridHeight = 120;
    internal const double MinimumInspectorHeight = 160;

    private CaptureViewModel? model;
    private SessionViewerLocation? appliedLocation;

    public CaptureView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as CaptureViewModel);
    }

    /// <summary>The inspector view, created on first open so an unopened capture only pays for the grid.</summary>
    internal InspectorView? Inspector { get; private set; }

    private InspectorView EnsureInspector()
    {
        if (Inspector is null)
        {
            Inspector = new InspectorView
            {
                DataContext = model?.Inspector,
                IsRightPane = model?.SessionViewerLocation == SessionViewerLocation.RightPane
            };
            Inspector.FocusGridRequested += (_, _) => FocusSelectedRow();
            InspectorHost.Child = Inspector;
        }
        return Inspector;
    }

    private void Attach(CaptureViewModel? next)
    {
        if (model is not null)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            model.Inspector.PropertyChanged -= OnInspectorPropertyChanged;
            model.Inspector.Loaded -= OnInspectorLoaded;
        }
        model = next;
        if (model is null)
        {
            return;
        }
        if (Inspector is not null)
        {
            Inspector.DataContext = model.Inspector;
        }
        model.PropertyChanged += OnModelPropertyChanged;
        model.Inspector.PropertyChanged += OnInspectorPropertyChanged;
        model.Inspector.Loaded += OnInspectorLoaded;
        FitColumns(model.Rows);
        UpdateInspectorLayout();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CaptureViewModel.SessionViewerLocation) or "" or null)
        {
            UpdateInspectorLayout();
        }
    }

    private void OnInspectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InspectorViewModel.IsOpen) or "" or null)
        {
            UpdateInspectorLayout();
        }
    }

    private void OnInspectorLoaded(object? sender, EventArgs e)
    {
        if (model?.Inspector.Row is { } row)
        {
            if (!ReferenceEquals(SessionGrid.SelectedItem, row))
            {
                SessionGrid.SelectedItem = row;
            }
            SessionGrid.ScrollIntoView(row);
        }
    }

    private void UpdateInspectorLayout()
    {
        var location = model?.SessionViewerLocation ?? SessionViewerLocation.BottomPane;
        if (appliedLocation is { } previous && previous != location && InspectorHost.IsVisible)
        {
            RememberSplitter(previous);
        }
        appliedLocation = location;
        var open = model is { Inspector.IsOpen: true } && location != SessionViewerLocation.NewWindow;
        if (open)
        {
            var inspector = EnsureInspector();
            inspector.IsRightPane = location == SessionViewerLocation.RightPane;
            inspector.DataContext = model?.Inspector;
        }
        else if (location == SessionViewerLocation.NewWindow && Inspector is not null)
        {
            Inspector.IsRightPane = false;
            Inspector.DataContext = null;
        }
        InspectorHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = InspectorHost.Visibility;
        if (!open)
        {
            ShowGridOnly();
        }
        else if (location == SessionViewerLocation.RightPane)
        {
            ShowRightPane();
        }
        else
        {
            ShowBottomPane();
        }
    }

    private void ShowGridOnly()
    {
        Grid.SetRow(SessionGrid, 1);
        Grid.SetRowSpan(SessionGrid, 3);
        Grid.SetColumn(SessionGrid, 0);
        Grid.SetColumnSpan(SessionGrid, 3);
        GridRowDefinition.Height = new GridLength(1, GridUnitType.Star);
        GridRowDefinition.MinHeight = 80;
        SplitterRowDefinition.Height = new GridLength(0);
        InspectorRowDefinition.Height = new GridLength(0);
        InspectorRowDefinition.MinHeight = 0;
        GridColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
        GridColumnDefinition.MinWidth = 80;
        SplitterColumnDefinition.Width = new GridLength(0);
        InspectorColumnDefinition.Width = new GridLength(0);
        InspectorColumnDefinition.MinWidth = 0;
    }

    private void ShowBottomPane()
    {
        var fraction = model?.Preferences.BottomPaneGridFraction ?? .40;
        Grid.SetRow(SessionGrid, 1);
        Grid.SetRowSpan(SessionGrid, 1);
        Grid.SetColumn(SessionGrid, 0);
        Grid.SetColumnSpan(SessionGrid, 3);
        Grid.SetRow(InspectorSplitter, 2);
        Grid.SetRowSpan(InspectorSplitter, 1);
        Grid.SetColumn(InspectorSplitter, 0);
        Grid.SetColumnSpan(InspectorSplitter, 3);
        Grid.SetRow(InspectorHost, 3);
        Grid.SetRowSpan(InspectorHost, 1);
        Grid.SetColumn(InspectorHost, 0);
        Grid.SetColumnSpan(InspectorHost, 3);
        GridRowDefinition.Height = new GridLength(fraction, GridUnitType.Star);
        GridRowDefinition.MinHeight = MinimumGridHeight;
        SplitterRowDefinition.Height = GridLength.Auto;
        InspectorRowDefinition.Height = new GridLength(1 - fraction, GridUnitType.Star);
        InspectorRowDefinition.MinHeight = MinimumInspectorHeight;
        GridColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
        GridColumnDefinition.MinWidth = 80;
        SplitterColumnDefinition.Width = new GridLength(0);
        InspectorColumnDefinition.Width = new GridLength(0);
        InspectorColumnDefinition.MinWidth = 0;
        InspectorSplitter.Width = double.NaN;
        InspectorSplitter.Height = 5;
        InspectorSplitter.ResizeDirection = GridResizeDirection.Rows;
        AutomationProperties.SetName(InspectorSplitter, "Resize session grid and bottom inspector");
        InspectorHost.BorderThickness = new Thickness(0, 1, 0, 0);
    }

    private void ShowRightPane()
    {
        var fraction = model?.Preferences.RightPaneGridFraction ?? .45;
        Grid.SetRow(SessionGrid, 1);
        Grid.SetRowSpan(SessionGrid, 3);
        Grid.SetColumn(SessionGrid, 0);
        Grid.SetColumnSpan(SessionGrid, 1);
        Grid.SetRow(InspectorSplitter, 1);
        Grid.SetRowSpan(InspectorSplitter, 3);
        Grid.SetColumn(InspectorSplitter, 1);
        Grid.SetColumnSpan(InspectorSplitter, 1);
        Grid.SetRow(InspectorHost, 1);
        Grid.SetRowSpan(InspectorHost, 3);
        Grid.SetColumn(InspectorHost, 2);
        Grid.SetColumnSpan(InspectorHost, 1);
        GridRowDefinition.Height = new GridLength(1, GridUnitType.Star);
        GridRowDefinition.MinHeight = 80;
        SplitterRowDefinition.Height = new GridLength(0);
        InspectorRowDefinition.Height = new GridLength(0);
        InspectorRowDefinition.MinHeight = 0;
        GridColumnDefinition.Width = new GridLength(fraction, GridUnitType.Star);
        GridColumnDefinition.MinWidth = MinimumGridWidth;
        SplitterColumnDefinition.Width = GridLength.Auto;
        InspectorColumnDefinition.Width = new GridLength(1 - fraction, GridUnitType.Star);
        InspectorColumnDefinition.MinWidth = MinimumInspectorWidth;
        InspectorSplitter.Width = 5;
        InspectorSplitter.Height = double.NaN;
        InspectorSplitter.ResizeDirection = GridResizeDirection.Columns;
        AutomationProperties.SetName(InspectorSplitter, "Resize session grid and right inspector");
        InspectorHost.BorderThickness = new Thickness(1, 0, 0, 0);
    }

    private void OnInspectorSplitterDragCompleted(object sender, DragCompletedEventArgs e) =>
        RememberSplitter(appliedLocation);

    private void OnInspectorSplitterKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
        {
            RememberSplitter(appliedLocation);
        }
    }

    private void RememberSplitter(SessionViewerLocation? location)
    {
        if (model is null)
        {
            return;
        }
        if (location == SessionViewerLocation.BottomPane)
        {
            var total = GridRowDefinition.ActualHeight + InspectorRowDefinition.ActualHeight;
            if (total > 0)
            {
                model.Preferences.BottomPaneGridFraction = GridRowDefinition.ActualHeight / total;
            }
        }
        else if (location == SessionViewerLocation.RightPane)
        {
            var total = GridColumnDefinition.ActualWidth + InspectorColumnDefinition.ActualWidth;
            if (total > 0)
            {
                model.Preferences.RightPaneGridFraction = GridColumnDefinition.ActualWidth / total;
            }
        }
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (model is null || !Enum.TryParse<SessionSortColumn>(e.Column.SortMemberPath, out var column))
        {
            return;
        }
        // Ascending → Descending → chronological (unsorted).
        ListSortDirection? next = e.Column.SortDirection switch
        {
            null => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => null
        };
        foreach (var other in SessionGrid.Columns)
        {
            other.SortDirection = null;
        }
        e.Column.SortDirection = next;
        model.Sessions.Sort(next is null ? SessionSortColumn.Index : column, next ?? ListSortDirection.Ascending);
        if (model.Inspector.Row is { } row && model.Sessions.VisibleRows.Contains(row))
        {
            SessionGrid.SelectedItem = row;
            SessionGrid.ScrollIntoView(row);
        }
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (model is not null && SessionGrid.SelectedItem is SessionRow row && !ReferenceEquals(model.Inspector.Row, row))
        {
            model.Inspector.Load(row);
        }
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(focus: true);

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenSelected(focus: true);
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        // Clicking the already-selected row reopens a closed inspector.
        if (model is { Inspector.IsOpen: false } && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SessionGrid, source) is DataGridRow { Item: SessionRow row }
            && ReferenceEquals(row, SessionGrid.SelectedItem))
        {
            model.Inspector.Load(row);
        }
    }

    private void OpenSelected(bool focus)
    {
        if (model is null || SessionGrid.SelectedItem is not SessionRow row)
        {
            return;
        }
        if (!model.Inspector.IsOpen || !ReferenceEquals(model.Inspector.Row, row))
        {
            model.Inspector.Load(row);
        }
        if (focus && model.SessionViewerLocation != SessionViewerLocation.NewWindow)
        {
            Dispatcher.BeginInvoke(EnsureInspector().FocusContent, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (model is null)
        {
            return;
        }
        if (model.Inspector.IsOpen && InspectorView.IsNavigationKey(e, out var delta))
        {
            model.Inspector.Navigate(delta);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (Inspector is { IsKeyboardFocusWithin: true } && model.Inspector.IsHttp)
            {
                Inspector.FocusSearch();
            }
            else
            {
                GridSearchBox.Focus();
                GridSearchBox.SelectAll();
            }
            e.Handled = true;
        }
    }

    private void FocusSelectedRow()
    {
        var item = SessionGrid.SelectedItem;
        if (item is null)
        {
            SessionGrid.Focus();
            return;
        }
        SessionGrid.ScrollIntoView(item);
        SessionGrid.UpdateLayout();
        SessionGrid.CurrentCell = new DataGridCellInfo(item, SessionGrid.Columns[0]);
        if (SessionGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
        {
            row.Focus();
        }
    }

    /// <summary>Sizes every column except URL to its widest content; URL takes the remaining width.</summary>
    private void FitColumns(IReadOnlyList<SessionRow> rows)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        const double HeaderChrome = 16 + 1 + 18; // padding, border, sort glyph
        const double CellChrome = 16 + 4;         // TextBlock margins + slack
        const double BadgeChrome = 16 + 14 + 2 + 4;

        double Measure(string text, Typeface face, double size) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        double Fit(DataGridColumn column, Func<SessionRow, string> value, double size = 13, double chrome = CellChrome)
        {
            var header = Measure((string)column.Header, bold, 13) + HeaderChrome;
            // Measuring the longest few strings (by length) is enough for a proportional font and keeps this O(n).
            var widest = rows.Select(value).Distinct().OrderByDescending(text => text.Length).Take(8)
                .Select(text => Measure(text, regular, size) + chrome).DefaultIfEmpty(0).Max();
            return Math.Ceiling(Math.Max(header, widest));
        }

        TimeColumn.Width = Fit(TimeColumn, row => row.Time);
        IdColumn.Width = Fit(IdColumn, row => row.Id);
        ResultColumn.Width = Fit(ResultColumn, row => row.Result);
        MethodColumn.Width = Fit(MethodColumn, row => row.Method, 12, BadgeChrome);
        ElapsedColumn.Width = Fit(ElapsedColumn, row => row.Elapsed);
        RequestColumn.Width = Fit(RequestColumn, row => row.RequestSize);
        ResponseColumn.Width = Fit(ResponseColumn, row => row.ResponseSize);
    }
}
