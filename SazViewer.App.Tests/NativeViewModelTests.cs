using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using SazViewer.App.Controls;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;
using SazViewer.Core;

namespace SazViewer.App.Tests;

internal sealed class FakeClipboard(bool succeed = true) : IClipboardService
{
    public string? Text { get; private set; }

    public bool TrySetText(string text)
    {
        if (!succeed)
        {
            return false;
        }
        Text = text;
        return true;
    }
}

/// <summary>Small in-memory captures for native view-model tests (parsed with deferred body decoding, like the app).</summary>
internal static class NativeCaptures
{
    public static SazReport Parse(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                output.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        stream.Position = 0;
        return new SazParser { DeferBodyDecoding = true }.Parse(stream);
    }

    public static string Timers(string begin, string? done = null) =>
        $"<Session><SessionTimers ClientBeginRequest=\"{begin}\"{(done is null ? "" : $" ClientDoneResponse=\"{done}\"")}/></Session>";

    /// <summary>Five sessions: JSON 200, 401 with auth, CONNECT 200, request-only, 500.</summary>
    public static SazReport Mixed() => Parse(
        ("raw/1_c.txt", "GET https://api.example.test/items HTTP/1.1\r\nHost: api.example.test\r\n\r\n"),
        ("raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"items\":[1,2,3],\"name\":\"alpha\"}"),
        ("raw/1_m.xml", Timers("2024-05-01T12:00:00.000Z", "2024-05-01T12:00:01.234Z")),
        ("raw/2_c.txt", "POST https://login.example.test/token HTTP/1.1\r\nHost: login.example.test\r\nAuthorization: Bearer abc.def.ghi\r\n\r\n"),
        ("raw/2_s.txt", "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Bearer realm=\"x\"\r\nContent-Type: text/plain\r\n\r\ndenied"),
        ("raw/2_m.xml", Timers("2024-05-01T12:00:02.000Z", "2024-05-01T12:00:02.050Z")),
        ("raw/3_c.txt", "CONNECT proxy.example.test:443 HTTP/1.1\r\nHost: proxy.example.test:443\r\n\r\n"),
        ("raw/3_s.txt", "HTTP/1.1 200 Connection Established\r\n\r\n"),
        ("raw/3_m.xml", Timers("2024-05-01T12:00:03.000Z")),
        ("raw/4_c.txt", "GET https://api.example.test/pending HTTP/1.1\r\nHost: api.example.test\r\n\r\n"),
        ("raw/4_m.xml", Timers("2024-05-01T12:00:04.000Z")),
        ("raw/5_c.txt", "GET https://api.example.test/boom HTTP/1.1\r\nHost: api.example.test\r\n\r\n"),
        ("raw/5_s.txt", "HTTP/1.1 500 Internal Server Error\r\nContent-Type: text/html\r\n\r\n<p>boom</p>"),
        ("raw/5_m.xml", Timers("2024-05-01T12:00:05.000Z", "2024-05-01T12:00:05.900Z")));
}

public sealed class SessionRowTests
{
    [Fact]
    public void FormatsResultElapsedAndFilterKeyLikeTheReport()
    {
        var rows = CaptureViewModel.BuildRows(NativeCaptures.Mixed());

        Assert.Equal(["1", "2", "3", "4", "5"], rows.Select(row => row.Id));
        Assert.Equal("200", rows[0].Result);
        Assert.Equal("1,234 ms", rows[0].Elapsed);
        Assert.Equal("2", rows[0].FilterKey);
        Assert.Equal("401", rows[1].Result);
        Assert.Equal("HTTP 401 Unauthorized", rows[1].ResultToolTip);
        Assert.Equal("4", rows[1].FilterKey);
        Assert.True(rows[2].IsConnect);
        Assert.Equal("\u2014", rows[3].Result);
        Assert.Equal("0", rows[3].FilterKey);
        Assert.Equal("\u2014", rows[3].Elapsed);
        Assert.Equal("5", rows[4].FilterKey);
        Assert.Equal("2024-05-01 12:00:00.000", rows[0].Time[..23]);
    }

    [Theory]
    [InlineData(0L, "0 ms")]
    [InlineData(1234567L, "1,234,567 ms")]
    [InlineData(null, "\u2014")]
    public void FormatElapsed(long? value, string expected) => Assert.Equal(expected, SessionRow.FormatElapsed(value));

    [Fact]
    public void SearchTextIsLowerCasedAndCoversUrlStatusAndElapsed()
    {
        var row = CaptureViewModel.BuildRows(NativeCaptures.Mixed())[1];

        Assert.Contains("https://login.example.test/token", row.SearchText, StringComparison.Ordinal);
        Assert.Contains("unauthorized", row.SearchText, StringComparison.Ordinal);
        Assert.Contains("50 ms", row.SearchText, StringComparison.Ordinal);
        Assert.Equal(row.SearchText, row.SearchText.ToLowerInvariant());
    }
}

