using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;

namespace LayerCAD;

[JsonConverter(typeof(JsonStringEnumConverter<PartKind>))]
public enum PartKind { Box, Cylinder, Tube, Extrusion, Mesh, Sketch }

[JsonConverter(typeof(JsonStringEnumConverter<SketchPlane>))]
public enum SketchPlane { XY, XZ, YZ }

internal static class PlaneCoordinates
{
    public static Point3D ToModel(this SketchPlane plane, double u, double v, double distance) =>
        plane switch
        {
            SketchPlane.XY => new Point3D(u, v, distance),
            SketchPlane.XZ => new Point3D(u, -distance, v),
            SketchPlane.YZ => new Point3D(distance, u, v),
            _ => throw new ArgumentOutOfRangeException(nameof(plane))
        };
}

public readonly record struct SketchAxis(double X, double Y, double Z)
{
    internal Vector3D Vector => new(X, Y, Z);
    internal static SketchAxis FromVector(Vector3D vector) => new(vector.X, vector.Y, vector.Z);
}

public sealed record SketchFrame(SketchAxis Horizontal, SketchAxis Vertical)
{
    internal Vector3D Normal => Vector3D.CrossProduct(Horizontal.Vector, Vertical.Vector);

    internal bool IsValid()
    {
        const double tolerance = 1e-8;
        var u = Horizontal.Vector;
        var v = Vertical.Vector;
        return Math.Abs(u.LengthSquared - 1) < tolerance &&
               Math.Abs(v.LengthSquared - 1) < tolerance &&
               Math.Abs(Vector3D.DotProduct(u, v)) < tolerance;
    }

    internal Point3D ToModel(double u, double v, double depth) =>
        new Point3D() + Horizontal.Vector * u + Vertical.Vector * v + Normal * depth;

    internal SketchPoint Project(Vector3D vector) =>
        new(Vector3D.DotProduct(vector, Horizontal.Vector), Vector3D.DotProduct(vector, Vertical.Vector));

    internal static SketchFrame FromNormal(Vector3D normal)
    {
        var vertical = new Vector3D(0, 0, 1) - normal * normal.Z;
        if (vertical.LengthSquared < 1e-12)
        {
            var horizontal = new Vector3D(1, 0, 0) - normal * normal.X;
            horizontal.Normalize();
            return new SketchFrame(SketchAxis.FromVector(horizontal),
                SketchAxis.FromVector(Vector3D.CrossProduct(normal, horizontal)));
        }
        vertical.Normalize();
        return new SketchFrame(SketchAxis.FromVector(Vector3D.CrossProduct(vertical, normal)),
            SketchAxis.FromVector(vertical));
    }
}

