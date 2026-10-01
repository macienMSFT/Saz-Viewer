using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SazViewer.Core;

namespace SazViewer.App;

public partial class MainWindow : Window
{
    private const string AppTitle = "SAZ Viewer";
    private readonly RecentFilesStore recentFiles;
    private readonly List<ReportPopupWindow> popups = [];
    private SecureReportSession? session;
    private ReportDocument? document;
    private bool busy;

    internal MainWindow(RecentFilesStore recentFiles)
    {
        this.recentFiles = recentFiles;
        InitializeComponent();
        RebuildRecentMenu();
        Closed += (_, _) => CloseAllPopups();
    }

    /// <summary>Parses a capture off the UI thread and, on success, replaces the current report.</summary>
    internal async Task OpenCaptureAsync(string path)
    {
        if (busy)
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowError($"Invalid path: {exception.Message}");
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
        var loaded = await RunBusyAsync(
            $"Opening {name}…",
            () => ReportBuilder.Build(fullPath, scrubAuth: false, new DialogPasswordProvider(this, name, "to open it")));
        if (loaded is null)
        {
            return;
        }

        if (!await EnsureWebViewAsync())
        {
            return;
        }
        CloseAllPopups();
        document = loaded;
        recentFiles.Add(fullPath);
        RebuildRecentMenu();
        Title = $"{name} - {AppTitle}";
        WelcomePanel.Visibility = Visibility.Collapsed;
        ReportView.Visibility = Visibility.Visible;
        ExportMenuItem.IsEnabled = true;
        ExportScrubbedMenuItem.IsEnabled = true;
        session!.NavigateToReport();
        StatusText.Text =
            $"{name}: {loaded.SessionCount:N0} HTTP sessions, {loaded.WebSocketMessageCount:N0} WebSocket messages, {loaded.WarningCount:N0} warnings";
    }

    private async Task<bool> EnsureWebViewAsync()
    {
        if (session is not null)
        {
            return true;
        }
        try
        {
            await ReportView.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("The Microsoft Edge WebView2 Runtime is required to display reports. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again. File › Export HTML still works without it.");
            return false;
        }

        session = CreateSession(ReportView.CoreWebView2, () => document);
        ReportView.CoreWebView2.ProcessFailed += OnProcessFailed;
        return true;
    }

    private SecureReportSession CreateSession(CoreWebView2 core, Func<ReportDocument?> provider)
    {
        var created = new SecureReportSession(core, provider);
        created.CaptureDropped += async (_, path) => await OpenCaptureAsync(path);
        created.CreatePopupAsync = () => CreatePopupAsync(provider());
        return created;
    }

    private async Task<CoreWebView2?> CreatePopupAsync(ReportDocument? source)
    {
        if (source is null || !ReferenceEquals(source, document))
        {
            return null;
        }
        var popup = new ReportPopupWindow($"{source.FileName} - Inspector - {AppTitle}");
        popups.Add(popup);
        popup.Closed += (_, _) => popups.Remove(popup);
        popup.Show();
        await popup.ReportView.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync());
        var core = popup.ReportView.CoreWebView2;
        CreateSession(core, () => source);
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
            && document is not null)
        {
            StatusText.Text = "The report view stopped responding and was reloaded.";
            session?.NavigateToReport();
        }
    }

    private async Task Export(bool scrubbed)
    {
        var current = document;
        if (current is null || busy)
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

        // The displayed report is unscrubbed; scrubbing mutates the parsed model, so a scrubbed export
        // re-parses the capture (prompting again for an encrypted capture rather than retaining its password).
        var exported = await RunBusyAsync(
            $"Exporting {Path.GetFileName(outputPath)}…",
            () =>
            {
                var source = scrubbed
                    ? ReportBuilder.Build(
                        current.SourcePath,
                        scrubAuth: true,
                        new DialogPasswordProvider(this, current.FileName, "to export a scrubbed copy"))
                    : current;
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

    private async Task<T?> RunBusyAsync<T>(string message, Func<T> work) where T : class
    {
        busy = true;
        var previousStatus = StatusText.Text;
        StatusText.Text = message;
        BusyIndicator.Visibility = Visibility.Visible;
        Cursor = Cursors.AppStarting;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            return await Task.Run(work);
        }
        catch (SazPasswordCancelledException)
        {
            StatusText.Text = previousStatus;
            return null;
        }
        catch (Exception exception) when (ReportBuilder.DescribeFailure(exception) is { } description)
        {
            StatusText.Text = previousStatus;
            ShowError(description);
            return null;
        }
        finally
        {
            busy = false;
            BusyIndicator.Visibility = Visibility.Collapsed;
            Cursor = null;
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
        RecentMenu.IsEnabled = !busy;
        ExportMenuItem.IsEnabled = !busy && document is not null;
        ExportScrubbedMenuItem.IsEnabled = !busy && document is not null;
    }

    private void CanOpen(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !busy;

    private async void OnOpen(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Fiddler capture",
            Filter = "Fiddler sessions (*.saz)|*.saz|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenCaptureAsync(dialog.FileName);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e) => await Export(scrubbed: false);

    private async void OnExportScrubbed(object sender, RoutedEventArgs e) => await Export(scrubbed: true);

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private static string? SingleDroppedCapture(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && files[0].EndsWith(".saz", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !busy && SingleDroppedCapture(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (SingleDroppedCapture(e) is { } path)
        {
            e.Handled = true;
            await OpenCaptureAsync(path);
        }
    }
}
