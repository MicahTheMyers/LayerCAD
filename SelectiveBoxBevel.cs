using System.Windows.Media.Media3D;
using System.Text.Json.Serialization;

namespace LayerCAD;

public enum ProfileBevelKind { None, Edge, VerticalEdge, Corner, Face, Cap }

public readonly record struct BoxBevelTarget(int X, int Y, int Z,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ProfileBevelKind ProfileKind = ProfileBevelKind.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ProfileIndex = 0)
{
    internal int AxisCount => ProfileKind switch
    {
        ProfileBevelKind.Edge or ProfileBevelKind.VerticalEdge => 2,
        ProfileBevelKind.Corner => 3,
        ProfileBevelKind.Face or ProfileBevelKind.Cap => 1,
        _ => Math.Abs(X) + Math.Abs(Y) + Math.Abs(Z)
    };
    internal bool IsCut => ProfileKind != ProfileBevelKind.None
        ? X == 0 && Y == 0 && ProfileIndex >= 0 && (ProfileKind switch
        {
            ProfileBevelKind.VerticalEdge => Z == 0,
            ProfileBevelKind.Edge or ProfileBevelKind.Corner => Math.Abs(Z) == 1,
            _ => false
        })
        : X is >= -1 and <= 1 && Y is >= -1 and <= 1 && Z is >= -1 and <= 1 && AxisCount is 2 or 3;

    internal Vector3D Direction => new(X, Y, Z);

    internal static BoxBevelTarget FromNormal(Vector3D normal) =>
        new(Sign(normal.X), Sign(normal.Y), Sign(normal.Z));

    internal bool OnFace(BoxBevelTarget face) =>
        (face.X == 0 || X == face.X) && (face.Y == 0 || Y == face.Y) && (face.Z == 0 || Z == face.Z);

    internal (BoxBevelTarget Start, BoxBevelTarget End) EdgeEnds() =>
        (new(X == 0 ? -1 : X, Y == 0 ? -1 : Y, Z == 0 ? -1 : Z),
         new(X == 0 ? 1 : X, Y == 0 ? 1 : Y, Z == 0 ? 1 : Z));

    internal static readonly BoxBevelTarget[] Faces = CreateTargets(1);
    internal static readonly BoxBevelTarget[] Edges = CreateTargets(2);
    internal static readonly BoxBevelTarget[] Corners = CreateTargets(3);

    private static int Sign(double value) => Math.Abs(value) > 1e-7 ? Math.Sign(value) : 0;

    private static BoxBevelTarget[] CreateTargets(int axes) =>
        (from x in Enumerable.Range(-1, 3)
         from y in Enumerable.Range(-1, 3)
         from z in Enumerable.Range(-1, 3)
         let target = new BoxBevelTarget(x, y, z)
         where target.AxisCount == axes
         select target).ToArray();
}

public readonly record struct BoxBevelCut(BoxBevelTarget Target, double Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Rounded = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Angle = null)
{
    internal double Slope => Angle is double degrees ? Math.Tan(degrees * Math.PI / 180) : 1;
    internal Vector3D FlatNormal => Target.Z != 0
        ? new(Target.X, Target.Y, Target.Z * Slope)
        : new(Target.X, Target.Y * Slope, 0);

    internal double FlatLimit(Part box)
    {
        var normal = FlatNormal;
        return Math.Abs(normal.X) * box.Width + Math.Abs(normal.Y) * box.Depth + Math.Abs(normal.Z) * box.Height;
    }
}

internal static class SelectiveBoxBevel
{
    public static List<Triangle> Build(Part part, Vector3D offset)
    {
        double x = part.Width / 2, y = part.Depth / 2, z = part.Height / 2;
        offset.Z += z;
        var faces = CreateBox(x, y, z);
        var cuts = new Dictionary<BoxBevelTarget, BoxBevelCut>();
        if (part.Bevel > 0)
        {
            foreach (var edge in BoxBevelTarget.Edges)
                cuts[edge] = new(edge, part.Bevel);
            foreach (var corner in BoxBevelTarget.Corners)
                cuts[corner] = new(corner, part.Bevel * 2);
        }
        foreach (var cut in part.BevelCuts!)
            cuts[cut.Target] = cut;

        foreach (var corner in BoxBevelTarget.Corners)
        {
            var adjacent = BoxBevelTarget.Edges.Where(edge => edge.OnFace(corner with { X = 0 }) ||
                edge.OnFace(corner with { Y = 0 }) || edge.OnFace(corner with { Z = 0 }))
                .Select(edge => cuts.GetValueOrDefault(edge)).ToArray();
            if (adjacent.All(edge => edge.Rounded && edge.Amount == adjacent[0].Amount))
                cuts.TryAdd(corner, new BoxBevelCut(corner, adjacent[0].Amount, true));
        }

        double tolerance = Math.Max(x, Math.Max(y, z)) * 1e-8;
        foreach (var (target, cut) in cuts.OrderBy(entry => entry.Key.AxisCount)
                     .ThenBy(entry => entry.Key.X).ThenBy(entry => entry.Key.Y).ThenBy(entry => entry.Key.Z))
        {
            if (!cut.Rounded)
            {
                var normal = cut.FlatNormal;
                double limit = Math.Abs(normal.X) * x + Math.Abs(normal.Y) * y + Math.Abs(normal.Z) * z - cut.Amount;
                double length = normal.Length;
                faces = Clip(faces, normal / length, limit / length, tolerance);
                continue;
            }
            var center = new Vector3D(target.X * (x - cut.Amount), target.Y * (y - cut.Amount), target.Z * (z - cut.Amount));
            foreach (var normal in RoundNormals(target))
                faces = Clip(faces, normal, Vector3D.DotProduct(normal, center) + cut.Amount, tolerance);
        }

        var triangles = new List<Triangle>();
        foreach (var face in faces)
        {
            // Preserve every boundary segment shared with an adjoining face.
            var center = Center(face);
            for (int i = 0; i < face.Count; i++)
                triangles.Add(new Triangle(center + offset, face[i] + offset, face[(i + 1) % face.Count] + offset));
        }
        return triangles;
    }

