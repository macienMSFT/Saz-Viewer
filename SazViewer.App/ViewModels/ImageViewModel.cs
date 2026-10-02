using System.Globalization;
using System.Windows.Media.Imaging;
using SazViewer.App.Model;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The Image tab (the report's <c>createImageView</c> / <c>renderImageView</c>): metadata, detection and MIME
/// mismatch warning, and the retained bytes decoded off the UI thread through <see cref="ImageSafety"/> when
/// the tab is shown. The bitmap is released when the tab is left.
/// </summary>
internal sealed class ImageViewModel : TabContentViewModel
{
    public const string PendingDimensions = "Dimensions load with the image.";

    private readonly ReadOnlyMemory<byte> bytes;
    private readonly Func<ReadOnlyMemory<byte>, string, ImageDecodeResult> decode;
    private BitmapSource? image;
    private string dimensions = PendingDimensions;
    private string status = "Image loads when this tab is selected.";
    private bool isWarning;
    private bool decodeFailed;
    private int generation;

    public ImageViewModel(HtmlReportGenerator.ImageViewInfo info, ReadOnlyMemory<byte> bytes, long length,
        Func<ReadOnlyMemory<byte>, string, ImageDecodeResult>? decode = null)
    {
        Info = info;
        this.bytes = bytes;
        this.decode = decode ?? ImageSafety.Decode;
        SizeText = $"{length.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} bytes";
    }

    public HtmlReportGenerator.ImageViewInfo Info { get; }

    public string MimeType => Info.MimeType;

    public string SizeText { get; }

    public string Animation => Info.Animation;

    public string Detection => Info.Detection;

    public string? Warning => Info.Warning;

    public string Dimensions
    {
        get => dimensions;
        private set => SetProperty(ref dimensions, value);
    }

    public BitmapSource? Image
    {
        get => image;
        private set => SetProperty(ref image, value);
    }

    private int pixelWidth;
    private int pixelHeight;

    public int PixelWidth
    {
        get => pixelWidth;
        private set => SetProperty(ref pixelWidth, value);
    }

    public int PixelHeight
    {
        get => pixelHeight;
        private set => SetProperty(ref pixelHeight, value);
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public bool IsWarning
    {
        get => isWarning;
        private set => SetProperty(ref isWarning, value);
    }

    /// <summary>The decode in flight, if any (tests await it).</summary>
    public Task? Loading { get; private set; }

    /// <summary>Starts decoding when the view is shown; a no-op once loaded or loading.</summary>
    public Task EnsureLoaded()
    {
        if (Loading is not null)
        {
            return Loading;
        }
        var token = ++generation;
        var context = SynchronizationContext.Current;
        Status = "Decoding image...";
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Loading = applied.Task;
        _ = Task.Run(() => decode(bytes, MimeType)).ContinueWith(task =>
        {
            void Apply()
            {
                try
                {
                    if (token == generation)
                    {
                        Complete(task.IsCompletedSuccessfully ? task.Result : new ImageDecodeResult(null, 0, 0, "the image decoder failed"));
                    }
                }
                finally
                {
                    applied.TrySetResult();
                }
            }
            if (context is null)
            {
                Apply();
            }
            else
            {
                context.Post(_ => Apply(), null);
            }
        }, TaskScheduler.Default);
        return Loading;
    }

    /// <summary>Drops the decoded bitmap when the tab is left; it decodes again when shown.</summary>
    public override void Deactivate()
    {
        generation++;
        Loading = null;
        Image = null;
    }

    /// <summary>The report's <c>imageMetadata</c>: the metadata, detection and warning lines.</summary>
    public string MetadataText()
    {
        var lines = new List<string> { MimeType, SizeText, Animation, Dimensions, Detection };
        if (Warning is not null)
        {
            lines.Add(Warning);
        }
        var text = string.Join('\n', lines.Where(line => line.Trim().Length > 0).Select(line => line.Trim()));
        return decodeFailed ? text + "\nImage decode failed." : text;
    }

    public override CopyResult GetCopyText() => CopyResult.Of(MetadataText());

    private void Complete(ImageDecodeResult result)
    {
        PixelWidth = result.Width;
        PixelHeight = result.Height;
        if (result.Width > 0 && result.Height > 0)
        {
            Dimensions = $"{result.Width}\u00d7{result.Height} pixels";
        }
        if (result.Image is null)
        {
            decodeFailed = true;
            Status = $"Image could not be loaded: {result.Error}.";
            IsWarning = true;
            return;
        }
        decodeFailed = false;
        Image = result.Image;
        Status = "Image decoded locally from retained body bytes.";
        IsWarning = false;
    }
}
