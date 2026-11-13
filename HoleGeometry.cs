using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;
using LibTessDotNet.Double;

namespace LayerCAD;

[JsonConverter(typeof(JsonStringEnumConverter<HoleAxis>))]
public enum HoleAxis { Profile, X, Y, Z }

[JsonConverter(typeof(JsonStringEnumConverter<HoleSeatKind>))]
public enum HoleSeatKind { Counterbore, Countersink }

public sealed record HoleSeat(HoleSeatKind Kind, double Diameter, double Depth = 0, double Angle = 0);

public sealed record HoleCut(HoleAxis Axis, SketchPoint Center, double Diameter, double Depth, bool Through, bool FromEnd,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HoleSeat? Seat = null)
{
    internal double OpeningDiameter => Seat?.Diameter ?? Diameter;
    internal double SeatDepth => Seat switch
    {
        { Kind: HoleSeatKind.Counterbore } seat => seat.Depth,
        { Kind: HoleSeatKind.Countersink } seat => (seat.Diameter - Diameter) / (2 * Math.Tan(seat.Angle * Math.PI / 360)),
        _ => 0
    };

    internal (double Low, double High) Shaft(HolePrism prism)
    {
        double low = Through || !FromEnd ? 0 : prism.Thickness - Depth;
        double high = Through || FromEnd ? prism.Thickness : Depth;
        return FromEnd ? (low, high - SeatDepth) : (low + SeatDepth, high);
    }
}

