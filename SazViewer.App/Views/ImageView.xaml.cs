using System.Windows;
using System.Windows.Controls;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Views;

/// <summary>Image tab: decodes the retained bytes off the UI thread while shown and releases them when hidden.</summary>
internal partial class ImageView : UserControl
{
    public ImageView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ImageViewModel)?.EnsureLoaded();
        Unloaded += (_, _) => (DataContext as ImageViewModel)?.Deactivate();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        (e.OldValue as ImageViewModel)?.Deactivate();
        if (IsLoaded)
        {
            (e.NewValue as ImageViewModel)?.EnsureLoaded();
        }
    }
}