public sealed record Part(PartKind Kind, string Name, double Width, double Depth, double Height,
                          double X, double Y, double Z, SketchPoint[]? Profile = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                          SketchPlane Plane = SketchPlane.XY,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          SketchFrame? FaceFrame = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          MeshData? Mesh = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                          double Bevel = 0,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          BoxBevelCut[]? BevelCuts = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                          bool Hidden = false,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          HoleCut[]? Holes = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          Guid? SketchId = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          ThreadSpec[]? Threads = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          SketchPoint[][]? AdditionalProfiles = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                          int SketchProfileIndex = 0,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          SketchPoint[][]? OpenPaths = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          ProfileDimensions?[]? Dimensions = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          ProfileConstraint[]?[]? Constraints = null,
                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                          SketchReferenceImage? ReferenceImage = null)
{
    [JsonIgnore]
    internal IEnumerable<SketchPoint[]> SketchProfiles => Profile is null ? [] : new[] { Profile }.Concat(AdditionalProfiles ?? []);

    internal (int Index, int Start, int Count) SketchProfileRange(int triangleIndex)
    {
        int index = 0, start = 0;
        foreach (var profile in SketchProfiles)
        {
            // Each simple profile emits n - 2 consecutive triangles in Geometry.Build.
            int count = profile.Length - 2;
            if (triangleIndex < start + count)
                return (index, start, count);
            start += count;
            index++;
        }
        return (-1, start, 0);
    }
    [JsonIgnore]
    public string BrowserGroup => Kind == PartKind.Sketch ? "Sketches" : "Objects";

    [JsonIgnore]
    public double FootprintDepth => Kind == PartKind.Box ? Depth : Width;

    public bool IsValid() =>
        Enum.IsDefined(Kind) && Enum.IsDefined(Plane) && !string.IsNullOrWhiteSpace(Name) &&
        (SketchId is null || SketchId != Guid.Empty && Kind is PartKind.Sketch or PartKind.Extrusion) &&
        (SketchProfileIndex == 0 || SketchProfileIndex > 0 && Kind == PartKind.Extrusion && SketchId is not null) &&
        (AdditionalProfiles is null || Kind == PartKind.Sketch && Profile is not null && AdditionalProfiles.Length is > 0 and < 64 &&
            AdditionalProfiles.All(profile => SketchGeometry.TryTriangulate(profile, out _, out _))) &&
        (OpenPaths is null || Kind == PartKind.Sketch && OpenPaths.Length > 0 &&
            OpenPaths.Length + (AdditionalProfiles?.Length ?? 0) + (Profile is null ? 0 : 1) <= 64 &&
            OpenPaths.All(SketchGeometry.ValidOpenPath)) &&
        (Dimensions is null || Kind is PartKind.Sketch or PartKind.Extrusion &&
            Dimensions.Length == SketchProfiles.Count() && Dimensions.Any(item => item is not null) &&
            Dimensions.All(item => item is null || item.IsValid())) &&
        ValidConstraints() && ValidOffset(X) && ValidOffset(Y) && ValidOffset(Z) &&
        (Bevel == 0 || ValidBevel(Bevel)) &&
        (BevelCuts is null || (BevelCuts.Length > 0 &&
            BevelCuts.All(ValidBevelCut) &&
            BevelCuts.Select(cut => cut.Target).Distinct().Count() == BevelCuts.Length)) &&
        (Kind == PartKind.Mesh
            ? Plane == SketchPlane.XY && FaceFrame is null && Profile is null && Mesh?.IsValid() == true
            : Mesh is null && (Kind == PartKind.Sketch ? Height == 0 : ValidDimension(Height)) && IsValidModeledPart()) &&
        (BevelCuts is null || ValidBevelCombination()) &&
        (Holes is null || HoleGeometry.IsValid(this)) &&
        (Threads is null || ThreadGeometry.IsValid(this));

    private bool ValidConstraints()
    {
        if (Constraints is null)
            return true;
        var closed = SketchProfiles.ToArray();
        var outlines = closed.Concat(OpenPaths ?? []).ToArray();
        return Kind is PartKind.Sketch or PartKind.Extrusion && Constraints.Length == outlines.Length &&
            Constraints.Any(rules => rules is { Length: > 0 }) &&
            Constraints.Select((rules, i) => rules is null || outlines[i] is { Length: > 0 } outline &&
                rules.All(rule => rule is not null && Enum.IsDefined(rule.Kind) && rule.Vertex >= 0 && rule.Vertex < outline.Length &&
                    (rule.Kind == ProfileConstraintKind.CoincidentOrigin ? outline[rule.Vertex] == default :
                    (i < closed.Length || rule.Vertex < outline.Length - 1) &&
                    (rule.Kind == ProfileConstraintKind.Horizontal
                        ? outline[rule.Vertex].Y == outline[(rule.Vertex + 1) % outline.Length].Y
                        : outline[rule.Vertex].X == outline[(rule.Vertex + 1) % outline.Length].X)))).All(valid => valid);
    }

    private bool ValidBevel(double amount) => ValidDimension(amount) && BevelBox() is { } box &&
        amount < Math.Min(box.Width, Math.Min(box.Depth, box.Height)) / 2;

    private bool ValidBevelCut(BoxBevelCut cut)
    {
        if (!cut.Target.IsCut || !double.IsFinite(cut.Amount) || cut.Amount < 0.01 ||
            (cut.Angle is double angle && (cut.Rounded || !(angle is >= 1 and <= 89))))
            return false;
        if (cut.Target.ProfileKind != ProfileBevelKind.None)
            return PolygonBevel.Create(this) is { } polygon && cut.Target.ProfileIndex < polygon.Count &&
                cut.Amount < polygon.MaxAmount(cut);
        if (BevelBox() is { } box)
            return cut.Rounded ? ValidBevel(cut.Amount) : cut.Amount < cut.FlatLimit(box);
        return RoundBevel.IsRim(cut.Target) && RoundBevel.Surface(this) is { } round &&
            (cut.Rounded ? cut.Amount < RoundBevel.Limit(round) :
                cut.Amount < round.Radius && RoundBevel.Depth(cut) < round.Length);
    }

    private bool ValidBevelCombination()
    {
        if (BevelCuts!.Any(cut => cut.Target.ProfileKind != ProfileBevelKind.None))
            return BevelCuts!.All(cut => cut.Target.ProfileKind != ProfileBevelKind.None) &&
                PolygonBevel.Create(this)?.HasRemainingSolid() == true;
        if (BevelBox() is { } box)
            return !NeedsExtendedBevelVersion || SelectiveBoxBevel.HasRemainingSolid(box);
        return RoundBevel.Surface(this) is { } round && BevelCuts!.Sum(RoundBevel.Depth) < round.Length;
    }

    internal bool NeedsExtendedBevelVersion => BevelCuts?.Any(cut => !cut.Rounded &&
        (cut.Angle is not null || !(ValidBevel(cut.Amount) ||
         RoundBevel.Surface(this) is { } round && cut.Amount < RoundBevel.Limit(round)))) == true;

    private bool IsValidModeledPart() =>
        Kind is PartKind.Extrusion or PartKind.Sketch
            ? (FaceFrame is null || (Plane == SketchPlane.XY && FaceFrame.IsValid())) &&
              (Kind == PartKind.Sketch && Profile is null ? OpenPaths is not null : SketchGeometry.TryTriangulate(Profile, out _, out _))
            : Plane == SketchPlane.XY && FaceFrame is null && ValidDimension(Width) &&
              (Kind == PartKind.Cylinder ? double.IsFinite(Depth) : ValidDimension(Depth)) &&
              (Kind != PartKind.Tube || Width - Depth >= 0.02);

    public bool Fits(Bed bed)
    {
        var bounds = LocalBounds();
        return X + bounds.X >= -bed.X / 2 && X + (bounds.X + bounds.SizeX) <= bed.X / 2 &&
               Y + bounds.Y >= -bed.Y / 2 && Y + (bounds.Y + bounds.SizeY) <= bed.Y / 2 &&
               Z + bounds.Z >= 0 && Z + (bounds.Z + bounds.SizeZ) <= bed.Z;
    }

    internal Rect3D LocalBounds()
    {
        if (Kind == PartKind.Mesh)
            return Mesh!.Bounds;
        if (Kind is not (PartKind.Extrusion or PartKind.Sketch))
            return new Rect3D(-Width / 2, -FootprintDepth / 2, 0, Width, FootprintDepth, Height);

        return profileBounds.GetValue(this, static part => new ProfileBounds(part.CalculateProfileBounds())).Bounds;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Part, ProfileBounds> profileBounds = new();
    private sealed record ProfileBounds(Rect3D Bounds);

    private Rect3D CalculateProfileBounds()
    {
        var bounds = Rect3D.Empty;
        foreach (var point in SketchProfiles.Concat(OpenPaths ?? []).SelectMany(profile => profile))
        {
            bounds.Union(SketchVertex(point, 0));
            bounds.Union(SketchVertex(point, Height));
        }
        return bounds;
    }

    internal Point3D SketchVertex(SketchPoint point, double depth) =>
        FaceFrame?.ToModel(point.X, point.Y, depth) ?? Plane.ToModel(point.X, point.Y, depth);

    internal Part? BevelBox()
    {
        if (Kind == PartKind.Box)
            return this;
        if (Kind != PartKind.Extrusion || Profile is not { Length: 4 } points ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)) || !ValidDimension(Height))
            return null;
        if (FaceFrame is { } frame &&
            (!frame.IsValid() || !Cardinal(frame.Horizontal.Vector) || !Cardinal(frame.Vertical.Vector)))
            return null;

        var xs = points.Select(point => point.X).Distinct().ToArray();
        var ys = points.Select(point => point.Y).Distinct().ToArray();
        if (xs.Length != 2 || ys.Length != 2 || points.Distinct().Count() != 4)
            return null;
        var bounds = LocalBounds();
        return new Part(PartKind.Box, Name, bounds.SizeX, bounds.SizeY, bounds.SizeZ,
            X + bounds.X + bounds.SizeX / 2, Y + bounds.Y + bounds.SizeY / 2, Z + bounds.Z,
            Bevel: Bevel, BevelCuts: BevelCuts);

        static bool Cardinal(Vector3D axis) =>
            Math.Abs(axis.X) == 1 && axis.Y == 0 && axis.Z == 0 ||
            Math.Abs(axis.Y) == 1 && axis.X == 0 && axis.Z == 0 ||
            Math.Abs(axis.Z) == 1 && axis.X == 0 && axis.Y == 0;
    }

    public static bool ValidDimension(double value) => value is >= 0.01 and <= 10000;
    internal static bool ValidOffset(double value) => value is >= -10000 and <= 10000;
}

