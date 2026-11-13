using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LayerCAD;

public partial class SketchEditor
{
    private bool trimming;
    private bool breakingPath;
    private List<SketchPoint>? hoveredCircle;
    private const double TrimTolerance = 1e-8;

    private void BeginSketchTrim(object sender, RoutedEventArgs e)
        => BeginSketchEdgeEdit(breakPath: false);

    private void BeginSketchBreak(object sender, RoutedEventArgs e)
        => BeginSketchEdgeEdit(breakPath: true);

    private void BeginSketchEdgeEdit(bool breakPath)
    {
        EnterSketchSelection();
        trimming = true;
        breakingPath = breakPath;
        SketchSelectButton.IsChecked = false;
        if (!breakPath)
            SketchTrimButton.Background = new SolidColorBrush(Color.FromRgb(36, 106, 98));
        selectedDrawings.Clear();
        Redraw();
        ToolHint.Text = breakPath
            ? "Break · Click an open path to split it at the nearest point. Esc to select."
            : "Trim · Hover to preview the segment, then click to remove it. Esc to select.";
    }

    private void EndSketchTrim()
    {
        trimming = false;
        breakingPath = false;
        SketchTrimButton.ClearValue(BackgroundProperty);
        SketchEdgeHover.Data = null;
    }

    private (List<SketchPoint> Profile, int Edge, double Position)? SketchEdgeAt(Point pointer)
    {
        double best = 64;
        (List<SketchPoint>, int, double)? hit = null;
        foreach (var profile in profiles)
        {
            int edges = profile.Count - (openProfiles.Contains(profile) ? 1 : 0);
            for (int edge = 0; edge < edges; edge++)
            {
                var a = profile[edge];
                var b = profile[(edge + 1) % profile.Count];
                if (sketchProjection.Segment(WorldPoint(a), WorldPoint(b)) is not { } screen)
                    continue;
                var delta = screen.End - screen.Start;
                if (delta.LengthSquared <= TrimTolerance)
                    continue;
                double fraction = Math.Clamp(Vector.Multiply(pointer - screen.Start, delta) / delta.LengthSquared, 0, 1);
                var nearest = screen.Start + fraction * delta;
                double distance = (pointer - nearest).LengthSquared;
                if (distance >= best)
                    continue;
                var local = SketchPointAt(nearest);
                var line = Difference(a, b);
                if (!double.IsFinite(local.X) || line.LengthSquared <= TrimTolerance * TrimTolerance)
                    continue;
                best = distance;
                hit = (profile, edge, edge + Math.Clamp(Vector.Multiply(Difference(a, local), line) / line.LengthSquared, 0, 1));
            }
        }
        return hit;
    }

