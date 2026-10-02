using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;
using SazViewer.Core;

namespace SazViewer.App.Tests;

public sealed class AdvancedFilterViewModelTests
{
    [Fact]
    public async Task AdvancedFilterAndsWithQuickSearchStatusAndHideConnect()
    {
        var rows = new[]
        {
            Row("1", "GET", "https://example.test/keep", 200),
            Row("2", "GET", "https://example.test/keep", 404),
            Row("3", "POST", "https://example.test/other", 200),
            Row("4", "CONNECT", "https://example.test/keep", 200)
        };
        using var temp = new TempDirectory();
        var preferences = new UiPreferences(null);
        using var sessions = new SessionListViewModel(rows, preferences, payloadDebounce: TimeSpan.Zero);
        using var filters = new AdvancedFilterViewModel(
            sessions,
            preferences,
            new AdvancedFilterStore(temp.File("filters.json")));
        SetRule(filters, "url", AdvancedFilterOperator.Contains, "/keep");
        sessions.Query = "example.test";
        sessions.Filter = SessionListViewModel.FilterOptions.Single(option => option.Key == "2");
        sessions.HideConnect = true;

        Assert.True(await filters.ApplyAsync());

        Assert.Equal(["1"], sessions.VisibleRows.Select(row => row.Id));
        Assert.Equal("Showing 1 of 4 sessions.", filters.Status);
    }

    [Fact]
    public async Task BodyRulesUseCacheAndScrubbedColumnsNeverExposeRemovedValues()
    {
        var row = Row("1", "POST", "https://example.test/", 200);
        row.Session.Request = Message("POST / HTTP/1.1", "scrubbed-body");
        row.Session.Request.Headers.Add(new("Authorization", "[REDACTED:Bearer]"));
        var preferences = new UiPreferences(null);
        using var sessions = new SessionListViewModel([row], preferences, payloadDebounce: TimeSpan.Zero);
        using var temp = new TempDirectory();
        using var filters = new AdvancedFilterViewModel(
            sessions,
            preferences,
            new AdvancedFilterStore(temp.File("filters.json")));

        filters.Rules[0].FieldOption = filters.FieldOptions.Single(option =>
            option.Field?.Kind == AdvancedFilterFieldKind.RequestBody);
        filters.Rules[0].Operator = AdvancedFilterOperator.Contains;
        filters.Rules[0].Value = "SCRUBBED-BODY";
        Assert.True(await filters.ApplyAsync());
        Assert.Single(sessions.VisibleRows);

        var auth = filters.AddCustomField(SessionColumnSetting.RequestHeaderKind, "Authorization", "Auth");
        filters.Rules[0].FieldOption = auth;
        filters.Rules[0].Value = "original-secret";
        Assert.True(await filters.ApplyAsync());
        Assert.Empty(sessions.VisibleRows);
        filters.Rules[0].Value = "REDACTED";
        Assert.True(await filters.ApplyAsync());
        Assert.Single(sessions.VisibleRows);
    }

