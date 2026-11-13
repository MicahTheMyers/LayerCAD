using System.Windows.Media.Media3D;

namespace LayerCAD;

internal static class ModelingMeshes
{
    private const int RingSegments = 96;

    public static List<Triangle> Sphere(double radius)
    {
        const int latitudes = 48;
        var sections = new Point3D[latitudes + 1][];
        for (int i = 0; i <= latitudes; i++)
        {
            double angle = Math.PI * i / latitudes;
            double radial = i is 0 or latitudes ? 0 : radius * Math.Sin(angle);
            double z = radius * (1 - Math.Cos(angle));
            sections[i] = Ring(radial).Select(point => new Point3D(point.X, point.Y, z)).ToArray();
        }
        return Connect(sections);
    }

    public static List<Triangle> Torus(double centerRadius, double tubeRadius)
    {
        var sections = new Point3D[RingSegments][];
        for (int i = 0; i < sections.Length; i++)
        {
            double angle = Math.Tau * i / sections.Length;
            sections[i] = Ring(tubeRadius, 48).Select(point =>
                new Point3D((centerRadius + point.X) * Math.Cos(angle),
                    (centerRadius + point.X) * Math.Sin(angle), tubeRadius + point.Y)).ToArray();
        }
        return Connect(sections, closed: true);
    }

    public static List<Triangle>? Revolve(Part sketch, SketchPoint[] profile, bool horizontal, double degrees)
    {
        if (degrees is <= 0 or > 360 || !SketchGeometry.TryTriangulate(profile, out var cap, out bool counterclockwise))
            return null;
        var radial = profile.Select(point => horizontal ? point.Y : point.X).ToArray();
        if (radial.Min() < 0 && radial.Max() > 0)
            return null;

        bool closed = degrees == 360;
        int steps = Math.Max(1, (int)Math.Ceiling(degrees * RingSegments / 360));
        var origin = At(sketch, default);
        var axis = At(sketch, horizontal ? new(1, 0) : new(0, 1)) - origin;
        var sections = new Point3D[steps + (closed ? 0 : 1)][];
        for (int step = 0; step < sections.Length; step++)
        {
            var rotation = new RotateTransform3D(new AxisAngleRotation3D(axis, degrees * step / steps), origin);
            sections[step] = profile.Select((point, i) => radial[i] == 0 ? At(sketch, point) : rotation.Transform(At(sketch, point))).ToArray();
        }
        return Connect(sections, closed, cap, reverseCap: !counterclockwise);
    }

    public static List<Triangle>? Sweep(Part pathSketch, SketchPoint[] path, SketchPoint[] profile, bool centerProfile)
    {
        if (!SketchGeometry.TryTriangulate(profile, out var cap, out bool counterclockwise))
            return null;
        var center = centerProfile ? Center(profile) : default;
        var outline = profile.Select(point => new SketchPoint(point.X - center.X, point.Y - center.Y)).ToArray();
        var sections = PathSections(pathSketch, path, outline);
        return sections is null ? null : Connect(sections, cap: cap, reverseCap: !counterclockwise);
    }

    public static List<Triangle>? Pipe(Part sketch, SketchPoint[] path, double radius, double wall)
    {
        var outer = PathSections(sketch, path, Ring(radius, 48));
        var inner = PathSections(sketch, path, Ring(radius - wall, 48));
        if (outer is null || inner is null)
            return null;
        var mesh = Connect(outer, orient: false);
        mesh.AddRange(Connect(inner, orient: false).Select(Reverse));
        Bridge(mesh, inner[0], outer[0]);
        Bridge(mesh, outer[^1], inner[^1]);
        Orient(mesh);
        return mesh;
    }

    public static List<Triangle> Coil(double radius, double wireRadius, double pitch, double turns)
    {
        int steps = (int)Math.Ceiling(turns * RingSegments);
        var wire = Ring(wireRadius, 24);
        var sections = new Point3D[steps + 1][];
        for (int i = 0; i <= steps; i++)
        {
            double angle = Math.Tau * turns * i / steps;
            var radial = new Vector3D(Math.Cos(angle), Math.Sin(angle), 0);
            var tangent = new Vector3D(-radial.Y * radius, radial.X * radius, pitch / Math.Tau);
            tangent.Normalize();
            var up = Vector3D.CrossProduct(tangent, radial);
            var center = new Point3D() + radius * radial + new Vector3D(0, 0, pitch * turns * i / steps);
            sections[i] = wire.Select(point => center + radial * point.X + up * point.Y).ToArray();
        }
        return Connect(sections, fanCaps: true);
    }

