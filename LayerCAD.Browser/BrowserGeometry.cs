using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using System.Windows.Media.Media3D;

namespace LayerCAD;

[SupportedOSPlatform("browser")]
public static partial class BrowserGeometry
{
    public static void Main() {}

    [JSExport]
    public static string BuildPart(string json)
    {
        var part = JsonSerializer.Deserialize(json, BrowserJson.Default.Part);
        if (part is null || !part.IsValid())
            throw new ArgumentException("This part has invalid geometry or dimensions.");
        return JsonSerializer.Serialize(Geometry.Build(part), BrowserJson.Default.ListTriangle);
    }

    [JSExport]
    public static string PickFace(string json)
    {
        var request = JsonSerializer.Deserialize(json, BrowserJson.Default.FacePickRequest);
        if (request is null || request.Part.Kind == PartKind.Sketch || !request.Part.IsValid())
            return "null";
        var mesh = Geometry.Build(request.Part);
        var picker = new TrianglePicker(mesh);
        double distance = double.PositiveInfinity;
        int index = picker.Find(request.Origin, request.Direction, 0, bothSides: true, ref distance);
        var surface = index < 0 ? null : SketchSurface.Find(request.Part, mesh, index);
        if (surface is null)
            return "null";
        var center = surface.Center();
        var guide = surface.Guide(center).Select(edge => new[] { edge.Start, edge.End }).ToArray();
        return JsonSerializer.Serialize(new FacePickResult(center, surface.Frame, guide, distance,
            request.Origin + request.Direction * distance), BrowserJson.Default.FacePickResult);
    }

    [JSExport]
    public static string Bevel(string json, double amount)
    {
        var request = FeatureRequest(json);
        var part = request.Part;
        var normal = request.Frame?.Normal ?? new Vector3D(0, 0, 1);
        IEnumerable<BoxBevelTarget> targets;
        if (part.BevelBox() is { } box)
        {
            var face = BoxBevelTarget.FromNormal(normal);
            if (face.AxisCount != 1)
                throw new ArgumentException("Choose an original flat face, edge, or corner.");
            targets = BoxBevelTarget.Edges.Where(edge => edge.OnFace(face));
            if (request.Point is { } point && request.Kind is "Edge" or "Corner")
            {
                static int Sign(double value, double center, double half) =>
                    Math.Abs(Math.Abs(value - center) - half) < 1e-5 ? Math.Sign(value - center) : 0;
                var target = new BoxBevelTarget(Sign(point.X, box.X, box.Width / 2),
                    Sign(point.Y, box.Y, box.Depth / 2), Sign(point.Z, box.Z + box.Height / 2, box.Height / 2));
                if (target.AxisCount != (request.Kind == "Corner" ? 3 : 2))
                    throw new ArgumentException("Choose an original box edge or corner.");
                targets = [target];
            }
        }
        else if (PolygonBevel.Create(part) is { } polygon)
        {
            targets = polygon.Perimeter(polygon.Face(normal));
            if (request.Point is { } point && request.Kind is "Edge" or "Corner")
            {
                var candidates = request.Kind == "Corner" ? polygon.Corners() : polygon.Edges();
                double Distance(BoxBevelTarget target)
                {
                    if (request.Kind == "Corner") return (polygon.Corner(target) - point).LengthSquared;
                    var (start, end) = polygon.Segment(target);
                    var span = end - start;
                    var ratio = Math.Clamp(Vector3D.DotProduct(point - start, span) / span.LengthSquared, 0, 1);
                    return (start + span * ratio - point).LengthSquared;
                }
                var nearest = candidates.MinBy(Distance);
                if (Distance(nearest) > 1e-8)
                    throw new ArgumentException("Choose an original profile edge or corner.");
                targets = [nearest];
            }
        }
        else if (RoundBevel.Surface(part) is { } round)
        {
            var axial = Vector3D.DotProduct(normal, round.Prism.Frame.Normal);
            if (request.Frame is not null && Math.Abs(axial) < 0.999999)
                throw new ArgumentException("Choose a circular end face or rim.");
            targets = [RoundBevel.Rim(request.Frame is null || axial > 0)];
        }
        else throw new ArgumentException("Bevel supports box edges, convex sketch extrusions, and circular rims.");

        var cuts = (part.BevelCuts ?? []).ToDictionary(cut => cut.Target);
        foreach (var target in targets) cuts[target] = new BoxBevelCut(target, amount);
        return FeatureResult(part with { Bevel = 0, BevelCuts = cuts.Values.ToArray() },
            "The bevel is too large or this geometry cannot be beveled.");
    }

