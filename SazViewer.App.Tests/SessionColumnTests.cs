using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;
using SazViewer.Core;

namespace SazViewer.App.Tests;

public sealed class SessionColumnTests
{
    [Fact]
    public void PreferencesPersistOrderVisibilityWidthAndCustomColumns()
    {
        using var temp = new TempDirectory();
        var path = temp.File("preferences.json");
        var preferences = new UiPreferences(path)
        {
            Theme = "dark",
            HideConnectOnOpen = true
        };
        var custom = SessionColumnCatalog.CreateCustom(
            SessionColumnSetting.RequestHeaderKind,
            "X-Correlation-Id",
            "Correlation");
        preferences.SetGridColumns(
        [
            SessionColumnSetting.BuiltIn("url", width: 640),
            custom,
            SessionColumnSetting.BuiltIn("time", visible: false, width: 180)
        ]);

        var reloaded = new UiPreferences(path);

        Assert.Equal(["url", custom.Id, "time"], reloaded.GridColumns.Select(column => column.Id));
        Assert.Equal(640, reloaded.GridColumns[0].Width);
        Assert.False(reloaded.GridColumns[2].Visible);
        Assert.Equal(SessionColumnSetting.RequestHeaderKind, reloaded.GridColumns[1].Kind);
        Assert.Equal("X-Correlation-Id", reloaded.GridColumns[1].Source);
        Assert.Equal("Correlation", reloaded.GridColumns[1].Header);
        Assert.Equal("dark", reloaded.Theme);
        Assert.True(reloaded.HideConnectOnOpen);
        Assert.Contains("\"Version\": 3", File.ReadAllText(path), StringComparison.Ordinal);

        reloaded.ResetGridColumns();
        Assert.Equal(
            ["time", "id", "result", "method", "url", "elapsed", "request-size", "response-size"],
            new UiPreferences(path).GridColumns.Select(column => column.Id));

        reloaded.SetGridColumns(
        [
            SessionColumnSetting.BuiltIn("url", visible: false),
            SessionColumnSetting.BuiltIn("time", visible: false)
        ]);
        Assert.True(reloaded.GridColumns[0].Visible);
        Assert.False(reloaded.GridColumns[1].Visible);
    }

    [Fact]
    public void ExistingVersionTwoPreferencesGainDefaultColumnsWithoutLosingFields()
    {
        using var temp = new TempDirectory();
        var path = temp.File("preferences.json");
        File.WriteAllText(path, """{"Version":2,"Theme":"light","SearchPayloads":true,"SessionViewer":"right"}""");

        var preferences = new UiPreferences(path);

        Assert.Equal("light", preferences.Theme);
        Assert.True(preferences.SearchPayloads);
        Assert.Equal(SessionViewerLocation.RightPane, preferences.SessionViewer);
        Assert.Equal(8, preferences.GridColumns.Count);
    }

    [Fact]
    public void RemovedOrUnknownBuiltInsFallBackToTheDefaultGrid()
    {
        var preferences = new UiPreferences(null);
        preferences.SetGridColumns([new("retired", SessionColumnSetting.BuiltInKind, "retired", "", true)]);

        using var list = new SessionListViewModel([], preferences);

        Assert.Equal(
            ["time", "id", "result", "method", "url", "elapsed", "request-size", "response-size"],
            list.GridColumns.Select(column => column.Id));
    }

    [Fact]
    public void CustomHeadersJoinValuesMaskSecretsAndKeepScrubbedMarkers()
    {
        var row = Row("1", requestHeaders:
        [
            new("X-Test", "one"),
            new("X-Test", "two"),
            new("Authorization", "Bearer top-secret"),
            new("Cookie", "sid=top-secret"),
            new("X-Scrubbed", "[REDACTED:Authorization]")
        ], responseHeaders: [new("Set-Cookie", "sid=server-secret")],
            metadata: new Dictionary<string, string> { ["ui-comments"] = "Investigate" });

        Assert.Equal("one, two", ReadCustom(row, SessionColumnSetting.RequestHeaderKind, "X-Test"));
        Assert.Equal("Bearer [hidden]", ReadCustom(row, SessionColumnSetting.RequestHeaderKind, "Authorization"));
        Assert.Equal("[hidden]", ReadCustom(row, SessionColumnSetting.RequestHeaderKind, "Cookie"));
        Assert.Equal("[hidden]", ReadCustom(row, SessionColumnSetting.ResponseHeaderKind, "Set-Cookie"));
        Assert.Equal("[REDACTED:Authorization]", ReadCustom(row, SessionColumnSetting.RequestHeaderKind, "X-Scrubbed"));
        Assert.Equal("Investigate", ReadCustom(row, SessionColumnSetting.SessionFlagKind, "ui-comments"));
        Assert.Equal("Bearer", Read(row, "auth-scheme"));
    }

