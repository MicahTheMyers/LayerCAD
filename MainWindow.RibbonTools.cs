using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private sealed record OffsetSketchPlane(SketchFrame Frame, Point3D Origin);
    private bool measuring;
    private Point3D? measureStart;
    private Point3D? measureEnd;

    private void BeginOffsetPlane(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        var dialog = new ModelingDialog(this, "Sketch on Offset Plane", units);
        var face = selectedFace is { } selection && ReferenceEquals(Selected, selection.Surface.Part)
            ? selectedFace : null;
        string[] choices = face is null ? ["XY", "XZ", "YZ"] : ["Selected face", "XY", "XZ", "YZ"];
        var plane = dialog.Choice("Reference plane", choices);
        var offset = dialog.Length("Offset", 10, signed: true);
        var setup = dialog.RunValue(() => OffsetPlane(plane(), offset(), face), "Start Sketch");
        if (setup is null)
            return;
        OpenSketch(SketchPlane.XY, null, (profiles, paths, _, dimensions, constraints, referenceImage) =>
        {
            var (x, y, z) = (setup.Origin.X, setup.Origin.Y, setup.Origin.Z);
            AddFinishedSketch(new Part(PartKind.Sketch, NextPartName(PartKind.Sketch), 0, 0, 0, x, y, z,
                profiles.FirstOrDefault(), FaceFrame: setup.Frame, AdditionalProfiles: profiles.Length > 1 ? profiles[1..] : null,
                OpenPaths: paths.Length > 0 ? paths : null, Dimensions: dimensions, Constraints: constraints, ReferenceImage: referenceImage));
        }, setup.Frame, null, setup.Origin);
    }

    private static OffsetSketchPlane OffsetPlane(string reference, double offset, (SketchSurface Surface, Point3D Origin)? face)
    {
        var (frame, origin) = reference == "Selected face" && face is { } selected
            ? (selected.Surface.Frame, selected.Origin)
            : (reference switch
            {
                "XZ" => new SketchFrame(new(1, 0, 0), new(0, 0, 1)),
                "YZ" => new SketchFrame(new(0, 1, 0), new(0, 0, 1)),
                _ => new SketchFrame(new(1, 0, 0), new(0, 1, 0))
            }, new Point3D());
        var shifted = origin + frame.Normal * offset;
        if (!Part.ValidOffset(shifted.X) || !Part.ValidOffset(shifted.Y) || !Part.ValidOffset(shifted.Z))
            throw new System.ArgumentException("The offset plane is outside the supported coordinate range.");
        return new(frame, shifted);
    }

    private void BeginMeasure(object sender, RoutedEventArgs e)
    {
        bool start = !measuring;
        EndBuildFeature();
        EndSurfaceSelection();
        if (!start)
        {
            SetStatus("Measure closed.");
            return;
        }
        measuring = true;
        ResetSurfaceHover();
        MeasureButton.Background = new SolidColorBrush(Color.FromRgb(36, 106, 98));
        OrbitSurface.Focus();
        SetStatus("Measure · Click two points on visible geometry. Esc closes Measure.");
    }

    private void EndMeasure()
    {
        if (!measuring)
            return;
        measuring = false;
        measureStart = measureEnd = null;
        MeasureOverlay.Data = null;
        MeasureButton.ClearValue(BackgroundProperty);
    }

    private void PickMeasurePoint(Point pointer)
    {
        var hit = HitScene(pointer).Part ?? SketchBoundaryHit(pointer);
        if (hit is null)
        {
            SetStatus("Click a point on a visible solid or sketch.");
            return;
        }
        var point = hit.PointHit;
        if (pickableParts[hit.ModelHit] is { Part.Kind: not PartKind.Sketch } entry &&
            FindDetail(pointer, solidEdges.GetValue(entry.Triangles, static mesh => FindSolidEdges(mesh))).Corner is Point3D corner)
            point = corner;
        if (measureStart is not Point3D start || measureEnd is not null)
        {
            measureStart = point;
            measureEnd = null;
            SetStatus("Measure · Click a second point.");
        }
        else
        {
            measureEnd = point;
            var delta = point - start;
            string Length(double value) => units.FormatLabel(value);
            SetStatus($"Distance {Length(delta.Length)} {units.Symbol()} · ΔX {Length(delta.X)} · ΔY {Length(delta.Y)} · ΔZ {Length(delta.Z)}");
        }
        UpdateMeasureOverlay();
    }

    private void UpdateMeasureOverlay()
    {
        if (!measuring || measureStart is not Point3D start)
            return;
        var overlay = new GeometryGroup();
        if (ProjectViewportPoint(start) is Point a)
            overlay.Children.Add(new EllipseGeometry(a, 4, 4));
        if (measureEnd is Point3D end)
        {
            AddBevelLine(overlay, start, end);
            if (ProjectViewportPoint(end) is Point b)
                overlay.Children.Add(new EllipseGeometry(b, 4, 4));
        }
        MeasureOverlay.Data = overlay;
    }
}
