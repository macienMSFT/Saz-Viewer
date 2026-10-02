using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;

namespace SazViewer.App.Tests;

public sealed class NativeViewSmokeTests
{
    internal static Window Host(FrameworkElement content)
    {
        var window = new Window
        {
            Width = 1300,
            Height = 900,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Left = -20000,
            Top = -20000,
            Content = content
        };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Themes.ThemeManager.PaletteUri(Themes.AppTheme.Light) });
        window.Show();
        StaRunner.DoEvents();
        return window;
    }

    [Fact]
    public void CaptureViewRendersGridAndOpensInspector()
    {
        using var temp = new TempDirectory();
        var document = ReportBuilder.Build(TestCaptures.WritePlain(temp.File("c.saz")), false, new QueuePasswordProvider());
        StaRunner.Run(() =>
        {
            var model = new CaptureViewModel(document.Report);
            var view = new CaptureView { DataContext = model };
            var window = Host(view);
            try
            {
                var grid = (DataGrid)view.FindName("SessionGrid");
                var searchPayloads = (CheckBox)view.FindName("SearchPayloadsCheckBox");
                Assert.Equal(2, grid.Items.Count);
                Assert.Equal(
                    "Search request, response, WebSocket, and MAPI payloads",
                    AutomationProperties.GetName(searchPayloads));
                Assert.True(searchPayloads.Focusable);
                grid.SelectedIndex = 0;
                StaRunner.DoEvents();
                Assert.True(model.Inspector.IsOpen);
                Assert.Equal("1 of 2", model.Inspector.Position);
                Assert.False(document.IsHtmlGenerated);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