internal sealed record HolePrism(SketchFrame Frame, Point3D Origin, double Thickness, SketchPoint[] Outer,
                                SketchPoint[]? Inner, SketchPoint[]? Top = null, IReadOnlyList<Triangle>? StockSides = null)
{
    public static HolePrism? Create(Part part, HoleAxis axis)
    {
        if (axis == HoleAxis.Profile)
        {
            HolePrism? profilePrism = null;
            if (part.Kind == PartKind.Extrusion)
            {
                var origin = part.SketchVertex(default, 0);
                var u = part.SketchVertex(new SketchPoint(1, 0), 0) - origin;
                var v = part.SketchVertex(new SketchPoint(0, 1), 0) - origin;
                profilePrism = new HolePrism(new SketchFrame(SketchAxis.FromVector(u), SketchAxis.FromVector(v)),
                    origin, part.Height, part.Profile!, null);
            }
            else if (part.Kind is PartKind.Cylinder or PartKind.Tube)
                profilePrism = new HolePrism(FrameFor(HoleAxis.Z), new Point3D(), part.Height,
                    Circle(default, part.Width / 2), part.Kind == PartKind.Tube ? Circle(default, part.Depth / 2) : null);
            return profilePrism is not null && (part.Bevel > 0 || part.BevelCuts is not null)
                ? profilePrism.WithBeveledStock(part) : profilePrism;
        }

        if (part.BevelBox() is not { } box)
            return null;
        var center = new Point3D(box.X - part.X, box.Y - part.Y, box.Z - part.Z + box.Height / 2);
        var prism = axis switch
        {
            HoleAxis.X => new HolePrism(FrameFor(axis), center - new Vector3D(box.Width / 2, 0, 0),
                box.Width, Rectangle(box.Depth, box.Height), null),
            HoleAxis.Y => new HolePrism(FrameFor(axis), center + new Vector3D(0, box.Depth / 2, 0),
                box.Depth, Rectangle(box.Width, box.Height), null),
            HoleAxis.Z => new HolePrism(FrameFor(axis), center - new Vector3D(0, 0, box.Height / 2),
                box.Height, Rectangle(box.Width, box.Depth), null),
            _ => null
        };
        return prism is not null && (part.Bevel > 0 || part.BevelCuts is not null)
            ? prism.WithBeveledStock(part) : prism;
    }

    private HolePrism? WithBeveledStock(Part part)
    {
        var bottom = new List<Triangle>();
        var top = new List<Triangle>();
        var sides = new List<Triangle>();
        var normal = Frame.Normal;
        var stock = Geometry.Build(part with { X = 0, Y = 0, Z = 0, Holes = null, Threads = null });
        foreach (var triangle in stock)
        {
            double a = Vector3D.DotProduct(triangle.A - Origin, normal);
            double b = Vector3D.DotProduct(triangle.B - Origin, normal);
            double c = Vector3D.DotProduct(triangle.C - Origin, normal);
            if (Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c))) < 1e-7)
                bottom.Add(triangle);
            else if (Math.Max(Math.Abs(a - Thickness), Math.Max(Math.Abs(b - Thickness), Math.Abs(c - Thickness))) < 1e-7)
                top.Add(triangle);
            else
                sides.Add(triangle);
        }

        var bottomContour = Boundary(bottom);
        var topContour = Boundary(top);
        return bottomContour is not null && topContour is not null
            ? this with { Outer = bottomContour, Top = topContour, StockSides = sides } : null;
    }

    private SketchPoint[]? Boundary(IEnumerable<Triangle> facets)
    {
        var edges = new HashSet<(Point3D Start, Point3D End)>();
        foreach (var (a, b, c) in facets)
        {
            Add(a, b);
            Add(b, c);
            Add(c, a);
        }
        if (edges.Count < 3)
            return null;

        var next = new Dictionary<Point3D, Point3D>();
        foreach (var (start, end) in edges)
        {
            if (!next.TryAdd(start, end))
                return null;
        }
        var first = edges.First().Start;
        var current = first;
        var contour = new List<SketchPoint>();
        while (next.Remove(current, out var end))
        {
            contour.Add(Frame.Project(current - Origin));
            current = end;
        }
        return current == first && contour.Count == edges.Count ? contour.ToArray() : null;

        void Add(Point3D start, Point3D end)
        {
            if (!edges.Remove((end, start)))
                edges.Add((start, end));
        }
    }

    private static SketchFrame FrameFor(HoleAxis axis) => axis switch
    {
        HoleAxis.X => new SketchFrame(new SketchAxis(0, 1, 0), new SketchAxis(0, 0, 1)),
        HoleAxis.Y => new SketchFrame(new SketchAxis(1, 0, 0), new SketchAxis(0, 0, 1)),
        _ => new SketchFrame(new SketchAxis(1, 0, 0), new SketchAxis(0, 1, 0))
    };

    private static SketchPoint[] Rectangle(double width, double height) =>
        [new(-width / 2, -height / 2), new(width / 2, -height / 2), new(width / 2, height / 2), new(-width / 2, height / 2)];

    public static SketchPoint[] Circle(SketchPoint center, double radius) =>
        Enumerable.Range(0, SketchGeometry.CurveSegments).Select(index =>
        {
            double angle = index * Math.Tau / SketchGeometry.CurveSegments;
            return new SketchPoint(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        }).ToArray();

    public Point3D Vertex(SketchPoint point, double depth, Vector3D offset) =>
        Frame.ToModel(point.X, point.Y, depth) + (Vector3D)Origin + offset;

    public double Clearance(SketchPoint center)
    {
        if (!Inside(Outer, center) || Inner is not null && Inside(Inner, center))
            return 0;
        double outer = DistanceToBoundary(Outer, center);
        if (Top is not null)
            return Inside(Top, center) ? Math.Min(outer, DistanceToBoundary(Top, center)) : 0;
        return Inner is null ? outer : Math.Min(outer, DistanceToBoundary(Inner, center));
    }

    private static bool Inside(SketchPoint[] polygon, SketchPoint point)
    {
        bool inside = false;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToBoundary(SketchPoint[] polygon, SketchPoint point)
    {
        double distance = double.PositiveInfinity;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double fraction = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            distance = Math.Min(distance, Distance(point, new SketchPoint(a.X + fraction * dx, a.Y + fraction * dy)));
        }
        return distance;
    }

    internal static double Distance(SketchPoint a, SketchPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

internal sealed record HolePlacement(Part Source, HoleAxis Axis, HolePrism Prism, SketchPoint Center,
                                     bool FromEnd, SketchPoint? FaceCenter = null)
{
    public HolePlacement WithFaceCenter(Point3D center) =>
        this with { FaceCenter = Prism.Frame.Project(center - (Prism.Origin + new Vector3D(Source.X, Source.Y, Source.Z))) };

    public double Clearance
    {
        get
        {
            double clearance = Prism.Clearance(Center);
            foreach (var hole in Source.Holes ?? [])
                if (hole.Axis == Axis)
                    clearance = Math.Min(clearance, HolePrism.Distance(Center, hole.Center) - hole.OpeningDiameter / 2);
            return clearance;
        }
    }

    public static HolePlacement? Find(Part part, Point3D point, Vector3D normal)
    {
        if (part.Kind is not (PartKind.Box or PartKind.Cylinder or PartKind.Tube or PartKind.Extrusion))
            return null;
        var axis = part.BevelBox() is null || part.Holes?.Any(hole => hole.Axis == HoleAxis.Profile) == true ? HoleAxis.Profile :
            Math.Abs(normal.X) > 0.999999 ? HoleAxis.X : Math.Abs(normal.Y) > 0.999999 ? HoleAxis.Y : HoleAxis.Z;
        if (HolePrism.Create(part, axis) is not { } prism)
            return null;

        double facing = Vector3D.DotProduct(normal, prism.Frame.Normal);
        if (Math.Abs(facing) < 0.999999)
            return null;
        var local = point - (prism.Origin + new Vector3D(part.X, part.Y, part.Z));
        bool fromEnd = facing > 0;
        if (Math.Abs(Vector3D.DotProduct(local, prism.Frame.Normal) - (fromEnd ? prism.Thickness : 0)) > 1e-5)
            return null;

        var center = prism.Frame.Project(local);
        var placement = new HolePlacement(part, axis, prism, center, fromEnd);
        return placement.Clearance > 0.005001 ? placement : null;
    }

    public BuildChange? Preview(double diameter, double depth, bool through, HoleSeat? seat = null)
    {
        var hole = new HoleCut(Axis, Center, diameter, through ? 0 : depth, through, FromEnd, seat);
        var solid = Source with { Holes = [..(Source.Holes ?? []), hole] };
        if (!solid.IsValid())
            return null;
        var offset = new Vector3D(solid.X, solid.Y, solid.Z);
        var origin = Prism.Vertex(Center, FromEnd ? Prism.Thickness : 0, offset);
        var direction = Prism.Frame.Normal * (FromEnd ? -1 : 1);
        var interior = HoleGeometry.Bore(Prism, hole, HolePrism.Circle(Center, diameter / 2), offset);
        var overlay = new List<Triangle>();
        foreach (var triangle in interior)
        {
            var normal = Vector3D.CrossProduct(triangle.B - triangle.A, triangle.C - triangle.A);
            normal.Normalize();
            overlay.Add(triangle.Translate(normal * Math.Min(0.006, diameter * 0.02)));
        }
        return new BuildChange(solid, overlay, true, origin, direction, through ? Prism.Thickness : depth);
    }
}

internal static class HoleGeometry
{
    public static bool IsValid(Part part)
    {
        if (part.Holes is not { Length: > 0 } holes || holes.Any(hole => hole is null || !Enum.IsDefined(hole.Axis)))
            return false;
        var axes = holes.Select(hole => hole.Axis).Distinct().ToArray();
        if (axes.Length > 1 && (part.BevelBox() is null || axes.Contains(HoleAxis.Profile)))
            return false;
        var prisms = axes.ToDictionary(axis => axis, axis => HolePrism.Create(part, axis));

        for (int i = 0; i < holes.Length; i++)
        {
            var hole = holes[i];
            if (prisms[hole.Axis] is not { } prism || !Part.ValidDimension(hole.Diameter) ||
                (hole.Through ? hole.Depth != 0 : !Part.ValidDimension(hole.Depth) || hole.Depth >= prism.Thickness - 1e-6) ||
                !(prism.Clearance(hole.Center) > hole.OpeningDiameter / 2 + 1e-6) || !ValidSeat(hole, prism))
                return false;
            for (int j = 0; j < i; j++)
            {
                var other = holes[j];
                if ((hole.Axis == other.Axis || hole.Seat is not null || other.Seat is not null) &&
                    HoleDistance(prism, hole, prisms[other.Axis]!, other) <=
                    (hole.OpeningDiameter + other.OpeningDiameter) / 2 + 1e-6)
                    return false;
            }
        }
        return true;
    }

    private static bool ValidSeat(HoleCut hole, HolePrism prism)
    {
        if (hole.Seat is not { } seat)
            return true;
        if (!Part.ValidDimension(seat.Diameter) || seat.Diameter <= hole.Diameter + 1e-6)
            return false;
        bool valid = seat.Kind switch
        {
            HoleSeatKind.Counterbore => seat.Angle == 0 && Part.ValidDimension(seat.Depth),
            HoleSeatKind.Countersink => seat.Depth == 0 && seat.Angle is >= 30 and <= 150,
            _ => false
        };
        return valid && Part.ValidDimension(hole.SeatDepth) &&
            hole.SeatDepth < (hole.Through ? prism.Thickness : hole.Depth) - 1e-6;
    }

    internal static double HoleDistance(HolePrism first, HoleCut a, HolePrism second, HoleCut b)
    {
        if (a.Axis == b.Axis)
            return HolePrism.Distance(a.Center, b.Center);
        var left = BoreBounds(first, a);
        var right = BoreBounds(second, b);
        var gap = new Vector3D(
            Gap(left.X, left.SizeX, right.X, right.SizeX),
            Gap(left.Y, left.SizeY, right.Y, right.SizeY),
            Gap(left.Z, left.SizeZ, right.Z, right.SizeZ));
        return gap.Length;

        static double Gap(double a, double aw, double b, double bw) => Math.Max(0, Math.Max(a - b - bw, b - a - aw));
        static Rect3D BoreBounds(HolePrism prism, HoleCut hole)
        {
            double low = hole.Through || !hole.FromEnd ? 0 : prism.Thickness - hole.Depth;
            double high = hole.Through || hole.FromEnd ? prism.Thickness : hole.Depth;
            var start = prism.Vertex(hole.Center, low, default);
            var end = prism.Vertex(hole.Center, high, default);
            var bounds = new Rect3D(start, new Size3D());
            bounds.Union(end);
            return bounds;
        }
    }

    public static List<Triangle> Build(Part part, bool positioned)
    {
        var holes = part.Holes!;
        var prism = HolePrism.Create(part, holes[0].Axis)!;
        var offset = new Vector3D(part.X, part.Y, part.Z);
        if (!positioned)
        {
            var bounds = part.LocalBounds();
            offset = new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
        }
        if (holes.Any(hole => hole.Axis != holes[0].Axis))
            return BuildMultipleAxes(part, offset);

        var triangles = new List<Triangle>();
        var rings = holes.Select(hole => HolePrism.Circle(hole.Center, hole.Diameter / 2)).ToArray();
        var bottom = new List<SketchPoint[]> { prism.Outer };
        var top = new List<SketchPoint[]> { prism.Top ?? prism.Outer };
        if (prism.StockSides is { } sides)
            triangles.AddRange(sides.Select(triangle => triangle.Translate(offset)));
        else
            StockWall(ThreadSurface.Outer, prism.Outer, outward: true);
        if (prism.Inner is { } inner)
        {
            bottom.Add(inner);
            top.Add(inner);
            StockWall(ThreadSurface.TubeBore, inner, outward: false);
        }

        for (int i = 0; i < holes.Length; i++)
        {
            var hole = holes[i];
            var ring = rings[i];
            if (!hole.FromEnd || hole.Through)
                bottom.Add(Mouth(hole, ring, fromEnd: false));
            if (hole.FromEnd || hole.Through)
                top.Add(Mouth(hole, ring, fromEnd: true));
            var thread = part.Threads?.FirstOrDefault(item => item.Surface == i);
            triangles.AddRange(Bore(prism, hole, ring, offset, thread));
        }

        AddCap(triangles, prism, bottom, 0, false, offset);
        AddCap(triangles, prism, top, prism.Thickness, true, offset);
        return triangles;

        void StockWall(int surface, SketchPoint[] ring, bool outward)
        {
            if (part.Threads?.FirstOrDefault(thread => thread.Surface == surface) is { } thread)
                ThreadGeometry.AddWalls(triangles, ThreadSurface.Get(part, surface)!, thread, offset);
            else
                AddWalls(triangles, prism, ring, 0, prism.Thickness, outward, offset);
        }
    }

    private static List<Triangle> BuildMultipleAxes(Part part, Vector3D offset)
    {
        var stock = Geometry.Build(part with { X = 0, Y = 0, Z = 0, Holes = null, Threads = null });
        var bores = new List<Triangle>();
        var holes = part.Holes!;
        var prisms = holes.Select(hole => hole.Axis).Distinct().ToDictionary(axis => axis, axis => HolePrism.Create(part, axis)!);
        var walls = new List<Triangle>[holes.Length];
        var rings = holes.Select(hole => HolePrism.Circle(hole.Center, hole.Diameter / 2)).ToArray();
        foreach (var (axis, prism) in prisms)
        {
            var bottom = new List<SketchPoint[]> { prism.Outer };
            var top = new List<SketchPoint[]> { prism.Top ?? prism.Outer };
            for (int i = 0; i < holes.Length; i++)
            {
                var hole = holes[i];
                if (hole.Axis != axis)
                    continue;
                var ring = rings[i];
                if (!hole.FromEnd || hole.Through)
                    bottom.Add(Mouth(hole, ring, fromEnd: false));
                if (hole.FromEnd || hole.Through)
                    top.Add(Mouth(hole, ring, fromEnd: true));
                walls[i] = Bore(prism, hole, ring, offset, part.Threads?.FirstOrDefault(thread => thread.Surface == i));
            }
            ReplaceCap(bottom, 0, false);
            ReplaceCap(top, prism.Thickness, true);

            void ReplaceCap(List<SketchPoint[]> rings, double depth, bool facingUp)
            {
                if (rings.Count == 1)
                    return;
                var plane = prism.Origin + prism.Frame.Normal * depth;
                stock.RemoveAll(triangle => OnPlane(triangle.A) && OnPlane(triangle.B) && OnPlane(triangle.C));
                AddCap(bores, prism, rings, depth, facingUp, offset);

                bool OnPlane(Point3D point) => Math.Abs(Vector3D.DotProduct(point - plane, prism.Frame.Normal)) < 1e-7;
            }
        }
        bores.AddRange(stock.Select(triangle => triangle.Translate(offset)));
        return IntersectingBores.Combine(bores, walls, holes, prisms, rings, offset);
    }

    internal static List<Triangle> Bore(HolePrism prism, HoleCut hole, SketchPoint[] ring, Vector3D offset,
                                        ThreadSpec? thread = null)
    {
        var triangles = new List<Triangle>();
        var (low, high) = hole.Shaft(prism);
        if (thread is null)
            AddWalls(triangles, prism, ring, low, high, outward: false, offset);
        else
            ThreadGeometry.AddWalls(triangles,
                new ThreadSurface(prism, hole.Center, ring, hole.Diameter / 2, low, high, false), thread, offset);
        if (hole.Seat is { } seat)
        {
            var opening = Mouth(hole, ring, hole.FromEnd);
            double entry = hole.FromEnd ? prism.Thickness : 0;
            double shoulder = hole.FromEnd ? high : low;
            if (seat.Kind == HoleSeatKind.Counterbore)
            {
                AddWalls(triangles, prism, opening, Math.Min(entry, shoulder), Math.Max(entry, shoulder), outward: false, offset);
                for (int i = 0; i < opening.Length; i++)
                {
                    int next = (i + 1) % opening.Length;
                    var a = prism.Vertex(opening[i], shoulder, offset);
                    var b = prism.Vertex(opening[next], shoulder, offset);
                    var c = prism.Vertex(ring[next], shoulder, offset);
                    var d = prism.Vertex(ring[i], shoulder, offset);
                    triangles.Add(hole.FromEnd ? new(a, b, c) : new(a, c, b));
                    triangles.Add(hole.FromEnd ? new(a, c, d) : new(a, d, c));
                }
            }
            else if (hole.FromEnd)
                AddWallsBetween(triangles, prism, ring, opening, shoulder, entry, forward: false, offset);
            else
                AddWallsBetween(triangles, prism, opening, ring, entry, shoulder, forward: false, offset);
        }
        if (!hole.Through)
        {
            double floor = hole.FromEnd ? low : high;
            var center = prism.Vertex(hole.Center, floor, offset);
            for (int i = 0; i < ring.Length; i++)
            {
                var a = prism.Vertex(ring[i], floor, offset);
                var b = prism.Vertex(ring[(i + 1) % ring.Length], floor, offset);
                triangles.Add(hole.FromEnd ? new Triangle(center, a, b) : new Triangle(center, b, a));
            }
        }
        return triangles;
    }

    private static SketchPoint[] Mouth(HoleCut hole, SketchPoint[] shaft, bool fromEnd) =>
        hole.Seat is { } seat && hole.FromEnd == fromEnd ? HolePrism.Circle(hole.Center, seat.Diameter / 2) : shaft;

    internal static void AddWalls(List<Triangle> triangles, HolePrism prism, SketchPoint[] contour,
                                 double low, double high, bool outward, Vector3D offset)
    {
        double area = 0;
        for (int i = 0; i < contour.Length; i++)
        {
            var a = contour[i];
            var b = contour[(i + 1) % contour.Length];
            area += a.X * b.Y - a.Y * b.X;
        }
        bool forward = (area > 0) == outward;
        AddWallsBetween(triangles, prism, contour, contour, low, high, forward, offset);
    }

    private static void AddWallsBetween(List<Triangle> triangles, HolePrism prism, SketchPoint[] lower, SketchPoint[] upper,
                                        double low, double high, bool forward, Vector3D offset)
    {
        for (int i = 0; i < lower.Length; i++)
        {
            int next = (i + 1) % lower.Length;
            int start = forward ? i : next, end = forward ? next : i;
            var a = prism.Vertex(lower[start], low, offset);
            var b = prism.Vertex(lower[end], low, offset);
            var c = prism.Vertex(upper[end], high, offset);
            var d = prism.Vertex(upper[start], high, offset);
            triangles.Add(new Triangle(a, b, c));
            triangles.Add(new Triangle(a, c, d));
        }
    }

    internal static void AddCap(List<Triangle> triangles, HolePrism prism, List<SketchPoint[]> contours,
                               double depth, bool facingUp, Vector3D offset)
    {
        var tess = new Tess();
        foreach (var contour in contours)
        {
            var vertices = contour.Select(point => new ContourVertex
            {
                Position = new Vec3 { X = point.X, Y = point.Y, Z = 0 }, Data = point
            }).ToArray();
            tess.AddContour(vertices);
        }
        tess.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3);
        for (int i = 0; i < tess.ElementCount * 3; i += 3)
        {
            var a = Read(tess.Elements[i]);
            var b = Read(tess.Elements[i + 1]);
            var c = Read(tess.Elements[i + 2]);
            double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (cross == 0)
                continue;
            if ((cross > 0) != facingUp)
                (b, c) = (c, b);
            triangles.Add(new Triangle(prism.Vertex(a, depth, offset), prism.Vertex(b, depth, offset),
                prism.Vertex(c, depth, offset)));
        }

        SketchPoint Read(int index)
        {
            var vertex = tess.Vertices[index];
            return vertex.Data is SketchPoint point ? point : new SketchPoint(vertex.Position.X, vertex.Position.Y);
        }
    }
}
