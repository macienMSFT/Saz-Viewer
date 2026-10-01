using SazViewer.App.Model;
using SazViewer.App.ViewModels;
using SazViewer.Core;

namespace SazViewer.App.Tests;

public sealed class CanonicalJsonTests
{
    private static string Pretty(string json) => CanonicalJson.Pretty(CanonicalJson.Parse(json));

    [Fact]
    public void PrettyMatchesTheReportFormatting()
    {
        Assert.Equal(
            "{\r\n  \"a\": 1,\r\n  \"b\": [\r\n    true,\r\n    null,\r\n    -1.5e+3\r\n  ],\r\n  \"c\": {},\r\n  \"d\": []\r\n}",
            Pretty("{\"a\":1, \"b\":[true,null,-1.5e+3], \"c\":{ }, \"d\":[ ]}"));
        Assert.Equal("\"x\"", Pretty(" \"x\" "));
        Assert.Equal("1.0", Pretty("1.0"));
    }

    [Fact]
    public void StringsUseDotNetJsonEscaping()
    {
        Assert.Equal(
            "\"q\\\" b\\\\ \\n\\r\\t\\b\\f \\u0001 \\u003C\\u003E\\u0026\\u0027 \\u00E9 / +\"",
            Pretty("\"q\\\" b\\\\ \\n\\r\\t\\b\\f \\u0001 <>&' \u00e9 \\/ +\""));
    }

