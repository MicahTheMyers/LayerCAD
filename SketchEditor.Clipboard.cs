using System.Windows.Input;

namespace LayerCAD;

public partial class SketchEditor
{
    private sealed record CopiedDrawing(SketchPoint[] Points, bool Open);
    private const string DrawingClipboard = LayerClipboard.Profiles + ".Drawing";

    private void HandleSketchClipboard(Key key)
    {
        if (key == Key.A)
        {
            if (drawingToolActive)
                return;
            EndSketchMove(commit: false);
            CancelPlacement();
            SetDrawingSelection(profiles, extend: false, toggle: false);
        }
        else if (key == Key.C)
        {
            var drawings = selectedDrawings.Count > 0 ? profiles.Where(selectedDrawings.Contains).ToArray() : profiles.Contains(points) ? [points] : [];
            var selection = drawings.Select(profile => new CopiedDrawing(profile.ToArray(), openProfiles.Contains(profile))).ToArray();
            Feedback.Text = selection.Length == 0 ? "Select a completed profile to copy." :
                LayerClipboard.Copy(DrawingClipboard, selection)
                    ? $"Copied {selection.Length} {(selection.Length == 1 ? "profile" : "profiles")}." :
                    "The clipboard is unavailable.";
        }
        else
            PasteProfiles();
    }

    private void PasteProfiles()
    {
        var copied = LayerClipboard.Paste<CopiedDrawing[]>(DrawingClipboard) ??
            LayerClipboard.Paste<SketchPoint[][]>(LayerClipboard.Profiles)?.Select(profile => new CopiedDrawing(profile, false)).ToArray();
        if (copied is not { Length: > 0 } || copied.Any(profile => profile is null || (profile.Open
            ? !SketchGeometry.ValidOpenPath(profile.Points) : !SketchGeometry.TryTriangulate(profile.Points, out _, out _))))
        {
            Feedback.Text = "The clipboard has no LayerCAD sketch profiles.";
            return;
        }
        if (profiles.Count + copied.Length > 64)
        {
            Feedback.Text = "A sketch supports up to 64 profiles.";
            return;
        }
        EndSketchMove(commit: false);
        CancelPlacement();
        var before = CaptureSketch();
        double dx = Offset(point => point.X), dy = Offset(point => point.Y);
        foreach (var profile in copied)
        {
            var path = profile.Points.Select(point => new SketchPoint(point.X + dx, point.Y + dy)).ToList();
            profiles.Add(path);
            if (profile.Open)
                openProfiles.Add(path);
        }
        points = profiles[^1];
        RecordSketchEdit(before);
        Refresh(-1);
        Feedback.Text = $"Pasted {copied.Length} {(copied.Length == 1 ? "profile" : "profiles")}.";

        double Offset(Func<SketchPoint, double> coordinate) => copied.SelectMany(profile => profile.Points).All(point => coordinate(point) <= 4995)
            ? 5 : copied.SelectMany(profile => profile.Points).All(point => coordinate(point) >= -4995) ? -5 : 0;
    }
}
