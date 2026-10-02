using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
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
    private readonly Dictionary<DataGridColumn, SessionColumnDefinition> columnDefinitions = [];
    private bool rebuildingColumns;
    private bool widthSavePending;

    public CaptureView()
    {
        InitializeComponent();
        SessionGrid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnColumnHeaderDragCompleted));
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
            model.Sessions.GridColumnsChanged -= OnGridColumnsChanged;
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
        model.Sessions.GridColumnsChanged += OnGridColumnsChanged;
        BuildColumns();
        UpdateInspectorLayout();
    }

    private void OnGridColumnsChanged(object? sender, EventArgs e) => BuildColumns();

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
        if (model is null || !columnDefinitions.TryGetValue(e.Column, out var column))
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
        model.Sessions.Sort(next is null ? null : column, next ?? ListSortDirection.Ascending);
        if (model.Inspector.Row is { } row && model.Sessions.VisibleRows.Contains(row))
        {
            SessionGrid.SelectedItem = row;
            SessionGrid.ScrollIntoView(row);
        }
    }

    private void OnColumnReordered(object sender, DataGridColumnEventArgs e) => QueueSaveColumnLayout();

    private void OnColumnHeaderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }
        QueueSaveColumnLayout();
    }

    private void QueueSaveColumnLayout()
    {
        if (rebuildingColumns || widthSavePending)
        {
            return;
        }
        widthSavePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            widthSavePending = false;
            SaveColumnLayout();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnGridPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is null || model is null)
        {
            return;
        }
        var menu = new ContextMenu();
        foreach (var setting in model.Preferences.GridColumns)
        {
            var definition = SessionColumnCatalog.Resolve(setting);
            if (definition is null)
            {
                continue;
            }
            var item = new MenuItem
            {
                Header = definition.Header,
                IsCheckable = true,
                IsChecked = setting.Visible,
                StaysOpenOnClick = true
            };
            AutomationProperties.SetName(item, $"Show {definition.Header} column");
            item.Click += (_, _) =>
            {
                if (!SetColumnVisibility(setting.Id, item.IsChecked))
                {
                    item.IsChecked = true;
                }
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var more = new MenuItem { Header = "More columns\u2026" };
        AutomationProperties.SetName(more, "Customize session columns");
        more.Click += (_, _) => ShowColumnChooser();
        menu.Items.Add(more);
        var reset = new MenuItem { Header = "Reset columns to default" };
        reset.Click += (_, _) => model.Preferences.ResetGridColumns();
        menu.Items.Add(reset);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
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
        if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ShowAdvancedFilter();
            e.Handled = true;
        }
        else if (model.Inspector.IsOpen && InspectorView.IsNavigationKey(e, out var delta))
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

    internal void ShowAdvancedFilter()
    {
        if (model is null)
        {
            return;
        }
        model.Filters.IsPanelOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (FilterRuleList.ItemContainerGenerator.ContainerFromIndex(0) is UIElement first)
            {
                first.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            else
            {
                AdvancedFilterButton.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnToggleAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (model is null)
        {
            return;
        }
        model.Filters.IsPanelOpen = !model.Filters.IsPanelOpen;
        if (model.Filters.IsPanelOpen)
        {
            ShowAdvancedFilter();
        }
        else
        {
            AdvancedFilterButton.Focus();
        }
    }

    private async void OnApplyAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (model is not null)
        {
            await model.Filters.ApplyAsync();
        }
    }

    private void OnFilterFieldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (model is null
            || sender is not ComboBox { DataContext: FilterRuleViewModel rule }
            || e.AddedItems.OfType<FilterFieldOption>().FirstOrDefault() is not { Prompt: not FilterFieldPrompt.None } selected)
        {
            return;
        }
        var (kind, title, label) = selected.Prompt switch
        {
            FilterFieldPrompt.RequestHeader => (SessionColumnSetting.RequestHeaderKind, "Request header filter", "Header name"),
            FilterFieldPrompt.ResponseHeader => (SessionColumnSetting.ResponseHeaderKind, "Response header filter", "Header name"),
            _ => (SessionColumnSetting.SessionFlagKind, "Session flag filter", "Session flag name")
        };
        var dialog = new CustomColumnWindow(title, label) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            rule.FieldOption = model.Filters.AddCustomField(kind, dialog.SourceName, dialog.ColumnHeader);
        }
        else
        {
            rule.CancelFieldPrompt();
        }
    }

    private void OnLoadAdvancedFilter(object sender, RoutedEventArgs e) => model?.Filters.LoadSelected();

    private void OnSaveAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (model is null)
        {
            return;
        }
        var dialog = new FilterNameWindow(model.Filters.SelectedSavedFilter?.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            model.Filters.SaveAs(dialog.FilterName);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Save named filter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (model?.Filters.SelectedSavedFilter is not { } selected
            || MessageBox.Show(Window.GetWindow(this), $"Delete the named filter \u201c{selected.Name}\u201d?",
                "Delete named filter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        model.Filters.DeleteSelected();
    }

    private void OnImportAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (model is null)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Import named filters",
            Filter = "SAZ Viewer filters (*.sazfilter.json)|*.sazfilter.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            model.Filters.Import(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Import named filters",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (model?.Filters.SelectedSavedFilter is not { } selected)
        {
            MessageBox.Show(Window.GetWindow(this), "Choose a saved filter to export.", "Export named filter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Export named filter",
            FileName = SafeFileName(selected.Name) + ".sazfilter.json",
            DefaultExt = ".sazfilter.json",
            Filter = "SAZ Viewer filters (*.sazfilter.json)|*.sazfilter.json|JSON files (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            model.Filters.ExportSelected(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Export named filter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return sanitized.Length == 0 ? "filter" : sanitized;
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

    private void BuildColumns()
    {
        if (model is null)
        {
            return;
        }
        rebuildingColumns = true;
        try
        {
            SessionGrid.Columns.Clear();
            columnDefinitions.Clear();
            foreach (var definition in model.Sessions.GridColumns.Where(column => column.Setting.Visible))
            {
                var column = CreateGridColumn(definition);
                if (definition.Setting.Width is { } width)
                {
                    column.Width = width;
                }
                SessionGrid.Columns.Add(column);
                columnDefinitions[column] = definition;
            }
            FitColumns(model.Rows);
        }
        finally
        {
            rebuildingColumns = false;
        }
    }

    private DataGridColumn CreateGridColumn(SessionColumnDefinition definition)
    {
        var binding = CellBinding(definition);
        if (definition.Id == "method")
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, binding);
            text.SetValue(TextBlock.FontSizeProperty, 12d);
            var badge = new FrameworkElementFactory(typeof(Border));
            badge.SetValue(FrameworkElement.StyleProperty, FindResource("Saz.MethodBadge"));
            badge.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 8, 0));
            badge.AppendChild(text);
            return new DataGridTemplateColumn
            {
                Header = definition.Header,
                SortMemberPath = definition.Id,
                MinWidth = 40,
                CellTemplate = new DataTemplate { VisualTree = badge }
            };
        }

        var baseStyle = (Style)FindResource(definition.Numeric ? "GridNumberText" : "GridCellText");
        var elementStyle = new Style(typeof(TextBlock), baseStyle);
        Binding tooltip = definition.Id switch
        {
            "time" => new Binding(nameof(SessionRow.TimeToolTip)) { Mode = BindingMode.OneTime },
            "result" => new Binding(nameof(SessionRow.ResultToolTip)) { Mode = BindingMode.OneTime },
            _ => CellBinding(definition)
        };
        elementStyle.Setters.Add(new Setter(ToolTipService.ToolTipProperty, tooltip));
        return new DataGridTextColumn
        {
            Header = definition.Header,
            Binding = binding,
            SortMemberPath = definition.Id,
            ElementStyle = elementStyle,
            MinWidth = definition.Id == "url" ? 120 : 40
        };
    }

    private static Binding CellBinding(SessionColumnDefinition definition) => new()
    {
        Mode = BindingMode.OneTime,
        Converter = SessionColumnValueConverter.Instance,
        ConverterParameter = definition
    };

    /// <summary>Sizes newly added columns from bounded text measurement without decoding bodies.</summary>
    private void FitColumns(IReadOnlyList<SessionRow> rows)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var samples = rows.Count <= 512
            ? rows
            : Enumerable.Range(0, 512)
                .Select(index => rows[(int)((long)index * (rows.Count - 1) / 511)])
                .ToArray();
        const double HeaderChrome = 16 + 1 + 18; // padding, border, sort glyph
        const double CellChrome = 16 + 4;         // TextBlock margins + slack

        double Measure(string text, Typeface face, double size) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        double Fit(DataGridColumn column, SessionColumnDefinition definition)
        {
            var header = Measure(definition.Header, bold, 13) + HeaderChrome;
            // Measuring the longest few strings (by length) is enough for a proportional font and keeps this O(n).
            var widest = samples.Select(row => row.ColumnValue(definition).Display)
                .Distinct().OrderByDescending(text => text.Length).Take(8)
                .Select(text => Measure(text, regular, 13) + CellChrome).DefaultIfEmpty(0).Max();
            return Math.Ceiling(Math.Max(header, widest));
        }

        foreach (var (column, definition) in columnDefinitions)
        {
            if (definition.Setting.Width is null)
            {
                column.Width = definition.Id == "url"
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : Fit(column, definition);
            }
        }
    }

    private void SaveColumnLayout()
    {
        if (model is null || rebuildingColumns || SessionGrid.Columns.Count == 0)
        {
            return;
        }
        var visible = SessionGrid.Columns.OrderBy(column => column.DisplayIndex)
            .Select(column =>
            {
                var definition = columnDefinitions[column];
                return definition.Setting with
                {
                    Visible = true,
                    Width = Math.Clamp(column.ActualWidth, 40, 2000)
                };
            })
            .ToList();
        var visibleIds = visible.Select(setting => setting.Id).ToHashSet(StringComparer.Ordinal);
        visible.AddRange(model.Preferences.GridColumns.Where(setting => !visibleIds.Contains(setting.Id)));
        model.Preferences.SetGridColumns(visible);
    }

    private bool SetColumnVisibility(string id, bool visible)
    {
        if (model is null)
        {
            return false;
        }
        var settings = model.Preferences.GridColumns.ToList();
        var index = settings.FindIndex(setting => setting.Id == id);
        if (index < 0)
        {
            return false;
        }
        if (!visible && settings.Count(setting => setting.Visible) <= 1)
        {
            return false;
        }
        settings[index] = settings[index] with { Visible = visible };
        model.Preferences.SetGridColumns(settings);
        return true;
    }

    private void ShowColumnChooser()
    {
        if (model is null)
        {
            return;
        }
        var dialog = new ColumnChooserWindow(model.Preferences)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private static T? FindAncestor<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value is not null)
        {
            if (value is T result)
            {
                return result;
            }
            value = VisualTreeHelper.GetParent(value);
        }
        return null;
    }

    private sealed class SessionColumnValueConverter : IValueConverter
    {
        public static SessionColumnValueConverter Instance { get; } = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is SessionRow row && parameter is SessionColumnDefinition definition
                ? row.ColumnValue(definition).Display
                : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
