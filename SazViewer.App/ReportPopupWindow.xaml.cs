using System.Windows;

namespace SazViewer.App;

/// <summary>
/// App-controlled window for the report's "Open in new tab" inspector. It is wired through its own
/// <see cref="SecureReportSession"/> with the same restrictions as the main window and is closed
/// whenever the main report is replaced or the app exits.
/// </summary>
internal partial class ReportPopupWindow : Window
{
    public ReportPopupWindow(string title)
    {
        InitializeComponent();
        Title = title;
        Closed += (_, _) => ReportView.Dispose();
    }
}
