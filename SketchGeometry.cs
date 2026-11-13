namespace LayerCAD;

public readonly record struct SketchPoint(double X, double Y);

internal static class SketchGeometry
{
    private const double Epsilon = 1e-9;
    public const int MaxVertices = 256;
    public const int CurveSegments = 192;

    internal static bool ValidOpenPath(SketchPoint[]? points) =>
        points is { Length: >= 2 and <= MaxVertices } &&
        points.All(point => point.X is >= -5000 and <= 5000 && point.Y is >= -5000 and <= 5000) &&
        points.Zip(points.Skip(1)).All(pair => pair.First != pair.Second);

    public static bool TryTriangulate(SketchPoint[]? points,
                                      out List<(int A, int B, int C)> triangles,
                                      out bool counterclockwise)
    {
        triangles = [];
        counterclockwise = false;
        if (points is null || points.Length is < 3 or > MaxVertices ||
            points.Any(point => point.X is < -5000 or > 5000 || point.Y is < -5000 or > 5000 ||
                                !double.IsFinite(point.X) || !double.IsFinite(point.Y)) ||
            points.Distinct().Count() != points.Length)
            return false;

        double area = 0;
        int count = points.Length;
        for (int i = 0; i < count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % count];
            var c = points[(i + 2) % count];
            if (Math.Abs(Cross(a, b, c)) <= Epsilon)
                return false;

            area += a.X * b.Y - a.Y * b.X;
            for (int j = i + 2; j < count; j++)
            {
                int next = (j + 1) % count;
                if (next != i && Intersects(a, b, points[j], points[next]))
                    return false;
            }
        }
        if (Math.Abs(area) <= Epsilon)
            return false;

        counterclockwise = area > 0;
        var remaining = Enumerable.Range(0, count).ToList();
        if (!counterclockwise)
            remaining.Reverse();

        while (remaining.Count > 3)
        {
            bool foundEar = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int a = remaining[(i + remaining.Count - 1) % remaining.Count];
                int b = remaining[i];
                int c = remaining[(i + 1) % remaining.Count];
                if (Cross(points[a], points[b], points[c]) <= Epsilon)
                    continue;

                bool containsVertex = remaining.Any(vertex => vertex != a && vertex != b && vertex != c &&
                    Cross(points[a], points[b], points[vertex]) >= -Epsilon &&
                    Cross(points[b], points[c], points[vertex]) >= -Epsilon &&
                    Cross(points[c], points[a], points[vertex]) >= -Epsilon);
                if (containsVertex)
                    continue;

                triangles.Add((a, b, c));
                remaining.RemoveAt(i);
                foundEar = true;
                break;
            }
            if (!foundEar)
                return false;
        }

        if (Cross(points[remaining[0]], points[remaining[1]], points[remaining[2]]) <= Epsilon)
            return false;
        triangles.Add((remaining[0], remaining[1], remaining[2]));
        return true;
    }

    private static double Cross(SketchPoint a, SketchPoint b, SketchPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool Intersects(SketchPoint a, SketchPoint b, SketchPoint c, SketchPoint d)
    {
        int abC = Side(Cross(a, b, c)), abD = Side(Cross(a, b, d));
        int cdA = Side(Cross(c, d, a)), cdB = Side(Cross(c, d, b));
        return (abC * abD < 0 && cdA * cdB < 0) ||
            (abC == 0 && Between(a, b, c)) || (abD == 0 && Between(a, b, d)) ||
            (cdA == 0 && Between(c, d, a)) || (cdB == 0 && Between(c, d, b));
    }

    private static int Side(double value) => Math.Abs(value) <= Epsilon ? 0 : Math.Sign(value);

    private static bool Between(SketchPoint a, SketchPoint b, SketchPoint p) =>
        p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon &&
        p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;
}
