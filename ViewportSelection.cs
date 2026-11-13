using System.Windows;
using System.Windows.Media;

namespace LayerCAD;

internal static class ViewportSelection
{
    public static Point[] Box(Point start, Point end)
    {
        var bounds = new Rect(start, end);
        return [bounds.TopLeft, bounds.TopRight, bounds.BottomRight, bounds.BottomLeft];
    }

    public static StreamGeometry Geometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], isFilled: true, isClosed: points.Count >= 3);
            for (int i = 1; i < points.Count; i++)
                path.LineTo(points[i], isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    public static bool Overlaps(StreamGeometry area, IReadOnlyList<Point> points) =>
        area.FillContainsWithDetail(Geometry(points)) != IntersectionDetail.Empty;

    public static bool Encloses(StreamGeometry area, IReadOnlyList<Point> points) =>
        points.All(point => area.FillContains(point)) &&
        area.FillContainsWithDetail(Geometry(points)) is not (IntersectionDetail.Intersects or IntersectionDetail.Empty);

    public static bool IsClockwise(IReadOnlyList<Point> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area > 0;
    }

    public static readonly Brush WindowStroke = Brushes.SkyBlue;
    public static readonly Brush CrossingStroke = Brushes.Aquamarine;
}
