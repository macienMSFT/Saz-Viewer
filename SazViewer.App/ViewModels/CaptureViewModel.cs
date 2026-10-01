using SazViewer.App.Model;
using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>Root view-model of one native capture tab: the session grid and the inspector.</summary>
internal sealed class CaptureViewModel : ObservableObject
{
    public CaptureViewModel(SazReport report, IClipboardService? clipboard = null, UiPreferences? preferences = null)
    {
        Preferences = preferences ?? UiPreferences.Current;
        Report = report;
        Rows = BuildRows(report);
        Sessions = new SessionListViewModel(Rows);
        Clipboard = clipboard ?? WpfClipboardService.Instance;
        Inspector = new InspectorViewModel(Sessions, Clipboard, preferences: Preferences);
        ScrubBanner = report.AuthScrub is { } scrub ? new ScrubBannerViewModel(scrub, Preferences) : null;
    }

    public IClipboardService Clipboard { get; }

    public UiPreferences Preferences { get; }

    /// <summary>The credential scrub banner, or null when the capture was opened unscrubbed.</summary>
    public ScrubBannerViewModel? ScrubBanner { get; }

    /// <summary>A detached inspector over the same visible rows that does not drive the grid selection.</summary>
    public InspectorViewModel CreatePopOutInspector(SessionRow row)
    {
        var inspector = new InspectorViewModel(Sessions, Clipboard, followsGrid: false, Preferences);
        inspector.Load(row);
        return inspector;
    }

    public SazReport Report { get; }

    public IReadOnlyList<SessionRow> Rows { get; }

    public SessionListViewModel Sessions { get; }

    public InspectorViewModel Inspector { get; }

    /// <summary>Rows in the report's order (archive/chronological), each with its WebSocket messages.</summary>
    public static IReadOnlyList<SessionRow> BuildRows(SazReport report)
    {
        var webSockets = report.WebSocketMessages
            .GroupBy(message => message.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<WebSocketMessage>)group.ToArray(), StringComparer.Ordinal);
        var rows = new SessionRow[report.Sessions.Count];
        for (var index = 0; index < rows.Length; index++)
        {
            var session = report.Sessions[index];
            rows[index] = new SessionRow(session, index, webSockets.TryGetValue(session.Id, out var messages) ? messages : []);
        }
        return rows;
    }
}
