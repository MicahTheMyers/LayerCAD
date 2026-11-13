using System.Windows;

namespace LayerCAD;

public partial class SketchEditor
{
    private void BeginSketchScale(object sender, RoutedEventArgs e)
    {
        var targets = selectedDrawings.Count > 0 ? profiles.Where(selectedDrawings.Contains).ToArray() :
            profiles.Contains(points) ? [points] : [];
        if (targets.Length == 0)
        {
            Feedback.Text = "Select drawings to scale.";
            return;
        }
        if (targets.Any(profile => profileDimensions.ContainsKey(profile) || constraints.ContainsKey(profile)))
        {
            Feedback.Text = "Use Profile Dimensions or clear them before scaling.";
            return;
        }
        if (Window.GetWindow(this) is not { } owner)
            return;
        var dialog = new ModelingDialog(owner, "Sketch Scale", units);
        dialog.Hint("Scale the selected drawings about the sketch origin.");
        var factor = dialog.Number("Uniform scale factor", 1, 0.001, 1000);
        var outlines = dialog.RunValue(() =>
        {
            double size = factor();
            var result = targets.Select(path => path.Select(point => new SketchPoint(point.X * size, point.Y * size)).ToArray()).ToArray();
            if (size == 1 || result.Where((path, i) => openProfiles.Contains(targets[i])
                ? !SketchGeometry.ValidOpenPath(path) : !SketchGeometry.TryTriangulate(path, out _, out _)).Any())
                throw new ArgumentException("Enter a different scale that keeps the geometry within the sketch limits.");
            return result;
        }, "Apply");
        if (outlines is null)
            return;
        EnterSketchSelection();
        RecordSketchEdit(CaptureSketch());
        for (int i = 0; i < targets.Length; i++)
        {
            targets[i].Clear();
            targets[i].AddRange(outlines[i]);
        }
        SetDrawingSelection(targets, extend: false, toggle: false);
        Feedback.Text = "Scaled sketch geometry.";
    }

    private void BeginSketchSpline(object sender, RoutedEventArgs e)
    {
        EndSketchMove(commit: false);
        CloseDrawingOptions();
        ActivateTool(SketchSplineButton, SketchTool.Spline, "Fit Point Spline");
        Focus();
    }

