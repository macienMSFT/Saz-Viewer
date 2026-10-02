using System.Diagnostics;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SazViewer.App.Themes;
using SazViewer.Core;

namespace SazViewer.App;

public partial class MainWindow : Window, ICaptureTabHost
{
    private const string AppTitle = "SAZ Viewer";
    private readonly RecentFilesStore recentFiles;
    private readonly FileAssociationService fileAssociation;
    private readonly CaptureTabCollection<CaptureTab> tabs = new();

    // Serializes parse work (open, reload, export) so password prompts never overlap and
    // de-duplication sees every tab that an earlier queued request created.
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private int busyCount;
    private string? busyMessage;
    private bool syncingSelection;
    private CapturePrefetch? prefetch;

    internal MainWindow(RecentFilesStore recentFiles, FileAssociationService fileAssociation)
    {
        this.recentFiles = recentFiles;
        this.fileAssociation = fileAssociation;
        InitializeComponent();
        tabs.ActiveChanged += OnActiveTabChanged;
        RebuildRecentMenu();
        Loaded += (_, _) => ReportStaleRegistration();
        Closed += (_, _) => tabs.CloseAll();
    }

    Window ICaptureTabHost.HostWindow => this;

    internal CaptureTabCollection<CaptureTab> Tabs => tabs;

    private bool Busy => busyCount > 0;

