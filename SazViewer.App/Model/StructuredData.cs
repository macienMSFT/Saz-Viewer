using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SazViewer.App.Model;

/// <summary>Kind of a <see cref="ValueTreeNode"/> (JSON values, XML nodes and budget/status entries).</summary>
internal enum ValueNodeKind
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
    Element,
    Attribute,
    Text,
    CData,
    Comment,
    Status
}

/// <summary>
/// A display node of a JSON or XML tree, bounded by the report's tree budgets. Containers without children are
/// leaves; <see cref="Value"/> is null for containers.
/// </summary>
internal sealed record ValueTreeNode(ValueNodeKind Kind, string Name, string? Value, IReadOnlyList<ValueTreeNode> Children)
{
    public bool Truncated { get; init; }

    public static ValueTreeNode Status(string text) => new(ValueNodeKind.Status, text, null, []);
}

/// <summary>The report's tree budgets (TREE_MAX_*), shared by the JSON and XML builders.</summary>
internal static class TreeBudget
{
    public const int MaxNodes = 4000;
    public const int MaxDepth = 40;
    public const int MaxChildren = 300;
    public const int MaxScalar = 300;
    public const int MaxFormattedCharacters = 256 * 1024;

    public static string OmittedText(int omitted) =>
        $"+{omitted.ToString(CultureInfo.InvariantCulture)} more not shown here \u2014 use Formatted Text to view the full content.";

    public const string DepthText = "Maximum nesting depth reached; deeper content is not shown here \u2014 use Formatted Text to view the full content.";

    public static (string Text, bool Truncated) Scalar(string value) =>
        value.Length > MaxScalar ? (value[..MaxScalar], true) : (value, false);

    internal sealed class Counter
    {
        public int Count;
    }
}

/// <summary>Display escaping of control, bidi and unpaired surrogate characters (the report's <c>safeProtocolText</c>).</summary>
internal static class ProtocolText
{
    public static string Safe(string value)
    {
        StringBuilder? result = null;
        for (var index = 0; index < value.Length; index++)
        {
            var code = value[index];
            var escape = code switch
            {
                '\0' => "\\0",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",
                _ when code < 32 || code == 127 || (code >= 128 && code <= 159) =>
                    "\\x" + ((int)code).ToString("X2", CultureInfo.InvariantCulture),
                _ when code is '\u061C' or '\u200E' or '\u200F' or '\u2028' or '\u2029' or '\uFEFF'
                    || (code >= '\u202A' && code <= '\u202E') || (code >= '\u2066' && code <= '\u2069')
                    || IsUnpairedSurrogate(value, index) =>
                    "\\u" + ((int)code).ToString("X4", CultureInfo.InvariantCulture),
                _ => null
            };
            if (escape is null)
            {
                result?.Append(code);
                continue;
            }
            result ??= new StringBuilder(value.Length + 8).Append(value, 0, index);
            result.Append(escape);
        }
        return result?.ToString() ?? value;
    }

    private static bool IsUnpairedSurrogate(string value, int index)
    {
        var code = value[index];
        if (char.IsHighSurrogate(code))
        {
            return index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]);
        }
        return char.IsLowSurrogate(code) && (index == 0 || !char.IsHighSurrogate(value[index - 1]));
    }
}

/// <summary>Canonical JSON (port of the report's <c>parseCanonicalJson</c>/<c>prettyCanonicalJson</c>/<c>jsonTreeNode</c>).</summary>
internal static partial class CanonicalJson
{
    public const int MaxNesting = 64;

    internal abstract record Node;

    internal sealed record ObjectNode(IReadOnlyList<(string Name, Node Value)> Properties) : Node;

    internal sealed record ArrayNode(IReadOnlyList<Node> Items) : Node;

    internal sealed record ScalarNode(ValueNodeKind Kind, string Raw, string Value) : Node;

