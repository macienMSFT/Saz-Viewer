using System.Windows;
using System.Windows.Controls;

namespace SazViewer.App;

/// <summary>Content of one capture tab: the file-change notice bar above a lazily created WebView2.</summary>
internal partial class CaptureTabView : UserControl
{
    public CaptureTabView()
    {
        InitializeComponent();
    }

    public event EventHandler? ReloadRequested;

    public event EventHandler? DismissRequested;

    public Grid Host => WebViewHost;

    /// <summary>Shows or hides the notice bar. A null <paramref name="message"/> hides it.</summary>
    public void ShowNotice(string? message, bool canReload, bool buttonsEnabled = true)
    {
        if (message is null)
        {
            NoticeBar.Visibility = Visibility.Collapsed;
            NoticeText.Text = "";
            return;
        }
        NoticeText.Text = message;
        ReloadButton.Visibility = canReload ? Visibility.Visible : Visibility.Collapsed;
        ReloadButton.IsEnabled = buttonsEnabled;
        DismissButton.IsEnabled = buttonsEnabled;
        NoticeBar.Visibility = Visibility.Visible;
    }

    public string? NoticeMessage => NoticeBar.Visibility == Visibility.Visible ? NoticeText.Text : null;

    private void OnReload(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);

    private void OnDismiss(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);
}
