using System.Windows.Media.Imaging;

namespace SazViewer.App.Tests;

public sealed class AppIconTests
{
    [Fact]
    public void WindowIconResourceHasEverySize()
    {
        StaRunner.Run(() =>
        {
            var decoder = BitmapDecoder.Create(
                new Uri("pack://application:,,,/SazViewer.App;component/Assets/SazViewer.ico"),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            var sizes = decoder.Frames.Select(frame => frame.PixelWidth).Order().ToArray();
            Assert.Equal([16, 24, 32, 48, 64, 128, 256], sizes);
            Assert.All(decoder.Frames, frame => Assert.Equal(frame.PixelWidth, frame.PixelHeight));
        });
    }
}
