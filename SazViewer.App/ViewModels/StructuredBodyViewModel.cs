using SazViewer.App.Model;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The JSON or XML tab (the report's <c>createStructuredBody</c>): format metadata, a Tree / Formatted Text
/// toggle, Expand all / Collapse all in Tree mode, and Copy of the formatted text.
/// </summary>
internal sealed class StructuredBodyViewModel : TabContentViewModel
{
    public const string ErrorPrefix = "Structured view could not be prepared: ";

    private readonly ToolbarAction expandAll;
    private readonly ToolbarAction collapseAll;
    private bool isTreeMode = true;

    public StructuredBodyViewModel(BodyFormat format, string? label, string status, Func<string> source)
    {
        Format = format;
        FormatName = format == BodyFormat.Json ? "JSON" : "XML";
        // WebSocket messages have no body format metadata line.
        MetaLine = label is null ? null : new DocumentLine(MessageDocuments.FormatMetaText(label, status, out var spans), LineKind.FormatMeta, spans, 0, 0, null);
        try
        {
            var (formatted, items) = Prepare(format, source());
            FormattedText = formatted;
            var display = formatted.Replace("\r\n", "\n", StringComparison.Ordinal);
            var highlight = format == BodyFormat.Json ? SyntaxHighlighter.Json(display) : SyntaxHighlighter.Xml(display);
            Formatted = new TextDocumentViewModel(LineDocument.FromText(display, LineKind.Code, highlight), GetCopyText);
            Tree = new ValueTreeViewModel(items, $"{FormatName} structure");
        }
        catch (Exception error) when (error is FormatException or InvalidDataException)
        {
            Error = ErrorPrefix + error.Message;
            ErrorDocument = new TextDocumentViewModel(LineDocument.FromText(Error, LineKind.Warning), GetCopyText);
        }
        expandAll = new ToolbarAction("Expand all", $"Expand all {FormatName} tree nodes", () => Tree?.ExpandAll());
        collapseAll = new ToolbarAction("Collapse all", $"Collapse all {FormatName} tree nodes", () => Tree?.CollapseAll());
        ToolbarActions = [expandAll, collapseAll];
        UpdateActions();
    }

    public BodyFormat Format { get; }

    public string FormatName { get; }

    public string ModeGroupName => $"{FormatName} view mode";

    public DocumentLine? MetaLine { get; }

    /// <summary>The formatted text with CRLF line breaks (what Copy returns), or null when preparation failed.</summary>
    public string? FormattedText { get; }

    public ValueTreeViewModel? Tree { get; }

    public TextDocumentViewModel? Formatted { get; }

    public string? Error { get; }

    public TextDocumentViewModel? ErrorDocument { get; }

    public override IReadOnlyList<ToolbarAction> ToolbarActions { get; }

    public bool IsTreeMode
    {
        get => isTreeMode;
        set
        {
            if (SetProperty(ref isTreeMode, value))
            {
                OnPropertyChanged(nameof(IsFormattedMode));
                OnPropertyChanged(nameof(ActiveView));
                UpdateActions();
                RaiseSearchTargetChanged();
            }
        }
    }

    public bool IsFormattedMode
    {
        get => !isTreeMode;
        set => IsTreeMode = !value;
    }

    /// <summary>The view-model shown below the toolbar for the current mode.</summary>
    public object? ActiveView => (object?)ErrorDocument ?? (IsTreeMode ? Tree : Formatted);

    public override ISearchableView? SearchTarget =>
        (ISearchableView?)ErrorDocument?.Document ?? (IsTreeMode ? Tree : Formatted?.Document);

    public override CopyResult GetCopyText() =>
        FormattedText is null ? CopyResult.Fail(Error ?? "Copy source is unavailable.") : CopyResult.Bounded(FormattedText);

    /// <summary>Parses <paramref name="text"/> and builds the formatted text and bounded tree.</summary>
    public static (string Formatted, IReadOnlyList<TreeItem> Tree) Prepare(BodyFormat format, string text)
    {
        string formatted;
        IReadOnlyList<TreeItem> items;
        if (format == BodyFormat.Json)
        {
            var parsed = CanonicalJson.Parse(text);
            formatted = CanonicalJson.Pretty(parsed);
            items = [ToItem(CanonicalJson.Tree(parsed))];
        }
        else
        {
            var parsed = CanonicalXml.Parse(text);
            formatted = CanonicalXml.Pretty(parsed);
            items = CanonicalXml.Tree(parsed).Select(ToItem).ToList();
        }
        if (formatted.Length > TreeBudget.MaxFormattedCharacters)
        {
            throw new FormatException("formatted body exceeds its display safety limit");
        }
        return (formatted, items);
    }

    /// <summary>MAPI-style label: bold name, then <c>: value</c> for scalars; no counts or brackets.</summary>
    public static TreeItem ToItem(ValueTreeNode node)
    {
        if (node.Kind == ValueNodeKind.Status)
        {
            return new TreeItem(node.Name, [], []) { IsStatus = true };
        }
        var name = ProtocolText.Safe(node.Name);
        var spans = new List<StyledSpan> { new(0, name.Length, SpanStyle.Name) };
        var text = name;
        if (node.Value is not null)
        {
            var value = node.Kind is ValueNodeKind.Number or ValueNodeKind.Boolean or ValueNodeKind.Null
                ? node.Value
                : ProtocolText.Safe(node.Value);
            var style = node.Kind switch
            {
                ValueNodeKind.Number or ValueNodeKind.Boolean => SpanStyle.Number,
                ValueNodeKind.Null => SpanStyle.Literal,
                ValueNodeKind.Comment => SpanStyle.Comment,
                ValueNodeKind.Attribute => SpanStyle.Attribute,
                _ => SpanStyle.String
            };
            spans.Add(new(text.Length + 2, value.Length, style));
            text = $"{text}: {value}";
            if (node.Truncated)
            {
                spans.Add(new(text.Length, " (truncated)".Length, SpanStyle.Warning));
                text += " (truncated)";
            }
        }
        return new TreeItem(text, spans, node.Children.Select(ToItem).ToList());
    }

    private void UpdateActions()
    {
        var visible = IsTreeMode && Tree is not null;
        expandAll.IsVisible = visible;
        collapseAll.IsVisible = visible;
    }
}