public sealed class SessionListViewModelTests
{
    private static SessionListViewModel List() => new(CaptureViewModel.BuildRows(NativeCaptures.Mixed()));

    [Fact]
    public void FilterOptionsMatchTheReport()
    {
        Assert.Equal(
            ["All sessions", "WebSocket only", "MAPI/NSPI only", "2xx", "3xx", "4xx", "5xx", "Missing/other"],
            SessionListViewModel.FilterOptions.Select(option => option.Label));
    }

    [Theory]
    [InlineData("2", new[] { "1", "3" })]
    [InlineData("4", new[] { "2" })]
    [InlineData("5", new[] { "5" })]
    [InlineData("0", new[] { "4" })]
    [InlineData("3", new string[0])]
    [InlineData("websocket", new string[0])]
    [InlineData("mapi", new string[0])]
    public void StatusFiltersSelectMatchingRows(string key, string[] expected)
    {
        var list = List();
        list.Filter = SessionListViewModel.FilterOptions.Single(option => option.Key == key);

        Assert.Equal(expected, list.VisibleRows.Select(row => row.Id));
    }

    [Fact]
    public void SearchHideConnectAndCountCombine()
    {
        var list = List();
        Assert.Equal("5 sessions", list.CountText);

        list.Query = "API.EXAMPLE";
        Assert.Equal(["1", "4", "5"], list.VisibleRows.Select(row => row.Id));
        Assert.Equal("3 of 5 sessions", list.CountText);

        list.Query = "";
        list.HideConnect = true;
        Assert.DoesNotContain(list.VisibleRows, row => row.IsConnect);
        Assert.Equal("4 of 5 sessions", list.CountText);

        list.Filter = SessionListViewModel.FilterOptions.Single(option => option.Key == "2");
        Assert.Equal(["1"], list.VisibleRows.Select(row => row.Id));
    }

    [Fact]
    public void SortingIsTypedStableAndRestoresChronologicalOrder()
    {
        var list = List();

        list.Sort(SessionSortColumn.Result, ListSortDirection.Descending);
        Assert.Equal(["5", "2", "1", "3", "4"], list.VisibleRows.Select(row => row.Id));

        list.Sort(SessionSortColumn.Result, ListSortDirection.Ascending);
        Assert.Equal("4", list.VisibleRows[0].Id);

        list.Sort(SessionSortColumn.Index, ListSortDirection.Ascending);
        Assert.Equal(["1", "2", "3", "4", "5"], list.VisibleRows.Select(row => row.Id));
    }

    [Fact]
    public void NeighborWalksVisibleRowsOnly()
    {
        var list = List();
        list.HideConnect = true;
        var second = list.VisibleRows[1];

        Assert.Equal("4", list.Neighbor(second, 1)!.Id);
        Assert.Equal("1", list.Neighbor(second, -1)!.Id);
        Assert.Null(list.Neighbor(list.VisibleRows[0], -1));
        Assert.Null(list.Neighbor(list.AllRows[2], 1));
        Assert.Null(list.Neighbor(null, 1));
    }
}

public sealed class InspectorViewModelTests
{
    [Fact]
    public void LoadNavigateAndClose()
    {
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard());
        var inspector = model.Inspector;
        Assert.False(inspector.IsOpen);

        inspector.Load(model.Sessions.VisibleRows[0]);
        Assert.True(inspector.IsOpen);
        Assert.Equal("1 of 5", inspector.Position);
        Assert.False(inspector.PreviousCommand.CanExecute(null));
        Assert.True(inspector.NextCommand.CanExecute(null));
        Assert.Same(model.Sessions.VisibleRows[0], model.Sessions.SelectedRow);

        inspector.Navigate(1);
        Assert.Equal("2", inspector.Row!.Id);
        Assert.Equal("2 of 5", inspector.Position);
        Assert.Equal("Session 2: POST https://login.example.test/token", inspector.Title);

        model.Sessions.Query = "login";
        Assert.Equal("1 of 1", inspector.Position);
        Assert.False(inspector.NextCommand.CanExecute(null));

