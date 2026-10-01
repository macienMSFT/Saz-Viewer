using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace SazViewer.App.Tests;

/// <summary>Runs WPF code on a dedicated STA thread with the app's theme and view resources loaded.</summary>
internal static class StaRunner
{
    private static readonly object Gate = new();

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureResources();
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        lock (Gate)
        {
            thread.Start();
            thread.Join();
        }
        failure?.Throw();
    }

    /// <summary>Like <see cref="Run"/>, but inside a running dispatcher loop (WebView2 requires one).</summary>
    public static void RunInLoop(Action action) =>
        Run(() =>
        {
            ExceptionDispatchInfo? inner = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    inner = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                }
            });
            Dispatcher.Run();
            inner?.Throw();
        });

    /// <summary>Processes queued dispatcher work (layout, bindings, loaded events).</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void EnsureResources()
    {
        if (Application.ResourceAssembly is null)
        {
            Application.ResourceAssembly = typeof(App).Assembly;
        }
    }
}
