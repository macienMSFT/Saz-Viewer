using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SazViewer.App.Themes;

namespace SazViewer.App.Tests;

public sealed class ScrollBarStyleTests
{
    [Fact]
    public void SharedScrollBarsAreSlimArrowlessDynamicAndKeepPaging()
    {
        StaRunner.Run(() =>
        {
            var light = new ResourceDictionary { Source = ThemeManager.PaletteUri(AppTheme.Light) };
            var controls = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SazViewer.App;component/Themes/Controls.xaml")
            };
            var viewer = new ScrollViewer
            {
                Width = 280,
                Height = 180,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top,
                Content = new Border { Width = 1000, Height = 1000 }
            };
            var window = new Window
            {
                Width = 320,
                Height = 220,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000,
                Content = viewer
            };
            window.Resources.MergedDictionaries.Add(light);
            window.Resources.MergedDictionaries.Add(controls);
            try
            {
                window.Show();
                StaRunner.DoEvents();

                var bars = Descendants<ScrollBar>(viewer).ToArray();
                var vertical = Assert.Single(bars, bar => bar.Orientation == Orientation.Vertical);
                var horizontal = Assert.Single(bars, bar => bar.Orientation == Orientation.Horizontal);
                Assert.Equal(12, vertical.ActualWidth);
                Assert.Equal(12, horizontal.ActualHeight);

                var verticalTrack = Assert.IsType<Track>(vertical.Template.FindName("PART_Track", vertical));
                Assert.Null(verticalTrack.DecreaseRepeatButton.Content);
                Assert.Null(verticalTrack.IncreaseRepeatButton.Content);
                Assert.Equal(6, verticalTrack.Thumb.Width);
                Assert.Equal(24, verticalTrack.Thumb.MinHeight);
                Assert.Equal(6, ((Track)horizontal.Template.FindName("PART_Track", horizontal)).Thumb.Height);

                viewer.LineDown();
                StaRunner.DoEvents();
                Assert.True(viewer.VerticalOffset > 0);
                var afterLine = viewer.VerticalOffset;
                ScrollBar.PageDownCommand.Execute(null, vertical);
                viewer.LineRight();
                StaRunner.DoEvents();
                Assert.True(viewer.VerticalOffset > afterLine);
                Assert.True(
                    viewer.HorizontalOffset > 0,
                    $"Horizontal offset {viewer.HorizontalOffset}; scrollable {viewer.ScrollableWidth}; extent {viewer.ExtentWidth}; viewport {viewer.ViewportWidth}; bar maximum {horizontal.Maximum}");

                var thumbChrome = Assert.IsType<Border>(
                    verticalTrack.Thumb.Template.FindName("ThumbChrome", verticalTrack.Thumb));
                Assert.Equal(ColorOf(light["Saz.ScrollBarThumb"]), ColorOf(thumbChrome.Background));
                var corner = Assert.IsType<Border>(viewer.Template.FindName("ScrollBarCorner", viewer));
                Assert.Equal(ColorOf(light["Saz.ScrollBarCorner"]), ColorOf(corner.Background));

                var dark = new ResourceDictionary { Source = ThemeManager.PaletteUri(AppTheme.Dark) };
                window.Resources.MergedDictionaries[0] = dark;
                StaRunner.DoEvents();
                Assert.Equal(ColorOf(dark["Saz.ScrollBarThumb"]), ColorOf(thumbChrome.Background));
                Assert.Equal(ColorOf(dark["Saz.ScrollBarCorner"]), ColorOf(corner.Background));

                var contrast = ThemeManager.HighContrastPalette();
                window.Resources.MergedDictionaries[0] = contrast;
                StaRunner.DoEvents();
                Assert.Equal(SystemColors.WindowTextColor, ColorOf(thumbChrome.Background));
                Assert.Equal(SystemColors.WindowColor, ColorOf(corner.Background));
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void SharedScrollViewerTemplatePreservesListVirtualization()
    {
        StaRunner.Run(() =>
        {
            var list = new ListBox
            {
                Width = 280,
                Height = 180,
                ItemsSource = Enumerable.Range(0, 10_000).ToArray()
            };
            ScrollViewer.SetCanContentScroll(list, true);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            var window = new Window
            {
                Width = 320,
                Height = 220,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000,
                Content = list
            };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeManager.PaletteUri(AppTheme.Light) });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/SazViewer.App;component/Themes/Controls.xaml")
            });
            try
            {
                window.Show();
                StaRunner.DoEvents();
                Assert.InRange(Descendants<ListBoxItem>(list).Count(), 1, 100);
                var bar = Assert.Single(Descendants<ScrollBar>(list), item => item.Orientation == Orientation.Vertical);
                Assert.Equal(12, bar.ActualWidth);

                list.ScrollIntoView(9999);
                StaRunner.DoEvents();
                Assert.InRange(Descendants<ListBoxItem>(list).Count(), 1, 100);
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
    }

    private static Color ColorOf(object value) => Assert.IsType<SolidColorBrush>(value).Color;

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