        inspector.Close();
        Assert.False(inspector.IsOpen);
        Assert.Equal("", inspector.Position);
        Assert.Null(inspector.Request);
    }

    [Fact]
    public void SideSelectionResetsSearchAndChoosesActivePane()
    {
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard());
        var inspector = model.Inspector;
        inspector.Load(model.Sessions.VisibleRows[0]);
        Assert.Same(inspector.Request, inspector.ActivePane);

        inspector.Search.Query = "host";
        Assert.True(inspector.Search.MatchCount > 0);

        inspector.SelectedSideIndex = 1;
        Assert.Equal(InspectorViewModel.ResponseSide, inspector.SelectedSide);
        Assert.Same(inspector.Response, inspector.ActivePane);
        Assert.Equal("", inspector.Search.Query);
        Assert.Equal(0, inspector.Search.MatchCount);
    }

    [Fact]
    public void DetailsIncludeTimersAndDeferredBodyWarnings()
    {
        var report = NativeCaptures.Parse(
            ("raw/1_c.txt", "GET https://example.test/ HTTP/1.1\r\nHost: example.test\r\n\r\n"),
            ("raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Type: text/plain\r\n\r\nnot gzip"),
            ("raw/1_m.xml", NativeCaptures.Timers("2024-05-01T12:00:00Z", "2024-05-01T12:00:01Z")));
        Assert.True(report.HasDeferredWork);
        var model = new CaptureViewModel(report, new FakeClipboard());

        model.Inspector.Load(model.Sessions.VisibleRows[0]);

        var details = model.Inspector.Details!;
        Assert.Contains("ClientBeginRequest", details.Timers, StringComparison.Ordinal);
        Assert.NotEmpty(details.Warnings);
    }
}

public sealed class MessagePaneViewModelTests
{
    private static InspectorViewModel Open(int index)
    {
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard());
        model.Inspector.Load(model.Sessions.VisibleRows[index]);
        return model.Inspector;
    }

    [Fact]
    public void TabsUseTheReportOrderAndInitialPreference()
    {
        var inspector = Open(0);
        var response = inspector.Response!;

        Assert.Equal(
            ["JSON", "XML", "MAPI", "Image", "WebView", "HexView", "Auth", "Headers", "Raw"],
            response.Tabs.Select(tab => tab.Label));
        Assert.Equal("json", response.SelectedTab!.Key);
        Assert.Equal("raw", inspector.Request!.SelectedTab!.Key);
        Assert.False(inspector.Request.Tab("hex").IsEnabled);
        Assert.False(inspector.Request.Tab("json").IsEnabled);
        Assert.True(inspector.Request.Tab("headers").IsEnabled);
    }

    [Fact]
    public void AuthTabIsEnabledOnlyWithAuthHeaders()
    {
        var inspector = Open(1);

        Assert.True(inspector.Request!.Tab("auth").IsEnabled);
        Assert.True(inspector.Response!.Tab("auth").IsEnabled);
        Assert.False(Open(0).Request!.Tab("auth").IsEnabled);
    }

    [Fact]
    public void MissingResponseHasNoTabs()
    {
        var response = Open(3).Response!;

        Assert.False(response.HasAnyTab);
        Assert.Null(response.SelectedTab);
        Assert.Equal("No response entry was captured.", response.EmptyText);
    }

    [Fact]
    public void InitialTabKeyPriority()
    {
        var flags = MessagePaneViewModel.TabOrder.ToDictionary(tab => tab.Key, _ => false);
        Assert.Null(MessagePaneViewModel.InitialTabKey(flags));
        flags["headers"] = true;
        Assert.Equal("headers", MessagePaneViewModel.InitialTabKey(flags));
        flags["raw"] = true;
        Assert.Equal("raw", MessagePaneViewModel.InitialTabKey(flags));
        flags["xml"] = true;
        Assert.Equal("xml", MessagePaneViewModel.InitialTabKey(flags));
        flags["json"] = true;
        Assert.Equal("json", MessagePaneViewModel.InitialTabKey(flags));
        flags["mapi"] = true;
        Assert.Equal("mapi", MessagePaneViewModel.InitialTabKey(flags));
    }

    [Fact]
    public void DisabledTabsCannotBeSelected()
    {
        var request = Open(0).Request!;

        Assert.False(request.Select("hex"));
        Assert.Equal("raw", request.SelectedTab!.Key);
        Assert.True(request.Select("headers"));
        Assert.Equal("headers", request.SelectedTab!.Key);
        request.SelectedTab = null;
        Assert.Equal("headers", request.SelectedTab!.Key);
    }

    [Fact]
    public void ViewContentIsCreatedOnlyWhenAccessed()
    {
        var response = Open(0).Response!;

        Assert.False(response.Tab("raw").IsCreated);
        Assert.NotNull(response.Tab("raw").Content);
        Assert.True(response.Tab("raw").IsCreated);
    }

    [Fact]
    public void CopyHeadersAndRawUseClipboard()
    {
        var clipboard = new FakeClipboard();
        var model = new CaptureViewModel(NativeCaptures.Mixed(), clipboard);
        model.Inspector.Load(model.Sessions.VisibleRows[0]);
        var response = model.Inspector.Response!;

        response.Tab("headers").Copy();
        Assert.Contains("Content-Type: application/json", clipboard.Text, StringComparison.Ordinal);
        Assert.Equal("Copied response headers.", response.Tab("headers").CopyStatus);
        Assert.Equal("Copied", response.Tab("headers").CopyButtonText);

        response.Tab("raw").Copy();
        Assert.Contains("{\"items\":[1,2,3],\"name\":\"alpha\"}", clipboard.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyReportsClipboardFailure()
    {
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(succeed: false));
        model.Inspector.Load(model.Sessions.VisibleRows[0]);
        var tab = model.Inspector.Response!.Tab("headers");

        tab.Copy();

        Assert.StartsWith("Copy failed.", tab.CopyStatus, StringComparison.Ordinal);
        Assert.Equal("Copy", tab.CopyButtonText);
    }
}