    [JSExport]
    public static string Hole(string json, double diameter)
    {
        var request = FeatureRequest(json);
        var part = request.Part;
        var placement = request.Point is { } point && request.Frame is { } frame
            ? HolePlacement.Find(part, point, frame.Normal) : null;
        if (placement is null)
            throw new ArgumentException("Click a flat face where you want the hole.");
        if (!Part.ValidDimension(diameter))
            throw new ArgumentException("Enter a positive hole diameter.");
        var change = placement.Preview(diameter, depth: 0, through: true);
        return change is null
            ? throw new ArgumentException("The hole does not fit at that point. Use a smaller diameter or pick another point.")
            : JsonSerializer.Serialize(change.Result, BrowserJson.Default.Part);
    }

    [JSExport]
    public static string Thread(string json, int surfaceIndex, string metric, double length, double pitch, double depth)
    {
        var part = JsonSerializer.Deserialize(json, BrowserJson.Default.Part)
            ?? throw new ArgumentException("Select a body.");
        var surface = ThreadSurface.Get(part, surfaceIndex)
            ?? throw new ArgumentException("Choose a cylindrical outer surface, tube bore, or existing hole.");
        double DiameterError(MetricThreadSize item) => Math.Abs(ThreadGeometry.MajorDiameter(surface, item) - item.Diameter);
        var size = metric == "custom" ? null : string.IsNullOrWhiteSpace(metric)
            ? MetricThreadSize.Coarse.Where(item => ThreadGeometry.FitsStock(surface, item)).MinBy(DiameterError)
            : MetricThreadSize.Find(metric.Trim().ToUpperInvariant());
        if (metric != "custom" && (size is null || !ThreadGeometry.FitsStock(surface, size) ||
            DiameterError(size) > Math.Max(0.05, size.Pitch * 0.16)))
            throw new ArgumentException("Use a shaft at the nominal diameter or a bore sized for tapping.");
        pitch = size?.Pitch ?? pitch;
        depth = size?.RadialDepth(surface.External) ?? depth;
        if (length == 0) length = Math.Min(surface.Length, pitch * 40);
        var fromEnd = surfaceIndex < 0 || part.Holes![surfaceIndex].Through || part.Holes[surfaceIndex].FromEnd;
        var thread = new ThreadSpec(surfaceIndex, pitch, depth, length, false, fromEnd, size?.Name);
        return FeatureResult(part with { Threads = [.. (part.Threads ?? []).Where(item => item.Surface != surfaceIndex), thread] },
            "The thread does not fit this surface. Try a shorter length or allow more clearance.");
    }

    private static FeaturePickRequest FeatureRequest(string json) =>
        JsonSerializer.Deserialize(json, BrowserJson.Default.FeaturePickRequest)
            ?? throw new ArgumentException("Select a body.");

    private static string FeatureResult(Part part, string error) => part.IsValid()
        ? JsonSerializer.Serialize(part, BrowserJson.Default.Part)
        : throw new ArgumentException(error);