    private void UpdateSketchEdgeHover(Point pointer)
    {
        if (scale <= 0 || moveOriginal is not null || SketchEdgeAt(pointer) is not { } hit)
        {
            SketchEdgeHover.Data = null;
            return;
        }
        if (!trimming && ReferenceEquals(hoveredCircle, hit.Profile) && SketchEdgeHover.Data is not null)
            return;
        bool circular = !openProfiles.Contains(hit.Profile) && IsCircularProfile(hit.Profile);
        if (breakingPath)
        {
            var split = PathBetween(hit.Profile, hit.Position, hit.Position + 0.5)[0];
            SketchEdgeHover.Stroke = Brushes.LightSkyBlue;
            SketchEdgeHover.Data = openProfiles.Contains(hit.Profile) &&
                hit.Position > TrimTolerance && hit.Position < hit.Profile.Count - 1 - TrimTolerance &&
                sketchProjection.Project(WorldPoint(split)) is Point screen ? new EllipseGeometry(screen, 5, 5) : null;
            return;
        }
        if (!trimming && !circular)
        {
            SketchEdgeHover.Data = null;
            return;
        }
        var path = trimming ? TrimRange(hit.Profile, hit.Edge, hit.Position, circular) : (0d, (double)hit.Profile.Count);
        var outline = PathBetween(hit.Profile, path.Item1, path.Item2);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int i = 1; i < outline.Length; i++)
            {
                if (sketchProjection.Segment(WorldPoint(outline[i - 1]), WorldPoint(outline[i])) is not { } segment)
                    continue;
                context.BeginFigure(segment.Start, isFilled: false, isClosed: false);
                context.LineTo(segment.End, isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        hoveredCircle = trimming ? null : hit.Profile;
        SketchEdgeHover.Stroke = trimming ? Brushes.Salmon : Brushes.Orange;
        SketchEdgeHover.Data = geometry;
    }

    private bool TrimAt(Point pointer)
    {
        if (SketchEdgeAt(pointer) is not { } hit)
            return false;
        var profile = hit.Profile;
        bool open = openProfiles.Contains(profile);
        int edges = profile.Count - (open ? 1 : 0);
        if (breakingPath && (!open || hit.Position <= TrimTolerance || hit.Position >= edges - TrimTolerance))
        {
            Feedback.Text = "Choose a point inside an open path.";
            return false;
        }
        var (start, end) = breakingPath ? (hit.Position, hit.Position) :
            TrimRange(profile, hit.Edge, hit.Position, !open && IsCircularProfile(profile));
        if (!breakingPath && !open && TryMergeTrimmedProfiles(profile, start, end))
            return true;
        var pieces = open
            ? new[] { PathBetween(profile, 0, start), PathBetween(profile, end, edges) }
            : new[] { PathBetween(profile, end, start + edges) };
        var remaining = pieces.Where(piece => piece.Length >= 2).ToArray();
        if (remaining.Any(piece => !SketchGeometry.ValidOpenPath(piece)) || profiles.Count - 1 + remaining.Length > 64)
        {
            Feedback.Text = "The remaining paths exceed the sketch limits.";
            return false;
        }
        var before = CaptureSketch();
        int index = profiles.IndexOf(profile);
        profiles.RemoveAt(index);
        originalProfiles.Remove(profile);
        profileDimensions.Remove(profile);
        constraints.Remove(profile);
        openProfiles.Remove(profile);
        foreach (var piece in remaining)
        {
            var path = piece.ToList();
            profiles.Insert(index++, path);
            openProfiles.Add(path);
        }
        points = profiles.LastOrDefault() ?? [];
        RecordSketchEdit(before);
        Refresh(-1);
        Feedback.Text = breakingPath ? "Split the path into two drawings." : "Trimmed sketch segment.";
        return true;
    }

    private bool TryMergeTrimmedProfiles(List<SketchPoint> profile, double start, double end)
    {
        var removed = PathBetween(profile, start, end);
        if (removed.Length < 2)
            return false;
        var remaining = PathBetween(profile, end, start + profile.Count);
        if (remaining.Length < 2)
            return false;

        foreach (var other in profiles)
        {
            if (ReferenceEquals(profile, other) || openProfiles.Contains(other))
                continue;
            if (!TryFindSharedTrim(other, removed, out var otherStart, out var otherEnd, out var reversed))
                continue;
            var otherRemaining = PathBetween(other, otherEnd, otherStart + other.Count);
            if (otherRemaining.Length < 2)
                continue;
            if (!reversed)
                Array.Reverse(otherRemaining);
            var joined = SketchProfileSplit.Clean(remaining.Concat(otherRemaining.Skip(1).SkipLast(1)));
            if (!SketchGeometry.TryTriangulate(joined, out _, out _))
                continue;

            var before = CaptureSketch();
            int index = Math.Min(profiles.IndexOf(profile), profiles.IndexOf(other));
            foreach (var old in new[] { profile, other })
            {
                profiles.Remove(old);
                profileDimensions.Remove(old);
                constraints.Remove(old);
                selectedDrawings.Remove(old);
            }
            bool hasOriginal = originalProfiles.Remove(profile, out int original);
            bool hasOtherOriginal = originalProfiles.Remove(other, out int otherOriginal);
            if (hasOtherOriginal)
                original = hasOriginal ? Math.Min(original, otherOriginal) : otherOriginal;
            points = joined.ToList();
            profiles.Insert(index, points);
            if (hasOriginal || hasOtherOriginal)
                originalProfiles.Add(points, original);
            RecordSketchEdit(before);
            Refresh(-1);
            Feedback.Text = "Trimmed the shared edge and joined the two profiles.";
            return true;
        }
        return false;
    }

    private static bool TryFindSharedTrim(List<SketchPoint> profile, SketchPoint[] removed,
        out double start, out double end, out bool reversed)
    {
        foreach (double first in Stations(removed[0]))
        foreach (double last in Stations(removed[^1]))
        {
            start = first;
            end = last > first ? last : last + profile.Count;
            reversed = false;
            if (Matches(PathBetween(profile, start, end), removed))
                return true;
            start = last;
            end = first > last ? first : first + profile.Count;
            reversed = true;
            if (Matches(PathBetween(profile, start, end).Reverse(), removed))
                return true;
        }
        start = end = 0;
        reversed = false;
        return false;

        IEnumerable<double> Stations(SketchPoint point)
        {
            for (int i = 0; i < profile.Count; i++)
            {
                var origin = profile[i];
                var edge = Difference(origin, profile[(i + 1) % profile.Count]);
                if (edge.LengthSquared <= TrimTolerance * TrimTolerance)
                    continue;
                double t = Vector.Multiply(Difference(origin, point), edge) / edge.LengthSquared;
                if (t >= 0 && t < 1 && (Difference(origin, point) - t * edge).Length <= TrimTolerance)
                    yield return i + t;
                else if (Difference(origin, point).Length <= TrimTolerance)
                    yield return i;
            }
        }

        static bool Matches(IEnumerable<SketchPoint> path, SketchPoint[] expected)
        {
            var vertices = path.ToArray();
            return vertices.Length == expected.Length &&
                vertices.Zip(expected).All(pair => Difference(pair.First, pair.Second).Length <= TrimTolerance);
        }
    }

    private (double Start, double End) TrimRange(List<SketchPoint> profile, int edge, double position, bool circular)
    {
        int first = circular ? 0 : edge;
        int last = circular ? profile.Count : edge + 1;
        var cuts = new SortedSet<double>();
        for (int i = first; i < last; i++)
        {
            var a = profile[i];
            var line = Difference(a, profile[(i + 1) % profile.Count]);
            foreach (var other in profiles)
            {
                bool same = ReferenceEquals(other, profile);
                if (same && circular)
                    continue;
                int count = other.Count - (openProfiles.Contains(other) ? 1 : 0);
                for (int j = 0; j < count; j++)
                {
                    if (same && (Math.Abs(i - j) <= 1 || !openProfiles.Contains(profile) && Math.Abs(i - j) == count - 1))
                        continue;
                    var b = other[j];
                    var segment = Difference(b, other[(j + 1) % other.Count]);
                    double cross = Vector.CrossProduct(line, segment);
                    if (Math.Abs(cross) <= TrimTolerance)
                        continue;
                    var gap = Difference(a, b);
                    double t = Vector.CrossProduct(gap, segment) / cross;
                    double u = Vector.CrossProduct(gap, line) / cross;
                    if (t is >= -TrimTolerance and <= 1 + TrimTolerance && u is >= -TrimTolerance and <= 1 + TrimTolerance)
                    {
                        double station = i + (t <= TrimTolerance ? 0 : t >= 1 - TrimTolerance ? 1 : t);
                        if (circular && station == last)
                            station = 0;
                        if (cuts.GetViewBetween(station - TrimTolerance, station + TrimTolerance).Count == 0)
                            cuts.Add(station);
                    }
                }
            }
        }
        if (circular && cuts.Count == 0)
            return (0, last);
        if (!circular)
        {
            cuts.Add(first);
            cuts.Add(last);
        }
        var stops = cuts.ToArray();
        int after = Array.FindIndex(stops, cut => cut > position + TrimTolerance);
        if (after == 0)
            return (stops[^1] - last, stops[0]);
        if (after < 0)
            return circular ? (stops[^1], stops[0] + last) : (stops[^2], stops[^1]);
        return (stops[after - 1], stops[after]);
    }

    private static SketchPoint[] PathBetween(List<SketchPoint> profile, double start, double end)
    {
        if (end - start <= TrimTolerance)
            return [];
        var path = new List<SketchPoint> { At(start) };
        for (int index = (int)Math.Floor(start) + 1; index < end - TrimTolerance; index++)
            path.Add(At(index));
        path.Add(At(end));
        return path.ToArray();

        SketchPoint At(double station)
        {
            int index = (int)Math.Floor(station);
            int i = (index % profile.Count + profile.Count) % profile.Count;
            var a = profile[i];
            var delta = Difference(a, profile[(i + 1) % profile.Count]) * (station - index);
            return new(a.X + delta.X, a.Y + delta.Y);
        }
    }

    private static Vector Difference(SketchPoint a, SketchPoint b) => new(b.X - a.X, b.Y - a.Y);
}