public sealed class LineDocumentSearchTests
{
    [Fact]
    public void SearchIsCaseInsensitiveLiteralAndStepsThroughMatches()
    {
        var document = LineDocument.FromText("Alpha beta\nALPHA.*\nnone");
        var search = new ActiveSearchViewModel(() => document, "", "");

        search.Query = "alpha";
        Assert.Equal(2, search.MatchCount);
        Assert.Equal("1 of 2 matches", search.Status);

        search.Move(1);
        Assert.Equal("2 of 2 matches", search.Status);
        search.Move(1);
        Assert.Equal("1 of 2 matches", search.Status);
        search.Move(-1);
        Assert.Equal("2 of 2 matches", search.Status);

        search.Query = ".*";
        Assert.Equal(1, search.MatchCount);

        search.Reset();
        Assert.Equal("0 matches", search.Status);
        Assert.Equal(0, document.HighlightedRowCount);
    }

    [Fact]
    public void SearchIsCappedAt5000Matches()
    {
        var document = LineDocument.FromText(string.Concat(Enumerable.Repeat("ab\n", 6000)));
        var search = new ActiveSearchViewModel(() => document, "", "");

        search.Query = "a";

        Assert.Equal(SearchText.MaxMatches, search.MatchCount);
        Assert.True(search.IsCapped);
        Assert.Equal("1 of 5000+ matches (capped)", search.Status);
    }

    [Fact]
    public void MatchesSpanningWrappedRowsHighlightBothRows()
    {
        var text = new string('x', LineDocumentBuilder.RowLength - 2) + "needle";
        var document = LineDocument.FromText(text);

        Assert.Equal(2, document.AllRows.Count);
        Assert.Equal(1, document.Search("needle").Count);
        Assert.Equal(2, document.HighlightedRowCount);
        Assert.Equal(text, LineDocument.JoinRows(document.AllRows));
    }

    [Fact]
    public void SearchRevealsCollapsedSectionsAndRestoresThem()
    {
        var document = new LineDocumentBuilder()
            .Add("visible")
            .BeginSection("Captured bytes")
            .Add("hidden secret")
            .EndSection()
            .Build();
        var section = Assert.Single(document.Sections);
        Assert.False(section.IsExpanded);
        Assert.Equal(2, document.VisibleRows.Count);

        Assert.Equal(1, document.Search("secret").Count);
        Assert.True(section.IsExpanded);
        Assert.Equal(3, document.VisibleRows.Count);

        document.ClearSearch();
        Assert.False(section.IsExpanded);
        Assert.Equal(2, document.VisibleRows.Count);
    }

    [Fact]
    public void SearchOutcomeIsEmptyForNoMatches()
    {
        var document = LineDocument.FromText("abc");

        Assert.Equal(SearchOutcome.None, document.Search("zzz"));
    }
}

public sealed class RichLineSegmentTests
{
    [Fact]
    public void SplitsStyleAndMatchBoundariesAndMergesUniformRuns()
    {
        var segments = RichLine.Segments(
            10,
            [new StyledSpan(0, 4, SpanStyle.Key)],
            [new RowHighlight(2, 4, 0), new RowHighlight(8, 2, 1)],
            currentMatch: 1);

        Assert.Equal(
            [
                new RichLine.RenderSegment(0, 2, SpanStyle.Key, RichLine.MatchState.None),
                new RichLine.RenderSegment(2, 2, SpanStyle.Key, RichLine.MatchState.Match),
                new RichLine.RenderSegment(4, 2, null, RichLine.MatchState.Match),
                new RichLine.RenderSegment(6, 2, null, RichLine.MatchState.None),
                new RichLine.RenderSegment(8, 2, null, RichLine.MatchState.Current)
            ],
            segments);
    }

    [Fact]
    public void EmptyLineHasNoSegments() => Assert.Empty(RichLine.Segments(0, [], [], -1));
}
