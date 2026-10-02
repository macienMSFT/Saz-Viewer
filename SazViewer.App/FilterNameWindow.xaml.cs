using System.Windows;
using SazViewer.App.Themes;

namespace SazViewer.App;

internal partial class FilterNameWindow : Window
{
    public FilterNameWindow(string? initialName = null)
    {
        InitializeComponent();
        NameBox.Text = initialName ?? "";
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string FilterName => NameBox.Text.Trim();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (FilterName.Length == 0)
        {
            MessageBox.Show(this, "Enter a filter name.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
