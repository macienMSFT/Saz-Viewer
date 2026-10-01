using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SazViewer.App;

/// <summary>Services a <see cref="CaptureTab"/> needs from its window.</summary>
internal interface ICaptureTabHost
{
    Window Owner { get; }

    /// <summary>Opens dropped captures (each in its own tab).</summary>
    Task OpenCapturesAsync(IReadOnlyList<string> paths);

    void RequestClose(CaptureTab tab);

    Task ReloadAsync(CaptureTab tab);

    /// <summary>The tab's status text or notice changed.</summary>
    void OnTabStateChanged(CaptureTab tab);
}

/// <summary>
/// One open capture: its in-memory report, strip header, content view, lazily created WebView2 with its
/// <see cref="SecureReportSession"/>, inspector popups, and file watcher. Disposing releases all of them.
/// </summary>
internal sealed class CaptureTab : ICaptureTab
{
    private const string AppTitle = "SAZ Viewer";
    private readonly ICaptureTabHost host;
    private readonly List<ReportPopupWindow> popups = [];
    private readonly CaptureFileWatcher watcher;
    private readonly FileChangeTracker tracker;
    private ReportDocument? document;
    private WebView2? webView;
    private SecureReportSession? session;
    private Task<bool>? webViewInitialization;
    private FileChangeNotice notice;
    private string? reloadError;
    private bool reloading;
    private bool disposed;

    public CaptureTab(ReportDocument document, FileFingerprint? fingerprint, ICaptureTabHost host)
    {
        this.document = document;
        this.host = host;
        SourcePath = document.SourcePath;
        tracker = new FileChangeTracker(fingerprint);
        View = new CaptureTabView { Visibility = Visibility.Collapsed };
        View.ReloadRequested += async (_, _) => await host.ReloadAsync(this);
        View.DismissRequested += (_, _) => Dismiss();
        TabItem = CreateTabItem();
        watcher = new CaptureFileWatcher(SourcePath, CaptureFileWatcher.DefaultQuietPeriod, TimeProvider.System);
        watcher.Settled += OnFileSettled;
    }

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    public ReportDocument? Document => disposed ? null : document;

    public TabItem TabItem { get; }

    public CaptureTabView View { get; }

    public bool IsDisposed => disposed;

    public bool IsWebViewCreated => webView is not null;

    public int PopupCount => popups.Count;

    public string Status { get; private set; } = "";

    public bool IsReloading
    {
        get => reloading;
        set
        {
            reloading = value;
            RenderNotice();
        }
    }

    /// <summary>Shows this tab's content, creating its WebView2 on first activation.</summary>
    public async Task ActivateAsync()
    {
        View.Visibility = Visibility.Visible;
        UpdateStatus();
        if (disposed)
        {
            return;
        }
        webViewInitialization ??= InitializeWebViewAsync();
        await webViewInitialization;
    }

    public void Deactivate() => View.Visibility = Visibility.Collapsed;

    /// <summary>Swaps in a freshly parsed report, closing popups that show the previous one.</summary>
    public void ReplaceDocument(ReportDocument replacement, FileFingerprint? fingerprint)
    {
        if (disposed)
        {
            return;
        }
        CloseAllPopups();
        document = replacement;
        reloadError = null;
        tracker.Reset(fingerprint);
        notice = tracker.Evaluate(FileFingerprint.TryRead(SourcePath));
        RenderNotice();
        session?.NavigateToReport();
        UpdateStatus();
    }

    public void ShowReloadError(string message)
    {
        reloadError = message;
        RenderNotice();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        watcher.Settled -= OnFileSettled;
        watcher.Dispose();
        CloseAllPopups();
        if (webView is not null)
        {
            if (webView.CoreWebView2 is { } core)
            {
                core.ProcessFailed -= OnProcessFailed;
            }
            View.Host.Children.Remove(webView);
            webView.Dispose();
            webView = null;
        }
        session = null;
        document = null;
        (View.Parent as Panel)?.Children.Remove(View);
    }

