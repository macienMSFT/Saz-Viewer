using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;

namespace SazViewer.App.Tests;

public sealed class SearchBoxTests
{
    [Fact]
    public void ClearButtonAndEscapeClearSearchRestoreStateAndKeepFocus()
    {
        StaRunner.Run(() =>
        {
            var document = new LineDocumentBuilder()
                .Add("visible")
                .BeginSection("Captured bytes")
                .Add("hidden secret")
                .EndSection()
                .Build();
            var section = Assert.Single(document.Sections);
            var search = new ActiveSearchViewModel(() => document, "Search active view...", "Search active view");
            var bar = new ActiveSearchBar { DataContext = search };
            var window = NativeViewSmokeTests.Host(bar);
            try
            {
                var box = Assert.IsType<SearchBox>(bar.FindName("QueryBox"));
                box.ApplyTemplate();
                var placeholder = Assert.IsType<TextBlock>(box.Template.FindName("Placeholder", box));
                var contentHost = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
                var clear = Assert.IsType<Button>(box.Template.FindName("PART_ClearButton", box));

                Assert.False(placeholder.IsHitTestVisible);
                Assert.Equal(contentHost.Margin, placeholder.Margin);
                Assert.Equal("Clear search", clear.ToolTip);
                Assert.Equal("Clear search", AutomationProperties.GetName(clear));
                Assert.Equal(Visibility.Collapsed, clear.Visibility);

                search.Query = "secret";
                StaRunner.DoEvents();
                Assert.True(section.IsExpanded);
                Assert.Equal(Visibility.Visible, clear.Visibility);

                var peer = new ButtonAutomationPeer(clear);
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                StaRunner.DoEvents();

                Assert.Equal("", search.Query);
                Assert.False(section.IsExpanded);
                Assert.Same(box, Keyboard.FocusedElement);
                Assert.Equal(0, box.CaretIndex);
                Assert.Equal(Visibility.Collapsed, clear.Visibility);

                search.Query = "secret";
                StaRunner.DoEvents();
                var escape = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(box),
                    Environment.TickCount,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                box.RaiseEvent(escape);
                StaRunner.DoEvents();

                Assert.True(escape.Handled);
                Assert.Equal("", search.Query);
                Assert.False(section.IsExpanded);
                Assert.Same(box, Keyboard.FocusedElement);
                Assert.Equal(0, box.CaretIndex);
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void EveryDesktopSearchSurfaceUsesSharedSearchBox()
    {
        StaRunner.Run(() =>
        {
            using var capture = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), new UiPreferences(null));
            var captureView = new CaptureView { DataContext = capture };
            var captureWindow = NativeViewSmokeTests.Host(captureView);
            var columns = new ColumnChooserWindow(new UiPreferences(null)) { ShowInTaskbar = false };
            var webSocketCapture = new CaptureViewModel(WebSocketViewTests.Report(), new FakeClipboard());
            var webSocketRow = webSocketCapture.Sessions.VisibleRows.Single(row => row.IsWebSocket);
            webSocketCapture.Inspector.Load(webSocketRow);
            var webSocketView = new WebSocketInspectorView { DataContext = webSocketCapture.Inspector.WebSocket };
            var webSocketWindow = NativeViewSmokeTests.Host(webSocketView);
            var filterView = new AdvancedFilterView { DataContext = capture.Filters };
            var filterWindow = NativeViewSmokeTests.Host(filterView);
            try
            {
                columns.Show();
                StaRunner.DoEvents();

                Assert.IsType<SearchBox>(captureView.FindName("GridSearchBox"));
                Assert.IsType<SearchBox>(columns.FindName("SearchBox"));
                Assert.IsType<SearchBox>(webSocketView.FindName("FilterBox"));
                Assert.Contains(
                    Descendants<SearchBox>(filterView),
                    box => AutomationProperties.GetName(box) == "Filter comparison value");
            }
            finally
            {
                filterWindow.Close();
                webSocketWindow.Close();
                webSocketCapture.Dispose();
                columns.Close();
                captureWindow.Close();
                StaRunner.DoEvents();
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
