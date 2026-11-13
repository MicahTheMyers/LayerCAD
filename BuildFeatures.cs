using System.Windows;
using System.Windows.Media.Media3D;

namespace LayerCAD;

internal sealed record FaceExtrusion(Part Source, Point3D Origin, Vector3D Normal, int Axis, bool Positive, int ProfileEdge = -1)
{
    public static FaceExtrusion? Find(Part part, Point3D origin, Vector3D normal)
    {
        if (part.Holes is not null || part.Threads is not null)
            return null;
        if (part.Kind == PartKind.Box && part.Bevel == 0 && part.BevelCuts is null)
        {
            if (Math.Abs(normal.X) > 0.999999)
                return new FaceExtrusion(part, origin, normal, 0, normal.X > 0);
            if (Math.Abs(normal.Y) > 0.999999)
                return new FaceExtrusion(part, origin, normal, 1, normal.Y > 0);
        }
        if (part.Kind is PartKind.Mesh or PartKind.Sketch || part.Bevel > 0 || part.BevelCuts is not null)
            return null;

        var extrusionAxis = part.Kind == PartKind.Extrusion
            ? part.SketchVertex(default, 1) - part.SketchVertex(default, 0)
            : new Vector3D(0, 0, 1);
        double facing = Vector3D.DotProduct(normal, extrusionAxis);
        if (Math.Abs(facing) > 0.999999)
            return new FaceExtrusion(part, origin, normal, 2, facing > 0);
        if (part.Kind != PartKind.Extrusion || part.Profile is not { } profile || Math.Abs(facing) > 1e-6)
            return null;

        var frame = ProfileFrame(part);
        var local = frame.Project(origin - part.SketchVertex(default, 0) - new Vector3D(part.X, part.Y, part.Z));
        var outward = frame.Project(normal);
        for (int i = 0; i < profile.Length; i++)
        {
            var a = profile[i];
            var b = profile[(i + 1) % profile.Length];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double along = ((local.X - a.X) * dx + (local.Y - a.Y) * dy) / length;
            if (along >= -1e-6 && along <= length + 1e-6 &&
                Math.Abs((local.X - a.X) * outward.X + (local.Y - a.Y) * outward.Y) < 1e-5 &&
                Math.Abs((dx * outward.X + dy * outward.Y) / length) < 1e-6 &&
                (IsCorner(profile[(i + profile.Length - 1) % profile.Length], a, b) ||
                 IsCorner(a, b, profile[(i + 2) % profile.Length])))
                return new FaceExtrusion(part, origin, normal, 3, true, i);
        }
        return null;

        static bool IsCorner(SketchPoint a, SketchPoint b, SketchPoint c)
        {
            var incoming = new Vector(b.X - a.X, b.Y - a.Y);
            var outgoing = new Vector(c.X - b.X, c.Y - b.Y);
            return Vector.Multiply(incoming, outgoing) < 0.995 * incoming.Length * outgoing.Length;
        }
    }

    public BuildChange? Preview(double amount, bool remove)
    {
        if (!Part.ValidDimension(amount))
            return null;
        double delta = remove ? -amount : amount;
        if (ProfileEdge >= 0)
            return PreviewProfileSide(delta, remove);
        Part result;
        Part region;
        if (Axis == 0)
        {
            result = Source with { Width = Source.Width + delta, X = Source.X + Normal.X * delta / 2 };
            region = Source with { Width = amount, X = Source.X + Normal.X * (Source.Width + delta) / 2 };
        }
        else if (Axis == 1)
        {
            result = Source with { Depth = Source.Depth + delta, Y = Source.Y + Normal.Y * delta / 2 };
            region = Source with { Depth = amount, Y = Source.Y + Normal.Y * (Source.Depth + delta) / 2 };
        }
        else
        {
            var direction = Positive ? Normal : -Normal;
            result = Translate(Source with { Height = Source.Height + delta }, Positive ? default : Normal * delta);
            double oldEnd = Positive ? Source.Height : 0;
            double newEnd = oldEnd + (Positive ? delta : -delta);
            region = Translate(Source with { Height = amount }, direction * Math.Min(oldEnd, newEnd));
        }

        if (!result.IsValid())
            return null;
        return new BuildChange(result, Geometry.Build(region), remove, Origin, Normal * (remove ? -1 : 1), amount);
    }