    private TabItem CreateTabItem()
    {
        var title = new TextBlock { Text = FileName, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = "\u2715",
            FontSize = 10,
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(8, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Focusable = false,
            ToolTip = "Close (Ctrl+W)",
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(close, $"Close {FileName}");
        close.Click += (_, _) => host.RequestClose(this);
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(title);
        header.Children.Add(close);
        var item = new TabItem { Header = header, ToolTip = SourcePath, Tag = this };
        AutomationProperties.SetName(item, FileName);
        item.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                host.RequestClose(this);
            }
        };
        return item;
    }

    private async Task<bool> InitializeWebViewAsync()
    {
        var view = new WebView2();
        webView = view;
        View.Host.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(host.Owner,
                "The Microsoft Edge WebView2 Runtime is required to display reports. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again. File › Export HTML still works without it.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            webViewInitialization = null;
            View.Host.Children.Remove(view);
            view.Dispose();
            webView = null;
            return false;
        }
        if (disposed)
        {
            return false;
        }
        session = CreateSession(view.CoreWebView2, () => Document);
        view.CoreWebView2.ProcessFailed += OnProcessFailed;
        session.NavigateToReport();
        return true;
    }

    private SecureReportSession CreateSession(CoreWebView2 core, Func<ReportDocument?> provider)
    {
        var created = new SecureReportSession(core, provider);
        created.CaptureDropped += async (_, path) => await host.OpenCapturesAsync([path]);
        created.CreatePopupAsync = () => CreatePopupAsync(provider());
        return created;
    }

    private async Task<CoreWebView2?> CreatePopupAsync(ReportDocument? source)
    {
        if (source is null || !ReferenceEquals(source, Document))
        {
            return null;
        }
        var popup = new ReportPopupWindow($"{source.FileName} - Inspector - {AppTitle}");
        popups.Add(popup);
        popup.Closed += (_, _) => popups.Remove(popup);
        popup.Show();
        await popup.ReportView.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
        var core = popup.ReportView.CoreWebView2;
        CreateSession(core, () => ReferenceEquals(source, Document) ? source : null);
        core.WindowCloseRequested += (_, _) => popup.Close();
        return core;
    }

    private void CloseAllPopups()
    {
        foreach (var popup in popups.ToArray())
        {
            popup.Close();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            && Document is not null)
        {
            Status = "The report view stopped responding and was reloaded.";
            host.OnTabStateChanged(this);
            session?.NavigateToReport();
        }
    }

    private void OnFileSettled(object? sender, FileFingerprint? current) =>
        View.Dispatcher.BeginInvoke(() =>
        {
            if (disposed)
            {
                return;
            }
            notice = tracker.Evaluate(current);
            RenderNotice();
        });

    private void Dismiss()
    {
        tracker.Dismiss(FileFingerprint.TryRead(SourcePath));
        notice = FileChangeNotice.None;
        reloadError = null;
        RenderNotice();
    }

    private void RenderNotice()
    {
        if (disposed)
        {
            return;
        }
        if (reloading)
        {
            View.ShowNotice($"Reloading {FileName}…", canReload: true, buttonsEnabled: false);
        }
        else if (reloadError is not null)
        {
            View.ShowNotice($"Reload failed; the previous report is still shown. {reloadError}", canReload: notice != FileChangeNotice.Deleted);
        }
        else
        {
            switch (notice)
            {
                case FileChangeNotice.Changed:
                    View.ShowNotice("This file changed on disk.", canReload: true);
                    break;
                case FileChangeNotice.Deleted:
                    View.ShowNotice("File no longer exists. The loaded report remains available.", canReload: false);
                    break;
                default:
                    View.ShowNotice(null, canReload: false);
                    break;
            }
        }
        host.OnTabStateChanged(this);
    }

    private void UpdateStatus()
    {
        if (Document is { } current)
        {
            Status = $"{FileName}: {current.SessionCount:N0} HTTP sessions, {current.WebSocketMessageCount:N0} WebSocket messages, {current.WarningCount:N0} warnings";
        }
        host.OnTabStateChanged(this);
    }
}