public sealed record Bed(double X, double Y, double Z)
{
    public bool IsValid() => Part.ValidDimension(X) && Part.ValidDimension(Y) && Part.ValidDimension(Z);
}

public sealed record Project(int Version, Bed Bed, Part[] Parts)
{
    public bool IsValid() => Version is >= 1 and <= 22 && Bed is not null && Bed.IsValid() &&
        Parts is not null && Parts.All(part => part is not null && part.IsValid() &&
            (Version >= 2 || part.Kind != PartKind.Extrusion) &&
            (Version >= 3 || part.Plane == SketchPlane.XY) &&
            (Version >= 4 || part.FaceFrame is null) &&
            (Version >= 5 || part.Kind != PartKind.Mesh) &&
            (Version >= 6 || part.Bevel == 0) &&
            (Version >= 7 || part.BevelCuts is null) &&
            (Version >= 8 || !part.Hidden) &&
            (Version >= 9 || (part.Kind != PartKind.Sketch &&
                (part.Kind == PartKind.Box || part.BevelCuts is null && part.Bevel == 0))) &&
            (Version >= 10 || part.Holes is null) &&
            (Version >= 11 || part.SketchId is null) &&
            (Version >= 12 || part.Threads is null) &&
            (Version >= 13 || part.Threads?.All(thread => thread.MetricSize is null) != false) &&
            (Version >= 14 || part.AdditionalProfiles is null && part.SketchProfileIndex == 0) &&
            (Version >= 15 || part.BevelCuts is null ||
                part.BevelBox() is not null && part.BevelCuts.All(cut => !cut.Rounded)) &&
            (Version >= 16 || !part.NeedsExtendedBevelVersion) &&
            (Version >= 17 || part.OpenPaths is null && part.Holes?.Select(hole => hole.Axis).Distinct().Count() is not > 1) &&
            (Version >= 18 || part.BevelCuts?.Any(cut => cut.Target.ProfileKind != ProfileBevelKind.None) != true) &&
            (Version >= 19 || part.Holes?.Any(hole => hole.Seat is not null) != true) &&
            (Version >= 20 || part.Dimensions is null) &&
            (Version >= 21 || part.Constraints is null) &&
            (part.ReferenceImage is null || Version >= 22 && part.Kind == PartKind.Sketch && part.ReferenceImage.IsValid())) && ValidSketchLinks();

