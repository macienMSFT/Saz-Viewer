using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace SazViewer.App.Tests;

public sealed class InspectorWindowTests
{
    [Fact]
    public void ScrubbedCaptureDisplayNameIncludesSuffix()
    {
        using var temp = new TempDirectory();
        var path = TestCaptures.WritePlain(temp.File("capture.saz"));
        var document = ReportBuilder.Build(path, true, new QueuePasswordProvider());

        StaRunner.Run(() =>
        {
            var hostWindow = new Window { ShowInTaskbar = false };
            var tab = new CaptureTab(document, null, new FakeTabHost(hostWindow));
            try
            {
                Assert.Equal("capture.saz (scrubbed)", tab.DisplayName);
                Assert.Equal("capture.saz (scrubbed)", AutomationProperties.GetName(tab.TabItem));
                tab.ShowReloadError("The file no longer exists.");
                Assert.Equal(Visibility.Visible, ((FrameworkElement)tab.View.FindName("NoticeBar")).Visibility);
                Assert.Contains(
                    "The file no longer exists.",
                    ((TextBlock)tab.View.FindName("NoticeText")).Text);
            }
            finally
            {
                tab.Dispose();
                hostWindow.Close();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void PopOutIsIndependentPositionedAndEitherWindowCanBecomeForeground()
    {
        StaRunner.Run(() =>
        {
            var capture = new ViewModels.CaptureViewModel(
                NativeCaptures.Mixed(),
                new FakeClipboard(),
                new UiPreferences(null));
            var main = new Window
            {
                Title = "Main test window",
                Width = 900,
                Height = 650,
                Left = 120,
                Top = 80,
                ShowInTaskbar = true
            };
            var popOut = new InspectorWindow(
                "capture.saz",
                capture.CreatePopOutInspector(capture.Sessions.VisibleRows[0]));
            try
            {
                main.Show();
                popOut.PositionRelativeTo(main, 1);
                popOut.Show();
                StaRunner.DoEvents();

                Assert.Null(popOut.Owner);
                Assert.True(popOut.ShowInTaskbar);
                Assert.Equal(WindowStartupLocation.Manual, popOut.WindowStartupLocation);
                Assert.NotNull(popOut.Icon);

                var workArea = InspectorWindow.WorkAreaFor(main);
                Assert.InRange(popOut.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - popOut.Width));
                Assert.InRange(popOut.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - popOut.Height));

                var mainHandle = new WindowInteropHelper(main).Handle;
                var popOutHandle = new WindowInteropHelper(popOut).Handle;
                Assert.NotEqual(IntPtr.Zero, mainHandle);
                Assert.NotEqual(IntPtr.Zero, popOutHandle);
                Assert.Equal(IntPtr.Zero, GetWindow(popOutHandle, GetWindowOwner));

                MoveAboveAndAssert(popOutHandle, mainHandle);
                MoveAboveAndAssert(mainHandle, popOutHandle);
                MoveAboveAndAssert(popOutHandle, mainHandle);
                ActivateAndAssert(mainHandle);
                ActivateAndAssert(popOutHandle);
            }
            finally
            {
                popOut.Close();
                main.Close();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void ReloadAndTabCloseStillCloseIndependentPopOuts()
    {
        using var temp = new TempDirectory();
        var path = TestCaptures.WritePlain(temp.File("capture.saz"));
        var first = ReportBuilder.Build(path, false, new QueuePasswordProvider());
        var replacement = ReportBuilder.Build(path, false, new QueuePasswordProvider());

        StaRunner.Run(() =>
        {
            var hostWindow = new Window { Width = 900, Height = 650, ShowInTaskbar = false };
            var host = new FakeTabHost(hostWindow);
            var tab = new CaptureTab(first, null, host);
            try
            {
                hostWindow.Show();
                OpenPopOut(tab);
                var stale = Assert.Single(tab.PopOuts);
                Assert.Null(stale.Owner);

                tab.ReplaceDocument(replacement, null);
                StaRunner.DoEvents();

                Assert.Empty(tab.PopOuts);
                Assert.False(stale.IsVisible);

                OpenPopOut(tab);
                var closing = Assert.Single(tab.PopOuts);
                tab.Dispose();
                StaRunner.DoEvents();

                Assert.Empty(tab.PopOuts);
                Assert.False(closing.IsVisible);
            }

            finally
            {
                if (!tab.IsDisposed)
                {
                    tab.Dispose();
                }
                hostWindow.Close();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void NewWindowViewerFollowsGridSelectionReusesWindowAndSwitchesBackToPane()
    {
        using var temp = new TempDirectory();
        var path = TestCaptures.WritePlain(temp.File("capture.saz"));
        var document = ReportBuilder.Build(path, false, new QueuePasswordProvider());
        var preferences = new UiPreferences(null) { SessionViewer = SessionViewerLocation.NewWindow };

        StaRunner.Run(() =>
        {
            var hostWindow = new Window { Width = 1100, Height = 760, ShowInTaskbar = false };
            var host = new FakeTabHost(hostWindow);
            var tab = new CaptureTab(document, null, host, preferences);
            hostWindow.Content = tab.View;
            try
            {
                hostWindow.Show();
                _ = tab.ActivateAsync();
                StaRunner.DoEvents();
                var grid = (DataGrid)tab.NativeView!.FindName("SessionGrid");
                Assert.True(grid.Focus());
                StaRunner.DoEvents();
                var gridFocus = Keyboard.FocusedElement;
                Assert.NotNull(gridFocus);
                grid.SelectedIndex = 0;
                StaRunner.DoEvents();

                var firstWindow = Assert.IsType<InspectorWindow>(tab.SessionViewerWindow);
                Assert.True(firstWindow.IsVisible);
                Assert.False(firstWindow.ShowActivated);
                Assert.Null(firstWindow.Owner);
                Assert.Same(tab.ViewModel!.Sessions.VisibleRows[0], firstWindow.Model.Row);
                Assert.Same(gridFocus, Keyboard.FocusedElement);
                var firstHandle = new WindowInteropHelper(firstWindow).Handle;
                Assert.Equal(IntPtr.Zero, GetWindow(firstHandle, GetWindowOwner));

                grid.SelectedIndex = 1;
                StaRunner.DoEvents();

                Assert.Same(firstWindow, tab.SessionViewerWindow);
                Assert.Same(tab.ViewModel.Sessions.VisibleRows[1], firstWindow.Model.Row);
                Assert.Same(gridFocus, Keyboard.FocusedElement);

                firstWindow.Model.Navigate(-1);
                StaRunner.DoEvents();
                Assert.Same(tab.ViewModel.Sessions.VisibleRows[0], grid.SelectedItem);
                Assert.Same(firstWindow, tab.SessionViewerWindow);

                firstWindow.Close();
                StaRunner.DoEvents();
                Assert.Null(tab.SessionViewerWindow);
                Assert.False(tab.ViewModel.Inspector.IsOpen);

                grid.SelectedIndex = 1;
                StaRunner.DoEvents();
                var reopened = Assert.IsType<InspectorWindow>(tab.SessionViewerWindow);
                Assert.NotSame(firstWindow, reopened);

                preferences.SessionViewer = SessionViewerLocation.BottomPane;
                tab.ApplyPreferences();
                StaRunner.DoEvents();

                Assert.Null(tab.SessionViewerWindow);
                Assert.True(tab.ViewModel.Inspector.IsOpen);
                var inspectorHost = (FrameworkElement)tab.NativeView.FindName("InspectorHost");
                Assert.True(inspectorHost.IsVisible);
                Assert.Same(gridFocus, Keyboard.FocusedElement);
            }
            finally
            {
                tab.Dispose();
                hostWindow.Close();
                StaRunner.DoEvents();
            }
        });
    }

    private static void OpenPopOut(CaptureTab tab)
    {
        var viewModel = Assert.IsType<ViewModels.CaptureViewModel>(tab.ViewModel);
        viewModel.Inspector.Load(viewModel.Sessions.VisibleRows[0]);
        viewModel.Inspector.PopOutCommand.Execute(null);
        StaRunner.DoEvents();
    }

    private static void MoveAboveAndAssert(IntPtr upper, IntPtr lower)
    {
        Assert.True(SetWindowPos(
            upper,
            HwndTop,
            0,
            0,
            0,
            0,
            SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoActivate));
        Assert.True(IsAbove(upper, lower));
    }

    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, GetWindowNext))
        {
            if (window == upper)
            {
                return true;
            }
            if (window == lower)
            {
                return false;
            }
        }
        return false;
    }

    private static void ActivateAndAssert(IntPtr handle)
    {
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            Assert.True(SetForegroundWindow(handle));
            StaRunner.DoEvents();
            Assert.Equal(handle, GetForegroundWindow());
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private sealed class FakeTabHost(Window window) : ICaptureTabHost
    {
        public Window HostWindow => window;

        public Task OpenCapturesAsync(IReadOnlyList<string> paths) => Task.CompletedTask;

        public void RequestClose(CaptureTab tab) => tab.Dispose();

        public Task ReloadAsync(CaptureTab tab) => Task.CompletedTask;

        public void OnTabStateChanged(CaptureTab tab)
        {
        }
    }

    private const uint GetWindowOwner = 4;
    private const uint GetWindowNext = 2;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoActivate = 0x0010;
    private static readonly IntPtr HwndTop = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr parent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint sourceThread, uint targetThread, bool attach);
}
