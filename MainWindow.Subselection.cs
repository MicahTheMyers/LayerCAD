using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private sealed record SharpEdge(Point3D Start, Point3D End, List<Vector3D> Normals)
    {
        public bool FacesCamera(Point3D eye) => Normals.Any(normal => Vector3D.DotProduct(eye - Start, normal) > 0);
        public Point3D Other(Point3D end) => end == Start ? End : Start;
    }

    private sealed record SolidEdges(Dictionary<Point3D, List<SharpEdge>> Joints, HashSet<Point3D> Corners, SharpEdge[] Edges);
    private Part? selectionMeshPart;
    private readonly ConditionalWeakTable<List<Triangle>, SolidEdges> solidEdges = new();
    private (SketchSurface Surface, Point3D Origin)? selectedFace;
    private readonly List<(SketchSurface Surface, Point3D Origin)> selectedFaces = [];
    private GeometryModel3D? selectedFaceHighlight;
    private readonly HashSet<SharpEdge> selectedEdges = [];
    private readonly HashSet<Point3D> selectedCorners = [];

    private void HoverSolidFace(Point mouse)
    {
        var hit = HitScene(mouse).Part;
        if (HoverExtrusionPreview(mouse, hit))
            return;
        if (DetailModel(mouse, hit) is { } model && ShowHoverDetail(mouse, model, replaceFace: true))
            return;
        HoverEdgeMarker.Data = HoverCornerMarker.Data = null;
        if (hit is not null && pickableParts[hit.ModelHit] is { Part.Kind: PartKind.Sketch } sketch)
        {
            if (featureSource?.Kind != PartKind.Sketch)
            {
                if (hoveredModel is not null)
                    ResetSurfaceHover();
                return;
            }
            var (index, start, count) = sketch.Part.SketchProfileRange(hit.TriangleIndex);
            if (ReferenceEquals(hoveredModel, hit.ModelHit) && hoveredTriangle == index)
                return;
            ResetSurfaceHover();
            hoveredModel = hit.ModelHit;
            hoveredTriangle = index;
            SurfaceHighlight.Content = SketchProfileModel(sketch.Part, sketch.Triangles.Skip(start).Take(count));
            return;
        }
        if (hit is null)
        {
            if (hoveredModel is not null)
                ResetSurfaceHover();
            return;
        }
        if (ReferenceEquals(hoveredModel, hit.ModelHit) &&
            (hoveredTriangle == hit.TriangleIndex || hoveredFace?.Contains(hit.TriangleIndex) == true))
            return;

        ResetSurfaceHover();
        hoveredModel = hit.ModelHit;
        hoveredTriangle = hit.TriangleIndex;
        var (part, triangles) = pickableParts[hit.ModelHit];
        var translation = previewTranslations.GetValueOrDefault(hit.ModelHit);
        if (featureSource is null && selectedFaces.FirstOrDefault(item =>
                ReferenceEquals(item.Surface.Part, part) && item.Surface.Contains(hit.TriangleIndex)) is { Surface: not null } selected)
        {
            hoveredFace = selected.Surface;
            return;
        }
        hoveredFace = FindSketchableFace(part, triangles, hit with { PointHit = hit.PointHit - translation });
        if (hoveredFace is { } face)
            SurfaceHighlight.Content = MakeModel(face.Highlight(FaceHighlightLift).Select(triangle => triangle.Translate(translation)),
                Color.FromArgb(83, 255, 187, 113));
    }

    private bool ShowHoverDetail(Point mouse, GeometryModel3D model, bool replaceFace)
    {
        var topology = solidEdges.GetValue(pickableParts[model].Triangles, static mesh => FindSolidEdges(mesh));
        var translation = previewTranslations.GetValueOrDefault(model);
        var (corner, edge) = FindDetail(mouse, topology, translation);
        if (replaceFace && (corner is not null || edge is not null))
            ResetSurfaceHover();
        HoverCornerMarker.Data = corner is Point3D point && ProjectViewportPoint(point + translation) is Point screen
            ? new EllipseGeometry(screen, 5, 5) : null;
        HoverEdgeMarker.Data = null;
        if (edge is not null)
        {
            var outline = new GeometryGroup();
            AddBevelLine(outline, edge.Start + translation, edge.End + translation);
            HoverEdgeMarker.Data = outline;
        }
        return corner is not null || edge is not null;
    }

    private double FaceHighlightLift => Math.Clamp(Math.Max(bed.X, bed.Y) * 0.00004, 0.00001, 0.02);

    private bool PickSolidDetail(Point mouse) => PickSolidDetailAt(mouse, ExtendViewportSelection);

    private bool PickSolidDetailAt(Point mouse, bool extend)
    {
        bool toggle = extend && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var hit = HitScene(mouse).Part;
        var model = DetailModel(mouse, hit);
        if (model is null)
        {
            if (!extend)
                ClearSolidDetail();
            return false;
        }
        var entry = pickableParts[model];
        if (!ReferenceEquals(selectionMeshPart, entry.Part))
        {
            selectedEdges.Clear();
            selectedCorners.Clear();
            selectionMeshPart = entry.Part;
        }
        var topology = solidEdges.GetValue(entry.Triangles, static mesh => FindSolidEdges(mesh));
        var (corner, nearest) = FindDetail(mouse, topology);
        if (corner is Point3D point)
        {
            if (!extend)
                ClearSolidDetail();
            if (!selectedCorners.Add(point) && toggle)
                selectedCorners.Remove(point);
            CompleteDetailSelection(entry.Part, "Corner");
            return true;
        }
        if (nearest is null)
        {
            if (hit is not null && ReferenceEquals(hit.ModelHit, model) && PickSolidFace(hit, entry.Part, entry.Triangles, extend, toggle))
                return true;
            if (!extend)
                ClearSolidDetail();
            return false;
        }
        if (!extend)
            ClearSolidDetail();
        var chain = new HashSet<SharpEdge> { nearest };
        ExtendChain(nearest, nearest.Start);
        ExtendChain(nearest, nearest.End);
        if (toggle && chain.All(selectedEdges.Contains))
            selectedEdges.ExceptWith(chain);
        else
            selectedEdges.UnionWith(chain);
        CompleteDetailSelection(entry.Part, "Edge");
        return true;

        void ExtendChain(SharpEdge edge, Point3D end)
        {
            while (!topology.Corners.Contains(end) && topology.Joints[end] is { Count: 2 } adjacent)
            {
                var next = ReferenceEquals(adjacent[0], edge) ? adjacent[1] : adjacent[0];
                if (!chain.Add(next))
                    break;
                end = next.Other(end);
                edge = next;
            }
        }
    }

    private GeometryModel3D? DetailModel(Point mouse, SceneHit? hit)
    {
        var model = hit?.ModelHit;
        if (model is null && Selected is { Hidden: false } selected)
            model = pickableParts.FirstOrDefault(entry => ReferenceEquals(entry.Value.Part, selected)).Key;
        if (model is null)
        {
            foreach (var offset in new Vector[] { new(4, 0), new(-4, 0), new(0, 4), new(0, -4) })
            {
                model = HitScene(mouse + offset).Part?.ModelHit;
                if (model is not null)
                    break;
            }
        }
        return model is not null && pickableParts[model].Part.Kind != PartKind.Sketch ? model : null;
    }

    private (Point3D? Corner, SharpEdge? Edge) FindDetail(Point mouse, SolidEdges topology, Vector3D translation = default)
    {
        foreach (var corner in topology.Corners
                     .Select(point => (Point: point, Screen: ProjectViewportPoint(point + translation)))
                     .Where(item => item.Screen is Point screen && (mouse - screen).LengthSquared < 100)
                     .OrderBy(item => (mouse - item.Screen!.Value).LengthSquared))
        {
            if (!topology.Joints[corner.Point].Any(edge => edge.FacesCamera(Camera.Position - translation)) ||
                !IsDetailVisible(corner.Point + translation, corner.Screen!.Value))
                continue;
            return (corner.Point, null);
        }

        SharpEdge? nearest = null;
        double gap = 8;
        foreach (var edge in topology.Edges)
        {
            if (!edge.FacesCamera(Camera.Position - translation) ||
                ProjectViewportPoint(edge.Start + translation) is not Point start || ProjectViewportPoint(edge.End + translation) is not Point end)
                continue;
            var line = end - start;
            if (line.LengthSquared < 1e-8)
                continue;
            double fraction = Math.Clamp(Vector.Multiply(mouse - start, line) / line.LengthSquared, 0, 1);
            var projected = start + fraction * line;
            double distance = (mouse - projected).Length;
            if (distance >= gap || !IsDetailVisible(edge.Start + translation + fraction * (edge.End - edge.Start), projected))
                continue;
            gap = distance;
            nearest = edge;
        }
        return (null, nearest);
    }

    private bool PickSolidFace(SceneHit hit, Part part, List<Triangle> triangles, bool extend, bool toggle)
    {
        int index = selectedFaces.FindIndex(item => ReferenceEquals(item.Surface.Part, part) && item.Surface.Contains(hit.TriangleIndex));
        var face = index >= 0 ? selectedFaces[index].Surface : null;
        if (ReferenceEquals(hoveredModel, hit.ModelHit) && hoveredFace?.Contains(hit.TriangleIndex) == true)
            face ??= hoveredFace;
        face ??= FindSketchableFace(part, triangles, hit);
        if (face is null)
            return false;

        selectedEdges.Clear();
        selectedCorners.Clear();
        if (!extend)
            selectedFaces.Clear();
        if (extend && index >= 0)
        {
            if (toggle)
                selectedFaces.RemoveAt(index);
        }
        else
            selectedFaces.Add((face, hit.PointHit));
        RebuildFaceHighlights();
        ResetSurfaceHover();
        CompleteDetailSelection(part, "Face");
        return true;
    }

    private bool IsDetailVisible(Point3D detail, Point projected)
    {
        var hit = HitScene(projected).Part;
        if (hit is null)
            return true;
        double distance = (detail - Camera.Position).Length;
        return (hit.PointHit - Camera.Position).Length >= distance - Math.Max(0.03, distance * 0.005);
    }

    private void CompleteDetailSelection(Part part, string type)
    {
        partPopupDismissed = false;
        PartList.SelectedItem = part;
        UpdatePartPopup();
        UpdateSolidDetailMarkers();
        SetStatus(selectedFaces.Count > 1 ? $"{selectedFaces.Count} faces selected." : selectedFace is not null ? $"Selected a face on {part.Name}. Create Sketch opens on this face." :
            selectedEdges.Count > 0 || selectedCorners.Count > 0
            ? $"{type} selection updated on {part.Name}." : $"Selected {part.Name}.");
    }

    private void RebuildFaceHighlights()
    {
        selectedFace = selectedFaces.Count > 0 ? selectedFaces[^1] : null;
        selectedFaceHighlight = selectedFaces.Count == 0 ? null :
            MakeModel(selectedFaces.SelectMany(item => item.Surface.Highlight(FaceHighlightLift)),
                Color.FromArgb(126, 255, 164, 73));
    }

    private void ClearSelectedFace()
    {
        selectedFaces.Clear();
        selectedFace = null;
        selectedFaceHighlight = null;
        SolidFaceHighlight.Content = null;
    }

    private void ClearSolidDetail()
    {
        ClearSelectedFace();
        selectedEdges.Clear();
        selectedCorners.Clear();
        SolidEdgeMarkers.Data = SolidCornerMarkers.Data = null;
    }

    private void UpdateSolidDetailMarkers()
    {
        SolidEdgeMarkers.Data = SolidCornerMarkers.Data = null;
        if (SelectingSurface || featureSource is not null)
        {
            SolidFaceHighlight.Content = null;
            return;
        }
        SolidFaceHighlight.Content = selectedFaceHighlight;
        if (!ReferenceEquals(Selected, selectionMeshPart))
            return;
        var edges = new GeometryGroup();
        foreach (var edge in selectedEdges.Where(edge => edge.FacesCamera(Camera.Position)))
            AddBevelLine(edges, edge.Start, edge.End);
        SolidEdgeMarkers.Data = edges;
        var corners = new GeometryGroup();
        foreach (var corner in selectedCorners)
        {
            if (ProjectViewportPoint(corner) is Point screen && IsDetailVisible(corner, screen))
                corners.Children.Add(new EllipseGeometry(screen, 4.5, 4.5));
        }
        SolidCornerMarkers.Data = corners;
    }

    private static SolidEdges FindSolidEdges(IEnumerable<Triangle> triangles)
    {
        var mesh = new Dictionary<(Point3D, Point3D), SharpEdge>();
        foreach (var triangle in triangles)
        {
            var normal = BevelFacetNormal(triangle);
            Add(triangle.A, triangle.B, normal);
            Add(triangle.B, triangle.C, normal);
            Add(triangle.C, triangle.A, normal);
        }
        var joints = new Dictionary<Point3D, List<SharpEdge>>();
        foreach (var edge in mesh.Values)
        {
            if (edge.Normals.Count > 1 && edge.Normals.Skip(1)
                    .All(normal => Vector3D.DotProduct(normal, edge.Normals[0]) > 0.96))
                continue;
            Join(edge.Start, edge);
            Join(edge.End, edge);
        }
        return new SolidEdges(joints, joints.Where(pair => IsCorner(pair.Key, pair.Value)).Select(pair => pair.Key).ToHashSet(), joints.Values.SelectMany(edges => edges).Distinct().ToArray());

        void Add(Point3D a, Point3D b, Vector3D normal)
        {
            if (!mesh.TryGetValue((a, b), out var edge) && !mesh.TryGetValue((b, a), out edge))
                mesh.Add((a, b), edge = new SharpEdge(a, b, []));
            edge.Normals.Add(normal);
        }

        void Join(Point3D point, SharpEdge edge)
        {
            if (!joints.TryGetValue(point, out var edges))
                joints.Add(point, edges = []);
            edges.Add(edge);
        }

        static bool IsCorner(Point3D point, List<SharpEdge> edges)
        {
            if (edges.Count != 2)
                return edges.Count > 2;
            var a = edges[0].Other(point) - point;
            var b = edges[1].Other(point) - point;
            return Vector3D.DotProduct(a, b) > -0.96 * a.Length * b.Length;
        }
    }
}
