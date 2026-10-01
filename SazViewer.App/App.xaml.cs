using System.Windows;
using System.Windows.Threading;

namespace SazViewer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var arguments = AppArguments.Parse(e.Args);
        var window = new MainWindow(new RecentFilesStore(AppPaths.RecentFilesPath));
        MainWindow = window;
        window.Show();

        if (arguments.Error is not null)
        {
            MessageBox.Show(window, $"{arguments.Error}\n\n{AppArguments.Usage}", "SAZ Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (arguments.ShowHelp)
        {
            MessageBox.Show(window, AppArguments.Usage, "SAZ Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (arguments.CapturePath is not null)
        {
            _ = window.OpenCaptureAsync(arguments.CapturePath);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            MainWindow,
            $"An unexpected error occurred: {e.Exception.GetType().Name}: {e.Exception.Message}",
            "SAZ Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