    public BuildChange? PreviewThroughCut(double amount)
    {
        if (!Part.ValidDimension(amount))
            return null;
        var preview = Preview(amount, remove: true);
        if (ProfileEdge < 0 && preview is not null)
            return preview;

        var inward = -Normal * amount;
        IReadOnlyList<Triangle> sweep;
        if (ProfileEdge >= 0)
        {
            var profile = Source.Profile!;
            var a = profile[ProfileEdge];
            var b = profile[(ProfileEdge + 1) % profile.Length];
            var offset = new Vector3D(Source.X, Source.Y, Source.Z);
            Point3D At(SketchPoint point, double depth) => Source.SketchVertex(point, depth) + offset;
            var rim = new[] { At(a, 0), At(b, 0), At(b, Source.Height), At(a, Source.Height) };
            if (Vector3D.DotProduct(Vector3D.CrossProduct(rim[1] - rim[0], rim[2] - rim[0]), Normal) < 0)
                Array.Reverse(rim);
            var faces = new List<Triangle>
            {
                new(rim[0], rim[1], rim[2]), new(rim[0], rim[2], rim[3]),
                new(rim[2] + inward, rim[1] + inward, rim[0] + inward),
                new(rim[3] + inward, rim[2] + inward, rim[0] + inward)
            };
            for (int i = 0; i < rim.Length; i++)
            {
                var start = rim[i];
                var end = rim[(i + 1) % rim.Length];
                faces.Add(new(end, start, start + inward));
                faces.Add(new(end, start + inward, end + inward));
            }
            sweep = faces;
        }
        else
        {
            var region = Axis switch
            {
                0 => Source with { Width = amount, X = Source.X + Normal.X * (Source.Width - amount) / 2 },
                1 => Source with { Depth = amount, Y = Source.Y + Normal.Y * (Source.Depth - amount) / 2 },
                _ => Translate(Source with { Height = amount }, Positive ? Normal * (Source.Height - amount) : default)
            };
            sweep = Geometry.Build(region);
        }
        return new BuildChange(preview?.Result ?? Source, sweep, true, Origin, -Normal, amount);
    }

    private BuildChange? PreviewProfileSide(double delta, bool remove)
    {
        var profile = Source.Profile!;
        int next = (ProfileEdge + 1) % profile.Length;
        var before = profile[(ProfileEdge + profile.Length - 1) % profile.Length];
        var a = profile[ProfileEdge];
        var b = profile[next];
        var after = profile[(next + 1) % profile.Length];
        var outward = ProfileFrame(Source).Project(Normal);
        var movedA = OffsetIntersection(a, before);
        var movedB = OffsetIntersection(b, after);
        if (movedA is not SketchPoint newA || movedB is not SketchPoint newB ||
            (newB.X - newA.X) * (b.X - a.X) + (newB.Y - newA.Y) * (b.Y - a.Y) <= 0)
            return null;

        var changed = (SketchPoint[])profile.Clone();
        changed[ProfileEdge] = newA;
        changed[next] = newB;
        var result = Source with { Profile = changed, SketchId = null, SketchProfileIndex = 0, Dimensions = null, Constraints = null };
        var region = result with { Profile = [a, b, newB, newA] };
        if (!result.IsValid() || !region.IsValid())
            return null;
        return new BuildChange(result, Geometry.Build(region), remove, Origin, Normal * (remove ? -1 : 1), Math.Abs(delta));

        SketchPoint? OffsetIntersection(SketchPoint corner, SketchPoint neighbor)
        {
            double x = neighbor.X - corner.X, y = neighbor.Y - corner.Y;
            double approach = x * outward.X + y * outward.Y;
            if (Math.Abs(approach) < 1e-8)
                return null;
            double travel = delta / approach;
            return new(corner.X + x * travel, corner.Y + y * travel);
        }
    }

