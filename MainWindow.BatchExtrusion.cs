using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private sealed record ExtrusionTarget(Part Part, int Profile, FaceExtrusion? Face = null);
    private ExtrusionTarget[] batchExtrusionTargets = [];
    private (Part Source, Part Result)[] batchExtrusionResults = [];

    private ExtrusionTarget[] SelectedExtrusionTargets()
    {
        var targets = new List<ExtrusionTarget>();
        foreach (var sketch in parts.Where(part => !part.Hidden && part.Kind == PartKind.Sketch &&
                     (lassoSelection.Contains(part) || ReferenceEquals(part, Selected))))
        {
            int count = sketch.SketchProfiles.Count();
            IEnumerable<int> profiles = regionSketchSelections.TryGetValue(sketch, out var selection)
                ? selection.Order() : lassoSelection.Contains(sketch) ? Enumerable.Range(0, count)
                : [sketchProfileSelection is { } picked && ReferenceEquals(picked.Sketch, sketch) ? picked.Index : 0];
            targets.AddRange(profiles.Where(index => index >= 0 && index < count)
                .Select(index => new ExtrusionTarget(sketch, index)));
        }

        var faces = selectedFaces.Select(face => FaceExtrusion.Find(face.Surface.Part, face.Origin, face.Surface.Frame.Normal))
            .OfType<FaceExtrusion>();
        foreach (var group in faces.GroupBy(face => face.Source, ReferenceEqualityComparer.Instance))
        {
            // A region selection can include several visible faces of a single body.
            // Use the face aimed most directly at the camera for whole-body selections.
            var first = group.OrderByDescending(face => Vector3D.DotProduct(face.Normal, -Camera.LookDirection)).First();
            if (lassoSelection.Contains(first.Source))
                targets.Add(new ExtrusionTarget(first.Source, 0, first));
            else
                targets.AddRange(group.Select(face => new ExtrusionTarget(face.Source, 0, face)));
        }
        foreach (var body in lassoSelection.Where(part => !part.Hidden && part.Kind != PartKind.Sketch &&
                     !targets.Any(target => ReferenceEquals(target.Part, part))))
        {
            var normal = body.Kind == PartKind.Extrusion
                ? body.SketchVertex(default, 1) - body.SketchVertex(default, 0)
                : new Vector3D(0, 0, 1);
            var bounds = PartWorldBounds(body);
            var center = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            double radius = (Math.Abs(normal.X) * bounds.SizeX + Math.Abs(normal.Y) * bounds.SizeY +
                Math.Abs(normal.Z) * bounds.SizeZ) / 2;
            if (FaceExtrusion.Find(body, center + normal * radius, normal) is { } face)
                targets.Add(new ExtrusionTarget(body, 0, face));
        }
        return targets.ToArray();
    }

    private void UpdateBatchExtrusionOptions()
    {
        bool single = batchExtrusionTargets.Length == 0;
        RemoveMaterial.IsEnabled = MergeMaterial.IsEnabled = single;
        if (!single)
            FeatureTitle.Text = $"Extrude {batchExtrusionTargets.Length} targets";
    }

    private BuildChange? PreviewBatchExtrusion(double amount)
    {
        batchExtrusionResults = [];
        var results = new List<(Part Source, Part Result)>();
        var updatedBodies = new Dictionary<Part, Part>(ReferenceEqualityComparer.Instance);
        var region = new List<Triangle>();
        BuildChange? first = null;
        foreach (var target in batchExtrusionTargets)
        {
            var change = target.Face is { } face
                ? (face with { Source = updatedBodies.GetValueOrDefault(target.Part, target.Part) }).Preview(amount, remove: false)
                : PreviewSketchExtrusion(target.Part, amount, target.Profile);
            if (change is null)
            {
                extrusionFeedback = $"Adjust the distance so the extrusion fits on {target.Part.Name}.";
                return null;
            }
            first ??= change;
            region.AddRange(change.Region);
            if (target.Face is null)
                results.Add((target.Part, change.Result));
            else
                updatedBodies[target.Part] = change.Result;
        }
        results.AddRange(updatedBodies.Select(pair => (pair.Key, pair.Value)));
        batchExtrusionResults = results.ToArray();
        extrusionFeedback = $"Adds material to {batchExtrusionTargets.Length} targets. Cut and Merge are available for single targets.";
        return first! with { Region = region };
    }

    private void ApplyBatchExtrusion((Part Source, Part Result)[] results)
    {
        if (results.Length == 0 || results.Any(result => !parts.Any(part => ReferenceEquals(part, result.Source))))
            return;
        var links = new Dictionary<Part, Guid>(ReferenceEqualityComparer.Instance);
        var selection = new List<Part>();
        foreach (var (source, result) in results)
        {
            if (source.Kind == PartKind.Sketch)
            {
                if (!links.TryGetValue(source, out var link))
                {
                    link = source.SketchId ?? Guid.NewGuid();
                    links.Add(source, link);
                    int index = parts.FindIndex(part => ReferenceEquals(part, source));
                    parts[index] = source with { SketchId = link,
                        Hidden = source.Hidden || source.AdditionalProfiles is null && source.OpenPaths is null };
                }
                var solid = result with { SketchId = link, Name = NextPartName(PartKind.Extrusion) };
                parts.Add(solid);
                selection.Add(solid);
            }
            else
            {
                parts[parts.FindIndex(part => ReferenceEquals(part, source))] = result;
                selection.Add(result);
            }
        }
        MarkDirty();
        RefreshParts(parts.FindIndex(part => ReferenceEquals(part, selection[0])));
        SetLassoSelection(selection.ToArray(), extend: false);
        OrbitSurface.Focus();
        SetStatus($"Extruded {results.Length} profiles or objects.");
    }
}