    [Fact]
    public void EscapedQuotesInsideStringsParse()
    {
        // The shipped report's tokenizer mishandles \" inside strings; the native port follows RFC 8259.
        var root = (CanonicalJson.ObjectNode)CanonicalJson.Parse("{\"k\\\"ey\":\"va\\\"l\\u0041\"}");
        Assert.Equal("k\"ey", root.Properties[0].Name);
        Assert.Equal("va\"lA", ((CanonicalJson.ScalarNode)root.Properties[0].Value).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[1,]")]
    [InlineData("{\"a\" 1}")]
    [InlineData("01")]
    [InlineData("\"tab\there\"")]
    [InlineData("\"bad \\x escape\"")]
    [InlineData("\"bad \\u12G4\"")]
    [InlineData("tru")]
    [InlineData("{} x")]
    [InlineData("'single'")]
    public void InvalidJsonIsRejected(string json) => Assert.Throws<FormatException>(() => CanonicalJson.Parse(json));

    [Fact]
    public void NestingBeyondTheSafeDepthIsRejected()
    {
        CanonicalJson.Parse(new string('[', 65) + new string(']', 65));
        Assert.Throws<FormatException>(() => CanonicalJson.Parse(new string('[', 66) + new string(']', 66)));
    }

    [Fact]
    public void TreeUsesRootPropertyNamesAndIndexedItems()
    {
        var tree = CanonicalJson.Tree(CanonicalJson.Parse("{\"items\":[{\"id\":1},[]],\"empty\":{},\"s\":\"v\"}"));

        Assert.Equal("Root", tree.Name);
        var items = tree.Children[0];
        Assert.Equal("items", items.Name);
        Assert.Equal(["items[0]", "items[1]"], items.Children.Select(child => child.Name));
        Assert.Equal("id", items.Children[0].Children[0].Name);
        Assert.Empty(items.Children[1].Children);
        Assert.Empty(tree.Children[1].Children);
        Assert.Equal(("s", "v"), (tree.Children[2].Name, tree.Children[2].Value));
        Assert.Equal("[0]", CanonicalJson.Tree(CanonicalJson.Parse("[1]")).Children[0].Name);
    }

    [Fact]
    public void TreeAppliesChildScalarAndDepthBudgets()
    {
        var wide = CanonicalJson.Tree(CanonicalJson.Parse("[" + string.Join(",", Enumerable.Repeat("1", 305)) + "]"));
        Assert.Equal(301, wide.Children.Count);
        Assert.Equal(ValueNodeKind.Status, wide.Children[^1].Kind);
        Assert.Equal(TreeBudget.OmittedText(5), wide.Children[^1].Name);

        var longValue = CanonicalJson.Tree(CanonicalJson.Parse("\"" + new string('x', 400) + "\""));
        Assert.True(longValue.Truncated);
        Assert.Equal(300, longValue.Value!.Length);

        var deep = CanonicalJson.Tree(CanonicalJson.Parse(new string('[', 45) + "1" + new string(']', 45)));
        var node = deep;
        for (var depth = 0; depth < TreeBudget.MaxDepth; depth++)
        {
            node = node.Children[0];
        }
        Assert.Equal(TreeBudget.DepthText, Assert.Single(node.Children).Name);
    }

    [Fact]
    public void TreeStopsAtTheNodeBudget()
    {
        var json = "[" + string.Join(",", Enumerable.Repeat("[" + string.Join(",", Enumerable.Repeat("0", 200)) + "]", 30)) + "]";
        var tree = CanonicalJson.Tree(CanonicalJson.Parse(json));
        var count = Count(tree);
        Assert.InRange(count, TreeBudget.MaxNodes, TreeBudget.MaxNodes + 40);
        Assert.Contains(tree.Children, child => child.Kind == ValueNodeKind.Status);

        static int Count(ValueTreeNode node) => (node.Kind == ValueNodeKind.Status ? 0 : 1) + node.Children.Sum(Count);
    }
}

public sealed class CanonicalXmlTests
{
    private static string Pretty(string xml) => CanonicalXml.Pretty(CanonicalXml.Parse(xml));

    [Fact]
    public void PrettyMatchesTheReportFormatting()
    {
        Assert.Equal(
            "<!--top-->\r\n<root a=\"1 &amp; &quot;2&quot;\">\r\n  <empty />\r\n  <p>Hi <b>there</b> &lt;you&gt;</p>\r\n  <![CDATA[x<y]]>\r\n  <!--c-->\r\n</root>",
            Pretty("<?xml version=\"1.0\"?><!--top--><root a='1 &amp; \"2\"'><empty></empty><p>Hi <b>there</b> &lt;you&gt;</p><![CDATA[x<y]]><!--c--></root>"));
    }

    [Fact]
    public void WhitespaceTextNodesAreKeptLikeTheReport()
    {
        Assert.Equal("<a>\r\n  \n  \r\n  <b />\r\n  \n\r\n</a>", Pretty("<a>\n  <b/>\n</a>"));
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"v\">]><x>&e;</x>")]
    [InlineData("<!doctype html><x/>")]
    [InlineData("<a><b></a>")]
    [InlineData("<a/><b/>")]
    [InlineData("not xml")]
    public void InvalidOrUnsafeXmlIsRejected(string xml) => Assert.Throws<FormatException>(() => CanonicalXml.Parse(xml));

    [Fact]
    public void TreeUsesMapiStyleLabelsWithoutCounts()
    {
        var tree = CanonicalXml.Tree(CanonicalXml.Parse("<root id=\"7\">\n <name>alpha</name>\n <list><i/><i/></list>\n <!--note--><![CDATA[raw]]>mixed</root>"));

        var root = Assert.Single(tree);
        Assert.Equal("root", root.Name);
        Assert.Equal(["@id", "name", "list", "#comment", "#cdata", "#text"], root.Children.Select(child => child.Name));
        Assert.Equal("7", root.Children[0].Value);
        Assert.Equal("alpha", root.Children[1].Value);
        Assert.Empty(root.Children[1].Children);
        Assert.Equal(2, root.Children[2].Children.Count);
        Assert.Empty(root.Children[2].Children[0].Children);
        Assert.Equal("mixed", root.Children[5].Value);
    }
}

public sealed class StructuredBodyViewModelTests
{
    private static StructuredBodyViewModel Json(string body) => new(BodyFormat.Json, "JSON", "Detected", () => body);

    [Fact]
    public void TreeLabelsAreNameValueWithoutBrackets()
    {
        var model = Json("{\"items\":[1,\"a\\nb\",null,true],\"o\":{}}");
        var rows = model.Tree!.VisibleRows.Select(row => row.Text).ToList();

        Assert.Equal(["Root", "items", "items[0]: 1", "items[1]: a\\nb", "items[2]: null", "items[3]: true", "o"], rows);
        Assert.DoesNotContain(rows, text => text.Contains('{') || text.Contains('(') || text.Contains('<'));
        var item = model.Tree.VisibleRows[2];
        Assert.Equal(new StyledSpan(0, 8, SpanStyle.Name), item.Line.Spans[0]);
        Assert.Equal(new StyledSpan(10, 1, SpanStyle.Number), item.Line.Spans[1]);
        Assert.False(model.Tree.VisibleRows[^1].IsExpandable);
        Assert.Equal(2, item.Depth);
    }

