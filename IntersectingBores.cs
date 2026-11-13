using System.Windows.Media.Media3D;

namespace LayerCAD;

internal static class IntersectingBores
{
    private const double Tolerance = 1e-7;

    private readonly record struct Plane(Vector3D Normal, Point3D Origin)
    {
        public double Distance(Point3D point) => Vector3D.DotProduct(point - Origin, Normal);
    }

    private sealed record Volume(Plane[] Planes, Rect3D Bounds);

    public static List<Triangle> Combine(List<Triangle> stock, List<Triangle>[] walls, HoleCut[] holes,
        Dictionary<HoleAxis, HolePrism> prisms, SketchPoint[][] rings, Vector3D offset)
    {
        var volumes = new Volume?[holes.Length];
        bool crossing = false;
        for (int i = 0; i < holes.Length; i++)
        {
            List<Volume> cutters = [];
            for (int j = 0; j < holes.Length; j++)
            {
                if (holes[i].Axis == holes[j].Axis ||
                    HoleGeometry.HoleDistance(prisms[holes[i].Axis], holes[i], prisms[holes[j].Axis], holes[j]) >=
                    (holes[i].Diameter + holes[j].Diameter) / 2)
                    continue;
                cutters.Add(volumes[j] ??= CreateVolume(prisms[holes[j].Axis], holes[j], rings[j], offset));
            }
            if (cutters.Count == 0)
            {
                stock.AddRange(walls[i]);
                continue;
            }
            crossing = true;
            foreach (var triangle in walls[i])
            {
                var bounds = new Rect3D(triangle.A, new Size3D());
                bounds.Union(triangle.B);
                bounds.Union(triangle.C);
                List<List<Point3D>> pieces = [[triangle.A, triangle.B, triangle.C]];
                foreach (var cutter in cutters.Where(cutter => cutter.Bounds.IntersectsWith(bounds)))
                    pieces = pieces.SelectMany(piece => Subtract(piece, cutter)).ToList();
                foreach (var piece in pieces)
                    for (int j = 2; j < piece.Count; j++)
                        AddTriangle(stock, piece[0], piece[j - 1], piece[j]);
            }
        }
        return crossing ? Stitch(stock) : stock;
    }

    private static Volume CreateVolume(HolePrism prism, HoleCut hole, SketchPoint[] ring, Vector3D offset)
    {
        double low = hole.Through || !hole.FromEnd ? 0 : prism.Thickness - hole.Depth;
        double high = hole.Through || hole.FromEnd ? prism.Thickness : hole.Depth;
        var center = prism.Vertex(hole.Center, low, offset);
        var axis = prism.Frame.Normal;
        var bounds = Rect3D.Empty;
        var planes = new List<Plane> { new(-axis, center), new(axis, prism.Vertex(hole.Center, high, offset)) };
        for (int i = 0; i < ring.Length; i++)
        {
            var start = prism.Vertex(ring[i], low, offset);
            var end = prism.Vertex(ring[(i + 1) % ring.Length], low, offset);
            var normal = (start - center) + (end - center);
            normal.Normalize();
            planes.Add(new Plane(normal, start));
            bounds.Union(start);
            bounds.Union(prism.Vertex(ring[i], high, offset));
        }
        return new Volume(planes.ToArray(), bounds);
    }

    private static IEnumerable<List<Point3D>> Subtract(List<Point3D> polygon, Volume cutter)
    {
        if (cutter.Planes.Any(plane => polygon.All(point => plane.Distance(point) >= -Tolerance)))
        {
            yield return polygon;
            yield break;
        }
        foreach (var plane in cutter.Planes)
        {
            if (polygon.All(point => plane.Distance(point) <= Tolerance))
                continue;
            var inside = new List<Point3D>();
            var outside = new List<Point3D>();
            var previous = polygon[^1];
            double before = SignedDistance(previous);
            foreach (var point in polygon)
            {
                double current = SignedDistance(point);
                if ((before < 0 && current > 0) || (before > 0 && current < 0))
                {
                    var intersection = previous + (point - previous) * (before / (before - current));
                    inside.Add(intersection);
                    outside.Add(intersection);
                }
                if (current <= 0)
                    inside.Add(point);
                if (current >= 0)
                    outside.Add(point);
                previous = point;
                before = current;
            }
            if (outside.Count >= 3)
                yield return outside;
            if (inside.Count < 3)
                yield break;
            polygon = inside;

            double SignedDistance(Point3D point)
            {
                double distance = plane.Distance(point);
                return Math.Abs(distance) < Tolerance ? 0 : distance;
            }
        }
    }