    [Fact]
    public void VisibleCustomColumnsParticipateInSearchAndHiddenOnesDoNot()
    {
        var rows = new[]
        {
            Row("1", requestHeaders: [new("X-Trace", "needle-123")]),
            Row("2", requestHeaders: [new("X-Trace", "other")])
        };
        var preferences = new UiPreferences(null);
        var custom = SessionColumnCatalog.CreateCustom(SessionColumnSetting.RequestHeaderKind, "X-Trace", "Trace");
        preferences.SetGridColumns([SessionColumnSetting.BuiltIn("id"), custom]);
        using var list = new SessionListViewModel(rows, preferences, payloadDebounce: TimeSpan.Zero);

        list.Query = "NEEDLE";
        Assert.Equal(["1"], list.VisibleRows.Select(row => row.Id));

        preferences.SetGridColumns(
        [
            SessionColumnSetting.BuiltIn("id"),
            custom with { Visible = false }
        ]);
        Assert.Empty(list.VisibleRows);
    }

    [Fact]
    public void TypedSortsKeepBlanksLastInBothDirections()
    {
        var rows = new[]
        {
            Row("1", metadata: new Dictionary<string, string> { ["x-hostip"] = "10.0.0.12" }),
            Row("2", metadata: new Dictionary<string, string> { ["x-hostip"] = "10.0.0.2" }),
            Row("3")
        };
        using var list = new SessionListViewModel(rows);
        var serverIp = Assert.Single(SessionColumnCatalog.BuiltIns, column => column.Id == "server-ip");

        list.Sort(serverIp, ListSortDirection.Ascending);
        Assert.Equal(["2", "1", "3"], list.VisibleRows.Select(row => row.Id));
        list.Sort(serverIp, ListSortDirection.Descending);
        Assert.Equal(["1", "2", "3"], list.VisibleRows.Select(row => row.Id));
    }

    [Fact]
    public void GridColumnsNeverForceDeferredBodyDecoding()
    {
        var decoded = 0;
        var body = new HttpMessage("HTTP/1.1 200 OK", () =>
        {
            decoded++;
            return EmptyBody(4096);
        });
        body.Headers.Add(new HttpHeader("Content-Type", "application/json"));
        body.Headers.Add(new HttpHeader("Content-Length", "120"));
        var session = BaseSession("1");
        session.Response = body;
        session.ContentType = "application/json";
        var row = new SessionRow(session, 0, []);

        foreach (var column in SessionColumnCatalog.BuiltIns)
        {
            _ = row.ColumnValue(column);
        }

        Assert.Equal(0, decoded);
        Assert.Equal("", Read(row, "response-decoded-size"));
        Assert.Equal("", Read(row, "compression-ratio"));
        Assert.Equal("120 B", Read(row, "response-wire-size"));

        var decodedWithoutWireLength = new HttpMessage
        {
            StartLine = "HTTP/1.1 200 OK",
            Body = EmptyBody(4096)
        };
        var secondSession = BaseSession("2");
        secondSession.Response = decodedWithoutWireLength;
        var secondRow = new SessionRow(secondSession, 1, []);
        Assert.Equal("", Read(secondRow, "response-wire-size"));
    }