    private bool ValidSketchLinks()
    {
        var sketches = new Dictionary<Guid, int>();
        foreach (var part in Parts)
        {
            if (part.Kind == PartKind.Sketch && part.SketchId is Guid id &&
                !sketches.TryAdd(id, (part.Profile is null ? 0 : 1) + (part.AdditionalProfiles?.Length ?? 0)))
                return false;
        }
        return Parts.All(part => part.Kind != PartKind.Extrusion || part.SketchId is not Guid id ||
            sketches.TryGetValue(id, out int count) && part.SketchProfileIndex < count);
    }
}

public readonly record struct Triangle(Point3D A, Point3D B, Point3D C)
{
    internal bool IsValid() => ValidPoint(A) && ValidPoint(B) && ValidPoint(C) &&
        Vector3D.CrossProduct(B - A, C - A).LengthSquared > 0;

    private static bool ValidPoint(Point3D point) =>
        InFloatRange(point.X) && InFloatRange(point.Y) && InFloatRange(point.Z);

    private static bool InFloatRange(double value) => value is >= -float.MaxValue and <= float.MaxValue;

    internal Triangle Translate(Vector3D offset) => new(A + offset, B + offset, C + offset);
}

public sealed class MeshData(Triangle[] triangles)
{
    public Triangle[] Triangles { get; } = triangles;
    private bool? valid;
    private Rect3D? bounds;

