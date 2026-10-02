using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SazViewer.App.Themes;
using SazViewer.App.ViewModels;

namespace SazViewer.App;

/// <summary>
/// A session inspector popped out of a capture tab ("Open in new window"). It navigates the tab's visible rows
/// independently of the grid selection and closes with its inspector (Close / Esc) or when the capture changes.
/// </summary>
internal partial class InspectorWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const double CascadeOffset = 28;
    private readonly string fileName;
    private readonly InspectorViewModel model;
    private bool closeInspectorOnClose;

    public InspectorWindow(string fileName, InspectorViewModel model, bool activateOnShow = true)
    {
        InitializeComponent();
        this.fileName = fileName;
        this.model = model;
        closeInspectorOnClose = true;
        ShowActivated = activateOnShow;
        Inspector.DataContext = model;
        model.PropertyChanged += OnModelPropertyChanged;
        Closed += (_, _) =>
        {
            model.PropertyChanged -= OnModelPropertyChanged;
            if (closeInspectorOnClose)
            {
                model.Close();
            }
        };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        if (activateOnShow)
        {
            Loaded += (_, _) => Inspector.FocusContent();
        }
        UpdateTitle();
    }

    public InspectorViewModel Model => model;

    /// <summary>Closes the window while leaving its inspector open for rehosting in the bottom pane.</summary>
    internal void ClosePreservingInspector()
    {
        closeInspectorOnClose = false;
        Close();
    }

    /// <summary>Centers the independent pop-out over the main window, cascading and clamping it to that monitor.</summary>
    internal void PositionRelativeTo(Window anchor, int cascadeIndex)
    {
        var workArea = WorkAreaFor(anchor);
        var anchorWidth = anchor.ActualWidth > 0 ? anchor.ActualWidth : anchor.Width;
        var anchorHeight = anchor.ActualHeight > 0 ? anchor.ActualHeight : anchor.Height;
        var offset = Math.Max(0, cascadeIndex) * CascadeOffset;
        var desiredLeft = anchor.Left + ((anchorWidth - Width) / 2) + offset;
        var desiredTop = anchor.Top + ((anchorHeight - Height) / 2) + offset;
        Left = Math.Clamp(desiredLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(desiredTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!model.IsOpen)
        {
            Dispatcher.BeginInvoke(Close);
            return;
        }
        if (e.PropertyName is nameof(InspectorViewModel.Title) or "" or null)
        {
            UpdateTitle();
        }
    }

    private void UpdateTitle() => Title = $"{model.Title} - {fileName} - SAZ Viewer";

    internal static Rect WorkAreaFor(Window anchor)
    {
        var handle = new WindowInteropHelper(anchor).Handle;
        if (handle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }
        var dpi = VisualTreeHelper.GetDpi(anchor);
        return new Rect(
            info.Work.Left / dpi.DpiScaleX,
            info.Work.Top / dpi.DpiScaleY,
            (info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
            (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
