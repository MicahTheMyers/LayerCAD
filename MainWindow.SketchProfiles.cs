using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private (Part Sketch, int Index)? sketchProfileSelection;

    private int SelectedSketchProfile(Part sketch) =>
        sketchProfileSelection is { } selected && ReferenceEquals(selected.Sketch, sketch) ? Math.Max(0, selected.Index) : 0;

    private void UpdateSketchProfileHighlight()
    {
        SketchProfileHighlight.Content = null;
        if (Selected is not { Kind: PartKind.Sketch, AdditionalProfiles: not null } sketch || SelectingSurface)
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
