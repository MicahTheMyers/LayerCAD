using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;

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
internal partial class BrowserJson : JsonSerializerContext;