    public static List<Triangle>? Loft((Part Sketch, SketchPoint[] Profile)[] profiles)
    {
        if (profiles.Length < 2)
            return null;
        var origin = At(profiles[0].Sketch, default);
        var axis = Normal(profiles[0].Sketch);
        var horizontal = At(profiles[0].Sketch, new(1, 0)) - origin;
        var vertical = Vector3D.CrossProduct(axis, horizontal);
        var levels = new List<(double Height, Point3D[] World, List<(int A, int B, int C)> Cap)>();
        foreach (var (sketch, profile) in profiles)
        {
            if (Math.Abs(Vector3D.DotProduct(axis, Normal(sketch))) < 1 - 1e-7 ||
                !SketchGeometry.TryTriangulate(profile, out var cap, out bool ccw))
                return null;
            bool sameNormal = Vector3D.DotProduct(axis, Normal(sketch)) > 0;
            bool reverseOutline = ccw != sameNormal;
            var ordered = reverseOutline ? profile.Reverse().ToArray() : profile;
            var world = ordered.Select(point => At(sketch, point)).ToArray();
            int seam = Enumerable.Range(0, world.Length)
                .OrderByDescending(i => Vector3D.DotProduct(world[i] - origin, horizontal))
                .ThenByDescending(i => Vector3D.DotProduct(world[i] - origin, vertical)).First();
            world = world.Skip(seam).Concat(world.Take(seam)).ToArray();
            int Index(int original)
            {
                int orderedIndex = reverseOutline ? world.Length - 1 - original : original;
                return (orderedIndex - seam + world.Length) % world.Length;
            }
            var orientedCap = cap.Select(triangle => sameNormal
                ? (Index(triangle.A), Index(triangle.B), Index(triangle.C))
                : (Index(triangle.A), Index(triangle.C), Index(triangle.B))).ToList();
            levels.Add((Vector3D.DotProduct(world[0] - origin, axis), world, orientedCap));
        }
        levels.Sort((a, b) => a.Height.CompareTo(b.Height));
        if (levels.Zip(levels.Skip(1)).Any(pair => pair.Second.Height - pair.First.Height < 0.001))
            return null;
        var fraction = levels.Select(level => Perimeter(level.World)).ToArray();
        var stations = new List<double>();
        foreach (double value in fraction.SelectMany(values => values.Take(values.Length - 1)).Order())
            if (stations.Count == 0 || value - stations[^1] > 1e-12)
                stations.Add(value);
        var sections = levels.Select((level, i) => stations.Select(t => Sample(level.World, fraction[i], t)).ToArray()).ToArray();
        var mesh = Connect(sections, orient: false);
        Cap(0, facingUp: false);
        Cap(sections.Length - 1, facingUp: true);
        Orient(mesh);
        return mesh;

        void Cap(int level, bool facingUp)
        {
            var section = sections[level];
            var outline = levels[level].World;
            int[] vertexIndices = fraction[level].Take(outline.Length).Select(t =>
            {
                int index = stations.BinarySearch(t);
                return index >= 0 ? index : ~index - 1;
            }).ToArray();
            foreach (var (a, b, c) in levels[level].Cap)
            {
                var center = outline[a] + ((outline[b] - outline[a]) + (outline[c] - outline[a])) / 3;
                AddEdge(a, b, center);
                AddEdge(b, c, center);
                AddEdge(c, a, center);
            }

            void AddEdge(int a, int b, Point3D center)
            {
                int start = vertexIndices[a], end = vertexIndices[b];
                if (b != (a + 1) % outline.Length)
                {
                    AddFacet(section[start], section[end], center);
                    return;
                }
                for (int i = start; i != end; i = (i + 1) % section.Length)
                    AddFacet(section[i], section[(i + 1) % section.Length], center);
            }

            void AddFacet(Point3D a, Point3D b, Point3D center) =>
                Add(mesh, facingUp ? new(center, a, b) : new(center, b, a));
        }
    }

    public static Triangle[]? Join(IReadOnlyList<List<Triangle>> walls)
    {
        var pending = walls.Skip(1).ToList();
        var combined = SolidBoolean.Create(walls[0]);
        try
        {
            if (combined.IsInvalid)
                return null;
            while (pending.Count > 0)
            {
                bool joined = false;
                for (int i = 0; i < pending.Count; i++)
                {
                    using var wall = SolidBoolean.Create(pending[i]);
                    if (wall.IsInvalid)
                        return null;
                    int outcome = SolidBoolean.Apply(combined, wall, merge: 1, out var result);
                    if (outcome != 1)
                    {
                        result.Dispose();
                        continue;
                    }
                    combined.Dispose();
                    combined = result;
                    pending.RemoveAt(i);
                    joined = true;
                    break;
                }
                if (!joined)
                    return null;
            }
            return SolidBoolean.ReadMesh(combined, default);
        }
        finally
        {
            combined.Dispose();
        }
    }

    private static double[] Perimeter(Point3D[] points)
    {
        var cumulative = new double[points.Length + 1];
        for (int i = 0; i < points.Length; i++)
            cumulative[i + 1] = cumulative[i] + (points[(i + 1) % points.Length] - points[i]).Length;
        double total = cumulative[^1];
        return cumulative.Select(distance => distance / total).ToArray();
    }

