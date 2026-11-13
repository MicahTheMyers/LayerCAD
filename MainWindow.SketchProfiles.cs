using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private (Part Sketch, int Index)? sketchProfileSelection;
    private readonly Dictionary<Part, HashSet<int>> regionSketchSelections = new(ReferenceEqualityComparer.Instance);

    private static IEnumerable<(int Index, int Start, int Count)> SketchDrawingRanges(Part sketch)
    {
        int index = 0, start = 0;
        foreach (int count in sketch.SketchProfiles.Select(profile => profile.Length - 2)
                     .Concat((sketch.OpenPaths ?? []).Select(path => (path.Length - 1) * 2)))
        {
            yield return (index++, start, count);
            start += count;
        }
    }

    private int SelectedSketchProfile(Part sketch) =>
        sketchProfileSelection is { } selected && ReferenceEquals(selected.Sketch, sketch) ? Math.Max(0, selected.Index) : 0;

    private void UpdateSketchProfileHighlight()
    {
        SketchProfileHighlight.Content = null;
        if (SelectingSurface)
            return;
        if (regionSketchSelections.Count > 0 && (featureSource is null || batchExtrusionTargets.Length > 0))
        {
            var group = new Model3DGroup();
            foreach (var (owner, mesh) in pickableParts.Values)
                if (regionSketchSelections.TryGetValue(owner, out var selected))
                    group.Children.Add(SketchProfileModel(owner,
                        SketchDrawingRanges(owner).Where(drawing => selected.Contains(drawing.Index))
                            .SelectMany(drawing => mesh.GetRange(drawing.Start, drawing.Count))));
            SketchProfileHighlight.Content = group;
            return;
        }
        if (Selected is not { Kind: PartKind.Sketch, AdditionalProfiles: not null } sketch || lassoSelection.Count > 0)
            return;
        int index = ReferenceEquals(featureSource, sketch) ? ExtrudeProfileChoice.SelectedIndex : SelectedSketchProfile(sketch);
        var triangles = pickableParts.Values.FirstOrDefault(entry => ReferenceEquals(entry.Part, sketch)).Triangles;
        if (index < 0 || triangles is null)
            return;
        int start = sketch.SketchProfiles.Take(index).Sum(profile => profile.Length - 2);
        int count = sketch.SketchProfiles.ElementAt(index).Length - 2;
        SketchProfileHighlight.Content = SketchProfileModel(sketch, triangles.Skip(start).Take(count));
    }

    private GeometryModel3D SketchProfileModel(Part sketch, IEnumerable<Triangle> triangles)
    {
        var normal = sketch.SketchVertex(default, 1) - sketch.SketchVertex(default, 0);
        return MakeModel(triangles.Select(triangle => triangle.Translate(normal * FaceHighlightLift)),
            Color.FromArgb(160, 255, 173, 92), bothSides: true);
    }

    private void EditProfileFromViewport(Part sketch, int index, bool delete, object sender, RoutedEventArgs e)
    {
        PartList.SelectedItem = sketch;
        sketchProfileSelection = (sketch, index);
        EditSketch(sender, e);
        if (SketchHost.Content is SketchEditor editor)
            editor.EditProfile(index, delete);
    }
}