    internal bool IsValid() => valid ??= Triangles is { Length: > 0 } && Triangles.All(triangle => triangle.IsValid());

    internal Rect3D Bounds => bounds ??= GetBounds(Triangles);

    internal static Rect3D GetBounds(IEnumerable<Triangle> triangles)
    {
        var bounds = Rect3D.Empty;
        foreach (var (a, b, c) in triangles)
        {
            bounds.Union(a);
            bounds.Union(b);
            bounds.Union(c);
        }
        return bounds;
    }
}

internal static class Geometry
{
    private const int Segments = SketchGeometry.CurveSegments;

    public static List<Triangle> Build(Part part, bool positioned = true)
    {
        if (part.Holes is not null)
            return HoleGeometry.Build(part, positioned);
        if (part.Threads is not null)
            return ThreadGeometry.BuildRound(part, positioned);
        if (part.BevelCuts is not null && RoundBevel.Surface(part) is { } round)
            return RoundBevel.Build(part, round, positioned);
        if (part.BevelCuts is not null && PolygonBevel.Create(part) is { } polygon)
            return polygon.Build(positioned);

        if (part.Kind == PartKind.Extrusion && (part.BevelCuts is not null || part.Bevel > 0))
            return Build(part.BevelBox()!, positioned);

        var triangles = new List<Triangle>();
        var offset = positioned ? new Vector3D(part.X, part.Y, part.Z) : new Vector3D();
        if (part.Kind == PartKind.Mesh)
        {
            if (!positioned)
            {
                var bounds = part.LocalBounds();
                offset = new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
            }
            return part.Mesh!.Triangles.Select(triangle => triangle.Translate(offset)).ToList();
        }
        Point3D At(double x, double y, double z) => new Point3D(x, y, z) + offset;
        void Tri(Point3D a, Point3D b, Point3D c) => triangles.Add(new Triangle(a, b, c));
        void Quad(Point3D a, Point3D b, Point3D c, Point3D d)
        {
            Tri(a, b, c);
            Tri(a, c, d);
        }

        if (part.Kind == PartKind.Sketch)
        {
            if (!positioned)
            {
                var bounds = part.LocalBounds();
                offset = new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
            }
            foreach (var profile in part.SketchProfiles)
            {
                if (!SketchGeometry.TryTriangulate(profile, out var faces, out _))
                    throw new ArgumentException("The sketch profile cannot be drawn.", nameof(part));
                foreach (var (a, b, c) in faces)
                    Tri(part.SketchVertex(profile[a], 0) + offset, part.SketchVertex(profile[b], 0) + offset,
                        part.SketchVertex(profile[c], 0) + offset);
            }
            foreach (var path in part.OpenPaths ?? [])
            {
                for (int i = 1; i < path.Length; i++)
                {
                    var a = path[i - 1];
                    var b = path[i];
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    double scale = 0.18 / Math.Sqrt(dx * dx + dy * dy);
                    Point3D Side(SketchPoint point, int sign) =>
                        part.SketchVertex(new(point.X - sign * dy * scale, point.Y + sign * dx * scale), 0) + offset;
                    Quad(Side(a, -1), Side(b, -1), Side(b, 1), Side(a, 1));
                }
            }
            return triangles;
        }

        if (part.Kind == PartKind.Extrusion)
        {
            var points = part.Profile!;
            if (!positioned)
            {
                var bounds = part.LocalBounds();
                offset = new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
            }
            Point3D Vertex(int index) =>
                part.SketchVertex(points[index % points.Length], index < points.Length ? 0 : part.Height) + offset;
            foreach (var (a, b, c) in ProfileExtrusion.Triangulate(points))
                Tri(Vertex(a), Vertex(b), Vertex(c));
            return triangles;
        }

        if (part.Kind == PartKind.Box)
        {
            if (part.BevelCuts is not null)
                return SelectiveBoxBevel.Build(part, offset);
            if (part.Bevel > 0)
            {
                var (flats, chamfers) = BoxBevel.Build(part, offset);
                flats.AddRange(chamfers);
                return flats;
            }
            double x = part.Width / 2, y = part.Depth / 2, z = part.Height;
            var a = At(-x, -y, 0);
            var b = At(x, -y, 0);
            var c = At(x, y, 0);
            var d = At(-x, y, 0);
            var e = At(-x, -y, z);
            var f = At(x, -y, z);
            var g = At(x, y, z);
            var h = At(-x, y, z);
            Quad(d, c, b, a);
            Quad(e, f, g, h);
            Quad(a, b, f, e);
            Quad(b, c, g, f);
            Quad(c, d, h, g);
            Quad(d, a, e, h);
            return triangles;
        }

        Point3D RingPoint(int index, double diameter, double height)
        {
            double angle = index % Segments * Math.Tau / Segments;
            return At(Math.Cos(angle) * diameter / 2, Math.Sin(angle) * diameter / 2, height);
        }

        for (int i = 0; i < Segments; i++)
        {
            var a = RingPoint(i, part.Width, 0);
            var b = RingPoint(i + 1, part.Width, 0);
            var c = RingPoint(i + 1, part.Width, part.Height);
            var d = RingPoint(i, part.Width, part.Height);
            Quad(a, b, c, d);

            if (part.Kind == PartKind.Tube)
            {
                var e = RingPoint(i, part.Depth, 0);
                var f = RingPoint(i + 1, part.Depth, 0);
                var g = RingPoint(i + 1, part.Depth, part.Height);
                var h = RingPoint(i, part.Depth, part.Height);
                Quad(f, e, h, g);
                Quad(d, c, g, h);
                Quad(b, a, e, f);
            }
            else
            {
                Tri(b, a, At(0, 0, 0));
                Tri(d, c, At(0, 0, part.Height));
            }
        }

        return triangles;
    }

    public static void WriteStl(Stream stream, Part part)
    {
        var triangles = Build(part, positioned: false);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("LayerCAD | dimensions in millimeters").CopyTo(header, 0);
        writer.Write(header);
        writer.Write((uint)triangles.Count);
        foreach (var (a, b, c) in triangles)
        {
            var normal = Vector3D.CrossProduct(b - a, c - a);
            normal.Normalize();
            WriteCoordinates(normal.X, normal.Y, normal.Z);
            WriteCoordinates(a.X, a.Y, a.Z);
            WriteCoordinates(b.X, b.Y, b.Z);
            WriteCoordinates(c.X, c.Y, c.Z);
            writer.Write((ushort)0);
        }

        void WriteCoordinates(double x, double y, double z)
        {
            writer.Write((float)x);
            writer.Write((float)y);
            writer.Write((float)z);
        }
    }
}


