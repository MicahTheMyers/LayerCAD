using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LayerCAD;

public sealed class SketchMeasurements : FrameworkElement
{
    public const double Gutter = 52;
    private static readonly Brush LabelBackground = BrushesFromRgb(16, 22, 32);
    private static readonly Brush Accent = BrushesFromRgb(203, 221, 246);
    private static readonly Brush OriginBrush = BrushesFromRgb(108, 185, 247);
    private static readonly Pen DimensionPen = CreatePen(Accent, 1.1);
    private static readonly Pen ExtensionPen = CreatePen(BrushesFromRgb(104, 125, 151), 1);
    private static readonly Pen DotBorder = CreatePen(LabelBackground, 1.5);
    private static readonly Pen OriginPen = CreatePen(OriginBrush, 1.8);
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private Rect bounds = Rect.Empty;
    private Point center;
    private Point origin;
    private double widthMm;
    private double heightMm;
    private bool showDimensions;
    private readonly List<(Rect Bounds, bool Width, double Length)> dimensionLabels = [];
    public DisplayUnits Units { get; set; }

    public void Update(IReadOnlyList<SketchPoint> profile, Func<SketchPoint, Point> project, bool closed, bool showDimensions = true)
    {
        this.showDimensions = showDimensions;
        bounds = Rect.Empty;
        origin = project(default);
        if (profile.Count >= 2)
        {
            double minX = profile.Min(point => point.X), maxX = profile.Max(point => point.X);
            double minY = profile.Min(point => point.Y), maxY = profile.Max(point => point.Y);
            widthMm = maxX - minX;
            heightMm = maxY - minY;
            bounds = new Rect(project(new SketchPoint(minX, maxY)), project(new SketchPoint(maxX, minY)));
            var midpoint = new SketchPoint((minX + maxX) / 2, (minY + maxY) / 2);
            center = project(closed ? AreaCenter(profile, midpoint) : midpoint);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        dimensionLabels.Clear();
        const double offset = 17;
        if (showDimensions && !bounds.IsEmpty && widthMm > 0)
        {
            DrawHorizontal(drawing, bounds.Top - offset, bounds.Top, -1);
            DrawHorizontal(drawing, bounds.Bottom + offset, bounds.Bottom, 1);
        }
        if (showDimensions && !bounds.IsEmpty && heightMm > 0)
        {
            DrawVertical(drawing, bounds.Left - offset, bounds.Left, -1);
            DrawVertical(drawing, bounds.Right + offset, bounds.Right, 1);
        }
        drawing.DrawEllipse(LabelBackground, OriginPen, origin, 7, 7);
        drawing.DrawEllipse(OriginBrush, null, origin, 2.5, 2.5);
        DrawText(drawing, "Origin", new Point(origin.X + 26, origin.Y + 15), 0, OriginBrush);
        if (!bounds.IsEmpty)
            drawing.DrawEllipse(Accent, DotBorder, center, 4.5, 4.5);
    }

    internal (Rect Bounds, bool Width, double Length)? DimensionAt(Point pointer)
    {
        if (!showDimensions)
            return null;
        foreach (var label in dimensionLabels)
            if (label.Bounds.Contains(pointer))
                return label;
        return null;
    }

    private void DrawHorizontal(DrawingContext drawing, double lineY, double edgeY, int side)
    {
        var left = new Point(bounds.Left, lineY);
        var right = new Point(bounds.Right, lineY);
        DrawExtension(drawing, left, new Vector(0, side), Math.Abs(lineY - edgeY));
        DrawExtension(drawing, right, new Vector(0, side), Math.Abs(lineY - edgeY));
        DrawArrows(drawing, left, right);
        DrawLabel(drawing, widthMm, new Point((left.X + right.X) / 2, lineY + side * 13), 0);
    }

    private void DrawVertical(DrawingContext drawing, double lineX, double edgeX, int side)
    {
        var top = new Point(lineX, bounds.Top);
        var bottom = new Point(lineX, bounds.Bottom);
        DrawExtension(drawing, top, new Vector(side, 0), Math.Abs(lineX - edgeX));
        DrawExtension(drawing, bottom, new Vector(side, 0), Math.Abs(lineX - edgeX));
        DrawArrows(drawing, top, bottom);
        DrawLabel(drawing, heightMm, new Point(lineX + side * 13, (top.Y + bottom.Y) / 2), -90);
    }

    private static void DrawExtension(DrawingContext drawing, Point tip, Vector outward, double offset) =>
        drawing.DrawLine(ExtensionPen, tip - outward * (offset - 3), tip + outward * 4);

    private static void DrawArrows(DrawingContext drawing, Point start, Point end)
    {
        var direction = end - start;
        double length = direction.Length;
        if (length <= 0)
            return;
        direction.Normalize();

        bool outside = length < 20;
        double extension = outside ? 11 : 0;
        drawing.DrawLine(DimensionPen, start - direction * extension, end + direction * extension);
        var arrowDirection = outside ? -direction : direction;
        DrawArrowhead(drawing, start, arrowDirection);
        DrawArrowhead(drawing, end, -arrowDirection);
    }

    private static void DrawArrowhead(DrawingContext drawing, Point tip, Vector back)
    {
        var wing = new Vector(-back.Y, back.X) * 3;
        drawing.DrawLine(DimensionPen, tip, tip + back * 6 + wing);
        drawing.DrawLine(DimensionPen, tip, tip + back * 6 - wing);
    }

    private void DrawLabel(DrawingContext drawing, double length, Point location, double angle)
    {
        var bounds = DrawText(drawing, $"{Units.FormatLabel(length)} {Units.Symbol()}", location, angle, Accent);
        dimensionLabels.Add((bounds, angle == 0, length));
    }

    private Rect DrawText(DrawingContext drawing, string label, Point location, double angle, Brush color)
    {
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelTypeface, 12,
            color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = new Point(location.X - text.Width / 2, location.Y - text.Height / 2);
        var transform = new RotateTransform(angle, location.X, location.Y);
        var bounds = new Rect(origin.X - 4, origin.Y - 1, text.Width + 8, text.Height + 2);
        drawing.PushTransform(transform);
        drawing.DrawRoundedRectangle(LabelBackground, null, bounds, 2, 2);
        drawing.DrawText(text, origin);
        drawing.Pop();
        return transform.TransformBounds(bounds);
    }

    private static SketchPoint AreaCenter(IReadOnlyList<SketchPoint> profile, SketchPoint fallback)
    {
        var origin = profile[0];
        double area = 0, x = 0, y = 0;
        for (int i = 0; i < profile.Count; i++)
        {
            var a = profile[i];
            var b = profile[(i + 1) % profile.Count];
            double ax = a.X - origin.X, ay = a.Y - origin.Y;
            double bx = b.X - origin.X, by = b.Y - origin.Y;
            double cross = ax * by - bx * ay;
            area += cross;
            x += (ax + bx) * cross;
            y += (ay + by) * cross;
        }
        return Math.Abs(area) > 1e-9
            ? new SketchPoint(origin.X + x / (3 * area), origin.Y + y / (3 * area))
            : fallback;
    }

    private static Brush BrushesFromRgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double width)
    {
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }
}