    [Fact]
    public async Task ResponseBodyRuleFindsChunkedGzipContentWithoutHydratingDisplayBody()
    {
        const string needle = "advanced-filter-compressed-needle";
        var compressed = Chunked(MediaCaptures.Gzip(new string('x', 100_000) + needle));
        var report = MediaCaptures.Parse(
            ("raw/1_c.txt", MediaCaptures.Ascii("GET /one HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/1_s.txt", MediaCaptures.Response(
                "Content-Type: text/plain; charset=utf-8\r\nTransfer-Encoding: chunked\r\nContent-Encoding: gzip\r\n",
                compressed)));
        var preferences = new UiPreferences(null);
        using var sessions = new SessionListViewModel(CaptureViewModel.BuildRows(report), preferences);
        using var temp = new TempDirectory();
        using var filters = new AdvancedFilterViewModel(
            sessions,
            preferences,
            new AdvancedFilterStore(temp.File("filters.json")));
        filters.Rules[0].FieldOption = filters.FieldOptions.Single(option =>
            option.Field?.Kind == AdvancedFilterFieldKind.ResponseBody);
        filters.Rules[0].Operator = AdvancedFilterOperator.Contains;
        filters.Rules[0].Value = needle;

        Assert.False(report.Sessions[0].Response!.IsBodyDecoded);
        Assert.True(await filters.ApplyAsync());
        Assert.Single(sessions.VisibleRows);
        Assert.False(report.Sessions[0].Response!.IsBodyDecoded);
    }

    [Fact]
    public void FieldRegistryIncludesHiddenBuiltInsConfiguredCustomFieldsPayloadsAndPrompts()
    {
        using var temp = new TempDirectory();
        var preferences = new UiPreferences(null);
        var custom = SessionColumnCatalog.CreateCustom(SessionColumnSetting.SessionFlagKind, "x-test", "Test flag")
            with { Visible = false };
        preferences.SetGridColumns([SessionColumnSetting.BuiltIn("id"), custom]);
        using var sessions = new SessionListViewModel([], preferences);
        using var filters = new AdvancedFilterViewModel(
            sessions,
            preferences,
            new AdvancedFilterStore(temp.File("filters.json")));

        Assert.All(SessionColumnCatalog.BuiltIns, column =>
            Assert.Contains(filters.FieldOptions, option => option.Field?.Column?.Source == column.Id));
        Assert.Contains(filters.FieldOptions, option => option.Label == "Test flag");
        Assert.Contains(filters.FieldOptions, option => option.Field?.Kind == AdvancedFilterFieldKind.RequestBody);
        Assert.Contains(filters.FieldOptions, option => option.Field?.Kind == AdvancedFilterFieldKind.ResponseBody);
        Assert.Equal(3, filters.FieldOptions.Count(option => option.Prompt != FilterFieldPrompt.None));
    }

    [Fact]
    public void CaptureViewExposesAccessibleFilterIconAndPopupEditor()
    {
        var preferences = new UiPreferences(null);
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), preferences);

        StaRunner.Run(() =>
        {
            var view = new CaptureView { DataContext = model };
            var host = new Window { Content = view, Width = 1400, Height = 850, ShowInTaskbar = false };
            AdvancedFilterWindow? popup = null;
            try
            {
                host.Show();
                StaRunner.DoEvents();
                var button = (Button)view.FindName("AdvancedFilterButton");
                Assert.Null(view.FindName("AdvancedFilterPanel"));
                Assert.Equal("Advanced filter", AutomationProperties.GetName(button));
                Assert.Equal("Advanced filter (Ctrl+Shift+F)", button.ToolTip);

                popup = new AdvancedFilterWindow("capture.saz", model.Filters, preferences, host);
                popup.Show();
                StaRunner.DoEvents();

                Assert.Null(popup.Owner);
                Assert.True(popup.ShowInTaskbar);
                Assert.Equal("Filter sessions \u2013 capture.saz", popup.Title);
                var list = (ItemsControl)popup.FilterView.FindName("FilterRuleList");
                Assert.Single(list.Items);
                Assert.Equal("Advanced filter rules", AutomationProperties.GetName(list));
                var names = Descendants<Control>(popup.FilterView)
                    .Select(AutomationProperties.GetName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("Remove filter rule", names);
                Assert.Contains("Enable filter rule", names);
                Assert.Contains("Opening parentheses", names);
                Assert.Contains("Filter field", names);
                Assert.Contains("Filter operator", names);
                Assert.Contains("Filter comparison value", names);
                Assert.Contains("Case-sensitive comparison", names);
                Assert.Contains("Closing parentheses", names);
                Assert.Contains("Join to next rule", names);
            }
            finally
            {
                popup?.Close();
                host.Close();
                model.Dispose();
                StaRunner.DoEvents();
            }
        });
    }

