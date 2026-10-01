using System.Windows;
using System.Windows.Media;

namespace SazViewer.App.Controls;

/// <summary>
/// Draws the dotted tree guides for one row (the report's <c>.tree-group</c> left border and elbow): a vertical
/// dotted line per ancestor level at <c>level * 20 + 7</c>, and an 11px elbow at y = 10 for the row's own level.
/// </summary>
internal sealed class TreeGuides : FrameworkElement
{
    public const double Indent = 20;
    public const double LineOffset = 7;
    public const double ElbowWidth = 11;
    public const double ElbowY = 10;

    public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(
        nameof(Depth), typeof(int), typeof(TreeGuides),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TreeGuides),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreeGuides)d).pen = null));

    private Pen? pen;

    public TreeGuides()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    public int Depth
    {
        get => (int)GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Depth * Indent, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var depth = Depth;
        if (depth <= 0)
        {
            return;
        }
        pen ??= CreatePen(Stroke);
        var height = ActualHeight;
        for (var level = 0; level < depth; level++)
        {
            var x = level * Indent + LineOffset + 0.5;
            drawingContext.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
        var elbowX = (depth - 1) * Indent + LineOffset;
        drawingContext.DrawLine(pen, new Point(elbowX, ElbowY + 0.5), new Point(elbowX + ElbowWidth, ElbowY + 0.5));
    }

    private static Pen CreatePen(Brush brush)
    {
        // Not frozen: freezing would also freeze the shared theme brush.
        return new Pen(brush, 1) { DashStyle = new DashStyle([1, 2], 0) };
    }
}
