using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SazViewer.App;

/// <summary>Services a <see cref="CaptureTab"/> needs from its window.</summary>
internal interface ICaptureTabHost
{
    Window HostWindow { get; }

    /// <summary>Opens dropped captures (each in its own tab).</summary>
    Task OpenCapturesAsync(IReadOnlyList<string> paths);

    void RequestClose(CaptureTab tab);

    Task ReloadAsync(CaptureTab tab);

    /// <summary>The tab's status text or notice changed.</summary>
    void OnTabStateChanged(CaptureTab tab);
}

/// <summary>
/// One open capture: its in-memory report, strip header, native <see cref="Views.CaptureView"/>, pop-out
/// inspector windows, and file watcher. Disposing releases all of them.
/// </summary>
internal sealed class CaptureTab : ICaptureTab
{
    private const string AppTitle = "SAZ Viewer";
    private readonly ICaptureTabHost host;
    private readonly List<InspectorWindow> popOuts = [];
    private readonly CaptureFileWatcher watcher;
    private readonly FileChangeTracker tracker;
    private readonly TextBlock headerTitle;
    private readonly UiPreferences preferences;
    private ReportDocument? document;
    private Views.CaptureView? nativeView;
    private ViewModels.CaptureViewModel? viewModel;
    private InspectorWindow? sessionViewerWindow;
    private FileChangeNotice notice;
    private string? reloadError;
    private bool reloading;
    private bool disposed;

    public CaptureTab(ReportDocument document, FileFingerprint? fingerprint, ICaptureTabHost host, UiPreferences? preferences = null)
    {
        this.document = document;
        this.host = host;
        this.preferences = preferences ?? UiPreferences.Current;
        SourcePath = document.SourcePath;
        tracker = new FileChangeTracker(fingerprint);
        View = new CaptureTabView { Visibility = Visibility.Collapsed };
        View.ReloadRequested += async (_, _) => await host.ReloadAsync(this);
        View.DismissRequested += (_, _) => Dismiss();
        headerTitle = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        TabItem = CreateTabItem();
        nativeView = new Views.CaptureView();
        View.Host.Children.Add(nativeView);
        Attach(document);
        watcher = new CaptureFileWatcher(SourcePath, CaptureFileWatcher.DefaultQuietPeriod, TimeProvider.System);
        watcher.Settled += OnFileSettled;
    }

    public string SourcePath { get; }

    public string FileName => Path.GetFileName(SourcePath);

    public string DisplayName => FileName + (IsScrubbed ? " (scrubbed)" : "");

    public ReportDocument? Document => disposed ? null : document;

    /// <summary>True when the capture is shown with credentials redacted (File › View scrubbed).</summary>
    public bool IsScrubbed => document?.IsScrubbed == true;

    public TabItem TabItem { get; }

    public CaptureTabView View { get; }

    public bool IsDisposed => disposed;

    /// <summary>The native capture view.</summary>
    public Views.CaptureView? NativeView => nativeView;

    public ViewModels.CaptureViewModel? ViewModel => viewModel;

    /// <summary>Open "Open in new window" inspector windows.</summary>
    public IReadOnlyList<InspectorWindow> PopOuts => popOuts;

    /// <summary>The reusable grid-following viewer window, when that option is active and a row is selected.</summary>
    internal InspectorWindow? SessionViewerWindow => sessionViewerWindow;

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

    /// <summary>Shows this tab's content.</summary>
    public Task ActivateAsync()
    {
        View.Visibility = Visibility.Visible;
        UpdateStatus();
        return Task.CompletedTask;
    }

    public void Deactivate() => View.Visibility = Visibility.Collapsed;

    /// <summary>Swaps in a freshly parsed report, closing pop-out inspectors that show the previous one.</summary>
    public void ReplaceDocument(ReportDocument replacement, FileFingerprint? fingerprint)
    {
        if (disposed)
        {
            return;
        }
        CloseAllPopOuts();
        document = replacement;
        reloadError = null;
        tracker.Reset(fingerprint);
        notice = tracker.Evaluate(FileFingerprint.TryRead(SourcePath));
        RenderNotice();
        Attach(replacement);
        UpdateStatus();
    }

    public void ShowReloadError(string message)
    {
        reloadError = message;
        RenderNotice();
    }

