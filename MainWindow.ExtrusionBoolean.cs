using System.Runtime.CompilerServices;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private string? extrusionFeedback;
    private readonly ConditionalWeakTable<Part, SolidBoolean.Solid> booleanStocks = new();

    private BuildChange? PreviewExtrusion(Part source, double amount, bool remove, bool merge)
    {
        extrusionFeedback = null;
        var change = source.Kind == PartKind.Sketch ? PreviewSketchExtrusion(source, amount) :
            remove ? featureExtrusion?.PreviewThroughCut(amount) : featureExtrusion?.Preview(amount, remove: false);
        if (change is null || !remove && !merge)
            return change;

        var bounds = MeshData.GetBounds(change.Region);
        var targets = parts.Where(part => !part.Hidden && part.Kind != PartKind.Sketch &&
            (!ReferenceEquals(part, source) || remove) && bounds.IntersectsWith(PartWorldBounds(part))).ToList();
        if (merge && targets.Count == 0)
        {
            extrusionFeedback = "Extend the extrusion until it meets a solid to merge.";
            return null;
        }

        try
        {
            return remove ? PreviewExtrusionCut(source, change, targets) : PreviewExtrusionMerge(source, change, targets);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            extrusionFeedback = "The solid geometry engine is unavailable in this build.";
            return null;
        }
    }

    private BuildChange? PreviewExtrusionCut(Part source, BuildChange change, List<Part> targets)
    {
        var edits = new Dictionary<Part, Part?>(ReferenceEqualityComparer.Instance);
        using var tool = SolidBoolean.Create(change.Region);
        if (tool.IsInvalid)
            return InvalidBoolean(source);
        foreach (var part in targets)
        {
            if (ReferenceEquals(part, source) && !ReferenceEquals(change.Result, source))
            {
                edits.Add(part, change.Result);
                continue;
            }
            var stock = BooleanStock(part);
            if (stock.IsInvalid)
                return InvalidBoolean(part);
            int outcome = SolidBoolean.Apply(stock, tool, merge: 0, out var result);
            using (result)
            {
                if (outcome < 0)
                    return InvalidBoolean(part);
                if (outcome == 0)
                    continue;
                var mesh = SolidBoolean.ReadMesh(result, Offset(part));
                if (mesh is null)
                    return InvalidBoolean(part);
                var replacement = mesh.Length > 0 ? MeshPart(part, mesh) : null;
                if (replacement?.IsValid() == false)
                    return InvalidBoolean(part);
                edits.Add(part, replacement);
            }
        }
        if (edits.Count == 0)
        {
            extrusionFeedback = "Extend the extrusion until it passes through a solid.";
            return null;
        }
        extrusionFeedback = $"Cuts {edits.Count} {(edits.Count == 1 ? "solid" : "solids")} in the arrow direction.";
        return change with { Removes = true, CutParts = edits };
    }

    private BuildChange? PreviewExtrusionMerge(Part source, BuildChange change, List<Part> targets)
    {
        var merged = new List<Part>();
        using var initial = SolidBoolean.Create(Geometry.Build(change.Result));
        if (initial.IsInvalid)
            return InvalidBoolean(source);
        var combined = initial;
        try
        {
            // A body joined in one pass can connect the extrusion to another body.
            for (bool joined = true; joined && targets.Count > 0;)
            {
                joined = false;
                for (int index = 0; index < targets.Count; index++)
                {
                    var part = targets[index];
                    var stock = BooleanStock(part);
                    if (stock.IsInvalid)
                        return InvalidBoolean(part);
                    int outcome = SolidBoolean.Apply(stock, combined, merge: 1, out var result);
                    if (outcome <= 0)
                    {
                        result.Dispose();
                        if (outcome < 0)
                            return InvalidBoolean(part);
                        continue;
                    }
                    if (!ReferenceEquals(combined, initial))
                        combined.Dispose();
                    combined = result;
                    merged.Add(part);
                    targets.RemoveAt(index--);
                    joined = true;
                }
            }
            if (merged.Count == 0)
            {
                extrusionFeedback = "Extend the extrusion until it meets a solid to merge.";
                return null;
            }
            var basis = source.Kind == PartKind.Sketch ? merged[0] : source;
            var mesh = SolidBoolean.ReadMesh(combined, Offset(basis));
            var replacement = mesh is { Length: > 0 } ? MeshPart(basis, mesh) : null;
            if (replacement?.IsValid() != true)
                return InvalidBoolean(basis);
            if (source.Kind != PartKind.Sketch)
                merged.Insert(0, source);
            extrusionFeedback = $"Joins the extrusion and {merged.Count} {(merged.Count == 1 ? "solid" : "solids")} into one body.";
            return change with { Result = replacement, MergedParts = merged.ToArray() };
        }
        finally
        {
            if (!ReferenceEquals(combined, initial))
                combined.Dispose();
        }
    }

    private BuildChange? InvalidBoolean(Part part)
    {
        extrusionFeedback = $"The solid geometry on {part.Name} could not be combined. Adjust the extrusion or use a closed solid.";
        return null;
    }

    private SolidBoolean.Solid BooleanStock(Part part) =>
        booleanStocks.GetValue(part, item => SolidBoolean.Create(GetRenderedPart(item).Triangles));

    private static Vector3D Offset(Part part) => new(part.X, part.Y, part.Z);

    private static Part MeshPart(Part part, Triangle[] mesh) =>
        new(PartKind.Mesh, part.Name, 0, 0, 0, part.X, part.Y, part.Z, Mesh: new MeshData(mesh));

    private void ApplyExtrusionBoolean(Part source, BuildChange change)
    {
        Part? selected = null;
        string status;
        if (change.CutParts is { } cuts)
        {
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var original = parts[i];
                if (!cuts.TryGetValue(original, out var replacement))
                    continue;
                if (replacement is null)
                    parts.RemoveAt(i);
                else
                {
                    parts[i] = replacement;
                    if (selected is null || ReferenceEquals(original, source))
                        selected = replacement;
                }
            }
            selected ??= parts.FirstOrDefault(part => ReferenceEquals(part, source));
            status = $"Cut {cuts.Count} {(cuts.Count == 1 ? "solid" : "solids")}.";
        }
        else if (change.MergedParts is { } merged)
        {
            var consumed = new HashSet<Part>(merged, ReferenceEqualityComparer.Instance);
            int index = parts.FindIndex(consumed.Contains);
            parts.RemoveAll(consumed.Contains);
            parts.Insert(index, change.Result);
            if (source.Kind == PartKind.Sketch && source.AdditionalProfiles is null && source.OpenPaths is null)
            {
                int sketchIndex = parts.FindIndex(part => ReferenceEquals(part, source));
                parts[sketchIndex] = source with { Hidden = true };
            }
            selected = change.Result;
            status = $"Merged into {change.Result.Name}.";
        }
        else
            return;
        MarkDirty();
        RefreshParts(parts.FindIndex(part => ReferenceEquals(part, selected)));
        OrbitSurface.Focus();
        SetStatus(status);
    }
}
