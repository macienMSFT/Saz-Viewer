using System.ComponentModel;
using System.Windows;
using SazViewer.App.ViewModels;

namespace SazViewer.App;

/// <summary>
/// A session inspector popped out of a capture tab ("Open in new window"). It navigates the tab's visible rows
/// independently of the grid selection and closes with its inspector (Close / Esc) or when the capture changes.
/// </summary>
internal partial class InspectorWindow : Window
{
    private readonly string fileName;
    private readonly InspectorViewModel model;

    public InspectorWindow(string fileName, InspectorViewModel model)
    {
        InitializeComponent();
        this.fileName = fileName;
        this.model = model;
        Inspector.DataContext = model;
        model.PropertyChanged += OnModelPropertyChanged;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            model.Close();
        };
        Loaded += (_, _) => Inspector.FocusContent();
        UpdateTitle();
    }

    public InspectorViewModel Model => model;

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!model.IsOpen)
        {
            Dispatcher.BeginInvoke(Close);
            return;
        }
        if (e.PropertyName is nameof(InspectorViewModel.Title) or "" or null)
        {
            UpdateTitle();
        }
    }

    private void UpdateTitle() => Title = $"{model.Title} - {fileName} - SAZ Viewer";
}
