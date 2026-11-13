using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LayerCAD;

public partial class SketchEditor
{
    private sealed record SavedProfile(SketchPoint[] Points, int OriginalIndex, bool Open, ProfileDimensions? Dimensions, ProfileConstraint[]? Constraints);
    private sealed record SketchState(SavedProfile[] Profiles, int ActiveIndex);
    private readonly Dictionary<List<SketchPoint>, int> originalProfiles = new();
    private readonly EditHistory<SketchState> sketchHistory = new();
    private readonly Stack<SketchPoint> undoneVertices = new();
    private SketchState? moveHistoryStart;
    private bool canFinishEmpty;
    private bool requiresClosedProfile;
    internal int[] OriginalProfileIndices => profiles.Where(profile => !openProfiles.Contains(profile))
        .Select(profile => originalProfiles.GetValueOrDefault(profile, -1)).ToArray();

    private SketchState CaptureSketch() => new(profiles.Select(profile =>
        new SavedProfile(profile.ToArray(), originalProfiles.GetValueOrDefault(profile, -1), openProfiles.Contains(profile),
            profileDimensions.GetValueOrDefault(profile), constraints.GetValueOrDefault(profile))).ToArray(), profiles.IndexOf(points));

    private void RecordSketchEdit(SketchState state)
    {
        selectedDrawings.Clear();
        sketchHistory.Record(state);
        UpdateSketchHistoryButtons();
    }

    private void UndoSketch(object sender, RoutedEventArgs e) => StepSketchHistory(false);
    private void RedoSketch(object sender, RoutedEventArgs e) => StepSketchHistory(true);

    internal bool TryHandleHistoryKey(KeyEventArgs e)
    {
        if (!MainWindow.HistoryShortcut(e, out bool forward, allowText: true))
            return false;
        CloseInlineDimension();
        StepSketchHistory(forward);
        e.Handled = true;
        return true;
    }

    private void StepSketchHistory(bool forward)
    {
        if (!forward && moveOriginal is not null)
        {
            EndSketchMove(commit: false);
            return;
        }
        if (!forward && anchor is not null)
            CancelPlacement();
        if (drawingPolyline && StepPolylineHistory(forward))
            return;
        if (forward ? !sketchHistory.CanRedo : !sketchHistory.CanUndo)
            return;
        EndSketchMove(commit: false);
        CancelPlacement();
        if (sketchHistory.Step(CaptureSketch(), forward) is not { } state)
            return;
        profiles.Clear();
        openProfiles.Clear();
        originalProfiles.Clear();
        profileDimensions.Clear();
        constraints.Clear();
        foreach (var saved in state.Profiles)
        {
            var outline = saved.Points.ToList();
            profiles.Add(outline);
            if (saved.Dimensions is { } dimensions)
                profileDimensions.Add(outline, dimensions);
            if (saved.Constraints is { } rules)
                constraints.Add(outline, rules);
            if (saved.Open)
                openProfiles.Add(outline);
            if (saved.OriginalIndex >= 0)
                originalProfiles.Add(outline, saved.OriginalIndex);
        }
        points = state.ActiveIndex >= 0 ? profiles[state.ActiveIndex] : profiles.LastOrDefault() ?? [];
        Refresh(-1);
        UpdateSketchHistoryButtons();
    }

    private void UpdateSketchHistoryButtons()
    {
        UndoSketchButton.IsEnabled = sketchHistory.CanUndo || anchor is not null ||
            (drawingPolyline && points.Count > 0) || moveOriginal is not null;
        RedoSketchButton.IsEnabled = sketchHistory.CanRedo || undoneVertices.Count > 0;
    }

    private bool StepPolylineHistory(bool forward)
    {
        if (forward)
        {
            if (!undoneVertices.TryPop(out var vertex))
                return false;
            points.Add(vertex);
        }
        else
        {
            if (points.Count == 0)
                return false;
            undoneVertices.Push(points[^1]);
            points.RemoveAt(points.Count - 1);
        }
        EndDimensions();
        Refresh(points.Count - 1);
        if (points.Count > 0)
            BeginDimensions(points[^1]);
        return true;
    }

    private ContextMenu? ShowDrawingMenu(Point point)
    {
        if (moveOriginal is not null || PlacementStart is not null)
            return null;
        var target = profiles.LastOrDefault(profile => DrawingContains(profile, point));
        if (target is null)
            return null;
        if (!selectedDrawings.Contains(target))
            SetDrawingSelection([target], extend: false, toggle: false);
        var targets = profiles.Where(selectedDrawings.Contains).ToArray();
        var menu = new ContextMenu { PlacementTarget = Drawing, Placement = PlacementMode.MousePoint };
        var move = new MenuItem { Header = "_Move drawing…" };
        move.Click += (_, _) => StartSketchMove(targets);
        var delete = new MenuItem { Header = "_Delete drawing" };
        delete.Click += (_, _) => DeleteProfiles(targets);
        menu.Items.Add(move);
        menu.Items.Add(delete);
        menu.IsOpen = true;
        return menu;
    }

    internal void ActivateProfile(int index)
    {
        if (index < 0 || index >= profiles.Count || ReferenceEquals(points, profiles[index]))
            return;
        points = profiles[index];
        selectedDrawings.Clear();
        Refresh(-1);
    }

    internal void EditProfile(int index, bool delete)
    {
        if (index < 0 || index >= profiles.Count)
            return;
        ActivateProfile(index);
        if (delete)
            DeleteProfile(points);
        else
            StartSketchMove([points]);
    }

    private void DeleteProfile(List<SketchPoint> target) => DeleteProfiles([target]);

    private void DeleteProfiles(IEnumerable<List<SketchPoint>> targets)
    {
        var before = CaptureSketch();
        foreach (var target in targets)
        {
            profiles.Remove(target);
            openProfiles.Remove(target);
            originalProfiles.Remove(target);
            profileDimensions.Remove(target);
            constraints.Remove(target);
        }
        points = profiles.LastOrDefault() ?? [];
        RecordSketchEdit(before);
        Refresh(-1);
    }

    private bool DrawingContains(List<SketchPoint> profile, Point point)
    {
        if (openProfiles.Contains(profile))
        {
            for (int i = 1; i < profile.Count; i++)
            {
                if (sketchProjection.Segment(WorldPoint(profile[i - 1]), WorldPoint(profile[i])) is { } segment &&
                    new LineGeometry(segment.Start, segment.End).StrokeContains(new Pen(Brushes.White, 10), point))
                    return true;
            }
            return false;
        }
        if (profile.Count < 3)
            return false;
        var positions = ProjectOutline(profile);
        if (positions.Length < 3)
            return false;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(positions[0], isFilled: true, isClosed: true);
            path.PolyLineTo(positions[1..], isStroked: true, isSmoothJoin: false);
        }
        return geometry.FillContains(point) || geometry.StrokeContains(new Pen(Brushes.White, 10), point);
    }
}
