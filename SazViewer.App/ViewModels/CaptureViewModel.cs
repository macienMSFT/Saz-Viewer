using SazViewer.App.Model;
using SazViewer.App.Mvvm;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>Root view-model of one native capture tab: the session grid and the inspector.</summary>
internal sealed class CaptureViewModel : ObservableObject
{
    public CaptureViewModel(SazReport report, IClipboardService? clipboard = null)
    {
        Report = report;
        Rows = BuildRows(report);
        Sessions = new SessionListViewModel(Rows);
        Inspector = new InspectorViewModel(Sessions, clipboard ?? WpfClipboardService.Instance);
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