    [Fact]
    public void CaptureGridBuildsReorderableVirtualizedColumnsAndPersistsLayout()
    {
        var preferences = new UiPreferences(null);
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), preferences);

        StaRunner.Run(() =>
        {
            var view = new CaptureView { DataContext = model };
            var host = new Window { Content = view, Width = 1100, Height = 700, ShowInTaskbar = false };
            try
            {
                host.Show();
                StaRunner.DoEvents();
                var grid = (DataGrid)view.FindName("SessionGrid");
                Assert.True(grid.CanUserReorderColumns);
                Assert.True(grid.EnableColumnVirtualization);
                Assert.Equal(
                    ["Time", "ID", "Result", "Method", "URL", "Elapsed Time", "Req", "Resp"],
                    grid.Columns.OrderBy(column => column.DisplayIndex).Select(column => column.Header));

                grid.Columns[4].DisplayIndex = 0;
                grid.Columns[4].Width = 333;
                typeof(CaptureView).GetMethod("SaveColumnLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, null);
                StaRunner.DoEvents();

                Assert.Equal("url", preferences.GridColumns[0].Id);
                Assert.Equal(333, preferences.GridColumns[0].Width);
                Assert.Equal("URL", grid.Columns.OrderBy(column => column.DisplayIndex).First().Header);
            }

            finally
            {
                host.Close();
                model.Dispose();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void RightPaneMatchesBottomPaneColumnWidthsAndUsesHorizontalOverflow()
    {
        var preferences = new UiPreferences(null);
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), preferences);

        StaRunner.Run(() =>
        {
            var view = new CaptureView { DataContext = model };
            var host = new Window { Content = view, Width = 900, Height = 700, ShowInTaskbar = false };
            try
            {
                host.Show();
                StaRunner.DoEvents();
                var grid = (DataGrid)view.FindName("SessionGrid");
                grid.SelectedIndex = 0;
                StaRunner.DoEvents();

                var url = Assert.Single(grid.Columns, column => Equals(column.Header, "URL"));
                Assert.True(url.Width.IsStar);
                var bottomWidths = grid.Columns.ToDictionary(
                    column => column.Header?.ToString() ?? "",
                    column => column.ActualWidth);

                preferences.SessionViewer = SessionViewerLocation.RightPane;
                model.ApplyPreferences();
                StaRunner.DoEvents();

                Assert.True(url.Width.IsAbsolute);
                Assert.True(grid.Columns.Sum(column => column.ActualWidth) > grid.ActualWidth);
                Assert.All(bottomWidths, pair =>
                    Assert.Equal(pair.Value, grid.Columns.Single(column => Equals(column.Header, pair.Key)).ActualWidth, 1));

                host.Width = 700;
                StaRunner.DoEvents();

                Assert.All(bottomWidths, pair =>
                    Assert.Equal(pair.Value, grid.Columns.Single(column => Equals(column.Header, pair.Key)).ActualWidth, 1));

                preferences.SessionViewer = SessionViewerLocation.BottomPane;
                model.ApplyPreferences();
                StaRunner.DoEvents();
                url = Assert.Single(grid.Columns, column => Equals(column.Header, "URL"));
                Assert.True(url.Width.IsStar);
                url.Width = 275;
                typeof(CaptureView).GetMethod("SaveColumnLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(view, null);
                StaRunner.DoEvents();

                preferences.SessionViewer = SessionViewerLocation.RightPane;
                model.ApplyPreferences();
                StaRunner.DoEvents();

                url = Assert.Single(grid.Columns, column => Equals(column.Header, "URL"));
                Assert.True(url.Width.IsAbsolute);
                Assert.Equal(275, url.ActualWidth);
            }
            finally
            {
                host.Close();
                model.Dispose();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public void ColumnChooserListsBuiltInsByCategoryWithAccessibleToggles()
    {
        StaRunner.Run(() =>
        {
            var dialog = new ColumnChooserWindow(new UiPreferences(null));
            try
            {
                Assert.Contains(dialog.Items, item => item.Category == "Timing" && item.Header == "TTFB");
                Assert.Contains(dialog.Items, item => item.Category == "Connection and process" && item.Header == "Process");
                Assert.Contains(dialog.Items, item => item.Category == "Content-specific" && item.Header == "Auth scheme");
                Assert.All(dialog.Items, item => Assert.StartsWith("Show ", item.ToggleName, StringComparison.Ordinal));
                Assert.Equal("Customize session columns", AutomationProperties.GetName(dialog));
            }
            finally
            {
                dialog.Close();
                StaRunner.DoEvents();
            }
        });
    }

    private static string Read(SessionRow row, string id) =>
        row.ColumnValue(Assert.Single(SessionColumnCatalog.BuiltIns, column => column.Id == id)).Display;

    private static string ReadCustom(SessionRow row, string kind, string source) =>
        row.ColumnValue(SessionColumnCatalog.Resolve(SessionColumnCatalog.CreateCustom(kind, source))!).Display;

    private static SessionRow Row(
        string id,
        IReadOnlyList<HttpHeader>? requestHeaders = null,
        IReadOnlyList<HttpHeader>? responseHeaders = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var session = BaseSession(id);
        var request = new HttpMessage
        {
            StartLine = "GET https://example.test/path HTTP/1.1",
            Body = EmptyBody()
        };
        if (requestHeaders is not null)
        {
            request.Headers.AddRange(requestHeaders);
        }
        session.Request = request;
        if (responseHeaders is not null)
        {
            var response = new HttpMessage
            {
                StartLine = "HTTP/1.1 200 OK",
                Body = EmptyBody()
            };
            response.Headers.AddRange(responseHeaders);
            session.Response = response;
        }
        if (metadata is not null)
        {
            foreach (var pair in metadata)
            {
                session.Metadata[pair.Key] = pair.Value;
            }
        }
        return new SessionRow(session, int.Parse(id), []);
    }

    private static HttpSession BaseSession(string id) => new()
    {
        Id = id,
        Method = "GET",
        Url = "https://example.test/path"
    };

    private static BodyPreview EmptyBody(long length = 0) => new()
    {
        Length = length,
        CapturedLength = 0,
        Preview = ""
    };
}