    [Fact]
    public async Task EvaluationHonorsCancellation()
    {
        var rows = Enumerable.Range(0, 200).Select(index =>
            Row(index.ToString(), "GET", $"https://example.test/{index}", 200)).ToArray();
        var compilation = AdvancedFilterCompiler.Compile(new AdvancedFilterDefinition(
        [
            new(true, 0, AdvancedFilterField.ForColumn(
                    SessionColumnCatalog.BuiltIns.Single(column => column.Id == "url").Setting),
                AdvancedFilterOperator.Contains, "example", 0, AdvancedFilterJoin.And, false)
        ]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AdvancedFilterEvaluator.EvaluateAsync(
                compilation.Filter!,
                rows,
                new PayloadSearchCache(),
                null,
                cancellation.Token));
    }

    [Fact]
    public async Task ClearingAnActiveBodyFilterCancelsWithoutPublishingStaleResults()
    {
        var rows = Enumerable.Range(0, 48).Select(index =>
        {
            var row = Row(index.ToString(), "POST", $"https://example.test/{index}", 200);
            row.Session.Request = new HttpMessage(
                "POST / HTTP/1.1",
                () => Body("slow payload"),
                _ =>
                {
                    Thread.Sleep(40);
                    return Body("slow payload");
                });
            return row;
        }).ToArray();
        var preferences = new UiPreferences(null);
        using var sessions = new SessionListViewModel(rows, preferences);
        using var temp = new TempDirectory();
        using var filters = new AdvancedFilterViewModel(
            sessions,
            preferences,
            new AdvancedFilterStore(temp.File("filters.json")));
        filters.Rules[0].FieldOption = filters.FieldOptions.Single(option =>
            option.Field?.Kind == AdvancedFilterFieldKind.RequestBody);
        filters.Rules[0].Operator = AdvancedFilterOperator.Contains;
        filters.Rules[0].Value = "payload";

        var applying = filters.ApplyAsync();
        await Task.Delay(20);
        filters.Clear();
        Assert.False(await applying);

        Assert.False(filters.HasAppliedFilter);
        Assert.Equal("No advanced filter applied.", filters.Status);
        Assert.Equal(rows.Length, sessions.VisibleRows.Count);
    }

    [Fact]
    public async Task LastAppliedFilterCanBeRestoredInAnotherTabDuringTheAppSession()
    {
        using var temp = new TempDirectory();
        var preferences = new UiPreferences(null);
        using var firstSessions = new SessionListViewModel(
            [Row("1", "GET", "https://example.test/restore-me", 200)],
            preferences);
        using (var first = new AdvancedFilterViewModel(
                   firstSessions,
                   preferences,
                   new AdvancedFilterStore(temp.File("first.json"))))
        {
            SetRule(first, "url", AdvancedFilterOperator.Contains, "restore-me");
            Assert.True(await first.ApplyAsync());
        }

        using var secondSessions = new SessionListViewModel(
            [Row("2", "GET", "https://example.test/other", 200)],
            preferences);
        using var second = new AdvancedFilterViewModel(
            secondSessions,
            preferences,
            new AdvancedFilterStore(temp.File("second.json")));
        Assert.True(second.RestoreLastCommand.CanExecute(null));

        second.RestoreLastCommand.Execute(null);

        Assert.Equal("restore-me", second.Rules[0].Value);
        Assert.Contains("last filter", second.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetRule(
        AdvancedFilterViewModel filters,
        string fieldId,
        AdvancedFilterOperator filterOperator,
        string value)
    {
        var rule = filters.Rules[0];
        rule.FieldOption = filters.FieldOptions.Single(option => option.Field?.Column?.Source == fieldId);
        rule.Operator = filterOperator;
        rule.Value = value;
    }

    private static SessionRow Row(string id, string method, string url, int status)
    {
        var session = new HttpSession
        {
            Id = id,
            Method = method,
            Url = url,
            StatusCode = status,
            Request = Message($"{method} {url} HTTP/1.1", ""),
            Response = Message($"HTTP/1.1 {status} Test", "")
        };
        return new SessionRow(session, int.TryParse(id, out var index) ? index : 0, []);
    }

    private static HttpMessage Message(string startLine, string body)
    {
        var preview = Body(body);
        return new HttpMessage
        {
            StartLine = startLine,
            Body = preview
        };
    }

    private static BodyPreview Body(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new BodyPreview
        {
            Length = bytes.Length,
            CapturedLength = bytes.Length,
            Preview = body,
            DecodedBytes = bytes,
            CapturedBytes = bytes,
            Charset = "utf-8"
        };
    }

    private static byte[] Chunked(byte[] bytes)
    {
        var prefix = Encoding.ASCII.GetBytes($"{bytes.Length:X}\r\n");
        return [.. prefix, .. bytes, 13, 10, 48, 13, 10, 13, 10];
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
