using System.Windows.Media.Media3D;

namespace LayerCAD;

internal sealed class PolygonBevel
{
    private readonly Part part;
    private readonly SketchPoint[] outline;
    private readonly Vector3D[] sides;
    private readonly bool ccw;
    private readonly SketchFrame frame;

    public int Count => outline.Length;
    public double Limit { get; }
    public static BoxBevelTarget Target(ProfileBevelKind kind, int index, int end = 0) => new(0, 0, end, kind, index);

    private PolygonBevel(Part part, SketchPoint[] profile, bool ccw)
    {
        this.part = part;
        outline = profile;
        this.ccw = ccw;
        var origin = part.SketchVertex(default, 0);
        frame = new SketchFrame(SketchAxis.FromVector(part.SketchVertex(new(1, 0), 0) - origin),
            SketchAxis.FromVector(part.SketchVertex(new(0, 1), 0) - origin));
        sides = new Vector3D[Count];
        for (int i = 0; i < Count; i++)
        {
            var a = outline[i];
            var b = outline[Next(i)];
            sides[i] = new Vector3D(b.Y - a.Y, a.X - b.X, 0) * (ccw ? 1 : -1);
            sides[i].Normalize();
        }
        Limit = Math.Min(part.Height, Math.Min(profile.Max(p => p.X) - profile.Min(p => p.X),
            profile.Max(p => p.Y) - profile.Min(p => p.Y))) / 2;
    }

    public static PolygonBevel? Create(Part part)
    {
        if (part.Kind != PartKind.Extrusion || part.Profile is not { Length: >= 3 and < 96 } profile ||
            part.Bevel != 0 || part.BevelBox() is not null)
            return null;
        double sign = 0;
        for (int i = 0; i < profile.Length; i++)
        {
            var a = profile[i];
            var b = profile[(i + 1) % profile.Length];
            var c = profile[(i + 2) % profile.Length];
            double turn = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (!double.IsFinite(turn) || Math.Abs(turn) < 1e-9 || sign != 0 && Math.Sign(turn) != sign)
                return null;
            sign = Math.Sign(turn);
        }
        return new PolygonBevel(part, profile, sign > 0);
    }