    private static SketchFrame ProfileFrame(Part part)
    {
        var origin = part.SketchVertex(default, 0);
        return new SketchFrame(SketchAxis.FromVector(part.SketchVertex(new(1, 0), 0) - origin),
            SketchAxis.FromVector(part.SketchVertex(new(0, 1), 0) - origin));
    }

    private static Part Translate(Part part, Vector3D offset) =>
        part with { X = part.X + offset.X, Y = part.Y + offset.Y, Z = part.Z + offset.Z };
}

internal sealed record BuildChange(Part Result, IReadOnlyList<Triangle> Region, bool Removes,
                                   Point3D? ArrowStart = null, Vector3D ArrowDirection = default, double ArrowLength = 0,
                                   IReadOnlyDictionary<Part, Part>? MovedParts = null,
                                   IReadOnlyDictionary<Part, Part?>? CutParts = null,
                                   Part[]? MergedParts = null);

internal static class BoxBevel
{
    public static (List<Triangle> Flats, List<Triangle> Chamfers) Build(Part part, Vector3D offset)
    {
        double[] half = [part.Width / 2, part.Depth / 2, part.Height / 2];
        double[] inner = half.Select(extent => extent - part.Bevel).ToArray();
        var flats = new List<Triangle>(12);
        var chamfers = new List<Triangle>(32);
        offset.Z += half[2];

        for (int axis = 0; axis < 3; axis++)
        {
            int b = (axis + 1) % 3, c = (axis + 2) % 3;
            foreach (int sign in new[] { -1, 1 })
            {
                AddFace(flats, Axis(axis, sign), [
                    Point(axis, sign * half[axis], b, -inner[b], c, -inner[c]),
                    Point(axis, sign * half[axis], b, inner[b], c, -inner[c]),
                    Point(axis, sign * half[axis], b, inner[b], c, inner[c]),
                    Point(axis, sign * half[axis], b, -inner[b], c, inner[c])]);
            }
        }

        for (int a = 0; a < 3; a++)
        {
            for (int b = a + 1; b < 3; b++)
            {
                int c = 3 - a - b;
                foreach (int sa in new[] { -1, 1 })
                foreach (int sb in new[] { -1, 1 })
                {
                    AddFace(chamfers, Axis(a, sa) + Axis(b, sb), [
                        Point(a, sa * half[a], b, sb * inner[b], c, -inner[c]),
                        Point(a, sa * inner[a], b, sb * half[b], c, -inner[c]),
                        Point(a, sa * inner[a], b, sb * half[b], c, inner[c]),
                        Point(a, sa * half[a], b, sb * inner[b], c, inner[c])]);
                }
            }
        }

        foreach (int x in new[] { -1, 1 })
        foreach (int y in new[] { -1, 1 })
        foreach (int z in new[] { -1, 1 })
        {
            AddFace(chamfers, new Vector3D(x, y, z), [
                new Point3D(x * half[0], y * inner[1], z * inner[2]) + offset,
                new Point3D(x * inner[0], y * half[1], z * inner[2]) + offset,
                new Point3D(x * inner[0], y * inner[1], z * half[2]) + offset]);
        }
        return (flats, chamfers);

        Point3D Point(int a, double x, int b, double y, int c, double z) =>
            new Point3D() + Axis(a, x) + Axis(b, y) + Axis(c, z) + offset;
    }

    private static Vector3D Axis(int axis, double value) => axis switch
    {
        0 => new Vector3D(value, 0, 0),
        1 => new Vector3D(0, value, 0),
        _ => new Vector3D(0, 0, value)
    };

    private static void AddFace(List<Triangle> triangles, Vector3D outward, Point3D[] corners)
    {
        var normal = Vector3D.CrossProduct(corners[1] - corners[0], corners[2] - corners[0]);
        if (Vector3D.DotProduct(normal, outward) < 0)
            Array.Reverse(corners);
        for (int i = 1; i < corners.Length - 1; i++)
            triangles.Add(new Triangle(corners[0], corners[i], corners[i + 1]));
    }
}