    private static Point3D Sample(Point3D[] points, double[] stations, double t)
    {
        int index = Array.BinarySearch(stations, t);
        if (index >= 0)
            return points[index % points.Length];
        index = ~index - 1;
        if (index + 1 < points.Length && stations[index + 1] - t <= 1e-12)
            return points[index + 1];
        return points[index] + (points[(index + 1) % points.Length] - points[index]) *
            ((t - stations[index]) / (stations[index + 1] - stations[index]));
    }

    private static Point3D[][]? PathSections(Part sketch, SketchPoint[] path, SketchPoint[] profile)
    {
        if (!SketchGeometry.ValidOpenPath(path) || path[0] == path[^1])
            return null;
        var points = path.Select(point => At(sketch, point)).ToArray();
        var normal = Normal(sketch);
        var directions = points.Zip(points.Skip(1)).Select(pair => Unit(pair.Second - pair.First)).ToArray();
        var sections = new Point3D[points.Length][];
        for (int i = 0; i < points.Length; i++)
        {
            var incoming = directions[Math.Max(0, i - 1)];
            var outgoing = directions[Math.Min(i, directions.Length - 1)];
            if (Vector3D.DotProduct(incoming, outgoing) <= -0.95)
                return null;
            var across = Unit(Vector3D.CrossProduct(normal, incoming + outgoing));
            var miter = across / Vector3D.DotProduct(across, Vector3D.CrossProduct(normal, incoming));
            var point = points[i];
            sections[i] = profile.Select(p => point + miter * p.X + normal * p.Y).ToArray();
            if (i > 0 && sections[i].Zip(sections[i - 1]).Any(pair =>
                Vector3D.DotProduct(pair.First - pair.Second, incoming) <= 1e-7))
                return null;
        }
        return sections;
    }

    private static List<Triangle> Connect(Point3D[][] sections, bool closed = false,
        List<(int A, int B, int C)>? cap = null, bool reverseCap = false, bool fanCaps = false, bool orient = true)
    {
        var mesh = new List<Triangle>();
        for (int i = 0; i < sections.Length - (closed ? 0 : 1); i++)
            Bridge(mesh, sections[i], sections[(i + 1) % sections.Length]);
        if (!closed && cap is not null)
        {
            var start = sections[0];
            var end = sections[^1];
            foreach (var (a, b, c) in cap)
            {
                Add(mesh, reverseCap ? new(start[a], start[b], start[c]) : new(start[c], start[b], start[a]));
                Add(mesh, reverseCap ? new(end[c], end[b], end[a]) : new(end[a], end[b], end[c]));
            }
        }
        if (!closed && fanCaps)
        {
            var start = sections[0];
            var end = sections[^1];
            var firstCenter = Center(start);
            var lastCenter = Center(end);
            for (int i = 0; i < start.Length; i++)
            {
                int next = (i + 1) % start.Length;
                Add(mesh, new(firstCenter, start[next], start[i]));
                Add(mesh, new(lastCenter, end[i], end[next]));
            }
        }
        if (orient)
            Orient(mesh);
        return mesh;
    }

    private static void Bridge(List<Triangle> mesh, Point3D[] first, Point3D[] second)
    {
        for (int i = 0; i < first.Length; i++)
        {
            int next = (i + 1) % first.Length;
            Add(mesh, new(first[i], first[next], second[next]));
            Add(mesh, new(first[i], second[next], second[i]));
        }
    }

    private static void Add(List<Triangle> mesh, Triangle triangle)
    {
        if (triangle.IsValid())
            mesh.Add(triangle);
    }

    private static void Orient(List<Triangle> mesh)
    {
        if (mesh.Count == 0)
            return;
        var origin = mesh[0].A;
        if (mesh.Sum(t => Vector3D.DotProduct(t.A - origin, Vector3D.CrossProduct(t.B - origin, t.C - origin))) < 0)
            for (int i = 0; i < mesh.Count; i++)
                mesh[i] = Reverse(mesh[i]);
    }

    internal static Triangle Reverse(Triangle triangle) => new(triangle.A, triangle.C, triangle.B);
    private static Vector3D Unit(Vector3D direction) { direction.Normalize(); return direction; }
    private static Point3D Center(Point3D[] points) => new(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z));
    private static SketchPoint Center(SketchPoint[] points) => new(points.Average(p => p.X), points.Average(p => p.Y));
    private static SketchPoint[] Ring(double radius, int count = RingSegments) => Enumerable.Range(0, count).Select(i =>
        new SketchPoint(radius * Math.Cos(Math.Tau * i / count), radius * Math.Sin(Math.Tau * i / count))).ToArray();
    internal static Point3D At(Part sketch, SketchPoint point) =>
        sketch.SketchVertex(point, 0) + new Vector3D(sketch.X, sketch.Y, sketch.Z);
    private static Vector3D Normal(Part sketch) => sketch.SketchVertex(default, 1) - sketch.SketchVertex(default, 0);
}
