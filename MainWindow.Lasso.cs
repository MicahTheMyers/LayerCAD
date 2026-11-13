using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private readonly HashSet<Part> lassoSelection = new(ReferenceEqualityComparer.Instance);
    private List<Point>? lassoPoints;
    private bool boxSelectionPress;
    private bool changingViewportSelection;
    private static bool ExtendViewportSelection => (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

    private bool CanLasso => LassoModeButton.IsChecked == true && !measuring && !SelectingSurface && featureSource is null;

    private void ChangeViewportMouseMode(object sender, RoutedEventArgs e)
    {
        ResetSurfaceHover();
        UpdatePartPopup();
        UpdateViewportMouseHint();
    }

    private void UpdateViewportMouseHint() => ViewportMouseHint.Text = featureMove
        ? "Drag to move · Scroll to zoom" : CanLasso
            ? "Lasso clockwise to enclose · Counterclockwise to cross" : "Drag right to enclose · Drag left to cross";

    private bool BeginLasso(Point point)
    {
        if (!CanLasso || !OrbitSurface.CaptureMouse())
            return false;
        ResetSurfaceHover();
        lassoPoints = [point];
        OrbitSurface.Cursor = Cursors.Cross;
        return true;
    }

    private void UpdateLasso(Point point)
    {
        if (lassoPoints is not { } points || (point - points[^1]).LengthSquared < 9)
            return;
        points.Add(point);
        LassoOutline.Stroke = ViewportSelection.IsClockwise(points) ? ViewportSelection.WindowStroke : ViewportSelection.CrossingStroke;
        LassoOutline.Data = ViewportSelection.Geometry(points);
    }

    private void EndLasso(Point point, bool extend)
    {
        var points = lassoPoints!;
        UpdateLasso(point);
        CancelLasso();
        if (points.Count < 3)
        {
            PickViewportSelection(point, extend);
            return;
        }
        SelectRegion(points, extend, ViewportSelection.IsClockwise(points));
    }

    private void CancelLasso()
    {
        if (lassoPoints is null)
            return;
        ClearLassoGesture();
        OrbitSurface.ReleaseMouseCapture();
    }

    private void ClearLassoGesture()
    {
        if (lassoPoints is null && !boxSelectionPress)
            return;
        lassoPoints = null;
        boxSelectionPress = false;
        LassoOutline.Data = null;
        OrbitSurface.Cursor = null;
    }

    private bool CompleteBoxSelection(Point point, bool extend)
    {
        if (!boxSelectionPress || selectionPress is not Point start || (point - start).LengthSquared < 16)
            return false;
        var box = ViewportSelection.Box(start, point);
        OrbitSurface.ReleaseMouseCapture();
        SelectRegion(box, extend, enclose: point.X >= start.X);
        return true;
    }

    private void SelectRegion(IReadOnlyList<Point> points, bool extend, bool enclose)
    {
        var lasso = ViewportSelection.Geometry(points);
        var selection = pickableParts.Values
            .Where(entry => enclose ? EnclosesPart(entry.Triangles, lasso) :
                IntersectsLasso(entry.Part, entry.Triangles, lasso))
            .Select(entry => entry.Part).ToArray();
        if (extend && lassoSelection.Count == 0 && Selected is { Hidden: false } current)
            lassoSelection.Add(current);
        if (extend && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            lassoSelection.SymmetricExceptWith(selection);
            selectedFaces.RemoveAll(face => !lassoSelection.Contains(face.Surface.Part));
            selection = lassoSelection.ToArray();
        }
        SetLassoSelection(selection, extend, preserveFaces: true);
        SelectFacesInRegion(lasso, extend);
    }

    private bool EnclosesPart(List<Triangle> triangles, StreamGeometry region) =>
        triangles.Count > 0 && triangles.All(triangle =>
            ProjectViewportPoint(triangle.A) is Point a && ProjectViewportPoint(triangle.B) is Point b &&
            ProjectViewportPoint(triangle.C) is Point c &&
            (Math.Abs(Vector.CrossProduct(b - a, c - a)) < 1e-8
                ? region.FillContains(a) && region.FillContains(b) && region.FillContains(c)
                : ViewportSelection.Encloses(region, [a, b, c])));

    private void PickViewportSelection(Point point, bool extend)
    {
        var hit = HitScene(point).Part ?? SketchBoundaryHit(point);
        if (extend && hit is null)
            return;
        if (extend && hit is not null && pickableParts[hit.ModelHit].Part is { Kind: PartKind.Sketch } sketch)
        {
            TogglePartSelection(sketch, toggle: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            return;
        }
        bool redraw = !extend && lassoSelection.Count > 0;
        if (!extend)
            lassoSelection.Clear();
        changingViewportSelection = extend;
        try
        {
            if (!PickSolidDetailAt(point, extend))
                PickPart(point);
        }
        finally
        {
            changingViewportSelection = false;
        }
        if (redraw)
            DrawScene();
    }

    private void TogglePartSelection(Part part, bool toggle = true)
    {
        if (lassoSelection.Count == 0 && Selected is { Hidden: false } previous && selectedFaces.Count == 0)
            lassoSelection.Add(previous);
        if (!lassoSelection.Add(part) && toggle)
            lassoSelection.Remove(part);
        SetLassoSelection(lassoSelection.ToArray(), extend: false, preserveFaces: true);
    }

    private bool IntersectsLasso(Part part, List<Triangle> triangles, StreamGeometry lasso)
    {
        var screenBounds = Rect.Empty;
        bool entirelyProjected = true;
        foreach (var corner in BoundsCorners(PartWorldBounds(part)))
        {
            if (ProjectViewportPoint(corner) is Point projected)
                screenBounds.Union(projected);
            else
                entirelyProjected = false;
        }
        if (entirelyProjected && !screenBounds.IntersectsWith(lasso.Bounds))
            return false;

        foreach (var (a, b, c) in triangles)
            if (TriangleOverlaps(lasso, new Triangle(a, b, c)))
                return true;
        return false;
    }

    private bool TriangleOverlaps(StreamGeometry area, Triangle triangle)
    {
        if (ProjectViewportPoint(triangle.A) is not Point a || ProjectViewportPoint(triangle.B) is not Point b ||
            ProjectViewportPoint(triangle.C) is not Point c)
            return false;
        var bounds = new Rect(a, b);
        bounds.Union(c);
        return bounds.IntersectsWith(area.Bounds) &&
            (area.FillContains(a) || area.FillContains(b) || area.FillContains(c) ||
             ViewportSelection.Overlaps(area, [a, b, c]));
    }

    private void SelectFacesInRegion(StreamGeometry area, bool extend)
    {
        if (!extend)
            selectedFaces.Clear();
        selectedEdges.Clear();
        selectedCorners.Clear();
        foreach (var (model, (part, triangles)) in pickableParts)
        {
            if (part.Kind == PartKind.Sketch || !lassoSelection.Contains(part))
                continue;
            var visited = selectedFaces.Where(face => ReferenceEquals(face.Surface.Part, part))
                .SelectMany(face => face.Surface.TriangleIndices).ToHashSet();
            for (int i = 0; i < triangles.Count; i++)
            {
                var triangle = triangles[i];
                var normal = BevelFacetNormal(triangle);
                if (visited.Contains(i) || Vector3D.DotProduct(Camera.Position - triangle.A, normal) <= 0 ||
                    !TriangleOverlaps(area, triangle))
                    continue;
                var center = triangle.A + ((triangle.B - triangle.A) + (triangle.C - triangle.A)) / 3;
                if (ProjectViewportPoint(center) is not Point projected || !IsDetailVisible(center, projected))
                    continue;
                if (FindSketchableFace(part, triangles, new SceneHit(model, center, i)) is not { } face)
                    continue;
                visited.UnionWith(face.TriangleIndices);
                selectedFaces.Add((face, center));
            }
        }
        RebuildFaceHighlights();
        UpdateSolidDetailMarkers();
        if (selectedFaces.Count > 0)
            SetStatus($"Selected {lassoSelection.Count} items and {selectedFaces.Count} faces.");
    }

    private void SetLassoSelection(Part[] selection, bool extend, bool preserveFaces = false)
    {
        if (!preserveFaces)
            ClearSolidDetail();
        sketchProfileSelection = null;
        if (!extend)
            lassoSelection.Clear();
        lassoSelection.UnionWith(selection);
        var primary = parts.FirstOrDefault(part => part.Kind != PartKind.Sketch && lassoSelection.Contains(part)) ??
            parts.FirstOrDefault(lassoSelection.Contains);
        bool changed = !ReferenceEquals(Selected, primary);
        changingViewportSelection = preserveFaces;
        try
        {
            PartList.SelectedItem = primary;
        }
        finally
        {
            changingViewportSelection = false;
        }
        if (!changed)
        {
            UpdatePartPopup();
            DrawScene();
        }
        SetStatus(lassoSelection.Count switch
        {
            0 => "No items selected.",
            1 => $"Selected {primary!.Name}.",
            _ => $"{lassoSelection.Count} items selected."
        });
    }


}
