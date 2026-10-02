using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using SazViewer.App.Model;
using SazViewer.App.Themes;

namespace SazViewer.App;

internal partial class ColumnChooserWindow : Window, INotifyPropertyChanged
{
    private static readonly string[] DefaultIds =
    [
        "time", "id", "result", "method", "url", "elapsed", "request-size", "response-size"
    ];

    private readonly UiPreferences preferences;
    private ColumnChoiceItem? selectedItem;

    public ColumnChooserWindow(UiPreferences preferences)
    {
        InitializeComponent();
        this.preferences = preferences;
        Items = BuildItems(preferences.GridColumns);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = Filter;
        DataContext = this;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => SearchBox.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal ObservableCollection<ColumnChoiceItem> Items { get; private set; }

    public ICollectionView ItemsView { get; private set; }

    public ColumnChoiceItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (ReferenceEquals(selectedItem, value))
            {
                return;
            }
            selectedItem = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));
        }
    }

    private static ObservableCollection<ColumnChoiceItem> BuildItems(IReadOnlyList<SessionColumnSetting> settings)
    {
        var items = new ObservableCollection<ColumnChoiceItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settings)
        {
            if (SessionColumnCatalog.Resolve(setting) is { } definition && ids.Add(setting.Id))
            {
                items.Add(new ColumnChoiceItem(definition));
            }
        }
        foreach (var definition in SessionColumnCatalog.BuiltIns)
        {
            if (ids.Add(definition.Id))
            {
                items.Add(new ColumnChoiceItem(definition with
                {
                    Setting = definition.Setting with { Visible = false }
                }));
            }
        }
        return items;
    }

    private bool Filter(object value)
    {
        if (value is not ColumnChoiceItem item)
        {
            return false;
        }
        var query = SearchBox.Text.Trim();
        return query.Length == 0
               || item.Header.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.Source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ItemsView.Refresh();

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (SelectedItem is null)
        {
            return;
        }
        var index = Items.IndexOf(SelectedItem);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }
        Items.Move(index, target);
        ColumnList.ScrollIntoView(SelectedItem);
    }

    private void OnAddRequestHeader(object sender, RoutedEventArgs e) =>
        AddCustom(SessionColumnSetting.RequestHeaderKind, "Request header column", "Header name");

    private void OnAddResponseHeader(object sender, RoutedEventArgs e) =>
        AddCustom(SessionColumnSetting.ResponseHeaderKind, "Response header column", "Header name");

    private void OnAddSessionFlag(object sender, RoutedEventArgs e) =>
        AddCustom(SessionColumnSetting.SessionFlagKind, "Session flag column", "Session flag name");

    private void AddCustom(string kind, string title, string sourceLabel)
    {
        var dialog = new CustomColumnWindow(title, sourceLabel) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var setting = SessionColumnCatalog.CreateCustom(kind, dialog.SourceName, dialog.ColumnHeader);
        var definition = SessionColumnCatalog.Resolve(setting)!;
        var item = new ColumnChoiceItem(definition);
        Items.Add(item);
        SelectedItem = item;
        ColumnList.ScrollIntoView(item);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var defaults = DefaultIds.Select(id => SessionColumnSetting.BuiltIn(id)).ToArray();
        Items = BuildItems(defaults);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = Filter;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Items)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemsView)));
        SelectedItem = Items.FirstOrDefault();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (!Items.Any(item => item.IsVisible))
        {
            MessageBox.Show(this, "Keep at least one session column visible.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        preferences.SetGridColumns(Items.Select(item => item.ToSetting()));
        DialogResult = true;
    }

    internal sealed class ColumnChoiceItem(SessionColumnDefinition definition) : INotifyPropertyChanged
    {
        private bool isVisible = definition.Setting.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Header => definition.Header;

        public string Category => definition.Category;

        public string Source => definition.Setting.Kind == SessionColumnSetting.BuiltInKind
            ? definition.Header
            : definition.Setting.Source;

        public string ToggleName => $"Show {Header} column";

        public bool IsVisible
        {
            get => isVisible;
            set
            {
                if (isVisible == value)
                {
                    return;
                }
                isVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            }
        }

        public SessionColumnSetting ToSetting() => definition.Setting with
        {
            Header = definition.Header,
            Visible = IsVisible
        };
    }
}