    /// <summary>Applies app-wide viewer and inspector-layout options to this open capture.</summary>
    internal void ApplyPreferences()
    {
        if (viewModel is null || disposed)
        {
            return;
        }
        viewModel.ApplyPreferences();
        foreach (var popOut in popOuts)
        {
            popOut.Model.ApplyDefaultLayoutPreference();
        }
        if (viewModel.SessionViewerLocation == SessionViewerLocation.NewWindow)
        {
            EnsureSessionViewerWindow();
        }
        else
        {
            CloseSessionViewerWindow(preserveInspector: true);
        }
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
        CloseAllPopOuts();
        Detach();
        if (nativeView is not null)
        {
            nativeView.DataContext = null;
            View.Host.Children.Remove(nativeView);
            nativeView = null;
        }
        document = null;
        (View.Parent as Panel)?.Children.Remove(View);
    }

    private void Attach(ReportDocument source)
    {
        Detach();
        viewModel = new ViewModels.CaptureViewModel(source.Report, preferences: preferences);
        viewModel.Inspector.PopOutRequested += OnPopOutRequested;
        viewModel.Inspector.Loaded += OnInspectorLoaded;
        nativeView!.DataContext = viewModel;
        ApplyPreferences();
        UpdateHeader();
    }

    private void Detach()
    {
        if (viewModel is not null)
        {
            viewModel.Inspector.PopOutRequested -= OnPopOutRequested;
            viewModel.Inspector.Loaded -= OnInspectorLoaded;
            viewModel.Inspector.Close();
            viewModel.Dispose();
            viewModel = null;
        }
    }

    private void OnInspectorLoaded(object? sender, EventArgs e)
    {
        if (viewModel?.SessionViewerLocation == SessionViewerLocation.NewWindow)
        {
            EnsureSessionViewerWindow();
        }
    }

    private void EnsureSessionViewerWindow()
    {
        if (sessionViewerWindow is not null || viewModel is not { Inspector.IsOpen: true } current)
        {
            return;
        }
        var window = new InspectorWindow(
            FileName + (IsScrubbed ? " (scrubbed)" : ""),
            current.Inspector,
            activateOnShow: false);
        window.PositionRelativeTo(host.HostWindow, 0);
        sessionViewerWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(sessionViewerWindow, window))
            {
                sessionViewerWindow = null;
            }
        };
        window.Show();
    }

    private void CloseSessionViewerWindow(bool preserveInspector)
    {
        if (sessionViewerWindow is not { } window)
        {
            return;
        }
        sessionViewerWindow = null;
        if (preserveInspector)
        {
            window.ClosePreservingInspector();
        }
        else
        {
            window.Close();
        }
    }

    private void OnPopOutRequested(object? sender, Model.SessionRow row)
    {
        if (viewModel is null || disposed)
        {
            return;
        }
        var window = new InspectorWindow(FileName + (IsScrubbed ? " (scrubbed)" : ""), viewModel.CreatePopOutInspector(row));
        window.PositionRelativeTo(host.HostWindow, popOuts.Count + (sessionViewerWindow is null ? 0 : 1));
        popOuts.Add(window);
        window.Closed += (_, _) => popOuts.Remove(window);
        // The report's "Open in new tab" moves the inspector: close it in the main window.
        viewModel.Inspector.Close();
        window.Show();
    }

    private void CloseAllPopOuts()
    {
        CloseSessionViewerWindow(preserveInspector: false);
        foreach (var window in popOuts.ToArray())
        {
            window.Close();
        }
    }

    private void UpdateHeader()
    {
        var name = DisplayName;
        headerTitle.Text = name;
        AutomationProperties.SetName(TabItem, name);
    }

    private TabItem CreateTabItem()
    {
        headerTitle.Text = FileName;
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
        header.Children.Add(headerTitle);
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
            var warnings = current.WarningCount is { } count ? $"{count:N0} warnings" : "counting warnings…";
            Status = $"{FileName}: {current.SessionCount:N0} HTTP sessions, {current.WebSocketMessageCount:N0} WebSocket messages, {warnings}";
            if (current.WarningCount is null)
            {
                // Finish decoding in the background once the capture has rendered, then refresh the warning count.
                View.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    if (disposed || !ReferenceEquals(Document, current))
                    {
                        return;
                    }
                    _ = current.StartDeferredWork().ContinueWith(
                        task => View.Dispatcher.BeginInvoke(() =>
                        {
                            // A failed completion leaves the count pending; don't retry in a loop.
                            if (task.IsCompletedSuccessfully && !disposed && ReferenceEquals(Document, current))
                            {
                                UpdateStatus();
                            }
                        }),
                        TaskScheduler.Default);
                });
            }
        }
        host.OnTabStateChanged(this);
    }
}