    private static SketchPoint[] FitSpline(IReadOnlyList<SketchPoint> fit, bool closed)
    {
        if (fit.Count < 2)
            return fit.ToArray();
        int spans = closed ? fit.Count : fit.Count - 1;
        int samples = Math.Clamp(255 / spans, 1, 12);
        var curve = new List<SketchPoint>();
        for (int span = 0; span < spans; span++)
        {
            var a = At(span - 1);
            var b = At(span);
            var c = At(span + 1);
            var d = At(span + 2);
            curve.Add(b);
            for (int i = 1; i < samples; i++)
            {
                double t = (double)i / samples;
                curve.Add(new(Evaluate(a.X, b.X, c.X, d.X, t), Evaluate(a.Y, b.Y, c.Y, d.Y, t)));
            }
        }
        if (!closed)
            curve.Add(fit[^1]);
        return curve.ToArray();

        SketchPoint At(int index) => fit[closed ? (index + fit.Count) % fit.Count : Math.Clamp(index, 0, fit.Count - 1)];
        static double Evaluate(double a, double b, double c, double d, double t) =>
            b + 0.5 * t * (c - a + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a)));
    }

    private void BeginSketchOffset(object sender, RoutedEventArgs e)
    {
        var profile = selectedDrawings.Count == 1 ? selectedDrawings.Single() :
            selectedDrawings.Count == 0 && profiles.Contains(points) ? points : null;
        if (profile is null || openProfiles.Contains(profile))
        {
            Feedback.Text = "Select one closed profile to offset.";
            return;
        }
        if (!CanAddProfile() || Window.GetWindow(this) is not { } owner)
            return;
        var dialog = new ModelingDialog(owner, "Sketch Offset", units);
        dialog.Hint("Positive distances offset outward. Negative distances offset inward.");
        var distance = dialog.Length("Distance", 2, signed: true);
        var outline = dialog.RunValue(() => OffsetOutline(profile, distance()) ??
            throw new ArgumentException("Enter a distance that produces a closed profile without crossings."), "Create Offset");
        if (outline is null)
            return;
        EnterSketchSelection();
        var before = CaptureSketch();
        points = outline.ToList();
        profiles.Add(points);
        RecordSketchEdit(before);
        SetDrawingSelection([points], extend: false, toggle: false);
        Feedback.Text = "Created offset profile.";
    }

    private static SketchPoint[]? OffsetOutline(List<SketchPoint> profile, double distance)
    {
        if (Math.Abs(distance) < 1e-8 ||
            !SketchGeometry.TryTriangulate(profile.ToArray(), out _, out bool counterclockwise))
            return null;
        double signed = counterclockwise ? distance : -distance;
        var result = new SketchPoint[profile.Count];
        for (int i = 0; i < profile.Count; i++)
        {
            var point = profile[i];
            var incoming = Normal(profile[(i + profile.Count - 1) % profile.Count], point);
            var outgoing = Normal(point, profile[(i + 1) % profile.Count]);
            double denominator = 1 + Vector.Multiply(incoming, outgoing);
            if (denominator < 1e-8)
                return null;
            var shift = (incoming + outgoing) * (signed / denominator);
            result[i] = new(point.X + shift.X, point.Y + shift.Y);
        }
        for (int i = 0; i < profile.Count; i++)
        {
            int next = (i + 1) % profile.Count;
            var oldEdge = new Vector(profile[next].X - profile[i].X, profile[next].Y - profile[i].Y);
            var newEdge = new Vector(result[next].X - result[i].X, result[next].Y - result[i].Y);
            if (Vector.Multiply(oldEdge, newEdge) <= 0)
                return null;
        }
        return SketchGeometry.TryTriangulate(result, out _, out _) ? result : null;

        static Vector Normal(SketchPoint a, SketchPoint b)
        {
            var vector = new Vector(b.Y - a.Y, a.X - b.X);
            vector.Normalize();
            return vector;
        }
    }

    private void BeginSketchFillet(object sender, RoutedEventArgs e) => ModifySketchCorner(round: true);

    private void BeginSketchChamfer(object sender, RoutedEventArgs e) => ModifySketchCorner(round: false);

    private void ModifySketchCorner(bool round)
    {
        int index = PointList.SelectedIndex;
        if (index < 0 || !profiles.Contains(points) || openProfiles.Contains(points))
        {
            Feedback.Text = "Select a vertex from the Vertices list on a closed profile.";
            return;
        }
        if (Window.GetWindow(this) is not { } owner)
            return;
        var profile = points;
        if (profileDimensions.ContainsKey(profile) || constraints.ContainsKey(profile))
        {
            Feedback.Text = "Clear Profile Dimensions before modifying a corner.";
            return;
        }
        var dialog = new ModelingDialog(owner, round ? "Sketch Fillet" : "Sketch Chamfer", units);
        var size = dialog.Length(round ? "Radius" : "Setback", 2);
        var rounded = dialog.RunValue(() => (round ? FilletCorner(profile, index, size()) : ChamferCorner(profile, index, size())) ??
            throw new ArgumentException("Choose a size that fits the corner and keeps the outline closed."), "Apply");
        if (rounded is null)
            return;
        EnterSketchSelection();
        RecordSketchEdit(CaptureSketch());
        profile.Clear();
        profile.AddRange(rounded);
        points = profile;
        Refresh(-1);
        Feedback.Text = round ? "Rounded sketch corner." : "Chamfered sketch corner.";
    }

    private static SketchPoint[]? ChamferCorner(List<SketchPoint> profile, int index, double setback)
    {
        if (profile.Count >= SketchGeometry.MaxVertices)
            return null;
        var vertex = profile[index];
        var a = profile[(index + profile.Count - 1) % profile.Count];
        var b = profile[(index + 1) % profile.Count];
        var left = new Vector(a.X - vertex.X, a.Y - vertex.Y);
        var right = new Vector(b.X - vertex.X, b.Y - vertex.Y);
        if (setback >= Math.Min(left.Length, right.Length))
            return null;
        SketchPoint Along(Vector edge) => new(vertex.X + edge.X * setback / edge.Length, vertex.Y + edge.Y * setback / edge.Length);
        var result = profile.Take(index).Concat(new[] { Along(left), Along(right) }).Concat(profile.Skip(index + 1)).ToArray();
        return SketchGeometry.TryTriangulate(result, out _, out _) ? result : null;
    }

    private static SketchPoint[]? FilletCorner(List<SketchPoint> profile, int index, double radius)
    {
        int count = profile.Count;
        if (count < 3 || count + 16 > SketchGeometry.MaxVertices)
            return null;
        var vertex = profile[index];
        var a = profile[(index + count - 1) % count];
        var b = profile[(index + 1) % count];
        var incoming = new Vector(a.X - vertex.X, a.Y - vertex.Y);
        var outgoing = new Vector(b.X - vertex.X, b.Y - vertex.Y);
        double left = incoming.Length, right = outgoing.Length;
        if (left < 1e-8 || right < 1e-8)
            return null;
        incoming /= left;
        outgoing /= right;
        double angle = Math.Acos(Math.Clamp(Vector.Multiply(incoming, outgoing), -1, 1));
        if (angle < 1e-6 || angle > Math.PI - 1e-6)
            return null;
        double setback = radius / Math.Tan(angle / 2);
        if (setback >= Math.Min(left, right) - 1e-8)
            return null;
        var bisector = incoming + outgoing;
        bisector.Normalize();
        var center = bisector * (radius / Math.Sin(angle / 2));
        var start = incoming * setback - center;
        var end = outgoing * setback - center;
        double phase = Math.Atan2(start.Y, start.X);
        double sweep = Math.Atan2(Vector.CrossProduct(start, end), Vector.Multiply(start, end));
        var arc = Enumerable.Range(0, 17).Select(i =>
        {
            double theta = phase + sweep * i / 16;
            return new SketchPoint(vertex.X + center.X + radius * Math.Cos(theta),
                vertex.Y + center.Y + radius * Math.Sin(theta));
        });
        var result = profile.Take(index).Concat(arc).Concat(profile.Skip(index + 1)).ToArray();
        return SketchGeometry.TryTriangulate(result, out _, out _) ? result : null;
    }
}