    [JSExport]
    public static string Constrain(string json)
    {
        var request = JsonSerializer.Deserialize(json, BrowserJson.Default.ConstraintRequest);
        if (request is null || request.Points.Length is < 3 or > SketchGeometry.MaxVertices ||
            (uint)request.Preferred >= request.Points.Length ||
            request.Rules.Any(rule => (uint)rule.Vertex >= request.Points.Length || !Enum.IsDefined(rule.Kind)))
            throw new ArgumentException("Select a valid sketch vertex.");
        var solved = SketchConstraintSolver.Solve(request.Points, request.Rules, request.Preferred);
        if (!SketchGeometry.TryTriangulate(solved, out _, out _))
            throw new ArgumentException("That constraint would make the profile invalid.");
        return JsonSerializer.Serialize(solved, BrowserJson.Default.SketchPointArray);
    }

    [JSExport]
    public static string Model(string tool, string json, double a, double b, double c, double d)
    {
        var parts = JsonSerializer.Deserialize(json, BrowserJson.Default.PartArray) ?? [];
        if (parts.Any(part => !part.IsValid()) || !new[] { a,b,c,d }.All(double.IsFinite))
            throw new ArgumentException("Invalid modeling inputs.");
        var mesh = tool switch
        {
            "Sphere" when Part.ValidDimension(a) => ModelingMeshes.Sphere(a / 2),
            "Torus" when Part.ValidDimension(a) && Part.ValidDimension(b) && b < a => ModelingMeshes.Torus(a,b),
            "Coil" when Part.ValidDimension(a) && Part.ValidDimension(b) && b < a && c >= 2*b && c <= 10000 && d is >= 0.25 and <= 30 =>
                ModelingMeshes.Coil(a,b,c,d),
            "Revolve" when parts is [{ Profile: { } outline } sketch] => ModelingMeshes.Revolve(sketch,outline,b != 0,a),
            "Loft" when parts.Count(part => part.Profile is not null) >= 2 =>
                ModelingMeshes.Loft(parts.Where(part => part.Profile is not null).Select(part => (part,part.Profile!)).ToArray(),
                    parts.LastOrDefault() is { Profile: null, OpenPaths: { Length: > 0 } rail } guide ? (guide, rail[0]) : null),
            "Sweep" when parts is [{ Profile: { } sweepProfile }, { OpenPaths: { Length: > 0 } sweepPaths } pathSketch] =>
                ModelingMeshes.Sweep(pathSketch, sweepPaths[0], sweepProfile, centerProfile: false),
            "Pipe" when parts is [{ OpenPaths: { Length: > 0 } paths } sketch] && a > b && b > 0 =>
                ModelingMeshes.Pipe(sketch,paths[0],a,b),
            "Rib" when parts is [{ OpenPaths: { Length: > 0 } paths } sketch] &&
                Part.ValidDimension(a) && Part.ValidDimension(b) =>
                ModelingMeshes.Sweep(sketch, paths[0],
                    [new(-a / 2, 0), new(a / 2, 0), new(a / 2, b), new(-a / 2, b)], centerProfile: false),
            _ => null
        };
        if (mesh is null || !new MeshData(mesh.ToArray()).IsValid())
            throw new ArgumentException("The tool could not build a valid body from these inputs.");
        return JsonSerializer.Serialize(mesh, BrowserJson.Default.ListTriangle);
    }
}

[JsonSerializable(typeof(Part))]
[JsonSerializable(typeof(Part[]))]
[JsonSerializable(typeof(List<Triangle>))]
[JsonSerializable(typeof(FacePickRequest))]
[JsonSerializable(typeof(FacePickResult))]
[JsonSerializable(typeof(FeaturePickRequest))]
[JsonSerializable(typeof(ConstraintRequest))]
[JsonSerializable(typeof(SketchPoint[]))]
internal partial class BrowserJson : JsonSerializerContext;

internal sealed record FacePickRequest(Part Part, Point3D Origin, Vector3D Direction);
internal sealed record FacePickResult(Point3D Center, SketchFrame Frame, SketchPoint[][] Guide, double Distance, Point3D Point);
internal sealed record FeaturePickRequest(Part Part, string? Kind, Point3D? Point, SketchFrame? Frame);
internal sealed record ConstraintRequest(SketchPoint[] Points, ProfileConstraint[] Rules, int Preferred);
