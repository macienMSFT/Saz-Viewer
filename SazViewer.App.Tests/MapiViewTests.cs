using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SazViewer.App.Controls;
using SazViewer.App.ViewModels;
using SazViewer.App.Views;
using SazViewer.Core;

namespace SazViewer.App.Tests;

public sealed class MapiViewModelTests
{
    private static MapiMessageParse Parse(MapiNode root, bool complete = true, params string[] warnings) =>
        new(MapiDirection.Request, root, [.. warnings], complete, 24, 32);

    private static MapiNode Sample() => new(
        "Execute\u202E",
        MapiNodeKind.Operation,
        0,
        24,
        null,
        [
            MapiNode.Leaf("Flags", MapiNodeKind.Field, 0, 4, "0x0000000a (NoCompression)"),
            MapiNode.Leaf("Payload", MapiNodeKind.Raw, 4, 6, "0A0B0c"),
            new MapiNode("Rops", MapiNodeKind.Array, 10, 14, null,
            [
                MapiNode.Leaf("Name", MapiNodeKind.Property, 10, 6, "line1\nline2\0"),
                MapiNode.Leaf("Blob", MapiNodeKind.Raw, 16, 8, "not hex")
            ])
        ]);

    [Fact]
    public void DisplayValueMatchesTheReport()
    {
        Assert.Null(MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Field, 0, 1)));
        Assert.Equal(("NoCompression = 0x0000000A", false),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Field, 0, 4, "0x0000000a (NoCompression)")));
        Assert.Equal(("Binary (6 bytes): 0A 0B 0c \u2026 (3 more bytes)", true),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Raw, 0, 6, "0A0B0c")));
        Assert.Equal(("Binary (1 byte): FF", true),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Raw, 0, 1, "FF")));
        Assert.Equal(("Binary (5000 bytes): AB \u2026 (4,999 more bytes)", true),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Raw, 0, 5000, "AB ... [4,999 more bytes]")));
        Assert.Equal(("Binary (2 bytes): a\\tb", true),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Raw, 0, 2, "a\tb")));
        Assert.Equal(("a\\nb\\0\\x7F\\u200E", false),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Property, 0, 2, "a\nb\0\u007f\u200e")));
        Assert.Equal(("0x12 (a) (b)", false),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Field, 0, 1, "0x12 (a) (b)")));
        Assert.Equal(("Binary (1 byte): FF\\n", true),
            MapiViewModel.DisplayValue(MapiNode.Leaf("x", MapiNodeKind.Raw, 0, 1, "FF\n")));
    }

    [Fact]
    public void TreeIsFullyExpandedWithKindAndLocationMetadata()
    {
        var model = new MapiViewModel(Parse(Sample()));

        Assert.Equal(6, model.NodeCount);
        Assert.Equal("6 nodes", model.Status);
        Assert.Equal(6, model.Tree.VisibleRows.Count);
        var rows = model.Tree.VisibleRows;
        Assert.Equal("Execute\\u202E", rows[0].Text);
        Assert.Equal("Operation @0 +24", rows[0].Meta);
        Assert.Equal("Flags: NoCompression = 0x0000000A", rows[1].Text);
        Assert.Equal("Payload: Binary (6 bytes): 0A 0B 0c \u2026 (3 more bytes)", rows[2].Text);
        Assert.Contains(rows[2].Line.Spans, span => span.Style == SpanStyle.ProtocolBinary);
        Assert.Equal("Name: line1\\nline2\\0", rows[4].Text);
        Assert.Equal(3, rows[4].Line.Spans.Count);
        Assert.Equal(2, rows[4].Depth);
        Assert.Equal(new StyledSpan(0, "Property".Length, SpanStyle.ProtocolKind), Assert.Single(rows[4].MetaLine!.Spans));
        Assert.Equal("Name: line1\\nline2\\0; Property; offset 10; length 6", rows[4].AccessibleName);

        model.ToolbarActions[1].Command.Execute(null);
        Assert.Single(model.Tree.VisibleRows);
        model.ToolbarActions[0].Command.Execute(null);
        Assert.Equal(6, model.Tree.VisibleRows.Count);
        Assert.Equal(["Expand all", "Collapse all"], model.ToolbarActions.Select(action => action.Label));
        Assert.All(model.ToolbarActions, action => Assert.True(action.IsVisible));
    }

    [Fact]
    public void SearchCoversKindAndOffsetAndRevealsCollapsedAncestors()
    {
        var model = new MapiViewModel(Parse(Sample()));
        model.Tree.CollapseAll();

        var outcome = model.Tree.Search("property");

        Assert.Equal(1, outcome.Count);
        Assert.Equal(6, model.Tree.VisibleRows.Count);
        var row = model.Tree.AllRows.Single(candidate => candidate.Meta == "Property @10 +6");
        Assert.Single(row.MetaLine!.Highlights);
        Assert.Empty(row.Line.Highlights);
        model.Tree.ShowMatch(0);
        Assert.Equal(0, row.MetaLine.CurrentMatch);

        Assert.Equal(3, model.Tree.Search("@1").Count);

        model.Tree.ClearSearch();
        Assert.Single(model.Tree.VisibleRows);
        Assert.Empty(row.MetaLine.Highlights);
    }

    [Fact]
    public void CopyMatchesTheReportsProtocolOutline()
    {
        var model = new MapiViewModel(Parse(Sample(), false, "first warning"));

        var copy = model.GetCopyText();

        Assert.Equal(
            "MAPI protocol (partial; 24 of 32 bytes)\n" +
            "[Warning] first warning\n" +
            "Execute\u202E [Operation] @0 +24\n" +
            "  Flags [Field] @0 +4 = 0x0000000a (NoCompression)\n" +
            "  Payload [Raw] @4 +6 = 0A0B0c\n" +
            "  Rops [Array] @10 +14\n" +
            "    Name [Property] @10 +6 = line1\nline2\0\n" +
            "    Blob [Raw] @16 +8 = not hex",
            copy.Text);
        Assert.True(model.IsPartial);
        Assert.Equal("MAPI protocol (partial; 24 of 32 bytes)", model.Summary);
        Assert.Equal("first warning", model.WarningText);
    }

    [Fact]
    public void WarningsAreCappedAtFiftyWithAnOmittedLine()
    {
        var warnings = Enumerable.Range(1, 53).Select(index => $"w{index}").ToArray();
        var model = new MapiViewModel(Parse(MapiNode.Leaf("Root", MapiNodeKind.Structure, 0, 0), true, warnings));

        var lines = model.GetCopyText().Text!.Split('\n');

        Assert.Equal(50, lines.Count(line => line.StartsWith("[Warning] w", StringComparison.Ordinal)));
        Assert.Contains("[Warning] 3 additional warning(s) omitted from the report.", lines);
        Assert.Equal(50, model.Warnings.Count);
        Assert.Equal(3, model.OmittedWarnings);
        Assert.EndsWith("3 additional warning(s) omitted.", model.WarningText, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyFailsBeyondTheOneMebibyteLimit()
    {
        var children = Enumerable.Range(0, 200)
            .Select(index => MapiNode.Leaf($"n{index}", MapiNodeKind.Field, index, 1, new string('x', 6000)))
            .ToImmutableArray();
        var model = new MapiViewModel(Parse(new MapiNode("Root", MapiNodeKind.Structure, 0, 200, null, children)));

        var copy = model.GetCopyText();

        Assert.Null(copy.Text);
        Assert.Equal(MapiViewModel.CopyLimitError, copy.Error);
    }

    private static MessagePaneViewModel OpenMapi(out FakeClipboard clipboard)
    {
        var report = NativeCaptures.Parse(
            ("raw/1_c.txt", "POST https://mail.test/mapi/emsmdb HTTP/1.1\r\nHost: mail.test\r\nContent-Type: application/mapi-http\r\n\r\nxx"),
            ("raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Type: application/mapi-http\r\n\r\nyy"));
        report.Sessions[0].Mapi = new MapiSession("1", 0, MapiEndpoint.Mailbox, "Execute", "0", false, Parse(Sample()), null,
            ImmutableArray<string>.Empty);
        clipboard = new FakeClipboard();
        var model = new CaptureViewModel(report, clipboard);
        model.Inspector.Load(model.Sessions.VisibleRows[0]);
        model.Inspector.SelectedSide = InspectorViewModel.RequestSide;
        Assert.False(model.Inspector.Response!.Tab("mapi").IsEnabled);
        return model.Inspector.Request!;
    }

    [Fact]
    public void MapiViewRendersTheVirtualizedTreeWithMetadata()
    {
        var pane = OpenMapi(out _);
        StaRunner.Run(() =>
        {
            var view = new MessagePaneView { DataContext = pane, Height = 500, Width = 800 };
            var window = NativeViewSmokeTests.Host(view);
            try
            {
                var tree = Descendants(view).OfType<ValueTreeView>().Single();
                Assert.Equal(6, tree.List.Items.Count);
                var meta = Descendants(tree).OfType<RichLine>().Where(line => line.Line?.Text == "Raw @4 +6").ToList();
                Assert.Single(meta);
                Assert.Equal(Visibility.Visible, meta[0].Visibility);
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

    [Fact]
    public void MapiSessionsOpenOnTheMapiTab()
    {
        var request = OpenMapi(out var clipboard);


        Assert.Equal("mapi", request.SelectedTab!.Key);
        var mapi = Assert.IsType<MapiViewModel>(request.SelectedTab.Content);
        Assert.Same(mapi.Tree, mapi.SearchTarget);
        Assert.Equal("Copy request MAPI protocol tree", request.SelectedTab.CopyAccessibleName);
        request.SelectedTab.Copy();
        Assert.StartsWith("MAPI protocol (complete; 24 of 32 bytes)\nExecute", clipboard.Text, StringComparison.Ordinal);
    }
}
