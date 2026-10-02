using System.Windows;
using SazViewer.App.Themes;

namespace SazViewer.App;

internal partial class CustomColumnWindow : Window
{
    public CustomColumnWindow(string title, string sourceLabel)
    {
        InitializeComponent();
        Title = title;
        SourceLabel.Text = sourceLabel;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => SourceBox.Focus();
    }

    public string SourceName => SourceBox.Text.Trim();

    public string ColumnHeader => HeaderBox.Text.Trim();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (SourceName.Length == 0)
        {
            MessageBox.Show(this, "Enter a header or session flag name.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            SourceBox.Focus();
            return;
        }
        if (SourceName.Length > 256 || ColumnHeader.Length > 128)
        {
            MessageBox.Show(this, "The source name must be at most 256 characters and the heading at most 128.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