    /// <summary>Opens each capture in its own tab, focusing an existing tab for a capture that is already open.</summary>
    public async Task OpenCapturesAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            await OpenCaptureAsync(path);
        }
    }

    /// <summary>Parses a capture off the UI thread and, on success, adds and activates a tab for it.</summary>
    internal async Task OpenCaptureAsync(string path)
    {
        string fullPath;
        try
        {
            fullPath = CaptureTabCollection<CaptureTab>.NormalizePath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError($"Invalid path: {exception.Message}");
            return;
        }
        if (tabs.FocusExisting(fullPath) is not null)
        {
            return;
        }

        await operationGate.WaitAsync();
        try
        {
            if (tabs.FocusExisting(fullPath) is not null)
            {
                return;
            }
            if (!File.Exists(fullPath))
            {
                recentFiles.Remove(fullPath);
                RebuildRecentMenu();
                ShowError($"The capture was not found and has been removed from the recent files list:\n{fullPath}");
                return;
            }

            var name = Path.GetFileName(fullPath);
            var prefetch = TakePrefetch(fullPath);
            // Read before parsing: a write during the parse then still shows up as a change.
            var fingerprint = prefetch is not null ? prefetch.Fingerprint : FileFingerprint.TryRead(fullPath);
            var loaded = await RunBusyAsync<ReportDocument>(
                $"Opening {name}…",
                prefetch is not null
                    ? () => prefetch.Report.GetAwaiter().GetResult()
                    : () => ReportBuilder.Build(fullPath, scrubAuth: false, new DialogPasswordProvider(this, name, "to open it")));
            if (loaded is null)
            {
                return;
            }

            recentFiles.Add(fullPath);
            RebuildRecentMenu();
            var tab = new CaptureTab(loaded, fingerprint, this);
            StartupTrace.Mark("tab-created");
            ContentHost.Children.Add(tab.View);
            syncingSelection = true;
            TabStrip.Items.Add(tab.TabItem);
            syncingSelection = false;
            tabs.Add(tab);
            StartupTrace.Mark("tab-added");
            if (StartupTrace.IsEnabled)
            {
                _ = Dispatcher.BeginInvoke(() => StartupTrace.Mark("grid-idle"), System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>Hands over a parse <see cref="App"/> started before this window existed.</summary>
    internal void SetPrefetch(CapturePrefetch prefetch) => this.prefetch = prefetch;

    private CapturePrefetch? TakePrefetch(string fullPath)
    {
        var taken = prefetch;
        prefetch = null;
        return taken is not null && taken.Matches(fullPath) ? taken : null;
    }

    /// <summary>Handles paths forwarded by a second launch.</summary>
    internal async Task OpenForwardedAsync(ForwardedPaths forwarded)
    {
        BringToForeground();
        if (forwarded.Ignored > 0)
        {
            StatusText.Text = $"Ignored {forwarded.Ignored} forwarded path(s) that are not existing .saz files.";
        }
        await OpenCapturesAsync(forwarded.Accepted);
    }

    internal void BringToForeground()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
        // Toggling Topmost raises the window above others without keeping it pinned.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    void ICaptureTabHost.RequestClose(CaptureTab tab) => CloseTab(tab);

    Task ICaptureTabHost.ReloadAsync(CaptureTab tab) => ReloadAsync(tab);

    void ICaptureTabHost.OnTabStateChanged(CaptureTab tab) => OnTabStateChanged(tab);

    private void CloseTab(CaptureTab tab)
    {
        syncingSelection = true;
        TabStrip.Items.Remove(tab.TabItem);
        syncingSelection = false;
        tabs.Close(tab);
        UpdateChrome();
    }

    /// <summary>Re-parses the tab's capture; <paramref name="scrub"/> switches credential scrubbing (default: keep it).</summary>
    private async Task ReloadAsync(CaptureTab tab, bool? scrub = null)
    {
        var scrubAuth = scrub ?? tab.IsScrubbed;
        if (tab.IsReloading || tab.IsDisposed)
        {
            return;
        }
        tab.IsReloading = true;
        await operationGate.WaitAsync();
        try
        {
            if (tab.IsDisposed)
            {
                return;
            }
            if (!File.Exists(tab.SourcePath))
            {
                tab.ShowReloadError("The file no longer exists.");
                return;
            }
            var fingerprint = FileFingerprint.TryRead(tab.SourcePath);
            string? failure = null;
            var reloaded = await RunBusyAsync(
                scrubAuth == tab.IsScrubbed ? $"Reloading {tab.FileName}…" : scrubAuth ? $"Scrubbing {tab.FileName}…" : $"Reopening {tab.FileName} unscrubbed…",
                () => ReportBuilder.Build(tab.SourcePath, scrubAuth, new DialogPasswordProvider(this, tab.FileName, scrubAuth == tab.IsScrubbed ? "to reload it" : "to reopen it")),
                error => failure = error);
            if (tab.IsDisposed)
            {
                return;
            }
            tab.IsReloading = false;
            if (reloaded is not null)
            {
                tab.ReplaceDocument(reloaded, fingerprint);
            }
            else if (failure is not null)
            {
                tab.ShowReloadError(failure);
            }
        }
        finally
        {
            if (!tab.IsDisposed)
            {
                tab.IsReloading = false;
            }
            operationGate.Release();
            UpdateChrome();
        }
    }

    private void OnTabStateChanged(CaptureTab tab)
    {
        if (ReferenceEquals(tab, tabs.Active))
        {
            UpdateChrome();
        }
    }

    private void OnActiveTabChanged(object? sender, CaptureTab? active)
    {
        foreach (var tab in tabs.Tabs)
        {
            if (!ReferenceEquals(tab, active))
            {
                tab.Deactivate();
            }
        }
        syncingSelection = true;
        TabStrip.SelectedItem = active?.TabItem;
        syncingSelection = false;
        UpdateChrome();
        if (active is not null)
        {
            _ = active.ActivateAsync();
        }
    }

    private void OnTabStripSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection || !ReferenceEquals(e.OriginalSource, TabStrip))
        {
            return;
        }
        if (TabStrip.SelectedItem is TabItem { Tag: CaptureTab tab } && !tab.IsDisposed)
        {
            tabs.Activate(tab);
        }
    }

    private void UpdateChrome()
    {
        var active = tabs.Active;
        Title = active is null ? AppTitle : $"{active.FileName} - {AppTitle}";
        TabStrip.Visibility = tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomePanel.Visibility = tabs.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        CloseTabMenuItem.IsEnabled = active is not null;
        ExportMenuItem.IsEnabled = !Busy && active?.Document is not null;
        ExportScrubbedMenuItem.IsEnabled = !Busy && active?.Document is not null;
        ViewScrubbedMenuItem.IsEnabled = !Busy && active?.Document is not null && !active.IsReloading;
        ViewScrubbedMenuItem.IsChecked = active?.IsScrubbed == true;
        StatusText.Text = busyMessage ?? (string.IsNullOrEmpty(active?.Status) ? "Ready" : active.Status);
    }

    private async Task Export(bool scrubbed)
    {
        var current = tabs.Active?.Document;
        if (current is null || Busy)
        {
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(current.SourcePath);
        var dialog = new SaveFileDialog
        {
            Title = scrubbed ? "Export scrubbed HTML report" : "Export HTML report",
            Filter = "HTML report (*.html)|*.html|All files (*.*)|*.*",
            DefaultExt = ".html",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = baseName + (scrubbed ? ".scrubbed.html" : ".html"),
            InitialDirectory = Path.GetDirectoryName(current.SourcePath)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var outputPath = dialog.FileName;

        await operationGate.WaitAsync();
        try
        {
            // Scrubbing mutates the parsed model, so an export whose scrubbing differs from the displayed capture
            // re-parses it (prompting again for an encrypted capture rather than retaining its password).
            var exported = await RunBusyAsync(
                $"Exporting {Path.GetFileName(outputPath)}…",
                () =>
                {
                    var source = scrubbed == current.IsScrubbed
                        ? current
                        : ReportBuilder.Build(
                            current.SourcePath,
                            scrubAuth: scrubbed,
                            new DialogPasswordProvider(this, current.FileName, scrubbed ? "to export a scrubbed copy" : "to export it"));
                    ReportBuilder.WriteHtml(source, outputPath);
                    return source;
                });
            if (exported is not null)
            {
                StatusText.Text = exported.ScrubbedValueCount is { } count
                    ? $"Exported {outputPath} ({count:N0} credential value(s) replaced)"
                    : $"Exported {outputPath}";
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool with the busy indicator; null on cancel or expected failure.</summary>
    private async Task<T?> RunBusyAsync<T>(string message, Func<T> work, Action<string>? onError = null) where T : class
    {
        busyCount++;
        busyMessage = message;
        BusyIndicator.Visibility = Visibility.Visible;
        Cursor = Cursors.AppStarting;
        UpdateChrome();
        CommandManager.InvalidateRequerySuggested();
        try
        {
            return await Task.Run(work);
        }
        catch (SazPasswordCancelledException)
        {
            return null;
        }
        catch (Exception exception) when (ReportBuilder.DescribeFailure(exception) is { } description)
        {
            busyMessage = null;
            UpdateChrome();
            (onError ?? ShowError)(description);
            return null;
        }
        finally
        {
            busyCount--;
            if (busyCount == 0)
            {
                busyMessage = null;
                BusyIndicator.Visibility = Visibility.Collapsed;
                Cursor = null;
            }
            UpdateChrome();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var paths = recentFiles.Paths;
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var item = new MenuItem
            {
                Header = new TextBlock { Text = $"{index + 1}  {path}" },
                ToolTip = path
            };
            item.Click += async (_, _) => await OpenCaptureAsync(path);
            RecentMenu.Items.Add(item);
        }
        if (paths.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "_Clear recent files", IsEnabled = paths.Count > 0 };
        clear.Click += (_, _) =>
        {
            recentFiles.Clear();
            RebuildRecentMenu();
        };
        RecentMenu.Items.Add(clear);
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);

    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
    {
        RecentMenu.IsEnabled = !Busy;
        UpdateChrome();
    }

    private void OnOptionsMenuOpened(object sender, RoutedEventArgs e) => SyncOptionsMenu();

    private void SyncOptionsMenu()
    {
        var preferences = UiPreferences.Current;
        ThemeSystemMenuItem.IsChecked = preferences.Theme is null;
        ThemeLightMenuItem.IsChecked = preferences.Theme == "light";
        ThemeDarkMenuItem.IsChecked = preferences.Theme == "dark";
        LayoutAutomaticMenuItem.IsChecked = preferences.DefaultInspectorLayout == InspectorLayoutMode.Automatic;
        LayoutSplitMenuItem.IsChecked = preferences.DefaultInspectorLayout == InspectorLayoutMode.AlwaysSplit;
        LayoutSingleMenuItem.IsChecked = preferences.DefaultInspectorLayout == InspectorLayoutMode.AlwaysSingle;
        ViewerBottomMenuItem.IsChecked = preferences.SessionViewer == SessionViewerLocation.BottomPane;
        ViewerRightMenuItem.IsChecked = preferences.SessionViewer == SessionViewerLocation.RightPane;
        ViewerWindowMenuItem.IsChecked = preferences.SessionViewer == SessionViewerLocation.NewWindow;
        HideConnectOnOpenMenuItem.IsChecked = preferences.HideConnectOnOpen;
    }

    private static void SetTheme(string? theme)
    {
        UiPreferences.Current.Theme = theme;
        ThemeManager.Refresh();
    }

    private void SetDefaultInspectorLayout(InspectorLayoutMode layout)
    {
        UiPreferences.Current.DefaultInspectorLayout = layout;
        foreach (var tab in tabs.Tabs)
        {
            tab.ApplyPreferences();
        }
        SyncOptionsMenu();
    }

    private void SetSessionViewerLocation(SessionViewerLocation location)
    {
        UiPreferences.Current.SessionViewer = location;
        foreach (var tab in tabs.Tabs)
        {
            tab.ApplyPreferences();
        }
        SyncOptionsMenu();
    }

    private void OnThemeSystem(object sender, RoutedEventArgs e)
    {
        SetTheme(null);
        SyncOptionsMenu();
    }

    private void OnThemeLight(object sender, RoutedEventArgs e)
    {
        SetTheme("light");
        SyncOptionsMenu();
    }

    private void OnThemeDark(object sender, RoutedEventArgs e)
    {
        SetTheme("dark");
        SyncOptionsMenu();
    }

    private void OnLayoutAutomatic(object sender, RoutedEventArgs e) =>
        SetDefaultInspectorLayout(InspectorLayoutMode.Automatic);

    private void OnLayoutSplit(object sender, RoutedEventArgs e) =>
        SetDefaultInspectorLayout(InspectorLayoutMode.AlwaysSplit);

    private void OnLayoutSingle(object sender, RoutedEventArgs e) =>
        SetDefaultInspectorLayout(InspectorLayoutMode.AlwaysSingle);

    private void OnViewerBottom(object sender, RoutedEventArgs e) =>
        SetSessionViewerLocation(SessionViewerLocation.BottomPane);

    private void OnViewerRight(object sender, RoutedEventArgs e) =>
        SetSessionViewerLocation(SessionViewerLocation.RightPane);

    private void OnViewerWindow(object sender, RoutedEventArgs e) =>
        SetSessionViewerLocation(SessionViewerLocation.NewWindow);

    private void OnHideConnectOnOpen(object sender, RoutedEventArgs e)
    {
        UiPreferences.Current.HideConnectOnOpen = HideConnectOnOpenMenuItem.IsChecked;
        SyncOptionsMenu();
    }

    private void CanOpen(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !Busy;

    private async void OnOpen(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Fiddler capture",
            Filter = "Fiddler sessions (*.saz)|*.saz|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenCapturesAsync(dialog.FileNames);
        }
    }

    private void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (tabs.Active is { } active)
        {
            CloseTab(active);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = CurrentModifiers();
        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & ModifierKeys.Alt) != 0)
        {
            return;
        }
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        switch (key)
        {
            case Key.Tab:
                tabs.Cycle(forward: !shift);
                e.Handled = true;
                break;
            case Key.PageDown when !shift:
                tabs.Cycle(forward: true);
                e.Handled = true;
                break;
            case Key.PageUp when !shift:
                tabs.Cycle(forward: false);
                e.Handled = true;
                break;
            case Key.W or Key.F4 when !shift:
                if (tabs.Active is { } active)
                {
                    CloseTab(active);
                }
                e.Handled = true;
                break;
        }
    }

    // Keys typed into the WebView tab's WebView2 arrive as forwarded accelerator events, but the browser HWND owns
    // the input, so WPF's per-thread key state may not see Ctrl/Shift. Read the async state too.
    private static ModifierKeys CurrentModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        if (NativeMethods.IsDown(NativeMethods.VkControl)) modifiers |= ModifierKeys.Control;
        if (NativeMethods.IsDown(NativeMethods.VkShift)) modifiers |= ModifierKeys.Shift;
        if (NativeMethods.IsDown(NativeMethods.VkMenu)) modifiers |= ModifierKeys.Alt;
        return modifiers;
    }

    private static class NativeMethods
    {
        internal const int VkShift = 0x10;
        internal const int VkControl = 0x11;
        internal const int VkMenu = 0x12;

        internal static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }

    private async void OnExport(object sender, RoutedEventArgs e) => await Export(scrubbed: false);

    private async void OnExportScrubbed(object sender, RoutedEventArgs e) => await Export(scrubbed: true);

    private async void OnViewScrubbed(object sender, RoutedEventArgs e)
    {
        if (tabs.Active is { } tab)
        {
            await ReloadAsync(tab, scrub: !tab.IsScrubbed);
        }
        UpdateChrome();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private static string[] DroppedCaptures(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(file => file.EndsWith(".saz", StringComparison.OrdinalIgnoreCase)).Take(AppArguments.MaximumPaths).ToArray()
            : [];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedCaptures(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var files = DroppedCaptures(e);
        if (files.Length > 0)
        {
            e.Handled = true;
            await OpenCapturesAsync(files);
        }
    }

    private FileAssociationState? ReadRegistrationState()
    {
        if (AppPaths.ExecutablePath is not { } executable)
        {
            return null;
        }
        try
        {
            return fileAssociation.GetState(executable);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private void ReportStaleRegistration()
    {
        if (ReadRegistrationState() is { Status: FileAssociationStatus.Stale } && tabs.Count == 0 && !Busy)
        {
            StatusText.Text = "The .saz registration points to a different copy of SAZ Viewer. Use Tools › Register to update it.";
        }
    }

    private void OnToolsMenuOpened(object sender, RoutedEventArgs e)
    {
        var state = ReadRegistrationState();
        RegisterMenuItem.IsEnabled = AppPaths.ExecutablePath is not null;
        RegisterMenuItem.IsChecked = state?.Status == FileAssociationStatus.Registered;
        RegisterMenuItem.Header = state?.Status == FileAssociationStatus.Stale
            ? "_Register as .saz handler (update moved app path)…"
            : "_Register as .saz handler";
        UnregisterMenuItem.IsEnabled = state is not null && state.Status != FileAssociationStatus.NotRegistered;
    }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        if (AppPaths.ExecutablePath is not { } executable)
        {
            ShowError("The SAZ Viewer executable could not be located, so it cannot be registered.");
            return;
        }
        var state = ReadRegistrationState();
        if (state?.Status == FileAssociationStatus.Stale
            && MessageBox.Show(this,
                $"The .saz registration points to another location:\n{state.RegisteredCommand}\n\nUpdate it to this copy of SAZ Viewer?\n{executable}",
                AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            fileAssociation.Register(executable);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            ShowError($"Could not register SAZ Viewer for .saz files: {exception.Message}");
            return;
        }
        StatusText.Text = "Registered SAZ Viewer as a .saz handler for your account.";
        var openSettings = MessageBox.Show(this,
            "SAZ Viewer is now registered as a handler for .saz files for your account. Your current default app is not changed.\n\n"
            + "Windows may still ask you to confirm: right-click a .saz file, choose Open with › Choose another app, select SAZ Viewer, "
            + "and tick \"Always\". You can also choose it in Settings › Apps › Default apps.\n\nOpen Default apps settings now?",
            AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (openSettings == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowError($"Could not open Settings: {exception.Message}");
            }
        }
    }

    private void OnUnregister(object sender, RoutedEventArgs e)
    {
        try
        {
            fileAssociation.Unregister();
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            ShowError($"Could not unregister SAZ Viewer: {exception.Message}");
            return;
        }
        StatusText.Text = "Removed SAZ Viewer's .saz registration for your account.";
    }
}
