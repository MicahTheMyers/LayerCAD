using System.Windows;

namespace LayerCAD;

public sealed record ProfileDimensions(double Width, double Height, bool Centered, bool FollowInnerCircles = false)
{
    internal bool IsValid() => Part.ValidDimension(Width) && Part.ValidDimension(Height);

    internal SketchPoint[] Apply(IReadOnlyList<SketchPoint> profile)
    {
        double minX = profile.Min(p => p.X), minY = profile.Min(p => p.Y);
        double spanX = profile.Max(p => p.X) - minX, spanY = profile.Max(p => p.Y) - minY;
        double x = Centered ? -Width / 2 : minX, y = Centered ? -Height / 2 : minY;
        return profile.Select(p => new SketchPoint(x + (p.X - minX) * Width / spanX,
            y + (p.Y - minY) * Height / spanY)).ToArray();
    }
}

public partial class SketchEditor
{
    private readonly Dictionary<List<SketchPoint>, ProfileDimensions> profileDimensions = new();

    internal ProfileDimensions?[]? FinishedDimensions
    {
        get
        {
            var values = profiles.Where(profile => !openProfiles.Contains(profile))
                .Select(profile => profileDimensions.GetValueOrDefault(profile)).ToArray();
            return values.Any(value => value is not null) ? values : null;
        }
    }

    private void EditProfileDimensions(object sender, RoutedEventArgs e)
    {
        if (PlacementStart is not null || moveOriginal is not null || !profiles.Contains(points) || openProfiles.Contains(points) ||
            Window.GetWindow(this) is not { } owner)
        {
            Feedback.Text = "Select a completed closed profile to set its dimensions.";
            return;
        }
        var previous = profileDimensions.GetValueOrDefault(points);
        var dialog = new ModelingDialog(owner, "Profile Dimensions", units);
        bool circle = IsCircularProfile(points);
        var width = dialog.Length(circle ? "Diameter" : "Width", previous?.Width ?? points.Max(p => p.X) - points.Min(p => p.X));
        var height = circle ? width : dialog.Length("Height", previous?.Height ?? points.Max(p => p.Y) - points.Min(p => p.Y));
        var anchor = dialog.Choice("Position", new[] { "Keep lower-left position", "Center on sketch origin" },
            previous?.Centered == true ? 1 : 0);
        var inner = circle ? null : dialog.Choice("Interior circles", new[] { "Keep in place", "Follow profile size" },
            previous?.FollowInnerCircles == true ? 1 : 0);
        var result = dialog.RunValue(() =>
        {
            var dimensions = new ProfileDimensions(width(), height(), anchor() == "Center on sketch origin",
                inner?.Invoke() == "Follow profile size");
            if (!SketchGeometry.TryTriangulate(dimensions.Apply(points), out _, out _))
                throw new ArgumentException("These dimensions put the outline outside the sketch limits or make it invalid.");
            return dimensions;
        }, "Apply");
        if (result is not null)
        {
            if (!SetProfileDimensions(result))
                Feedback.Text = "The dimensions or dependent circles would leave the sketch limits.";
        }
    }

    private void ClearProfileDimensions(object sender, RoutedEventArgs e)
    {
        if (!profileDimensions.ContainsKey(points))
            return;
        RecordSketchEdit(CaptureSketch());
        profileDimensions.Remove(points);
        Refresh(-1);
        Feedback.Text = "Cleared profile dimensions and position constraint.";
    }

    private bool SetProfileDimensions(ProfileDimensions dimensions)
    {
        if (!dimensions.IsValid())
            return false;
        var changed = dimensions.Apply(points);
        if (!SketchGeometry.TryTriangulate(changed, out _, out _))
            return false;
        var circles = new List<(List<SketchPoint> Profile, SketchPoint[] Outline)>();
        if (dimensions.FollowInnerCircles)
        {
            var before = Bounds(points);
            var after = Bounds(changed);
            foreach (var profile in profiles.Where(profile => !ReferenceEquals(profile, points) &&
                !openProfiles.Contains(profile) && IsCircularProfile(profile)))
            {
                if (profileDimensions.GetValueOrDefault(profile)?.Centered == true ||
                    !profile.All(point => Contains(points, point)))
                    continue;
                double x = profile.Average(p => p.X), y = profile.Average(p => p.Y);
                double dx = after.X + (x - before.X) * after.Width / before.Width - x;
                double dy = after.Y + (y - before.Y) * after.Height / before.Height - y;
                var outline = profile.Select(p => new SketchPoint(p.X + dx, p.Y + dy)).ToArray();
                if (!outline.All(point => Contains(changed, point)) ||
                    !SketchGeometry.TryTriangulate(outline, out _, out _))
                    return false;
                circles.Add((profile, outline));
            }
        }
        RecordSketchEdit(CaptureSketch());
        points.Clear();
        points.AddRange(changed);
        profileDimensions[points] = dimensions;
        foreach (var (profile, outline) in circles)
        {
            profile.Clear();
            profile.AddRange(outline);
        }
        Refresh(-1);
        Feedback.Text = dimensions.Centered ? "Dimensions saved · Centered on sketch origin." : "Dimensions saved.";
        return true;

        static Rect Bounds(IEnumerable<SketchPoint> profile) =>
            new(new Point(profile.Min(p => p.X), profile.Min(p => p.Y)),
                new Point(profile.Max(p => p.X), profile.Max(p => p.Y)));

        static bool Contains(IReadOnlyList<SketchPoint> profile, SketchPoint point)
        {
            bool inside = false;
            for (int i = 0, j = profile.Count - 1; i < profile.Count; j = i++)
            {
                var a = profile[i];
                var b = profile[j];
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < a.X + (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y))
                    inside = !inside;
            }
            return inside;
        }
    }
}
