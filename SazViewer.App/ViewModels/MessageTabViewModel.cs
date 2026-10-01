using System.Windows.Threading;
using SazViewer.App.Mvvm;

namespace SazViewer.App.ViewModels;

/// <summary>One per-side tab (JSON, XML, MAPI, Image, WebView, HexView, Auth, Headers, Raw) with its copy toolbar.</summary>
internal sealed class MessageTabViewModel : ObservableObject
{
    private readonly Func<TabContentViewModel>? factory;
    private readonly IClipboardService clipboard;
    private TabContentViewModel? content;
    private string copyButtonText = "Copy";
    private string copyStatus = "";
    private int copyGeneration;

    public MessageTabViewModel(
        string key,
        string label,
        bool isEnabled,
        string unavailableText,
        string copyAccessibleName,
        string copyDescription,
        Func<TabContentViewModel>? factory,
        IClipboardService clipboard)
    {
        Key = key;
        Label = label;
        IsEnabled = isEnabled;
        UnavailableText = unavailableText;
        CopyAccessibleName = copyAccessibleName;
        CopyDescription = copyDescription;
        this.factory = factory;
        this.clipboard = clipboard;
        CopyCommand = new RelayCommand(Copy, () => IsEnabled);
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsEnabled { get; }

    public string UnavailableText { get; }

    public string CopyAccessibleName { get; }

    public string CopyDescription { get; }

    public RelayCommand CopyCommand { get; }

    public bool IsCreated => content is not null;

    /// <summary>The view content, created on first access (lazy per-session decoding).</summary>
    public TabContentViewModel? Content
    {
        get
        {
            if (content is null && IsEnabled && factory is not null)
            {
                content = factory();
            }
            return content;
        }
    }

    public string CopyButtonText
    {
        get => copyButtonText;
        private set => SetProperty(ref copyButtonText, value);
    }

    public string CopyStatus
    {
        get => copyStatus;
        private set => SetProperty(ref copyStatus, value);
    }

    public void Deactivate() => content?.Deactivate();

    public void Copy()
    {
        if (!IsEnabled)
        {
            return;
        }
        var generation = ++copyGeneration;
        var source = Content?.GetCopyText() ?? CopyResult.Fail("Copy source is unavailable.");
        if (source.Error is not null)
        {
            CopyStatus = source.Error;
            CopyButtonText = "Copy";
            return;
        }
        if (!clipboard.TrySetText(source.Text!))
        {
            CopyStatus = "Copy failed. Check clipboard permissions or select and copy the content manually.";
            CopyButtonText = "Copy";
            return;
        }
        CopyButtonText = "Copied";
        CopyStatus = $"Copied {CopyDescription}.";
        if (Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher)
        {
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Normal, (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                if (generation == copyGeneration)
                {
                    CopyButtonText = "Copy";
                }
            }, dispatcher);
            timer.Start();
        }
    }
}
