using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LayerCAD;

public partial class SketchEditor
{
    private (bool Width, double Length)? inlineDimension;

    private bool BeginInlineDimension(Point pointer)
    {
        if (moveOriginal is not null || Measurements.DimensionAt(pointer) is not { } label)
            return false;
        inlineDimension = (label.Width, label.Length);
        Canvas.SetLeft(InlineDimension, Math.Clamp(label.Bounds.Left, 0, Math.Max(0, Drawing.ActualWidth - InlineDimension.Width)));
        Canvas.SetTop(InlineDimension, Math.Clamp(label.Bounds.Top - 5, 0, Math.Max(0, Drawing.ActualHeight - 29)));
        InlineDimension.Text = units.Format(label.Length);
        InlineDimension.Visibility = Visibility.Visible;
        FocusDimension(InlineDimension);
        return true;
    }

    private void InlineDimensionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            CloseInlineDimension();
        else if (e.Key == Key.Enter)
        {
            if (inlineDimension is { } label && ApplyInlineDimension(label.Width, label.Length, units.Read(InlineDimension.Text)))
                CloseInlineDimension();
            else
                FocusDimension(InlineDimension);
        }
        else
            return;
        e.Handled = true;
    }

    private bool ApplyInlineDimension(bool width, double previous, double length)
    {
        if (!Part.ValidDimension(length) || previous <= 0)
        {
            Feedback.Text = $"Enter a positive dimension in {units.Symbol()}.";
            return false;
        }
        if (PlacementStart is SketchPoint start)
        {
            var input = width || SingleDimension ? DimensionA : DimensionB;
            double desired = length;
            if (SingleDimension)
            {
                var end = SnapSegment(start, cursorPoint);
                double current = fixedDimensions[0] ? units.Read(DimensionA.Text) :
                    Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) *
                    (tool == SketchTool.CenterDiameterCircle ? 2 : 1);
                desired = current * (length / previous);
                if (!Part.ValidDimension(desired))
                    return false;
            }
            input.Text = units.Format(desired);
            fixedDimensions[ReferenceEquals(input, DimensionA) ? 0 : 1] = true;
            UpdatePlacementPreview();
            return true;
        }

        if (!profiles.Contains(points))
            return false;
        if (constraints.ContainsKey(points))
        {
            Feedback.Text = "Use the Vertices list to resize this constrained drawing.";
            return false;
        }
        if (profileDimensions.TryGetValue(points, out var parameters))
        {
            bool round = IsCircularProfile(points);
            return SetProfileDimensions(parameters with
            {
                Width = width || round ? length : parameters.Width,
                Height = !width || round ? length : parameters.Height
            });
        }
        double minX = points.Min(point => point.X), minY = points.Min(point => point.Y);
        double factor = length / previous;
        bool circle = IsCircularProfile(points);
        var changed = points.Select(point => new SketchPoint(
            width || circle ? minX + (point.X - minX) * factor : point.X,
            !width || circle ? minY + (point.Y - minY) * factor : point.Y)).ToArray();
        bool valid = openProfiles.Contains(points) ? SketchGeometry.ValidOpenPath(changed) :
            SketchGeometry.TryTriangulate(changed, out _, out _);
        if (!valid)
        {
            Feedback.Text = "The dimension must keep the drawing within the sketch limits.";
            return false;
        }
        if (!points.SequenceEqual(changed))
        {
            RecordSketchEdit(CaptureSketch());
            points.Clear();
            points.AddRange(changed);
            Refresh(-1);
        }
        return true;
    }

    private static bool IsCircularProfile(List<SketchPoint> profile)
    {
        if (profile.Count is not (96 or SketchGeometry.CurveSegments))
            return false;
        var center = new SketchPoint(profile.Average(point => point.X), profile.Average(point => point.Y));
        double RadiusSquared(SketchPoint point) => Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2);
        double radius = RadiusSquared(profile[0]);
        return profile.All(point => Math.Abs(RadiusSquared(point) - radius) <= radius * 1e-8);
    }

    private void InlineDimensionLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseInlineDimension();

    private void CloseInlineDimension()
    {
        inlineDimension = null;
        InlineDimension.Visibility = Visibility.Collapsed;
    }
}
