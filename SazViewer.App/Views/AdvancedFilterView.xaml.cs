using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

internal partial class AdvancedFilterView : UserControl
{
    public AdvancedFilterView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusFirstRule();
    }

    internal void FocusFirstRule()
    {
        if (FilterRuleList.ItemContainerGenerator.ContainerFromIndex(0) is UIElement first)
        {
            first.MoveFocus(new System.Windows.Input.TraversalRequest(
                System.Windows.Input.FocusNavigationDirection.First));
        }
    }

    private AdvancedFilterViewModel? Model => DataContext as AdvancedFilterViewModel;

    private async void OnApplyAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (Model is { } model)
        {
            await model.ApplyAsync();
        }
    }

    private void OnFilterFieldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is null
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
            rule.FieldOption = Model.AddCustomField(kind, dialog.SourceName, dialog.ColumnHeader);
        }
        else
        {
            rule.CancelFieldPrompt();
        }
    }

    private void OnLoadAdvancedFilter(object sender, RoutedEventArgs e) => Model?.LoadSelected();

    private void OnSaveAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }
        var window = Window.GetWindow(this);
        var dialog = new FilterNameWindow(Model.SelectedSavedFilter?.Name) { Owner = window };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            Model.SaveAs(dialog.FilterName);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(window, exception.Message, "Save named filter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeleteAdvancedFilter(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (Model?.SelectedSavedFilter is not { } selected
            || MessageBox.Show(window, $"Delete the named filter \u201c{selected.Name}\u201d?",
                "Delete named filter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        Model.DeleteSelected();
    }

    private void OnImportAdvancedFilter(object sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }
        var window = Window.GetWindow(this);
        var dialog = new OpenFileDialog
        {
            Title = "Import named filters",
            Filter = "SAZ Viewer filters (*.sazfilter.json)|*.sazfilter.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(window) != true)
        {
            return;
        }
        try
        {
            Model.Import(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(window, exception.Message, "Import named filters",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportAdvancedFilter(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (Model?.SelectedSavedFilter is not { } selected)
        {
            MessageBox.Show(window, "Choose a saved filter to export.", "Export named filter",
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
        if (dialog.ShowDialog(window) != true)
        {
            return;
        }
        try
        {
            Model.ExportSelected(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(window, exception.Message, "Export named filter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return sanitized.Length == 0 ? "filter" : sanitized;
    }
}
