using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LayerCAD;

public partial class SketchEditor
{
    private static readonly Brush DrawingSelectionFill = new SolidColorBrush(Color.FromArgb(66, 255, 165, 0));
    private readonly HashSet<List<SketchPoint>> selectedDrawings = [];
    private bool drawingToolActive = true;
    private bool extendDrawingSelection;
    private bool toggleDrawingSelection;
    private List<Point>? drawingSelectionPath;

    private void ChangeSketchSelectionMode(object sender, RoutedEventArgs e) =>
        EnterSketchSelection(ReferenceEquals(sender, SketchLassoButton));

    private void EnterSketchSelection(bool lasso = false)
    {
        EndSketchTrim();
        EndSketchMove(commit: false);
        CloseDrawingOptions();
        drawingToolActive = false;
        activeTool.ClearValue(BackgroundProperty);
        SketchSelectButton.IsChecked = !lasso;
        SketchLassoButton.IsChecked = lasso;
        PlacePointButton.IsEnabled = false;
        ProfileChoice.IsEnabled = true;
        CancelPlacement();
        Focus();
    }

    private void BeginDrawingSelection(Point location)
    {
        toggleDrawingSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        extendDrawingSelection = toggleDrawingSelection || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        drawingSelectionPath = [location];
        Focus();
    }

    private void UpdateDrawingSelection(Point location)
    {
        var path = drawingSelectionPath!;
        bool freehand = SketchLassoButton.IsChecked == true;
        if (freehand && (location - path[^1]).LengthSquared >= 9)
            path.Add(location);
        SketchSelectionOutline.Stroke = (freehand ? ViewportSelection.IsClockwise(path) : location.X >= path[0].X)
            ? ViewportSelection.WindowStroke : ViewportSelection.CrossingStroke;
        SketchSelectionOutline.Data = ViewportSelection.Geometry(freehand ? path : ViewportSelection.Box(path[0], location));
    }

    private void EndDrawingSelection(Point location, bool click)
    {
        var path = drawingSelectionPath!;
        if (!click)
            UpdateDrawingSelection(location);
        bool freehand = SketchLassoButton.IsChecked == true;
        var region = freehand ? path.ToArray() : ViewportSelection.Box(path[0], location);
        bool enclose = freehand ? ViewportSelection.IsClockwise(region) : location.X >= path[0].X;
        bool extend = extendDrawingSelection;
        Drawing.ReleaseMouseCapture();
        ClearDrawingSelectionGesture();
        if (click)
        {
            var hit = profiles.LastOrDefault(profile => DrawingContains(profile, location));
            SetDrawingSelection(hit is null ? [] : [hit], extend, toggle: toggleDrawingSelection);
        }
        else if (region.Length >= 3)
            SelectDrawingRegion(region, extend, enclose);
    }

    private void ClearDrawingSelectionGesture()
    {
        drawingSelectionPath = null;
        SketchSelectionOutline.Data = null;
    }

    private void SelectDrawingRegion(IReadOnlyList<Point> region, bool extend, bool enclose)
    {
        var area = ViewportSelection.Geometry(region);
        var stroke = new Pen(Brushes.White, 2);
        SetDrawingSelection(profiles.Where(profile =>
        {
            if (!openProfiles.Contains(profile))
            {
                var outline = ProjectOutline(profile);
                return enclose
                    ? profile.All(point => sketchProjection.IsVisible(WorldPoint(point))) && ViewportSelection.Encloses(area, outline)
                    : outline.Length >= 3 && ViewportSelection.Overlaps(area, outline);
            }
            if (enclose)
                return profile.Zip(profile.Skip(1)).All(pair =>
                    sketchProjection.Project(WorldPoint(pair.First)) is Point a && sketchProjection.Project(WorldPoint(pair.Second)) is Point b &&
                    area.FillContainsWithDetail(new LineGeometry(a, b).GetWidenedPathGeometry(stroke)) == IntersectionDetail.FullyContains);
            for (int i = 1; i < profile.Count; i++)
            {
                if (sketchProjection.Segment(WorldPoint(profile[i - 1]), WorldPoint(profile[i])) is { } segment &&
                    area.FillContainsWithDetail(new LineGeometry(segment.Start, segment.End).GetWidenedPathGeometry(stroke))
                        != IntersectionDetail.Empty)
                    return true;
            }
            return false;
        }), extend, toggle: toggleDrawingSelection);
    }

    private void SetDrawingSelection(IEnumerable<List<SketchPoint>> drawings, bool extend, bool toggle)
    {
        if (!extend)
            selectedDrawings.Clear();
        foreach (var drawing in drawings)
        {
            if (!selectedDrawings.Add(drawing) && toggle)
                selectedDrawings.Remove(drawing);
        }
        var active = profiles.LastOrDefault(selectedDrawings.Contains);
        if (active is not null && !selectedDrawings.Contains(points))
            points = active;
        Refresh(-1);
        Feedback.Text = selectedDrawings.Count == 1 ? "Selected 1 drawing." : $"Selected {selectedDrawings.Count} drawings.";
    }
}