    public static bool HasRemainingSolid(Part box)
    {
        var mesh = Build(box, default);
        if (mesh.Count < 4 || mesh.Any(triangle => !triangle.IsValid()))
            return false;
        var origin = mesh[0].A;
        double volume = mesh.Sum(triangle => Vector3D.DotProduct(triangle.A - origin,
            Vector3D.CrossProduct(triangle.B - origin, triangle.C - origin))) / 6;
        return volume > box.Width * box.Depth * box.Height * 1e-10;
    }

    private static IEnumerable<Vector3D> RoundNormals(BoxBevelTarget target)
    {
        if (target.AxisCount == 2)
        {
            var axes = new[] { new Vector3D(target.X, 0, 0), new Vector3D(0, target.Y, 0), new Vector3D(0, 0, target.Z) }
                .Where(axis => axis.LengthSquared > 0).ToArray();
            const int segments = 24;
            for (int i = 1; i < segments; i++)
            {
                double angle = i * Math.PI / (2 * segments);
                yield return axes[0] * Math.Cos(angle) + axes[1] * Math.Sin(angle);
            }
            yield break;
        }
        const int divisions = 16;
        for (int x = 1; x < divisions - 1; x++)
        {
            for (int y = 1; y < divisions - x; y++)
            {
                var normal = new Vector3D(x * target.X, y * target.Y, (divisions - x - y) * target.Z);
                normal.Normalize();
                yield return normal;
            }
        }
    }
    private static List<List<Point3D>> CreateBox(double x, double y, double z)
    {
        var a = new Point3D(-x, -y, -z);
        var b = new Point3D(x, -y, -z);
        var c = new Point3D(x, y, -z);
        var d = new Point3D(-x, y, -z);
        var e = new Point3D(-x, -y, z);
        var f = new Point3D(x, -y, z);
        var g = new Point3D(x, y, z);
        var h = new Point3D(-x, y, z);
        return [[d, c, b, a], [e, f, g, h], [a, b, f, e], [b, c, g, f], [c, d, h, g], [d, a, e, h]];
    }

    internal static List<List<Point3D>> Clip(List<List<Point3D>> faces, Vector3D normal, double limit, double tolerance)
    {
        if (!faces.Any(face => face.Any(point => Distance(point) > 0)))
            return faces;

        var clipped = new List<List<Point3D>>();
        var cap = new HashSet<Point3D>();
        var intersections = new Dictionary<(Point3D, Point3D), Point3D>();
        foreach (var face in faces)
        {
            var result = new List<Point3D>();
            var last = face[^1];
            double lastDistance = Distance(last);
            foreach (var point in face)
            {
                double distance = Distance(point);
                if ((lastDistance < 0 && distance > 0) || (lastDistance > 0 && distance < 0))
                {
                    if (!intersections.TryGetValue((last, point), out var intersection))
                    {
                        intersection = last + (point - last) * (lastDistance / (lastDistance - distance));
                        intersections[(last, point)] = intersection;
                        intersections[(point, last)] = intersection;
                    }
                    result.Add(intersection);
                    cap.Add(intersection);
                }
                if (distance <= 0)
                    result.Add(point);
                if (distance == 0)
                    cap.Add(point);
                last = point;
                lastDistance = distance;
            }
            if (result.Count >= 3)
                clipped.Add(result);
        }

        if (cap.Count >= 3)
        {
            var center = Center(cap);
            var u = Vector3D.CrossProduct(new Vector3D(0, 0, 1), normal);
            u.Normalize();
            var v = Vector3D.CrossProduct(normal, u);
            v.Normalize();
            clipped.Add(cap.OrderBy(point => Math.Atan2(Vector3D.DotProduct(point - center, v),
                                                         Vector3D.DotProduct(point - center, u))).ToList());
        }
        return clipped;

        double Distance(Point3D point)
        {
            double distance = Vector3D.DotProduct((Vector3D)point, normal) - limit;
            return Math.Abs(distance) <= tolerance ? 0 : distance;
        }
    }

    private static Point3D Center(IEnumerable<Point3D> points)
    {
        var total = new Vector3D();
        int count = 0;
        foreach (var point in points)
        {
            total += (Vector3D)point;
            count++;
        }
        return new Point3D() + total / count;
    }
}

