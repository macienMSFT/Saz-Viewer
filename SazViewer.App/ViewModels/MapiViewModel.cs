using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SazViewer.App.Model;
using SazViewer.Core;

namespace SazViewer.App.ViewModels;

/// <summary>
/// The MAPI tab (the report's <c>renderProtocolTree</c>): a dense, fully expanded protocol tree whose rows show
/// <c>name: value</c> plus the node kind and <c>@offset +length</c>, Expand all / Collapse all, a node count
/// and Copy of the indented protocol outline (<c>protocolCopyText</c>).
/// </summary>
internal sealed partial class MapiViewModel : TabContentViewModel
{
    /// <summary>Warnings retained for display and copy, as in the report payload.</summary>
    public const int MaxWarnings = 50;

    /// <summary>The report's <c>MAX_COPY_CHARACTERS</c>.</summary>
    public const int MaxCopyCharacters = 1024 * 1024;

    public const string CopyLimitError = "MAPI copy exceeds the 1 MiB safety limit.";

    private readonly MapiMessageParse protocol;

    public MapiViewModel(MapiMessageParse protocol)
    {
        this.protocol = protocol;
        var count = 0;
        Tree = new ValueTreeViewModel([ToItem(protocol.Root, ref count)], "MAPI protocol structure");
        NodeCount = count;
        Status = $"{count.ToString("N0", CultureInfo.InvariantCulture)} nodes";
        Summary = $"MAPI protocol ({(protocol.Complete ? "complete" : "partial")}; "
            + $"{protocol.ParsedBytes.ToString("N0", CultureInfo.InvariantCulture)} of "
            + $"{protocol.TotalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
        Warnings = protocol.Warnings.Take(MaxWarnings).ToList();
        OmittedWarnings = Math.Max(0, protocol.Warnings.Length - MaxWarnings);
        ToolbarActions =
        [
            new ToolbarAction("Expand all", "Expand all MAPI tree nodes", Tree.ExpandAll),
            new ToolbarAction("Collapse all", "Collapse all MAPI tree nodes", Tree.CollapseAll)
        ];
    }

    public ValueTreeViewModel Tree { get; }

    public int NodeCount { get; }

    /// <summary>Toolbar status, e.g. "1,234 nodes".</summary>
    public string Status { get; }

    /// <summary>Completeness and parsed byte count shown above the tree.</summary>
    public string Summary { get; }

    public bool IsPartial => !protocol.Complete;

    public IReadOnlyList<string> Warnings { get; }

    public int OmittedWarnings { get; }

    /// <summary>Parser warnings shown above the tree (first <see cref="MaxWarnings"/> plus an omitted count).</summary>
    public string? WarningText => Warnings.Count == 0
        ? null
        : string.Join("\n", Warnings.Select(ProtocolText.Safe))
            + (OmittedWarnings > 0 ? $"\n{OmittedWarnings} additional warning(s) omitted." : "");

    public override IReadOnlyList<ToolbarAction> ToolbarActions { get; }

    public override ISearchableView SearchTarget => Tree;

    public override CopyResult GetCopyText() => CopyText(protocol);

    /// <summary>Port of the report's <c>protocolCopyText</c>, including its 1 MiB limit.</summary>
    public static CopyResult CopyText(MapiMessageParse protocol)
    {
        var text = new StringBuilder();
        bool Append(string line)
        {
            var separator = text.Length > 0 ? 1 : 0;
            if (text.Length + separator + line.Length > MaxCopyCharacters)
            {
                return false;
            }
            if (separator > 0)
            {
                text.Append('\n');
            }
            text.Append(line);
            return true;
        }
        var state = protocol.Complete ? "complete" : "partial";
        if (!Append($"MAPI protocol ({state}; {Number(protocol.ParsedBytes)} of {Number(protocol.TotalBytes)} bytes)"))
        {
            return CopyResult.Fail(CopyLimitError);
        }
        var exceeded = false;
        foreach (var warning in protocol.Warnings.Take(MaxWarnings))
        {
            exceeded |= !Append($"[Warning] {warning}");
        }
        var omitted = protocol.Warnings.Length - MaxWarnings;
        if (omitted > 0)
        {
            exceeded |= !Append($"[Warning] {omitted} additional warning(s) omitted from the report.");
        }
        var stack = new Stack<(MapiNode Node, int Depth)>();
        stack.Push((protocol.Root, 0));
        while (!exceeded && stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            var indent = new string(' ', Math.Min(depth, 64) * 2);
            var value = node.Value is null ? "" : $" = {node.Value}";
            if (!Append($"{indent}{node.Name} [{node.Kind}] @{Number(node.Offset)} +{Number(node.Length)}{value}"))
            {
                exceeded = true;
                break;
            }
            for (var index = node.Children.Length - 1; index >= 0; index--)
            {
                stack.Push((node.Children[index], depth + 1));
            }
        }
        return exceeded ? CopyResult.Fail(CopyLimitError) : CopyResult.Of(text.ToString());
    }