    [Fact]
    public void ToolbarShowsExpandCollapseOnlyInTreeModeAndModeSwitchChangesSearchTarget()
    {
        var model = Json("{\"a\":[1,2]}");
        var changes = 0;
        model.SearchTargetChanged += (_, _) => changes++;

        Assert.Equal(["Expand all", "Collapse all"], model.ToolbarActions.Select(action => action.Label));
        Assert.All(model.ToolbarActions, action => Assert.True(action.IsVisible));
        Assert.Same(model.Tree, model.SearchTarget);

        model.IsFormattedMode = true;
        Assert.Equal(1, changes);
        Assert.All(model.ToolbarActions, action => Assert.False(action.IsVisible));
        Assert.Same(model.Formatted!.Document, model.SearchTarget);
        Assert.Same(model.Formatted, model.ActiveView);

        model.IsTreeMode = true;
        Assert.Equal(2, changes);
        Assert.Same(model.Tree, model.ActiveView);
    }

    [Fact]
    public void ExpandAndCollapseAll()
    {
        var model = Json("{\"a\":{\"b\":[1]},\"c\":2}");
        var tree = model.Tree!;
        Assert.Equal(5, tree.VisibleRows.Count);

        model.ToolbarActions[1].Command.Execute(null);
        Assert.Equal(["Root"], tree.VisibleRows.Select(row => row.Text));
        Assert.Equal("+", tree.VisibleRows[0].ExpanderGlyph);
        Assert.Equal("collapsed", tree.VisibleRows[0].ItemStatus);

        model.ToolbarActions[0].Command.Execute(null);
        Assert.Equal(5, tree.VisibleRows.Count);
        Assert.Equal("\u2212", tree.VisibleRows[0].ExpanderGlyph);

        tree.Toggle(tree.VisibleRows[1]);
        Assert.Equal(["Root", "a", "c: 2"], tree.VisibleRows.Select(row => row.Text));
    }

    [Fact]
    public void FormattedTextIsHighlightedAndCopiedWithCrLf()
    {
        var model = Json("{\"k\":\"v\",\"n\":-2,\"t\":false}");
        var lines = model.Formatted!.Document.AllRows;

        Assert.Equal(["{", "  \"k\": \"v\",", "  \"n\": -2,", "  \"t\": false", "}"], lines.Select(line => line.Text));
        Assert.Contains(new StyledSpan(2, 3, SpanStyle.Key), lines[1].Spans);
        Assert.Contains(new StyledSpan(7, 3, SpanStyle.String), lines[1].Spans);
        Assert.Contains(new StyledSpan(7, 2, SpanStyle.Number), lines[2].Spans);
        Assert.Contains(new StyledSpan(7, 5, SpanStyle.Literal), lines[3].Spans);
        Assert.Equal("{\r\n  \"k\": \"v\",\r\n  \"n\": -2,\r\n  \"t\": false\r\n}", model.GetCopyText().Text);
    }

    [Fact]
    public void XmlHighlightingMarksTagsAndComments()
    {
        var spans = SyntaxHighlighter.Xml("<a x=\"1>2\">t<!--c--></a>");
        Assert.Equal(
            [new StyledSpan(0, 11, SpanStyle.Tag), new StyledSpan(12, 8, SpanStyle.Comment), new StyledSpan(20, 4, SpanStyle.Tag)],
            spans);
    }

    [Fact]
    public void PreparationFailureShowsWarningAndCopyFails()
    {
        var model = Json("{\"broken\":");

        Assert.Null(model.Tree);
        Assert.StartsWith(StructuredBodyViewModel.ErrorPrefix, model.Error);
        Assert.Same(model.ErrorDocument, model.ActiveView);
        Assert.Equal(LineKind.Warning, model.ErrorDocument!.Document.AllRows[0].Kind);
        Assert.All(model.ToolbarActions, action => Assert.False(action.IsVisible));
        Assert.Equal(model.Error, model.GetCopyText().Error);
    }

    [Fact]
    public void FormattedTextOverTheDisplayLimitFails()
    {
        var body = "[" + string.Join(",", Enumerable.Repeat("\"" + new string('x', 1000) + "\"", 300)) + "]";
        var model = Json(body);
        Assert.Equal(StructuredBodyViewModel.ErrorPrefix + "formatted body exceeds its display safety limit", model.Error);
    }

    [Fact]
    public void XmlBodyUsesTheXmlTree()
    {
        var model = new StructuredBodyViewModel(BodyFormat.Xml, "XML", "", () => "<r a=\"1\"><v>x</v></r>");
        Assert.Equal(["r", "@a: 1", "v: x"], model.Tree!.VisibleRows.Select(row => row.Text));
        Assert.Equal(new StyledSpan(4, 1, SpanStyle.Attribute), model.Tree.VisibleRows[1].Line.Spans[1]);
        Assert.Equal("XML view mode", model.ModeGroupName);
    }
}

public sealed class ValueTreeSearchTests
{
    private static ValueTreeViewModel Tree() => new StructuredBodyViewModel(
        BodyFormat.Json, "JSON", "", () => "{\"outer\":{\"inner\":{\"needle\":\"Needle\"}},\"other\":\"x\"}").Tree!;