    private static List<Triangle> Stitch(List<Triangle> triangles)
    {
        var cells = new Dictionary<(long, long, long), List<Point3D>>();
        var welded = triangles.Select(triangle => new Triangle(Weld(triangle.A), Weld(triangle.B), Weld(triangle.C))).ToArray();
        var vertices = cells.Values.SelectMany(points => points).ToArray();
        var sorted = Enumerable.Range(0, 3).Select(axis => vertices.OrderBy(point => Coordinate(point, axis)).ToArray()).ToArray();
        var result = new List<Triangle>();
        foreach (var (a, b, c) in welded)
        {
            if (Vector3D.CrossProduct(b - a, c - a).LengthSquared < 1e-20)
                continue;
            var perimeter = new List<Point3D>();
            Edge(a, b, perimeter);
            Edge(b, c, perimeter);
            Edge(c, a, perimeter);
            if (perimeter.Count == 3)
                AddTriangle(result, a, b, c);
            else
            {
                var center = a + ((b - a) + (c - a)) / 3;
                for (int i = 0; i < perimeter.Count; i++)
                    AddTriangle(result, center, perimeter[i], perimeter[(i + 1) % perimeter.Count]);
            }
        }
        return result;

        Point3D Weld(Point3D point)
        {
            long x = Cell(point.X), y = Cell(point.Y), z = Cell(point.Z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
                if (cells.TryGetValue((x + dx, y + dy, z + dz), out var bucket))
                    foreach (var candidate in bucket)
                        if ((candidate - point).LengthSquared <= Tolerance * Tolerance)
                            return candidate;
            if (!cells.TryGetValue((x, y, z), out var points))
                cells[(x, y, z)] = points = [];
            points.Add(point);
            return point;
        }

        void Edge(Point3D start, Point3D end, List<Point3D> perimeter)
        {
            perimeter.Add(start);
            var direction = end - start;
            double squared = direction.LengthSquared;
            if (squared <= Tolerance * Tolerance)
                return;
            int axis = Math.Abs(direction.X) >= Math.Abs(direction.Y) && Math.Abs(direction.X) >= Math.Abs(direction.Z) ? 0 :
                Math.Abs(direction.Y) >= Math.Abs(direction.Z) ? 1 : 2;
            double lower = Math.Min(Coordinate(start, axis), Coordinate(end, axis)) - Tolerance;
            double upper = Math.Max(Coordinate(start, axis), Coordinate(end, axis)) + Tolerance;
            var array = sorted[axis];
            int left = 0, right = array.Length;
            while (left < right)
            {
                int mid = (left + right) / 2;
                if (Coordinate(array[mid], axis) < lower)
                    left = mid + 1;
                else
                    right = mid;
            }
            List<(double Along, Point3D Point)> matches = [];
            double endTolerance = Tolerance / Math.Sqrt(squared);
            for (int i = left; i < array.Length && Coordinate(array[i], axis) <= upper; i++)
            {
                var point = array[i];
                double along = Vector3D.DotProduct(point - start, direction) / squared;
                if (along > endTolerance && along < 1 - endTolerance &&
                    (point - (start + direction * along)).LengthSquared <= Tolerance * Tolerance)
                    matches.Add((along, point));
            }
            perimeter.AddRange(matches.OrderBy(match => match.Along).Select(match => match.Point));
        }
    }

    private static long Cell(double value) => (long)Math.Floor(value / Tolerance);
    private static double Coordinate(Point3D point, int axis) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;

    private static void AddTriangle(List<Triangle> mesh, Point3D a, Point3D b, Point3D c)
    {
        if (Vector3D.CrossProduct(b - a, c - a).LengthSquared > 1e-20)
            mesh.Add(new Triangle(a, b, c));
    }
}
