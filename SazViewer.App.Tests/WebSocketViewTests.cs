using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SazViewer.App.Model;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;
using SazViewer.Core;

namespace SazViewer.App.Tests;

public sealed class WebSocketViewTests
{
    private const string Json = "{\"a\":[1,{\"b\":\"x\"}]}";

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Frame(byte first, byte[] payload, byte[]? mask = null)
    {
        Assert.InRange(payload.Length, 0, 125);
        if (mask is null)
        {
            return [first, (byte)payload.Length, .. payload];
        }
        var masked = payload.Select((value, index) => (byte)(value ^ mask[index % 4])).ToArray();
        return [first, (byte)(0x80 | payload.Length), .. mask, .. masked];
    }

    private static byte[] Capture(params (string Side, byte[] Frame)[] records)
    {
        using var stream = new MemoryStream();
        stream.Write(Ascii("Fiddler-WebSocket: 1\r\n\r\n"));
        for (var index = 0; index < records.Length; index++)
        {
            var (side, frame) = records[index];
            stream.Write(Ascii(
                $"{side}-Length: {frame.Length}\r\nID: {index + 1}\r\nBitFlags: 0\r\n"
                + $"DoneRead: 2024-05-01T12:00:{index:00}.0000000Z\r\n\r\n"));
            stream.Write(frame);
            stream.Write(Ascii("\r\n"));
        }
        return stream.ToArray();
    }

    internal static SazReport Report()
    {
        byte[] mask = [0x12, 0x34, 0x56, 0x78];
        return MediaCaptures.Parse(
            ("raw/1_c.txt", Ascii("GET /socket HTTP/1.1\r\nHost: ws.test\r\nUpgrade: websocket\r\n\r\n")),
            ("raw/1_s.txt", Ascii("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n\r\n")),
            ("raw/1_w.txt", Capture(
                ("Request", Frame(0x81, Encoding.UTF8.GetBytes(Json), mask)),
                ("Response", Frame(0x81, Ascii("hello   world\n second"))),
                ("Response", Frame(0x82, [0x00, 0x7F, 0xFF])),
                ("Request", Frame(0x89, Ascii("hi"), mask)),
                ("Response", Frame(0x8A, Ascii("hi"))),
                ("Response", Frame(0x88, [0x03, 0xE8])),
                ("Request", Frame(0x01, Ascii("foo"), mask)),
                ("Request", Frame(0x80, Ascii("bar"), mask)))),
            ("raw/2_c.txt", Ascii("GET /plain HTTP/1.1\r\nHost: ws.test\r\n\r\n")));
    }

    private static WebSocketInspectorViewModel Inspector(out FakeClipboard clipboard)
    {
        var report = Report();
        clipboard = new FakeClipboard();
        var model = new CaptureViewModel(report, clipboard);
        var row = model.Sessions.VisibleRows.Single(candidate => candidate.IsWebSocket);
        model.Inspector.Load(row);
        Assert.True(model.Inspector.IsWebSocket);
        Assert.Null(model.Inspector.Request);
        return model.Inspector.WebSocket!;
    }