    [GeneratedRegex(@"\G-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    /// <summary>Parses strict JSON, preserving property order, duplicates and number spelling.</summary>
    public static Node Parse(string source)
    {
        var index = 0;
        var root = Value(source, ref index, 0);
        SkipWhitespace(source, ref index);
        if (index != source.Length)
        {
            throw new FormatException("JSON has trailing content");
        }
        return root;
    }

    private static void SkipWhitespace(string source, ref int index)
    {
        while (index < source.Length && IsJsWhitespace(source[index]))
        {
            index++;
        }
    }

    // JavaScript's \s: Unicode white space plus the byte order mark.
    private static bool IsJsWhitespace(char value) => char.IsWhiteSpace(value) || value == '\uFEFF';

    private static Node Value(string source, ref int index, int depth)
    {
        if (depth > MaxNesting)
        {
            throw new FormatException("JSON nesting exceeds the safe depth");
        }
        SkipWhitespace(source, ref index);
        if (index < source.Length && source[index] == '"')
        {
            var (raw, value) = StringToken(source, ref index);
            return new ScalarNode(ValueNodeKind.String, raw, value);
        }
        if (index < source.Length && source[index] == '{')
        {
            index++;
            SkipWhitespace(source, ref index);
            var properties = new List<(string, Node)>();
            if (index < source.Length && source[index] == '}')
            {
                index++;
                return new ObjectNode(properties);
            }
            while (true)
            {
                SkipWhitespace(source, ref index);
                if (index >= source.Length || source[index] != '"')
                {
                    throw new FormatException("invalid JSON property");
                }
                var (_, name) = StringToken(source, ref index);
                SkipWhitespace(source, ref index);
                if (index >= source.Length || source[index++] != ':')
                {
                    throw new FormatException("invalid JSON property separator");
                }
                properties.Add((name, Value(source, ref index, depth + 1)));
                SkipWhitespace(source, ref index);
                if (index < source.Length && source[index] == '}')
                {
                    index++;
                    break;
                }
                if (index >= source.Length || source[index++] != ',')
                {
                    throw new FormatException("invalid JSON object");
                }
            }
            return new ObjectNode(properties);
        }
        if (index < source.Length && source[index] == '[')
        {
            index++;
            SkipWhitespace(source, ref index);
            var items = new List<Node>();
            if (index < source.Length && source[index] == ']')
            {
                index++;
                return new ArrayNode(items);
            }
            while (true)
            {
                items.Add(Value(source, ref index, depth + 1));
                SkipWhitespace(source, ref index);
                if (index < source.Length && source[index] == ']')
                {
                    index++;
                    break;
                }
                if (index >= source.Length || source[index++] != ',')
                {
                    throw new FormatException("invalid JSON array");
                }
            }
            return new ArrayNode(items);
        }
        var number = NumberPattern().Match(source, index);
        if (number.Success && number.Length > 0)
        {
            index += number.Length;
            return new ScalarNode(ValueNodeKind.Number, number.Value, number.Value);
        }
        foreach (var literal in (ReadOnlySpan<string>)["true", "false", "null"])
        {
            if (string.CompareOrdinal(source, index, literal, 0, literal.Length) == 0)
            {
                index += literal.Length;
                return new ScalarNode(literal == "null" ? ValueNodeKind.Null : ValueNodeKind.Boolean, literal, literal);
            }
        }
        throw new FormatException("invalid JSON value");
    }

    private static (string Raw, string Value) StringToken(string source, ref int index)
    {
        var start = index++;
        var value = new StringBuilder();
        while (index < source.Length)
        {
            var character = source[index++];
            if (character == '"')
            {
                return (source[start..index], value.ToString());
            }
            if (character < ' ')
            {
                throw new FormatException("invalid JSON string");
            }
            if (character != '\\')
            {
                value.Append(character);
                continue;
            }
            if (index >= source.Length)
            {
                throw new FormatException("invalid JSON escape");
            }
            var escape = source[index++];
            switch (escape)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case '/': value.Append('/'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    if (index + 4 > source.Length
                        || !ushort.TryParse(source.AsSpan(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                    {
                        throw new FormatException("invalid JSON Unicode escape");
                    }
                    value.Append((char)code);
                    index += 4;
                    break;
                default:
                    throw new FormatException("invalid JSON escape");
            }
        }
        throw new FormatException("unterminated JSON string");
    }

    /// <summary>Two-space indented text with CRLF newlines, matching System.Text.Json's escaping (the report's Formatted Text).</summary>
    public static string Pretty(Node node)
    {
        var builder = new StringBuilder();
        Pretty(node, 0, builder);
        return builder.ToString();
    }

    private static void Pretty(Node node, int depth, StringBuilder output)
    {
        switch (node)
        {
            case ObjectNode { Properties.Count: 0 }:
                output.Append("{}");
                return;
            case ObjectNode objectNode:
                output.Append("{\r\n");
                for (var index = 0; index < objectNode.Properties.Count; index++)
                {
                    if (index > 0)
                    {
                        output.Append(",\r\n");
                    }
                    output.Append(' ', (depth + 1) * 2);
                    AppendString(output, objectNode.Properties[index].Name);
                    output.Append(": ");
                    Pretty(objectNode.Properties[index].Value, depth + 1, output);
                }
                output.Append("\r\n").Append(' ', depth * 2).Append('}');
                return;
            case ArrayNode { Items.Count: 0 }:
                output.Append("[]");
                return;
            case ArrayNode arrayNode:
                output.Append("[\r\n");
                for (var index = 0; index < arrayNode.Items.Count; index++)
                {
                    if (index > 0)
                    {
                        output.Append(",\r\n");
                    }
                    output.Append(' ', (depth + 1) * 2);
                    Pretty(arrayNode.Items[index], depth + 1, output);
                }
                output.Append("\r\n").Append(' ', depth * 2).Append(']');
                return;
            case ScalarNode { Kind: ValueNodeKind.String } scalar:
                AppendString(output, scalar.Value);
                return;
            case ScalarNode scalar:
                output.Append(scalar.Raw);
                return;
        }
    }

    // The report's dotNetJsonString.
    private static void AppendString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (character < 32 || character > 126 || character is '<' or '>' or '&' or '\'')
                    {
                        output.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(character);
                    }
                    break;
            }
        }
        output.Append('"');
    }

    /// <summary>Bounded display tree: root is "Root", properties by name, array items as <c>name[i]</c>.</summary>
    public static ValueTreeNode Tree(Node root) => TreeNode(root, "Root", null, 0, new TreeBudget.Counter());

    private static ValueTreeNode TreeNode(Node node, string name, string? itemBase, int depth, TreeBudget.Counter budget)
    {
        budget.Count++;
        switch (node)
        {
            case ObjectNode or ArrayNode when depth >= TreeBudget.MaxDepth:
                var limitedKind = node is ObjectNode ? ValueNodeKind.Object : ValueNodeKind.Array;
                var hasEntries = node is ObjectNode { Properties.Count: > 0 } or ArrayNode { Items.Count: > 0 };
                return new ValueTreeNode(limitedKind, name, null, hasEntries ? [ValueTreeNode.Status(TreeBudget.DepthText)] : []);
            case ObjectNode objectNode:
            {
                var children = new List<ValueTreeNode>();
                for (var index = 0; index < objectNode.Properties.Count; index++)
                {
                    if (children.Count >= TreeBudget.MaxChildren || budget.Count >= TreeBudget.MaxNodes)
                    {
                        children.Add(ValueTreeNode.Status(TreeBudget.OmittedText(objectNode.Properties.Count - index)));
                        break;
                    }
                    var (childName, value) = objectNode.Properties[index];
                    children.Add(TreeNode(value, childName, childName, depth + 1, budget));
                }
                return new ValueTreeNode(ValueNodeKind.Object, name, null, children);
            }
            case ArrayNode arrayNode:
            {
                var children = new List<ValueTreeNode>();
                for (var index = 0; index < arrayNode.Items.Count; index++)
                {
                    if (children.Count >= TreeBudget.MaxChildren || budget.Count >= TreeBudget.MaxNodes)
                    {
                        children.Add(ValueTreeNode.Status(TreeBudget.OmittedText(arrayNode.Items.Count - index)));
                        break;
                    }
                    var label = $"{itemBase}[{index.ToString(CultureInfo.InvariantCulture)}]";
                    children.Add(TreeNode(arrayNode.Items[index], label, label, depth + 1, budget));
                }
                return new ValueTreeNode(ValueNodeKind.Array, name, null, children);
            }
            default:
                var scalar = (ScalarNode)node;
                var (text, truncated) = TreeBudget.Scalar(scalar.Value);
                return new ValueTreeNode(scalar.Kind, name, text, []) { Truncated = truncated };
        }
    }
}

/// <summary>Canonical XML (port of the report's <c>parseCanonicalXml</c>/<c>prettyCanonicalXml</c>/<c>xmlTree</c>).</summary>
internal static partial class CanonicalXml
{
    internal abstract record Node;

    internal sealed record Element(string Name, IReadOnlyList<(string Name, string Value)> Attributes, IReadOnlyList<Node> Children) : Node;

    internal sealed record Text(string Value) : Node;

    internal sealed record CData(string Value) : Node;

    internal sealed record Comment(string Value) : Node;

    [GeneratedRegex("<!DOCTYPE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocType();

    /// <summary>Parses XML without DTDs, external resources or processing instructions; returns the document's top-level nodes.</summary>
    public static IReadOnlyList<Node> Parse(string source)
    {
        if (DocType().IsMatch(source))
        {
            throw new FormatException("XML document types are not allowed");
        }
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
            IgnoreComments = false,
            IgnoreProcessingInstructions = true,
            CheckCharacters = true,
            MaxCharactersFromEntities = 0
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(source), settings);
            var top = new List<Node>();
            var stack = new Stack<(string Name, List<(string, string)> Attributes, List<Node> Children)>();
            while (reader.Read())
            {
                var target = stack.Count > 0 ? stack.Peek().Children : null;
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var name = reader.Name;
                        var attributes = new List<(string, string)>();
                        var empty = reader.IsEmptyElement;
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                attributes.Add((reader.Name, reader.Value));
                            }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        if (empty)
                        {
                            (target ?? top).Add(new Element(name, attributes, []));
                        }
                        else
                        {
                            stack.Push((name, attributes, []));
                        }
                        break;
                    }
                    case XmlNodeType.EndElement:
                    {
                        var (name, attributes, children) = stack.Pop();
                        var element = new Element(name, attributes, children);
                        (stack.Count > 0 ? stack.Peek().Children : top).Add(element);
                        break;
                    }
                    case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        // Like the DOM, the document node itself has no text children.
                        if (target is not null)
                        {
                            if (target.Count > 0 && target[^1] is Text previous)
                            {
                                target[^1] = new Text(previous.Value + reader.Value);
                            }
                            else
                            {
                                target.Add(new Text(reader.Value));
                            }
                        }
                        break;
                    case XmlNodeType.CDATA:
                        (target ?? top).Add(new CData(reader.Value));
                        break;
                    case XmlNodeType.Comment:
                        (target ?? top).Add(new Comment(reader.Value));
                        break;
                }
            }
            return top;
        }
        catch (XmlException error)
        {
            throw new FormatException($"XML parsing failed: {error.Message}", error);
        }
    }

    private static string EscapeText(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeAttribute(string value) => EscapeText(value).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static bool IsWhitespace(string value) => value.All(char.IsWhiteSpace);

    public static string Pretty(IReadOnlyList<Node> document) =>
        string.Join("\r\n", document.Select(node => Render(node, 0)).Where(text => text.Length > 0));

    private static string Render(Node node, int depth)
    {
        var indent = new string(' ', depth * 2);
        switch (node)
        {
            case Comment comment:
                return $"{indent}<!--{comment.Value}-->";
            case CData cdata:
                return $"{indent}<![CDATA[{cdata.Value}]]>";
            case Text text:
                return indent + EscapeText(text.Value);
        }
        var element = (Element)node;
        var attributes = string.Concat(element.Attributes.Select(attribute => $" {attribute.Name}=\"{EscapeAttribute(attribute.Value)}\""));
        if (element.Children.Count == 0)
        {
            return $"{indent}<{element.Name}{attributes} />";
        }
        var mixed = element.Children.Any(child => child is Text text && !IsWhitespace(text.Value));
        if (mixed)
        {
            var inner = string.Concat(element.Children.Select(child => child switch
            {
                Element => Render(child, 0).Trim(),
                CData cdata => $"<![CDATA[{cdata.Value}]]>",
                Comment comment => $"<!--{comment.Value}-->",
                Text text => EscapeText(text.Value),
                _ => ""
            }));
            return $"{indent}<{element.Name}{attributes}>{inner}</{element.Name}>";
        }
        var rendered = element.Children.Select(child => Render(child, depth + 1)).Where(text => text.Length > 0);
        return $"{indent}<{element.Name}{attributes}>\r\n{string.Join("\r\n", rendered)}\r\n{indent}</{element.Name}>";
    }

    private static List<Node> Renderable(IReadOnlyList<Node> nodes) =>
        nodes.Where(node => node is not Text text || !IsWhitespace(text.Value)).ToList();

    /// <summary>
    /// Bounded display tree in the MAPI style: elements by name, <c>@attribute: value</c>, <c>#text</c>,
    /// <c>#cdata</c> and <c>#comment</c> leaves; an element whose only content is text shows as <c>name: text</c>.
    /// </summary>
    public static IReadOnlyList<ValueTreeNode> Tree(IReadOnlyList<Node> document)
    {
        var budget = new TreeBudget.Counter();
        return Children(Renderable(document), 0, budget);
    }

    private static List<ValueTreeNode> Children(List<Node> source, int depth, TreeBudget.Counter budget)
    {
        var children = new List<ValueTreeNode>();
        for (var index = 0; index < source.Count; index++)
        {
            if (children.Count >= TreeBudget.MaxChildren || budget.Count >= TreeBudget.MaxNodes)
            {
                children.Add(ValueTreeNode.Status(TreeBudget.OmittedText(source.Count - index)));
                break;
            }
            children.Add(TreeNode(source[index], depth, budget));
        }
        return children;
    }

    private static ValueTreeNode Leaf(ValueNodeKind kind, string name, string value)
    {
        var (text, truncated) = TreeBudget.Scalar(value);
        return new ValueTreeNode(kind, name, text, []) { Truncated = truncated };
    }

    private static ValueTreeNode TreeNode(Node node, int depth, TreeBudget.Counter budget)
    {
        budget.Count++;
        switch (node)
        {
            case Comment comment:
                return Leaf(ValueNodeKind.Comment, "#comment", comment.Value);
            case CData cdata:
                return Leaf(ValueNodeKind.CData, "#cdata", cdata.Value);
            case Text text:
                return Leaf(ValueNodeKind.Text, "#text", text.Value);
        }
        var element = (Element)node;
        var attributes = element.Attributes.Select(attribute => Leaf(ValueNodeKind.Attribute, "@" + attribute.Name, attribute.Value)).ToList();
        var content = Renderable(element.Children);
        if (content is [Text only])
        {
            var (text, truncated) = TreeBudget.Scalar(only.Value);
            return new ValueTreeNode(ValueNodeKind.Element, element.Name, text, attributes) { Truncated = truncated };
        }
        if (depth >= TreeBudget.MaxDepth)
        {
            var limited = new List<ValueTreeNode>(attributes);
            if (content.Count > 0)
            {
                limited.Add(ValueTreeNode.Status(TreeBudget.DepthText));
            }
            return new ValueTreeNode(ValueNodeKind.Element, element.Name, null, limited);
        }
        attributes.AddRange(Children(content, depth + 1, budget));
        return new ValueTreeNode(ValueNodeKind.Element, element.Name, null, attributes);
    }
}