    [Fact]
    public void SearchRevealsCollapsedAncestorsAndRestoresThem()
    {
        var tree = Tree();
        tree.CollapseAll();
        var search = new ActiveSearchViewModel(() => tree, "", "");

        search.Query = "NEEDLE";
        Assert.Equal(2, search.MatchCount);
        Assert.Contains(tree.VisibleRows, row => row.Text == "needle: Needle");
        var match = tree.VisibleRows.Single(row => row.Text == "needle: Needle");
        Assert.Equal(2, match.Line.Highlights.Count);
        Assert.Equal(0, match.Line.CurrentMatch);

        search.Move(1);
        Assert.Equal(1, match.Line.CurrentMatch);

        search.Reset();
        Assert.Equal(["Root"], tree.VisibleRows.Select(row => row.Text));
        Assert.Empty(match.Line.Highlights);
    }

    [Fact]
    public void SearchRaisesScrollRequestForTheCurrentMatch()
    {
        var tree = Tree();
        TreeRow? scrolled = null;
        tree.ScrollRequested += (_, row) => scrolled = row;
        var search = new ActiveSearchViewModel(() => tree, "", "");

        search.Query = "other";
        Assert.Equal("other: x", scrolled?.Text);
    }

    [Fact]
    public void SearchIsCappedAt5000Matches()
    {
        var tree = new ValueTreeViewModel(Enumerable.Range(0, 3000).Select(_ => new TreeItem("aa", [], [])), "test");
        var outcome = tree.Search("a");
        Assert.Equal(new SearchOutcome(SearchText.MaxMatches, true), outcome);
    }

    [Fact]
    public void RowsCarryDepthParentsAndSiblingPositions()
    {
        var tree = Tree();
        var inner = tree.AllRows.Single(row => row.Text == "inner");
        Assert.Equal(2, inner.Depth);
        Assert.Equal("outer", inner.Parent!.Text);
        Assert.Equal((1, 1), (inner.PositionInSet, inner.SizeOfSet));
        var other = tree.AllRows.Single(row => row.Text == "other: x");
        Assert.Equal((2, 2), (other.PositionInSet, other.SizeOfSet));
        Assert.Equal("  outer\n    inner", ValueTreeViewModel.OutlineText([tree.AllRows[1], inner]));
    }
}

public sealed class ProtocolTextTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\0b\tc\nd\re", "a\\0b\\tc\\nd\\re")]
    [InlineData("\u0001\u007F\u0085", "\\x01\\x7F\\x85")]
    [InlineData("\u202Ex\u200F", "\\u202Ex\\u200F")]
    public void EscapesControlAndBidiCharacters(string value, string expected) => Assert.Equal(expected, ProtocolText.Safe(value));

    [Fact]
    public void EscapesOnlyUnpairedSurrogates()
    {
        // Built at run time: xUnit data serialization would replace lone surrogates.
        var high = ((char)0xD83D).ToString();
        var low = ((char)0xDE00).ToString();
        Assert.Equal(high + low, ProtocolText.Safe(high + low));
        Assert.Equal("\\uD83Dx", ProtocolText.Safe(high + "x"));
        Assert.Equal("x\\uDE00", ProtocolText.Safe("x" + low));
    }
}

public sealed class StructuredTabIntegrationTests
{
    [Fact]
    public void JsonResponseTabShowsTreeAndModeSwitchResetsSearch()
    {
        var model = new CaptureViewModel(NativeCaptures.Mixed(), new FakeClipboard());
        model.Inspector.Load(model.Sessions.VisibleRows[0]);
        var response = model.Inspector.Response!;
        var structured = Assert.IsType<StructuredBodyViewModel>(response.SelectedTab!.Content);

        Assert.Equal(["Root", "items", "items[0]: 1", "items[1]: 2", "items[2]: 3", "name: alpha"], structured.Tree!.VisibleRows.Select(row => row.Text));

        response.Search.Query = "alpha";
        Assert.Equal(1, response.Search.MatchCount);
        structured.IsFormattedMode = true;
        Assert.Equal(0, response.Search.MatchCount);
        Assert.Equal("", response.Search.Query);

        response.SelectedTab.Copy();
        Assert.Equal("Copied response JSON formatted text.", response.SelectedTab.CopyStatus);
    }
}
