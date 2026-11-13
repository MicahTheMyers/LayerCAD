using System.Windows;
using System.Windows.Controls;

namespace LayerCAD;

public enum ProfileConstraintKind { Horizontal, Vertical, CoincidentOrigin }

public sealed record ProfileConstraint(ProfileConstraintKind Kind, int Vertex);

public partial class SketchEditor
{
    private readonly Dictionary<List<SketchPoint>, ProfileConstraint[]> constraints = new();

    internal ProfileConstraint[]?[]? FinishedConstraints
    {
        get
        {
            var values = profiles.OrderBy(openProfiles.Contains).Select(profile => constraints.GetValueOrDefault(profile)).ToArray();
            return values.Any(value => value is not null) ? values : null;
        }
    }

    private void AddProfileConstraint(object sender, RoutedEventArgs e)
    {
        int vertex = PointList.SelectedIndex;
        if (vertex < 0 || !profiles.Contains(points))
        {
            Feedback.Text = "Select a vertex in the Vertices list.";
            return;
        }
        var kind = Enum.Parse<ProfileConstraintKind>((string)((MenuItem)sender).Tag);
        if (kind != ProfileConstraintKind.CoincidentOrigin && openProfiles.Contains(points) && vertex == points.Count - 1)
        {
            Feedback.Text = "Select the start vertex of a segment.";
            return;
        }
        if (profileDimensions.ContainsKey(points))
        {
            Feedback.Text = "Clear Profile Dimensions before adding vertex constraints to this drawing.";
            return;
        }
        ProfileConstraint[] next = [..constraints.GetValueOrDefault(points) ?? [], new(kind, vertex)];
        next = next.Distinct().ToArray();
        var outline = SolveConstraints(points, next, vertex);
        if (!ValidConstrainedOutline(outline))
        {
            Feedback.Text = "That constraint would make the drawing invalid.";
            return;
        }
        RecordSketchEdit(CaptureSketch());
        constraints[points] = next;
        points.Clear();
        points.AddRange(outline);
        Refresh(vertex);
        Feedback.Text = $"Applied {((MenuItem)sender).Header}.";
    }

    private void ClearProfileConstraints(object sender, RoutedEventArgs e)
    {
        if (!constraints.ContainsKey(points))
            return;
        RecordSketchEdit(CaptureSketch());
        constraints.Remove(points);
        Refresh(PointList.SelectedIndex);
    }

    private bool ValidConstrainedOutline(SketchPoint[] outline) => openProfiles.Contains(points)
        ? SketchGeometry.ValidOpenPath(outline)
        : SketchGeometry.TryTriangulate(outline, out _, out _);

    private static SketchPoint[] SolveConstraints(IReadOnlyList<SketchPoint> outline, ProfileConstraint[] rules, int preferred)
    {
        var x = Enumerable.Range(0, outline.Count).ToArray();
        var y = (int[])x.Clone();
        foreach (var rule in rules)
        {
            int next = (rule.Vertex + 1) % outline.Count;
            if (rule.Kind == ProfileConstraintKind.Vertical)
                x[Root(x, next)] = Root(x, rule.Vertex);
            if (rule.Kind == ProfileConstraintKind.Horizontal)
                y[Root(y, next)] = Root(y, rule.Vertex);
        }
        var origin = rules.Where(rule => rule.Kind == ProfileConstraintKind.CoincidentOrigin).Select(rule => rule.Vertex).ToArray();
        return Enumerable.Range(0, outline.Count).Select(i => new SketchPoint(
            Value(x, i, p => p.X), Value(y, i, p => p.Y))).ToArray();

        double Value(int[] groups, int index, Func<SketchPoint, double> coordinate)
        {
            int group = Root(groups, index);
            if (origin.Any(vertex => Root(groups, vertex) == group))
                return 0;
            return coordinate(outline[Root(groups, preferred) == group ? preferred : group]);
        }

        static int Root(int[] parents, int index)
        {
            while (parents[index] != index)
                index = parents[index];
            return index;
        }
    }
}