/// <summary>Formatted Text syntax colouring (ports of the report's <c>highlightJson</c> / <c>highlightXml</c>).</summary>
internal static class SyntaxHighlighter
{
    public static List<ViewModels.StyledSpan> Json(string value)
    {
        var spans = new List<ViewModels.StyledSpan>();
        var index = 0;
        while (index < value.Length)
        {
            var start = index;
            var character = value[index];
            if (character == '"')
            {
                index++;
                var escaped = false;
                while (index < value.Length)
                {
                    var current = value[index++];
                    if (current == '"' && !escaped)
                    {
                        break;
                    }
                    escaped = current == '\\' && !escaped;
                }
                var lookahead = index;
                while (lookahead < value.Length && char.IsWhiteSpace(value[lookahead]))
                {
                    lookahead++;
                }
                var key = lookahead < value.Length && value[lookahead] == ':';
                spans.Add(new(start, index - start, key ? ViewModels.SpanStyle.Key : ViewModels.SpanStyle.String));
            }
            else if (char.IsAsciiDigit(character) || character == '-')
            {
                index++;
                while (index < value.Length && (char.IsAsciiDigit(value[index]) || value[index] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    index++;
                }
                spans.Add(new(start, index - start, ViewModels.SpanStyle.Number));
            }
            else if (char.IsAsciiLetter(character))
            {
                index++;
                while (index < value.Length && char.IsAsciiLetter(value[index]))
                {
                    index++;
                }
                spans.Add(new(start, index - start, ViewModels.SpanStyle.Literal));
            }
            else
            {
                if (character is '{' or '}' or '[' or ']' or ':' or ',')
                {
                    spans.Add(new(start, 1, ViewModels.SpanStyle.Punctuation));
                }
                index++;
            }
        }
        return spans;
    }

    public static List<ViewModels.StyledSpan> Xml(string value)
    {
        var spans = new List<ViewModels.StyledSpan>();
        var index = 0;
        while (index < value.Length)
        {
            if (value[index] != '<')
            {
                var next = value.IndexOf('<', index);
                index = next < 0 ? value.Length : next;
                continue;
            }
            var comment = string.CompareOrdinal(value, index, "<!--", 0, 4) == 0;
            if (comment || string.CompareOrdinal(value, index, "<![CDATA[", 0, 9) == 0)
            {
                var terminator = comment ? "-->" : "]]>";
                var end = value.IndexOf(terminator, index, StringComparison.Ordinal);
                var stop = end < 0 ? value.Length : end + terminator.Length;
                spans.Add(new(index, stop - index, ViewModels.SpanStyle.Comment));
                index = stop;
                continue;
            }
            var scan = index + 1;
            var quote = '\0';
            while (scan < value.Length)
            {
                var current = value[scan++];
                if (quote != '\0')
                {
                    if (current == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (current is '"' or '\'')
                {
                    quote = current;
                }
                else if (current == '>')
                {
                    break;
                }
            }
            spans.Add(new(index, scan - index, ViewModels.SpanStyle.Tag));
            index = scan;
        }
        return spans;
    }
}
