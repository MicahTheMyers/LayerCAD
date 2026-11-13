using System.Windows;

namespace LayerCAD;

internal static class SketchProfileSplit
{
    private const double Tolerance = 1e-7;

    public static List<SketchPoint[]> Split(SketchPoint[] outline, IReadOnlyList<SketchPoint> path)
    {
        var crossings = new List<(double Order, SketchPoint Point)>();
        for (int segment = 1; segment < path.Count; segment++)
        {
            var start = path[segment - 1];
            var line = Delta(start, path[segment]);
            for (int i = 0; i < outline.Length; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Length];
                var edge = Delta(a, b);
                double cross = Vector.CrossProduct(line, edge);
                if (Math.Abs(cross) <= 1e-12 * line.Length * edge.Length)
                    continue;
                var relative = Delta(start, a);
                double along = Vector.CrossProduct(relative, edge) / cross;
                double boundary = Vector.CrossProduct(relative, line) / cross;
                if (along is < -Tolerance or > 1 + Tolerance || boundary is < -Tolerance or > 1 + Tolerance)
                    continue;
                var point = boundary <= Tolerance ? a : boundary >= 1 - Tolerance ? b : Offset(a, edge * boundary);
                crossings.Add((segment - 1 + Math.Clamp(along, 0, 1), point));
            }
        }

        var unique = new List<(double Order, SketchPoint Point)>();
        foreach (var crossing in crossings.OrderBy(hit => hit.Order))
        {
            if (unique.Count == 0 || Math.Abs(crossing.Order - unique[^1].Order) > Tolerance ||
                Delta(crossing.Point, unique[^1].Point).Length > Tolerance)
                unique.Add(crossing);
        }

        List<SketchPoint[]> regions = [outline];
        for (int i = 1; i < unique.Count; i++)
        {
            var first = unique[i - 1];
            var last = unique[i];
            List<SketchPoint> cut = [first.Point];
            for (int vertex = (int)Math.Floor(first.Order) + 1; vertex < last.Order - Tolerance; vertex++)
                cut.Add(path[vertex]);
            cut.Add(last.Point);
            for (int region = 0; region < regions.Count; region++)
            {
                if (!TrySplit(regions[region], cut, out var left, out var right))
                    continue;
                regions[region] = left;
                regions.Insert(region + 1, right);
                break;
            }
        }
        return regions;
    }

    private static bool TrySplit(SketchPoint[] outline, List<SketchPoint> cut, out SketchPoint[] left, out SketchPoint[] right)
    {
        left = right = [];
        var start = BoundaryPosition(outline, cut[0]);
        var end = BoundaryPosition(outline, cut[^1]);
        if (start is null || end is null || Math.Abs(start.Value - end.Value) < Tolerance)
            return false;
        for (int i = 1; i < cut.Count; i++)
        {
            var midpoint = Offset(cut[i - 1], Delta(cut[i - 1], cut[i]) * 0.5);
            if (!Inside(outline, midpoint))
                return false;
        }

        var insideVertices = cut.Skip(1).SkipLast(1).ToArray();
        left = Clean(Walk(outline, start.Value, end.Value, cut[0], cut[^1]).Concat(insideVertices.Reverse()));
        right = Clean(Walk(outline, end.Value, start.Value, cut[^1], cut[0]).Concat(insideVertices));
        return SketchGeometry.TryTriangulate(left, out _, out _) && SketchGeometry.TryTriangulate(right, out _, out _);
    }

    private static IEnumerable<SketchPoint> Walk(SketchPoint[] outline, double start, double end, SketchPoint a, SketchPoint b)
    {
        yield return a;
        if (end < start)
            end += outline.Length;
        for (int vertex = (int)Math.Floor(start) + 1; vertex < end - Tolerance; vertex++)
            yield return outline[vertex % outline.Length];
        yield return b;
    }

    private static double? BoundaryPosition(SketchPoint[] outline, SketchPoint point)
    {
        for (int i = 0; i < outline.Length; i++)
        {
            var a = outline[i];
            var line = Delta(a, outline[(i + 1) % outline.Length]);
            var offset = Delta(a, point);
            double along = Vector.Multiply(line, offset) / line.LengthSquared;
            if (along is < -Tolerance or > 1 + Tolerance || (offset - line * along).Length > Tolerance)
                continue;
            return along >= 1 - Tolerance ? (i + 1) % outline.Length : i + Math.Max(0, along);
        }
        return null;
    }

    private static bool Inside(SketchPoint[] outline, SketchPoint point)
    {
        if (BoundaryPosition(outline, point) is not null)
            return false;
        bool inside = false;
        for (int i = 0; i < outline.Length; i++)
        {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Length];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < a.X + (b.X - a.X) * ((point.Y - a.Y) / (b.Y - a.Y)))
                inside = !inside;
        }
        return inside;
    }

    private static SketchPoint[] Clean(IEnumerable<SketchPoint> source)
    {
        var points = source.ToList();
        for (int i = points.Count - 1; i >= 0 && points.Count >= 3; i--)
        {
            var previous = points[(i + points.Count - 1) % points.Count];
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            var a = Delta(previous, current);
            var b = Delta(current, next);
            if (Math.Abs(Vector.CrossProduct(a, b)) <= 1e-9 && Vector.Multiply(a, b) >= 0)
                points.RemoveAt(i);
        }
        return points.ToArray();
    }

    private static Vector Delta(SketchPoint start, SketchPoint end) => new(end.X - start.X, end.Y - start.Y);
    private static SketchPoint Offset(SketchPoint point, Vector delta) => new(point.X + delta.X, point.Y + delta.Y);
}