    /// <summary>
    /// Port of <c>protocolDisplayValue</c>: Raw hex becomes <c>Binary (N bytes): AA BB … (X more bytes)</c>;
    /// a semantic <c>0xHEX (text)</c> becomes <c>text = 0xHEX</c>; everything else is escaped text.
    /// </summary>
    public static (string Value, bool Binary)? DisplayValue(MapiNode node)
    {
        if (node.Value is null)
        {
            return null;
        }
        var text = node.Value;
        if (node.Kind == MapiNodeKind.Raw)
        {
            var bytes = $"Binary ({Number(node.Length)} byte{(node.Length == 1 ? "" : "s")}): ";
            var match = RawHex().Match(text);
            if (!match.Success)
            {
                return (bytes + ProtocolText.Safe(text), true);
            }
            var hex = match.Groups[1].Value;
            var pairs = new List<string>(hex.Length / 2);
            for (var index = 0; index + 1 < hex.Length; index += 2)
            {
                pairs.Add(hex.Substring(index, 2));
            }
            var suffix = match.Groups[2].Success
                ? $" \u2026 ({match.Groups[2].Value} more bytes)"
                : node.Length > pairs.Count ? $" \u2026 ({Number(node.Length - pairs.Count)} more bytes)" : "";
            return (bytes + string.Join(' ', pairs) + suffix, true);
        }
        var semantic = SemanticHex().Match(text);
        return semantic.Success
            ? ($"{ProtocolText.Safe(semantic.Groups[2].Value)} = 0x{semantic.Groups[1].Value.ToUpperInvariant()}", false)
            : (ProtocolText.Safe(text), false);
    }

    /// <summary>Builds the display item for one node: label spans, kind/location metadata and UIA name.</summary>
    public static TreeItem ToItem(MapiNode node, ref int count)
    {
        count++;
        var name = ProtocolText.Safe(node.Name);
        var spans = new List<StyledSpan> { new(0, name.Length, SpanStyle.Name) };
        var text = name;
        var display = DisplayValue(node);
        if (display is { } shown)
        {
            spans.Add(new(text.Length, 2, SpanStyle.Muted));
            spans.Add(new(text.Length + 2, shown.Value.Length, shown.Binary ? SpanStyle.ProtocolBinary : SpanStyle.Value));
            text = $"{text}: {shown.Value}";
        }
        var kind = node.Kind.ToString();
        var children = new List<TreeItem>(node.Children.Length);
        foreach (var child in node.Children)
        {
            children.Add(ToItem(child, ref count));
        }
        return new TreeItem(text, spans, children)
        {
            Meta = $"{kind} @{Number(node.Offset)} +{Number(node.Length)}",
            MetaSpans = [new StyledSpan(0, kind.Length, SpanStyle.ProtocolKind)],
            AccessibleName = $"{text}; {kind}; offset {Number(node.Offset)}; length {Number(node.Length)}"
        };
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^([0-9A-Fa-f]*)(?: \.\.\. \[([0-9,]+) more bytes\])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RawHex();

    [GeneratedRegex(@"^0x([0-9A-Fa-f]+) \(([^()]+)\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticHex();
}
