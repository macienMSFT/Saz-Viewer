using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>
/// Native view of one capture: the session grid with its toolbar and, below a splitter, the inspector.
/// </summary>
internal partial class CaptureView : UserControl
{
    private CaptureViewModel? model;

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
            Inspector = new InspectorView { DataContext = model?.Inspector };
            Inspector.FocusGridRequested += (_, _) => FocusSelectedRow();
            InspectorHost.Child = Inspector;
        }
        return Inspector;
    }

    private void Attach(CaptureViewModel? next)
    {
        if (model is not null)
        {
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
        model.Inspector.PropertyChanged += OnInspectorPropertyChanged;
        model.Inspector.Loaded += OnInspectorLoaded;
        FitColumns(model.Rows);
        UpdateInspectorLayout();
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
            SessionGrid.ScrollIntoView(row);
        }
    }

    private void UpdateInspectorLayout()
    {
        var open = model?.Inspector.IsOpen == true;
        if (open == (InspectorHost.Visibility == Visibility.Visible))
        {
            return;
        }
        if (open)
        {
            EnsureInspector();
        }
        InspectorHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = InspectorHost.Visibility;
        GridRowDefinition.Height = new GridLength(open ? 2 : 1, GridUnitType.Star);
        SplitterRowDefinition.Height = open ? GridLength.Auto : new GridLength(0);
        InspectorRowDefinition.Height = open ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        InspectorRowDefinition.MinHeight = open ? 160 : 0;
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
        if (focus)
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