    [Fact]
    public void MessageListMatchesTheReportColumns()
    {
        var model = Inspector(out _);

        Assert.Equal(7, model.Items.Count);
        Assert.Equal(["Text", "Text", "Binary", "Ping", "Pong", "Close", "Text"], model.Items.Select(item => item.ListType));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7"], model.Items.Select(item => item.IdText));
        var json = model.Items[0];
        Assert.Equal("Client", json.DirectionKey);
        Assert.Equal("\u2191", json.Arrow);
        Assert.Equal("Client to server", json.DirectionLabel);
        Assert.Equal(Json.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), json.Body);
        Assert.Equal(Json, json.Preview);
        Assert.Equal("\u2193", model.Items[1].Arrow);
        Assert.Equal("hello world second", model.Items[1].Preview);
        Assert.Equal("Binary (3 bytes): 00 7F FF", model.Items[2].Preview);
        Assert.Equal("Ping control (2 bytes): 68 69", model.Items[3].Preview);
        Assert.Equal("Pong control (2 bytes): 68 69", model.Items[4].Preview);
        Assert.Equal("Close control (2 bytes): 03 E8", model.Items[5].Preview);
        Assert.Equal("foobar", model.Items[6].Preview);
        Assert.Equal(2, model.Items[6].Message.Frames.Count);
        Assert.Equal($"Server to client, logical message 3, Binary, 3 bytes, Binary (3 bytes): 00 7F FF", model.Items[2].AutomationName);
        Assert.Null(model.OmittedWarning);
        Assert.Equal("7 of 7 messages", model.SearchStatus);
        Assert.Same(model.Items[0], model.SelectedItem);
    }

    [Fact]
    public void JsonMessagesOpenOnTheJsonTreeWithoutFormatMetadata()
    {
        var model = Inspector(out var clipboard);
        var detail = model.Detail!;

        Assert.Equal("Selected Text message", detail.Heading);
        Assert.Equal($"2024-05-01 12:00:00.000 +00:00 \u2022 Client to server \u2022 {Json.Length} bytes \u2022 1 frame", detail.Summary);
        Assert.Equal(["json", "text", "raw"], detail.Tabs.Select(tab => tab.Key));
        Assert.Equal("json", detail.SelectedTab!.Key);
        var structured = Assert.IsType<StructuredBodyViewModel>(detail.SelectedTab.Content);
        Assert.Null(structured.MetaLine);
        Assert.True(structured.IsTreeMode);
        Assert.Equal(["Root", "a", "a[0]: 1", "a[1]", "b: x"], structured.Tree!.VisibleRows.Select(row => row.Line.Text));
        detail.SelectedTab.Copy();
        Assert.Equal(CanonicalJson.Pretty(CanonicalJson.Parse(Json)), clipboard.Text);
        Assert.Equal("Copy WebSocket JSON formatted text", detail.SelectedTab.CopyAccessibleName);

        detail.Select("text");
        detail.SelectedTab.Copy();
        Assert.Equal(Json, clipboard.Text);
    }

    [Fact]
    public void BinaryAndControlMessagesOpenOnRaw()
    {
        var model = Inspector(out var clipboard);
        model.SelectedItem = model.Items[2];
        var detail = model.Detail!;

        Assert.False(detail.Tab("json").IsEnabled);
        Assert.False(detail.Tab("text").IsEnabled);
        Assert.Equal("Decoded UTF-8 text is unavailable for this message.", detail.Tab("text").UnavailableText);
        Assert.Equal("raw", detail.SelectedTab!.Key);
        detail.SelectedTab.Copy();
        Assert.Equal(HtmlReportGenerator.BuildWebSocketRaw(model.Items[2].Message), clipboard.Text);
        Assert.Contains("Payload:\n00000000  00 7F FF", clipboard.Text, StringComparison.Ordinal);

        model.SelectedItem = model.Items[5];
        Assert.Equal("Selected Close message", model.Detail!.Heading);
        Assert.Equal("raw", model.Detail.SelectedTab!.Key);
        Assert.Contains("opcode=0x8", HtmlReportGenerator.BuildWebSocketRaw(model.Items[5].Message), StringComparison.Ordinal);

        model.SelectedItem = model.Items[6];
        Assert.Equal("text", model.Detail!.SelectedTab!.Key);
        Assert.EndsWith("2 frames", model.Detail.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadFilterSearchesDecodedTextOnly()
    {
        var model = Inspector(out _);

        model.Query = "  WORLD ";
        Assert.Equal([model.Items[1]], model.VisibleItems);
        Assert.Same(model.Items[1], model.SelectedItem);
        Assert.Equal("1 of 7 messages", model.SearchStatus);
        Assert.Null(model.EmptyText);

        model.Query = "7F";
        Assert.Empty(model.VisibleItems);
        Assert.Null(model.Detail);
        Assert.Null(model.SelectedItem);
        Assert.Equal(WebSocketInspectorViewModel.NoMatchesText, model.EmptyText);
        Assert.False(model.SelectNextCommand.CanExecute(null));

        model.Query = "o";
        Assert.Equal([model.Items[1], model.Items[6]], model.VisibleItems);
        Assert.Same(model.Items[1], model.SelectedItem);
        model.SelectRelative(1);
        Assert.Same(model.Items[6], model.SelectedItem);
        model.SelectRelative(1);
        Assert.Same(model.Items[1], model.SelectedItem);
        model.SelectRelative(-1);
        Assert.Same(model.Items[6], model.SelectedItem);

        // Hidden rows cannot be selected.
        model.SelectedItem = model.Items[2];
        Assert.Same(model.Items[6], model.SelectedItem);

        model.Query = "";
        Assert.Equal(7, model.VisibleItems.Count);
        Assert.Same(model.Items[6], model.SelectedItem);
    }

    [Fact]
    public void PayloadViewSearchRerunsOnViewChangeAndResetsOnMessageChange()
    {
        var model = Inspector(out _);
        var detail = model.Detail!;

        detail.Select("raw");
        detail.Search.Query = "frame";
        Assert.True(detail.Search.MatchCount >= 2);
        detail.Select("text");
        Assert.Equal("frame", detail.Search.Query);
        Assert.Equal(0, detail.Search.MatchCount);
        detail.Select("json");
        detail.Search.Query = "b";
        Assert.Equal(1, detail.Search.MatchCount);
        var structured = (StructuredBodyViewModel)detail.SelectedTab!.Content!;
        structured.IsFormattedMode = true;
        Assert.Equal(1, detail.Search.MatchCount);

        model.SelectedItem = model.Items[1];
        Assert.NotSame(detail, model.Detail);
        Assert.Equal("", model.Detail!.Search.Query);
        Assert.Equal(0, detail.Search.MatchCount);
    }

    [Fact]
    public void MessageCountIsCappedWithAWarning()
    {
        var messages = Enumerable.Range(0, WebSocketInspectorViewModel.MaxMessages + 3).Select(index => new WebSocketMessage
        {
            SessionId = "1",
            MessageIndex = index,
            RecordIndex = index,
            Direction = index % 2 == 0 ? "Client" : "Mystery",
            Type = "Text",
            Preview = "x",
            Text = "x",
            IsComplete = index != 1,
            PayloadLength = 1
        }).ToList();

        var model = new WebSocketInspectorViewModel(messages, new FakeClipboard());

        Assert.Equal(WebSocketInspectorViewModel.MaxMessages, model.Items.Count);
        Assert.Equal("3 additional message(s) were omitted by the 5,000-message safety limit.", model.OmittedWarning);
        var partial = model.Items[1];
        Assert.Equal("Partial", partial.ListType);
        Assert.Equal("1*", partial.Body);
        Assert.Equal("\u2194", partial.Arrow);
        Assert.Equal("Unknown", partial.DirectionKey);
        Assert.Contains("retained content is limited or partial", partial.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyMessageListShowsTheNoMessagesText()
    {
        var model = new WebSocketInspectorViewModel([], new FakeClipboard());

        Assert.Null(model.Detail);
        Assert.Equal(WebSocketInspectorViewModel.NoMessagesText, model.EmptyText);
        Assert.Equal("0 of 0 messages", model.SearchStatus);
    }

    [Fact]
    public void InspectorViewRendersListDetailAndNarrowLayout()
    {
        var model = Inspector(out _);
        StaRunner.Run(() =>
        {
            var view = new WebSocketInspectorView { DataContext = model, Width = 1200, Height = 600 };
            var window = NativeViewSmokeTests.Host(view);
            try
            {
                var grid = Descendants(view).OfType<DataGrid>().Single(candidate => candidate.Name == "MessageGrid");
                Assert.Equal(7, grid.Items.Count);
                Assert.Same(model.Items[0], grid.SelectedItem);
                Assert.False(view.IsNarrow);
                var pane = Descendants(view).OfType<MessagePaneView>().Single();
                Assert.Same(model.Detail, pane.DataContext);
                Assert.Contains(Descendants(view).OfType<ValueTreeView>(), tree => tree.IsVisible);
                var splitter = Descendants(view).OfType<GridSplitter>().Single();
                Assert.Equal(Visibility.Visible, splitter.Visibility);
                Assert.Equal("Resize WebSocket traffic and payload panes", System.Windows.Automation.AutomationProperties.GetName(splitter));

                grid.SelectedItem = model.Items[2];
                StaRunner.DoEvents();
                Assert.Same(model.Items[2], model.SelectedItem);
                Assert.Equal("raw", model.Detail!.SelectedTab!.Key);

                view.Width = 700;
                view.UpdateLayout();
                StaRunner.DoEvents();
                Assert.True(view.IsNarrow);
                Assert.Equal(Visibility.Collapsed, splitter.Visibility);
            }
            finally
            {
                window.Close();
                StaRunner.DoEvents();
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
