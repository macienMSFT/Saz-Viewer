using System.Windows;
using System.Windows.Threading;

namespace SazViewer.App;

public partial class App : Application
{
    private SingleInstanceService? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var arguments = AppArguments.Parse(e.Args);
        if (arguments.Error is not null)
        {
            MessageBox.Show($"{arguments.Error}\n\n{AppArguments.Usage}", "SAZ Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (arguments.ShowHelp)
        {
            MessageBox.Show(AppArguments.Usage, "SAZ Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        var paths = new List<string>();
        var unresolved = new List<string>();
        foreach (var path in arguments.CapturePaths)
        {
            if (ForwardedPathValidator.TryResolve(path, Environment.CurrentDirectory) is { } resolved)
            {
                paths.Add(resolved);
            }
            else
            {
                unresolved.Add(path);
            }
        }

        var role = StartSingleInstance(paths);
        if (role is InstanceRole.Forwarded or InstanceRole.ForwardRejected)
        {
            if (role == InstanceRole.ForwardRejected)
            {
                MessageBox.Show("SAZ Viewer is already running but refused the request to open these files.", "SAZ Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(role == InstanceRole.Forwarded ? 0 : 1);
            return;
        }

        var window = new MainWindow(new RecentFilesStore(AppPaths.RecentFilesPath), FileAssociationService.ForCurrentUser());
        MainWindow = window;
        window.Show();
        if (unresolved.Count > 0)
        {
            MessageBox.Show(window, "Invalid capture path:\n" + string.Join("\n", unresolved), "SAZ Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        if (paths.Count > 0)
        {
            _ = window.OpenCapturesAsync(paths);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        singleInstance = null;
        base.OnExit(e);
    }

    private InstanceRole StartSingleInstance(IReadOnlyList<string> paths)
    {
        try
        {
            singleInstance = SingleInstanceService.ForCurrentUser(AppPaths.DataDirectory);
            // Subscribed before the server starts; queued work runs after OnStartup has created the window.
            singleInstance.PathsReceived += (_, forwarded) => Dispatcher.BeginInvoke(async () =>
            {
                if (MainWindow is MainWindow window)
                {
                    await window.OpenForwardedAsync(forwarded);
                }
            });
            var role = singleInstance.Start(paths);
            if (role != InstanceRole.Primary)
            {
                singleInstance.Dispose();
                singleInstance = null;
            }
            return role;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Security.SecurityException or ArgumentException)
        {
            singleInstance?.Dispose();
            singleInstance = null;
            return InstanceRole.Standalone;
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
