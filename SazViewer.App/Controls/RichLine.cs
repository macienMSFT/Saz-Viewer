using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SazViewer.App.ViewModels;

namespace SazViewer.App.Controls;

/// <summary>
/// Renders one <see cref="DocumentLine"/>: its syntax spans plus search highlights, as runs whose brushes are
/// theme resource references. Re-renders when the line's <see cref="DocumentLine.Version"/> changes.
/// </summary>
internal sealed class RichLine : TextBlock
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(DocumentLine), typeof(RichLine), new PropertyMetadata(null, OnRenderInputChanged));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(RichLine), new PropertyMetadata(0, OnRenderInputChanged));

    public DocumentLine? Line
    {
        get => (DocumentLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    private static void OnRenderInputChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((RichLine)sender).Render();

    private void Render()
    {
        Inlines.Clear();
        if (Line is not { } line)
        {
            return;
        }
        if (line.IsToggle)
        {
            var run = new Run((line.IsExpanded ? "▾ " : "▸ ") + line.Text);
            run.SetResourceReference(TextElement.ForegroundProperty, "Saz.Accent");
            Inlines.Add(run);
            return;
        }
        foreach (var segment in Segments(line.Text.Length, line.Spans, line.Highlights, line.CurrentMatch))
        {
            Inlines.Add(CreateRun(line.Text.Substring(segment.Start, segment.Length), segment));
        }
    }

    private static Run CreateRun(string text, RenderSegment segment)
    {
        var run = new Run(text);
        if (segment.Style is { } style)
        {
            ApplyStyle(run, style);
        }
        if (segment.Match != MatchState.None)
        {
            var current = segment.Match == MatchState.Current;
            run.SetResourceReference(TextElement.BackgroundProperty, current ? "Saz.CurrentBg" : "Saz.MatchBg");
            run.SetResourceReference(TextElement.ForegroundProperty, current ? "Saz.CurrentFg" : "Saz.MatchFg");
        }
        return run;
    }

    private static void ApplyStyle(Run run, SpanStyle style)
    {
        var brush = style switch
        {
            SpanStyle.Muted => "Saz.Muted",
            SpanStyle.Punctuation or SpanStyle.Comment => "Saz.SynPunct",
            SpanStyle.Badge or SpanStyle.Bold => null,
            SpanStyle.Key => "Saz.SynBlue",
            SpanStyle.Tag => "Saz.SynGreen",
            SpanStyle.String or SpanStyle.Value => "Saz.SynString",
            SpanStyle.Number => "Saz.SynNumber",
            SpanStyle.Literal => "Saz.SynRed",
            SpanStyle.Attribute => "Saz.SynPurple",
            SpanStyle.Accent or SpanStyle.Name => "Saz.Accent",
            SpanStyle.Warning => "Saz.Warn",
            _ => null
        };
        if (brush is not null)
        {
            run.SetResourceReference(TextElement.ForegroundProperty, brush);
        }
        if (style is SpanStyle.Badge or SpanStyle.Bold or SpanStyle.Name)
        {
            run.FontWeight = FontWeights.SemiBold;
        }
        if (style == SpanStyle.Badge)
        {
            run.SetResourceReference(TextElement.BackgroundProperty, "Saz.Panel2");
        }
        if (style == SpanStyle.Comment)
        {
            run.FontStyle = FontStyles.Italic;
        }
    }

    internal enum MatchState
    {
        None,
        Match,
        Current
    }

    internal readonly record struct RenderSegment(int Start, int Length, SpanStyle? Style, MatchState Match);

    /// <summary>Splits a row into maximal segments of uniform style and match state.</summary>
    internal static List<RenderSegment> Segments(int length, IReadOnlyList<StyledSpan> spans, IReadOnlyList<RowHighlight> highlights, int currentMatch)
    {
        var result = new List<RenderSegment>();
        if (length == 0)
        {
            return result;
        }
        var cuts = new SortedSet<int> { 0, length };
        foreach (var span in spans)
        {
            cuts.Add(Math.Clamp(span.Start, 0, length));
            cuts.Add(Math.Clamp(span.End, 0, length));
        }
        foreach (var highlight in highlights)
        {
            cuts.Add(Math.Clamp(highlight.Start, 0, length));
            cuts.Add(Math.Clamp(highlight.Start + highlight.Length, 0, length));
        }
        var points = cuts.ToArray();
        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            if (end <= start)
            {
                continue;
            }
            SpanStyle? style = null;
            foreach (var span in spans)
            {
                if (span.Start <= start && span.End >= end)
                {
                    style = span.Style;
                }
            }
            var match = MatchState.None;
            foreach (var highlight in highlights)
            {
                if (highlight.Start <= start && highlight.Start + highlight.Length >= end)
                {
                    match = highlight.MatchIndex == currentMatch ? MatchState.Current : MatchState.Match;
                    break;
                }
            }
            if (result.Count > 0 && result[^1] is var last && last.Style == style && last.Match == match && last.Start + last.Length == start)
            {
                result[^1] = last with { Length = last.Length + end - start };
            }
            else
            {
                result.Add(new RenderSegment(start, end - start, style, match));
            }
        }
        return result;
    }
}
