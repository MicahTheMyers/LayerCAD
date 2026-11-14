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
        return JsonSerializer.Serialize(new FacePickResult(center, surface.Frame, guide, distance), BrowserJson.Default.FacePickResult);
    }

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
[JsonSerializable(typeof(ConstraintRequest))]
[JsonSerializable(typeof(SketchPoint[]))]
internal partial class BrowserJson : JsonSerializerContext;

internal sealed record FacePickRequest(Part Part, Point3D Origin, Vector3D Direction);
internal sealed record FacePickResult(Point3D Center, SketchFrame Frame, SketchPoint[][] Guide, double Distance);
internal sealed record ConstraintRequest(SketchPoint[] Points, ProfileConstraint[] Rules, int Preferred);
