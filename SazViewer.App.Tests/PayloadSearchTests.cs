using System.Diagnostics;
using System.Globalization;
using System.Text;
using SazViewer.App.ViewModels;
using SazViewer.Core;
using Xunit.Abstractions;

namespace SazViewer.App.Tests;

public sealed class PayloadSearchTests(ITestOutputHelper output)
{
    [Fact]
    public void CheckboxChoicePersistsForNewCaptureModels()
    {
        using var temp = new TempDirectory();
        var path = temp.File("preferences.json");
        var preferences = new UiPreferences(path);
        using var first = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), preferences);

        first.Sessions.SearchPayloads = true;

        var reloaded = new UiPreferences(path);
        using var second = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard(), reloaded);
        Assert.True(reloaded.SearchPayloads);
        Assert.True(second.Sessions.SearchPayloads);
    }

    [Fact]
    public async Task MatchesHeadersChunkedCompressedBodiesAndBinaryFallbacksWithoutHydratingDisplayBodies()
    {
        var deepNeedle = "deep-compressed-payload-needle";
        var compressedText = new string('x', 100_000) + deepNeedle;
        var compressed = Chunked(MediaCaptures.Gzip(compressedText));
        var utf8Binary = Encoding.UTF8.GetBytes("prefix binary-utf8-\u2603 suffix");
        byte[] latin1Binary = [.. Encoding.ASCII.GetBytes("prefix binary-latin1-caf"), 0xE9, .. Encoding.ASCII.GetBytes(" suffix")];
        var report = MediaCaptures.Parse(
            ("raw/1_c.txt", MediaCaptures.Ascii("GET /one HTTP/1.1\r\nHost: test\r\nX-Payload-Test: header-only-needle\r\n\r\n")),
            ("raw/1_s.txt", MediaCaptures.Response(
                "Content-Type: text/plain; charset=utf-8\r\nTransfer-Encoding: chunked\r\nContent-Encoding: gzip\r\n",
                compressed)),
            ("raw/2_c.txt", MediaCaptures.Ascii("POST /two HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/2_s.txt", MediaCaptures.Response("Content-Type: application/octet-stream\r\n", utf8Binary)),
            ("raw/3_c.txt", MediaCaptures.Ascii("POST /three HTTP/1.1\r\nHost: test\r\n\r\n")),
            ("raw/3_s.txt", MediaCaptures.Response("Content-Type: application/octet-stream\r\n", latin1Binary)));
        var rows = CaptureViewModel.BuildRows(report);
        var cache = new PayloadSearchCache();

        Assert.All(report.Sessions.SelectMany(session => new[] { session.Request, session.Response }).Where(message => message is not null),
            message => Assert.False(message!.IsBodyDecoded));
        Assert.Single((await Search(cache, rows, "header-only-needle")).Matches);
        Assert.Single((await Search(cache, rows, deepNeedle)).Matches);
        Assert.Single((await Search(cache, rows, "binary-utf8-\u2603")).Matches);
        Assert.Single((await Search(cache, rows, "binary-latin1-caf\u00E9")).Matches);
        Assert.All(report.Sessions.SelectMany(session => new[] { session.Request, session.Response }).Where(message => message is not null),
            message => Assert.False(message!.IsBodyDecoded));
    }

    [Fact]
    public async Task MatchesWebSocketTextAndScrubbedContentButNeverOriginalSecrets()
    {
        var webSocketRows = CaptureViewModel.BuildRows(WebSocketViewTests.Report());
        var webSocket = await Search(new PayloadSearchCache(), webSocketRows, "second");
        Assert.Single(webSocket.Matches);
        Assert.True(webSocket.Matches.Single().IsWebSocket);

        const string canary = "payload-search-secret-canary";
        var scrubbed = NativeCaptures.Parse(
            ("raw/1_c.txt", $"POST https://test/token HTTP/1.1\r\nAuthorization: Bearer {canary}\r\nContent-Type: application/json\r\n\r\n{{\"password\":\"{canary}\"}}"),
            ("raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nok"));
        AuthScrubber.Scrub(scrubbed);
        var scrubbedRows = CaptureViewModel.BuildRows(scrubbed);
        var cache = new PayloadSearchCache();

        Assert.Empty((await Search(cache, scrubbedRows, canary)).Matches);
        Assert.Single((await Search(cache, scrubbedRows, "[redacted:bearer]")).Matches);
    }

    [Fact]
    public async Task MatchesAlreadyDecodedMapiNodeText()
    {
        var root = MapiNode.Leaf("DisplayName", MapiNodeKind.Property, 0, 12, "mapi-decoded-needle");
        var parse = new MapiMessageParse(MapiDirection.Request, root, [], true, 12, 12);
        var mapi = new MapiSession("1", 0, MapiEndpoint.Mailbox, "Execute", null, false, parse, null, []);
        var session = new HttpSession
        {
            Id = "1",
            Method = "POST",
            Url = "/mapi",
            Mapi = mapi,
            Request = new HttpMessage
            {
                StartLine = "POST /mapi HTTP/1.1",
                Body = new BodyPreview { Preview = "" }
            }
        };
        var report = new SazReport { SourceName = "mapi.saz" };
        report.Sessions.Add(session);

        var result = await Search(new PayloadSearchCache(), CaptureViewModel.BuildRows(report), "mapi-decoded-needle");

        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task NewQueryCancelsDebounceAndOnlyPublishesLatestPayloadMatches()
    {
        var report = NativeCaptures.Parse(
            ("raw/1_c.txt", "GET /one HTTP/1.1\r\nX-Test: alpha-payload-only\r\n\r\n"),
            ("raw/1_s.txt", "HTTP/1.1 200 OK\r\n\r\n"),
            ("raw/2_c.txt", "GET /two HTTP/1.1\r\nX-Test: beta-payload-only\r\n\r\n"),
            ("raw/2_s.txt", "HTTP/1.1 200 OK\r\n\r\n"));
        using var list = new SessionListViewModel(
            CaptureViewModel.BuildRows(report),
            payloadCache: new PayloadSearchCache(),
            payloadDebounce: TimeSpan.FromMilliseconds(30))
        {
            SearchPayloads = true
        };

        list.Query = "alpha-payload-only";
        var canceled = list.PendingPayloadSearch;
        Assert.True(list.IsPayloadSearching);
        Assert.True(list.CancelPayloadSearchCommand.CanExecute(null));
        Assert.Equal("Waiting to search payloads\u2026", list.PayloadSearchStatus);
        list.Query = "beta-payload-only";
        var latest = list.PendingPayloadSearch;
        await Task.WhenAll(canceled, latest).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(list.IsPayloadSearching);
        Assert.False(list.CancelPayloadSearchCommand.CanExecute(null));
        Assert.Single(list.VisibleRows);
        Assert.Equal("2", list.VisibleRows[0].Id);
        Assert.Contains("1 matching sessions", list.PayloadSearchStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LruNeverExceedsConfiguredByteBudget()
    {
        var report = NativeCaptures.Parse(
            ("raw/1_c.txt", $"POST /one HTTP/1.1\r\nX-Test: {new string('a', 600)}\r\n\r\n"),
            ("raw/1_s.txt", "HTTP/1.1 200 OK\r\n\r\n"),
            ("raw/2_c.txt", $"POST /two HTTP/1.1\r\nX-Test: {new string('b', 600)}\r\n\r\n"),
            ("raw/2_s.txt", "HTTP/1.1 200 OK\r\n\r\n"));
        var cache = new PayloadSearchCache(1_500);

        await Search(cache, CaptureViewModel.BuildRows(report), "not-present");

        Assert.InRange(cache.CachedBytes, 0, 1_500);
        Assert.InRange(cache.Count, 0, 1);
    }

    [Fact]
    public async Task ActiveParallelSearchObservesCancellationAndRepeatUsesCache()
    {
        var entries = Enumerable.Range(1, 80)
            .SelectMany(index => new[]
            {
                ($"raw/{index}_c.txt", $"GET /{index} HTTP/1.1\r\nX-Test: {new string((char)('a' + index % 20), 2_000)}\r\n\r\n"),
                ($"raw/{index}_s.txt", "HTTP/1.1 200 OK\r\n\r\n")
            })
            .ToArray();
        var rows = CaptureViewModel.BuildRows(NativeCaptures.Parse(entries));
        var canceledCache = new PayloadSearchCache();
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(value =>
        {
            if (value.Completed >= 1)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            canceledCache.SearchAsync(rows, "not-present", progress, cancellation.Token));

        var cache = new PayloadSearchCache();
        await Search(cache, rows, "not-present");
        var misses = cache.CacheMisses;
        await Search(cache, rows, "another-miss");
        Assert.Equal(rows.Count, misses);
        Assert.True(cache.CacheHits >= rows.Count);
        Assert.Equal(misses, cache.CacheMisses);
    }

    [Fact]
    public async Task Synthetic1172SessionColdAndWarmSearchMeetInteractiveTarget()
    {
        var entries = Enumerable.Range(1, 1_172)
            .SelectMany(index => new[]
            {
                ($"raw/{index}_c.txt", $"POST /{index} HTTP/1.1\r\nContent-Type: application/json\r\n\r\n"),
                ($"raw/{index}_s.txt", $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{{\"index\":{index},\"text\":\"benchmark-payload-{index}\"}}")
            })
            .ToArray();
        var rows = CaptureViewModel.BuildRows(NativeCaptures.Parse(entries));
        var cache = new PayloadSearchCache();

        var timer = Stopwatch.StartNew();
        var cold = await Search(cache, rows, "benchmark-payload-1172");
        var coldElapsed = timer.Elapsed;
        timer.Restart();
        var warm = await Search(cache, rows, "benchmark-payload-not-found");
        var warmElapsed = timer.Elapsed;
        output.WriteLine(
            "Payload search benchmark: {0} sessions; cold {1:F1} ms; warm {2:F1} ms; cache {3:N0} bytes.",
            rows.Count,
            coldElapsed.TotalMilliseconds,
            warmElapsed.TotalMilliseconds,
            cache.CachedBytes);

        Assert.Single(cold.Matches);
        Assert.Empty(warm.Matches);
        Assert.True(coldElapsed < TimeSpan.FromSeconds(2), $"Cold search took {coldElapsed.TotalMilliseconds:F1} ms.");
        Assert.True(warmElapsed < coldElapsed, $"Warm {warmElapsed.TotalMilliseconds:F1} ms; cold {coldElapsed.TotalMilliseconds:F1} ms.");
    }

    private static Task<PayloadSearchResult> Search(
        PayloadSearchCache cache,
        IReadOnlyList<Model.SessionRow> rows,
        string query) =>
        cache.SearchAsync(rows, SearchText.Fold(query), null, CancellationToken.None);

    private static byte[] Chunked(byte[] payload)
    {
        using var stream = new MemoryStream();
        var prefix = Encoding.ASCII.GetBytes(payload.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        stream.Write(prefix);
        stream.Write(payload);
        stream.Write("\r\n0\r\n\r\n"u8);
        return stream.ToArray();
    }

    private sealed class CallbackProgress(Action<PayloadSearchProgress> report) : IProgress<PayloadSearchProgress>
    {
        public void Report(PayloadSearchProgress value) => report(value);
    }
}