    public IEnumerable<BoxBevelTarget> Edges()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return Target(ProfileBevelKind.VerticalEdge, i);
            yield return Target(ProfileBevelKind.Edge, i, -1);
            yield return Target(ProfileBevelKind.Edge, i, 1);
        }
    }

    public IEnumerable<BoxBevelTarget> Corners()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return Target(ProfileBevelKind.Corner, i, -1);
            yield return Target(ProfileBevelKind.Corner, i, 1);
        }
    }

    public IEnumerable<BoxBevelTarget> Perimeter(BoxBevelTarget face) => face.ProfileKind switch
    {
        ProfileBevelKind.Cap => Enumerable.Range(0, Count).Select(index => Target(ProfileBevelKind.Edge, index, face.Z)),
        ProfileBevelKind.Face => [Target(ProfileBevelKind.Edge, face.ProfileIndex, -1),
            Target(ProfileBevelKind.Edge, face.ProfileIndex, 1), Target(ProfileBevelKind.VerticalEdge, face.ProfileIndex),
            Target(ProfileBevelKind.VerticalEdge, Next(face.ProfileIndex))],
        _ => [face]
    };

    public BoxBevelTarget Face(Vector3D normal)
    {
        double axial = Vector3D.DotProduct(normal, frame.Normal);
        if (Math.Abs(axial) > 0.999999)
            return Target(ProfileBevelKind.Cap, 0, axial > 0 ? 1 : -1);
        int side = Enumerable.Range(0, Count).MaxBy(index => Vector3D.DotProduct(normal, Direction(sides[index])));
        return Target(ProfileBevelKind.Face, side);
    }

    public (Point3D Start, Point3D End) Segment(BoxBevelTarget target)
    {
        var start = Local(target.ProfileIndex, target.Z);
        return (World(start), World(target.ProfileKind == ProfileBevelKind.VerticalEdge
            ? Local(target.ProfileIndex, 1) : Local(Next(target.ProfileIndex), target.Z)));
    }

    public Point3D Corner(BoxBevelTarget target) => World(Local(target.ProfileIndex, target.Z));

    public (Point3D Origin, Vector3D Direction, double Length) Arrow(BoxBevelCut cut)
    {
        var target = cut.Target;
        var normal = FlatNormal(cut);
        var (start, end) = Segment(target);
        var origin = target.ProfileKind == ProfileBevelKind.Corner ? start : start + (end - start) / 2;
        return (origin, -Direction(normal) / normal.Length, Setback(cut) / normal.Length);
    }

    public double MaxAmount(BoxBevelCut cut)
    {
        if (cut.Rounded)
            return Limit;
        var normal = FlatNormal(cut);
        var corner = Local(cut.Target.ProfileIndex, cut.Target.Z);
        double lower = outline.Min(point => point.X * normal.X + point.Y * normal.Y) + Math.Min(0, part.Height * normal.Z);
        return (Vector3D.DotProduct((Vector3D)corner, normal) - lower) / Setback(cut with { Amount = 1 });
    }

    public List<Triangle> Build(bool positioned)
    {
        var order = ccw ? Enumerable.Range(0, Count).ToArray() : Enumerable.Range(0, Count).Reverse().ToArray();
        List<List<Point3D>> faces = [order.Reverse().Select(i => Local(i, -1)).ToList(), order.Select(i => Local(i, 1)).ToList()];
        for (int i = 0; i < Count; i++)
        {
            int a = order[i], b = order[(i + 1) % Count];
            faces.Add([Local(a, -1), Local(b, -1), Local(b, 1), Local(a, 1)]);
        }
        foreach (var cut in part.BevelCuts!.OrderBy(cut => cut.Target.AxisCount))
            foreach (var (normal, limit) in Planes(cut))
                faces = SelectiveBoxBevel.Clip(faces, normal, limit, Math.Max(1e-9, Limit * 1e-8));

        var bounds = part.LocalBounds();
        var offset = positioned ? new Vector3D(part.X, part.Y, part.Z) :
            new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
        var mesh = new List<Triangle>();
        foreach (var face in faces)
        {
            var center = new Point3D(face.Average(p => p.X), face.Average(p => p.Y), face.Average(p => p.Z));
            for (int i = 0; i < face.Count; i++)
                mesh.Add(new Triangle(Vertex(center) + offset, Vertex(face[i]) + offset, Vertex(face[(i + 1) % face.Count]) + offset));
        }
        return mesh;
    }

    public bool HasRemainingSolid()
    {
        var mesh = Build(false);
        return mesh.Count >= 4 && mesh.All(triangle => triangle.IsValid()) &&
            mesh.Sum(triangle => Vector3D.DotProduct((Vector3D)triangle.A,
                Vector3D.CrossProduct((Vector3D)triangle.B, (Vector3D)triangle.C))) > 1e-8;
    }

    public Func<Triangle, bool> Highlight(IEnumerable<BoxBevelCut> cuts)
    {
        var planes = cuts.SelectMany(Planes).ToArray();
        var origin = World(default);
        return triangle =>
        {
            var normal = Vector3D.CrossProduct(triangle.B - triangle.A, triangle.C - triangle.A);
            normal.Normalize();
            var local = frame.Project(triangle.A - origin);
            var point = new Vector3D(local.X, local.Y, Vector3D.DotProduct(triangle.A - origin, frame.Normal));
            return planes.Any(plane => Vector3D.DotProduct(Direction(plane.Normal), normal) > 0.99999 &&
                Math.Abs(Vector3D.DotProduct(plane.Normal, point) - plane.Limit) < 1e-5);
        };
    }

    private IEnumerable<(Vector3D Normal, double Limit)> Planes(BoxBevelCut cut)
    {
        var corner = (Vector3D)Local(cut.Target.ProfileIndex, cut.Target.Z);
        if (!cut.Rounded)
        {
            var normal = FlatNormal(cut);
            double length = normal.Length;
            yield return (normal / length, (Vector3D.DotProduct(normal, corner) - Setback(cut)) / length);
            yield break;
        }
        var normals = Incident(cut.Target);
        double dot = Vector3D.DotProduct(normals[0], normals[1]);
        var center = corner - (normals[0] + normals[1]) * (cut.Amount / (1 + dot));
        if (normals.Length == 2)
        {
            double angle = Math.Acos(Math.Clamp(dot, -1, 1));
            var tangent = normals[1] - normals[0] * dot;
            tangent.Normalize();
            for (int i = 1; i < 24; i++)
            {
                double turn = angle * i / 24;
                var normal = normals[0] * Math.Cos(turn) + tangent * Math.Sin(turn);
                yield return (normal, Vector3D.DotProduct(normal, center) + cut.Amount);
            }
            yield break;
        }
        center -= normals[2] * cut.Amount;
        for (int i = 1; i < 15; i++)
            for (int j = 1; j < 16 - i; j++)
            {
                var normal = normals[0] * i + normals[1] * j + normals[2] * (16 - i - j);
                normal.Normalize();
                yield return (normal, Vector3D.DotProduct(normal, center) + cut.Amount);
            }
    }

    private Vector3D FlatNormal(BoxBevelCut cut)
    {
        var normals = Incident(cut.Target);
        return normals.Length == 2 ? normals[0] + normals[1] * cut.Slope :
            normals[0] + normals[1] + normals[2] * cut.Slope;
    }

    private double Setback(BoxBevelCut cut)
    {
        if (cut.Target.ProfileKind == ProfileBevelKind.Edge)
            return cut.Amount;
        var normals = Incident(cut.Target);
        return cut.Amount * Vector3D.CrossProduct(normals[0], normals[1]).Length;
    }

    private Vector3D[] Incident(BoxBevelTarget target) => target.ProfileKind switch
    {
        ProfileBevelKind.Edge => [sides[target.ProfileIndex], new(0, 0, target.Z)],
        ProfileBevelKind.VerticalEdge => [sides[Previous(target.ProfileIndex)], sides[target.ProfileIndex]],
        _ => [sides[Previous(target.ProfileIndex)], sides[target.ProfileIndex], new(0, 0, target.Z)]
    };

    private int Next(int index) => (index + 1) % Count;
    private int Previous(int index) => (index + Count - 1) % Count;
    private Point3D Local(int index, int end) => new(outline[index].X, outline[index].Y, end > 0 ? part.Height : 0);
    private Point3D Vertex(Point3D point) => part.SketchVertex(new(point.X, point.Y), point.Z);
    private Point3D World(Point3D point) => Vertex(point) + new Vector3D(part.X, part.Y, part.Z);
    private Vector3D Direction(Vector3D vector) => frame.Horizontal.Vector * vector.X + frame.Vertical.Vector * vector.Y + frame.Normal * vector.Z;
}
