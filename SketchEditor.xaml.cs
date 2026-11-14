using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LayerCAD;

public partial class SketchEditor : UserControl
{
    private enum SketchTool
    {
        Polyline, OrthogonalPolyline, Circle, DiameterCircle, Ellipse, Diamond,
        Rectangle, CenteredRectangle, Triangle, RightTriangle, Hexagon, Pentagon, Octagon,
        CenterDiameterCircle, ThreePointCircle, ThreePointArc, AngledRectangle, Slot, Spline
    }

    private List<SketchPoint> points;
    private readonly List<List<SketchPoint>> profiles;
    private readonly HashSet<List<SketchPoint>> openProfiles = [];
    private readonly DisplayUnits units;
    private readonly (SketchPoint Start, SketchPoint End)[] faceGuide;
    private SketchTool tool;
    private string toolLabel = "";
    private Button activeTool = null!;
    private readonly DispatcherTimer optionsClose = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private Popup? drawingOptions;
    private SketchPoint? anchor;
    private Shape? preview;
    private SketchPoint cursorPoint;
    private bool drawingPolyline;
    private bool refreshingProfiles;
    private bool updatingDimensions;
    private readonly bool[] fixedDimensions = new bool[2];
    private Point? orbitPoint;
    private Point orbitOrigin;
    private bool sketchOrbitMoved;
    private MouseButton orbitButton;
    private double orbitYaw, orbitPitch = Math.PI / 2;
    private readonly ViewportOrbitQueue orbitQueue;
    private bool validOutline;
    private bool sketchCameraNavigating;
    private SketchPoint[][]? moveOriginal;
    private List<SketchPoint>[] moveProfiles = [];
    private bool loadingMove;
    private double extent = 50;
    private double scale;
    private bool IsPolyline => tool is SketchTool.Polyline or SketchTool.OrthogonalPolyline or SketchTool.Spline;

    public event Action<SketchPoint[][], SketchPoint[][]>? Finished;
    public event Action? Cancelled;

    public SketchEditor(SketchPlane plane, DisplayUnits units, Part? part = null, bool onFace = false,
                        (SketchPoint Start, SketchPoint End)[]? guide = null)
    {
        this.units = units;
        orbitQueue = new ViewportOrbitQueue(DragSketchCameraBy);
        profiles = part?.SketchProfiles.Select(profile => profile.ToList()).ToList() ?? [];
        for (int i = 0; i < profiles.Count; i++)
        {
            originalProfiles.Add(profiles[i], i);
            if (part?.Dimensions?[i] is { } dimensions)
                profileDimensions.Add(profiles[i], dimensions);
        }
        foreach (var path in part?.OpenPaths ?? [])
        {
            var outline = path.ToList();
            profiles.Add(outline);
            openProfiles.Add(outline);
        }
        canFinishEmpty = part?.Kind == PartKind.Sketch;
        if (part?.Constraints is { } stored)
            for (int i = 0; i < profiles.Count; i++)
                if (stored[i] is { } rules)
                    constraints.Add(profiles[i], rules);
        requiresClosedProfile = part?.Kind == PartKind.Extrusion;
        points = profiles.FirstOrDefault() ?? [];
        faceGuide = guide ?? [];
        InitializeComponent();
        SketchViewCube.ViewSelected += SetSketchView;
        SketchViewCube.OrbitDragged += orbitQueue.Add;
        SketchViewCube.MouseEnter += ClearPreview;
        string axes = onFace ? "UV" : plane.ToString();
        PlaneTitle.Text = onFace ? "Face sketch" : $"{axes} sketch";
        HorizontalLabel.Content = $"_{axes[0]} ({units.Symbol()})";
        VerticalLabel.Content = $"_{axes[1]} ({units.Symbol()})";
        SketchMoveULabel.Content = $"Δ_{axes[0]} ({units.Symbol()})";
        SketchMoveVLabel.Content = $"Δ_{axes[1]} ({units.Symbol()})";
        Measurements.Units = units;
        optionsClose.Tick += (_, _) =>
        {
            optionsClose.Stop();
            if (drawingOptions is { } popup && !popup.PlacementTarget.IsMouseOver && !popup.Child.IsMouseOver)
                CloseDrawingOptions();
        };
        Unloaded += (_, _) =>
        {
            orbitQueue.Cancel();
            CloseDrawingOptions();
        };
        activeTool = PolylineButton;
        ActivateTool(PolylineButton, SketchTool.Polyline, "Polyline");
        Refresh(-1);
    }

    private void AddFromGrid(Point location)
    {
        CloseDrawingOptions();
        if (scale <= 0 || moveOriginal is not null)
            return;

        var point = DrawingSnap(location) ?? GridPoint(location);
        if (!InRange(point))
        {
            Feedback.Text = double.IsNaN(point.X) ? "Orbit or face the sketch plane to place points." :
                $"Choose a point within ±{units.FormatLabel(5000)} {units.Symbol()} of the sketch origin.";
            return;
        }
        PlaceConstrained(point);
    }

    private void AddPoint(object sender, RoutedEventArgs e)
    {
        if (TryReadPoint(out var point))
            Place(point);
    }

    private SketchPoint GridPoint(Point location)
    {
        double snap = units == DisplayUnits.Inches ? 0.05 * units.MillimetersPerUnit() : 1;
        var point = SketchPointAt(location);
        return new(Math.Round(point.X / snap) * snap, Math.Round(point.Y / snap) * snap);
    }

    private SketchPoint SketchPointAt(Point location)
    {
        if (referenceFrame is not { } frame || sketchProjection.Ray(location) is not Vector3D ray)
            return new(double.NaN, double.NaN);
        double approach = Vector3D.DotProduct(ray, frame.Normal);
        if (Math.Abs(approach) < 1e-8)
            return new(double.NaN, double.NaN);
        var offset = ReferenceCamera.Position - referenceOrigin;
        double depth = -Vector3D.DotProduct(offset, frame.Normal) / approach;
        if (depth < ReferenceCamera.NearPlaneDistance)
            return new(double.NaN, double.NaN);
        return frame.Project(offset + ray * depth);
    }

    private Point3D WorldPoint(SketchPoint point) => referenceOrigin +
        (Vector3D)(referenceFrame?.ToModel(point.X, point.Y, 0) ?? new Point3D(point.X, point.Y, 0));

    private Point ScreenPoint(SketchPoint point) => sketchProjection.Project(WorldPoint(point)) ?? default;

    private Point[] ProjectOutline(IEnumerable<SketchPoint> points) => sketchProjection.Polygon(points.Select(WorldPoint).ToArray());

    private bool FaceOn => referenceFrame is { } frame &&
        Vector3D.DotProduct(-ReferenceCamera.LookDirection / cameraDistance, frame.Normal) > 1 - 1e-9;

    private static bool InRange(SketchPoint point) =>
        point.X is >= -5000 and <= 5000 && point.Y is >= -5000 and <= 5000;

    private void ShowDrawingOptions(object sender, MouseEventArgs e) =>
        ShowDrawingOptions((StackPanel)sender);

    private void ShowDrawingOptions(StackPanel group)
    {
        var popup = (Popup)group.Children[1];
        if (!ReferenceEquals(popup, drawingOptions))
        {
            CloseDrawingOptions();
            drawingOptions = popup;
            popup.PlacementTarget = ((Grid)group.Children[0]).Children[0];
            popup.IsOpen = true;
        }
        optionsClose.Stop();
    }

    private void OpenDrawingOptions(object sender, RoutedEventArgs e)
    {
        var trigger = (Button)sender;
        ShowDrawingOptions((StackPanel)((Grid)trigger.Parent).Parent);
        drawingOptions?.Child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OpenSketchRibbonMenu(object sender, RoutedEventArgs e)
    {
        CloseDrawingOptions();
        var button = (Button)sender;
        var menu = button.ContextMenu;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ChooseSketchRibbonTool(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).Tag is string name && FindName(name) is Button button)
            SelectDefaultTool(button, e);
    }

    private void SelectDefaultTool(object sender, RoutedEventArgs e)
    {
        var trigger = (Button)sender;
        var group = (StackPanel)((Grid)trigger.Parent).Parent;
        var options = (StackPanel)((Border)((Popup)group.Children[1]).Child).Child;
        SelectDrawingTool(trigger, (Button)options.Children[0]);
    }

    private void KeepOptionsOpen(object sender, MouseEventArgs e) => optionsClose.Stop();

    private void ScheduleOptionsClose(object sender, MouseEventArgs e) => optionsClose.Start();

    private void CloseDrawingOptions()
    {
        optionsClose.Stop();
        var popup = drawingOptions;
        drawingOptions = null;
        if (popup is not null)
            popup.IsOpen = false;
    }

    private void OptionsKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleHistoryKey(e))
        {
            CloseDrawingOptions();
            return;
        }
        if (e.Key != Key.Escape || drawingOptions is not { } popup)
            return;

        CloseDrawingOptions();
        popup.PlacementTarget.Focus();
        e.Handled = true;
    }

    private void SelectToolOption(object sender, RoutedEventArgs e)
    {
        if (drawingOptions?.PlacementTarget is not Button trigger)
            return;
        SelectDrawingTool(trigger, (Button)sender);
    }

    private void SelectDrawingTool(Button trigger, Button option)
    {
        CloseDrawingOptions();
        EndSketchMove(commit: false);
        if ((string)option.Tag == "CancelPlacement")
        {
            CancelPlacement();
            return;
        }
        ActivateTool(trigger, Enum.Parse<SketchTool>((string)option.Tag), (string)option.Content);
    }

    internal void BeginSketchMove(object sender, RoutedEventArgs e)
    {
        if (trimming)
            EnterSketchSelection();
        StartSketchMove(selectedDrawings.Count > 0 ? profiles.Where(selectedDrawings.Contains).ToArray() : profiles.ToArray());
    }

    private void StartSketchMove(List<SketchPoint>[] targets)
    {
        if (targets.Any(profile => profileDimensions.GetValueOrDefault(profile)?.Centered == true ||
            constraints.GetValueOrDefault(profile)?.Any(rule => rule.Kind == ProfileConstraintKind.CoincidentOrigin) == true))
        {
            Feedback.Text = "Change the profile position setting before moving a centered drawing.";
            return;
        }
        if (moveOriginal is not null)
            return;
        if (targets.Length == 0)
        {
            Feedback.Text = "Draw a sketch before moving it.";
            return;
        }

        CloseDrawingOptions();
        CancelPlacement();
        moveProfiles = targets;
        moveHistoryStart = CaptureSketch();
        moveOriginal = targets.Select(profile => profile.ToArray()).ToArray();
        loadingMove = true;
        SketchMoveU.Text = SketchMoveV.Text = "0";
        loadingMove = false;
        SketchMovePanel.Visibility = Visibility.Visible;
        VertexInputs.Visibility = VertexActions.Visibility = Visibility.Collapsed;
        SketchMoveButton.Background = new SolidColorBrush(Color.FromRgb(36, 106, 98));
        ToolHint.Text = "Move sketch · Enter offsets along the sketch axes.";
        UpdateSketchMove();
        SketchMoveU.Focus();
        SketchMoveU.SelectAll();
    }

    private void SketchMoveInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!loadingMove && moveOriginal is not null)
            UpdateSketchMove();
    }

    private void UpdateSketchMove()
    {
        var original = moveOriginal!;
        double u = units.Read(SketchMoveU.Text), v = units.Read(SketchMoveV.Text);
        var moved = original.Select(profile => profile.Select(point => new SketchPoint(point.X + u, point.Y + v)).ToArray()).ToArray();
        bool valid = moved.All(profile => profile.All(InRange));
        ApplySketchMoveButton.IsEnabled = valid;
        if (!valid)
        {
            Feedback.Text = $"Enter offsets that keep the sketch within ±{units.FormatLabel(5000)} {units.Symbol()}.";
            return;
        }
        SetMovedProfiles(moved);
        Refresh(PointList.SelectedIndex);
        Feedback.Text = "Previewing the moved sketch.";
    }

    private void ApplySketchMove(object sender, RoutedEventArgs e)
    {
        if (ApplySketchMoveButton.IsEnabled)
            EndSketchMove(commit: true);
    }

    private void CancelSketchMove(object sender, RoutedEventArgs e) => EndSketchMove(commit: false);

    private void EndSketchMove(bool commit)
    {
        if (moveOriginal is not { } original)
            return;
        moveOriginal = null;
        if (!commit)
        {
            SetMovedProfiles(original);
        }
        else if (moveHistoryStart is { } before && moveProfiles.Where((profile, index) => !profile.SequenceEqual(original[index])).Any())
            RecordSketchEdit(before);
        moveHistoryStart = null;
        moveProfiles = [];
        SketchMovePanel.Visibility = Visibility.Collapsed;
        VertexInputs.Visibility = VertexActions.Visibility = Visibility.Visible;
        SketchMoveButton.ClearValue(BackgroundProperty);
        SetToolHint();
        Refresh(PointList.SelectedIndex);
    }

    private void SetMovedProfiles(SketchPoint[][] outlines)
    {
        for (int i = 0; i < outlines.Length; i++)
        {
            moveProfiles[i].Clear();
            moveProfiles[i].AddRange(outlines[i]);
        }
    }

    private void ActivateTool(Button button, SketchTool selectedTool, string label)
    {
        EndSketchTrim();
        drawingToolActive = true;
        SketchSelectButton.IsChecked = SketchLassoButton.IsChecked = false;
        PlacePointButton.IsEnabled = true;
        ProfileChoice.IsEnabled = false;
        selectedDrawings.Clear();
        activeTool.ClearValue(BackgroundProperty);
        activeTool = button;
        activeTool.Background = new SolidColorBrush(Color.FromRgb(36, 106, 98));
        tool = selectedTool;
        toolLabel = label;
        CancelPlacement();
        PlacePointButton.Content = IsPolyline ? "Add point" : "Place point";
    }

    private void SetToolHint()
    {
        if (!drawingToolActive)
        {
            ToolHint.Text = "Click or drag to select drawings · Ctrl-click to toggle · Ctrl+A to select all";
            return;
        }
        string guidance = ThreePointTool ? (anchor, secondAnchor) switch
        {
            (null, _) => tool == SketchTool.Slot ? "Place the first end center." : "Place the first point.",
            (_, null) => tool == SketchTool.Slot ? "Place the other end center." : "Place the second point.",
            _ => tool is SketchTool.AngledRectangle or SketchTool.Slot ? "Set the perpendicular size." : "Place a point on the curve."
        } : tool switch
        {
            SketchTool.Polyline => "Place vertices, then press Enter to end or click the first vertex to close.",
            SketchTool.Spline => "Place fit points, then press Enter to end or click the first fit point to close.",
            SketchTool.OrthogonalPolyline => "Place horizontal or vertical segments. Press Enter to end.",
            SketchTool.Rectangle or SketchTool.Ellipse => anchor is null
                ? "Place the first bounding corner."
                : "Place the opposite bounding corner.",
            SketchTool.CenteredRectangle or SketchTool.Diamond => anchor is null
                ? "Place the center."
                : "Place a bounding corner.",
            SketchTool.DiameterCircle => anchor is null
                ? "Place the first endpoint of the diameter."
                : "Place the opposite endpoint.",
            SketchTool.RightTriangle => anchor is null
                ? "Place the first corner."
                : "Place the opposite corner of the bounding box.",
            _ => anchor is null ? "Place the center." : "Place a point on the perimeter."
        };
        ToolHint.Text = $"{toolLabel} · {guidance} Shift-click a vertex to align. Esc to select.";
    }

    private void PlaceConstrained(SketchPoint point)
    {
        if (PlacementStart is SketchPoint startPoint && !TryConstrain(startPoint, point, out point))
        {
            Feedback.Text = $"Enter positive lengths in {units.Symbol()}.";
            return;
        }
        Place(point);
    }

    private void Place(SketchPoint point)
    {
        selectedDrawings.Clear();
        if (IsPolyline)
        {
            if (!drawingPolyline)
            {
                if (!CanAddProfile())
                    return;
                drawingPolyline = true;
                points = [];
            }
            if (points.Count >= 3 && point == points[0])
            {
                if (TryClosePolyline())
                    Refresh(-1);
                return;
            }
            if (tool == SketchTool.OrthogonalPolyline && points.Count > 0)
            {
                var last = points[^1];
                point = Math.Abs(point.X - last.X) >= Math.Abs(point.Y - last.Y)
                    ? point with { Y = last.Y }
                    : point with { X = last.X };
            }
            if (points.Count == 0 || points[^1] != point)
            {
                undoneVertices.Clear();
                Add(point);
                BeginDimensions(point);
            }
            return;
        }

        if (anchor is not SketchPoint start)
        {
            if (!CanAddProfile())
                return;
            anchor = point;
            SetToolHint();
            Feedback.Text = ThreePointTool ? "Place the second point." : "Place the second point to finish the shape.";
            if (!ThreePointTool)
                BeginDimensions(point);
            else
                cursorPoint = point;
            SketchEdgeHover.Data = null;
            DrawPlacementAnchor();
            return;
        }

        if (ThreePointTool && secondAnchor is null)
        {
            if (point == start)
                return;
            secondAnchor = cursorPoint = point;
            awaitingDragConfirmation = false;
            SetToolHint();
            Redraw();
            return;
        }
        var outline = secondAnchor is SketchPoint middle ? ThreePointOutline(start, middle, point) : Shape(start, point);
        if (ArcTool ? !SketchGeometry.ValidOpenPath(outline) : !SketchGeometry.TryTriangulate(outline, out _, out _))
        {
            Feedback.Text = $"Choose a point that creates valid geometry within ±{units.FormatLabel(5000)} {units.Symbol()}.";
            return;
        }

        var before = CaptureSketch();
        points = outline.ToList();
        profiles.Add(points);
        if (ArcTool)
            openProfiles.Add(points);
        RecordSketchEdit(before);
        anchor = null;
        secondAnchor = null;
        EndDimensions();
        SetToolHint();
        Refresh(-1);
    }

    private bool CanAddProfile()
    {
        if (profiles.Count < 64)
            return true;
        Feedback.Text = "A sketch supports up to 64 profiles.";
        return false;
    }

    private bool TryClosePolyline()
    {
        var outline = tool == SketchTool.Spline ? FitSpline(points, closed: true) : points.ToArray();
        if (!SketchGeometry.TryTriangulate(outline, out _, out _) ||
            tool == SketchTool.OrthogonalPolyline && !HasOrthogonalEdges())
        {
            UpdateFeedback();
            return false;
        }
        var before = CaptureSketch();
        if (tool == SketchTool.Spline)
            points = outline.ToList();
        profiles.Add(points);
        RecordSketchEdit(before);
        drawingPolyline = false;
        undoneVertices.Clear();
        EndDimensions();
        return true;
    }

    private void EndOpenPolyline(object sender, RoutedEventArgs e) => StopPolyline();

    private void StopPolyline()
    {
        if (!drawingPolyline)
            return;
        var outline = tool == SketchTool.Spline ? FitSpline(points, closed: false) : points.ToArray();
        if (!SketchGeometry.ValidOpenPath(outline))
        {
            CancelPlacement();
            return;
        }
        var before = CaptureSketch();
        if (tool == SketchTool.Spline)
            points = outline.ToList();
        bool split = tool != SketchTool.Spline && SplitProfilesWithPolyline(points);
        if (!split)
        {
            profiles.Add(points);
            openProfiles.Add(points);
        }
        drawingPolyline = false;
        undoneVertices.Clear();
        EndDimensions();
        RecordSketchEdit(before);
        Refresh(-1);
        Feedback.Text = split ? "Polyline split the outline into separate profiles." :
            "Open polyline saved. Start another drawing or finish the sketch.";
    }

    private bool SplitProfilesWithPolyline(List<SketchPoint> path)
    {
        var splits = profiles.Where(profile => !openProfiles.Contains(profile))
            .Select(profile => (Original: profile, Regions: SketchProfileSplit.Split(profile.ToArray(), path)))
            .Where(split => split.Regions.Count > 1).ToArray();
        int additional = splits.Sum(split => split.Regions.Count - 1);
        if (additional == 0 || profiles.Count + additional > 64)
            return false;

        foreach (var (original, regions) in splits)
        {
            int index = profiles.IndexOf(original);
            var first = regions[0].ToList();
            profiles[index] = first;
            profileDimensions.Remove(original);
            constraints.Remove(original);
            if (originalProfiles.Remove(original, out int originalIndex))
                originalProfiles.Add(first, originalIndex);
            profiles.InsertRange(index + 1, regions.Skip(1).Select(region => region.ToList()));
            points = first;
        }
        return true;
    }

    private SketchPoint[] Shape(SketchPoint start, SketchPoint end)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (tool == SketchTool.Rectangle)
            return [start, new(start.X + dx, start.Y), new(start.X + dx, start.Y + dy), new(start.X, start.Y + dy)];
        if (tool == SketchTool.CenteredRectangle)
            return [new(start.X - dx, start.Y - dy), new(start.X + dx, start.Y - dy),
                    new(start.X + dx, start.Y + dy), new(start.X - dx, start.Y + dy)];
        if (tool == SketchTool.Diamond)
            return [new(start.X + Math.Abs(dx), start.Y), new(start.X, start.Y + Math.Abs(dy)),
                    new(start.X - Math.Abs(dx), start.Y), new(start.X, start.Y - Math.Abs(dy))];
        if (tool == SketchTool.RightTriangle)
            return [start, new(end.X, start.Y), end];

        int sides = tool switch
        {
            SketchTool.Triangle => 3,
            SketchTool.Pentagon => 5,
            SketchTool.Hexagon => 6,
            SketchTool.Octagon => 8,
            _ => SketchGeometry.CurveSegments
        };
        double radius = Math.Sqrt(dx * dx + dy * dy);
        double radiusX = radius, radiusY = radius;
        double phase = Math.Atan2(dy, dx);
        var center = start;
        if (tool is SketchTool.DiameterCircle or SketchTool.Ellipse)
        {
            center = new SketchPoint((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            radiusX = tool == SketchTool.Ellipse ? Math.Abs(dx) / 2 : radius / 2;
            radiusY = tool == SketchTool.Ellipse ? Math.Abs(dy) / 2 : radius / 2;
            if (tool == SketchTool.Ellipse)
                phase = 0;
        }
        var vertices = new SketchPoint[sides];
        for (int i = 0; i < sides; i++)
        {
            double theta = phase + Math.Tau * i / sides;
            vertices[i] = new SketchPoint(center.X + radiusX * Math.Cos(theta), center.Y + radiusY * Math.Sin(theta));
        }
        return vertices;
    }

    private void PreviewFromGrid(object sender, MouseEventArgs e) => AdvanceSketchGesture(e.GetPosition(Drawing));

    private void AdvanceSketchGesture(Point location)
    {
        if (orbitPoint is Point previous)
        {
            if (!sketchOrbitMoved && IsSketchGestureClick(location))
                return;
            if (orbitButton == MouseButton.Middle)
            {
                sketchOrbitMoved = true;
                orbitPoint = location;
                orbitQueue.Add(location - previous);
            }
            else if (drawingSelectionPath is not null)
            {
                sketchOrbitMoved = true;
                UpdateDrawingSelection(location);
            }
            else if (orbitButton == MouseButton.Left && drawingToolActive && !awaitingDragConfirmation)
            {
                StartDragDrawing();
                UpdateCursorPreview(location);
            }
            else
                sketchOrbitMoved = true;
            return;
        }
        UpdateCursorPreview(location);
        if (PlacementStart is null)
            UpdateSketchEdgeHover(location);
    }

    private bool IsSketchGestureClick(Point location)
    {
        double threshold = orbitButton == MouseButton.Left && drawingToolActive && !awaitingDragConfirmation ? 1 : 16;
        return (location - orbitOrigin).LengthSquared < threshold;
    }

    private void StartDragDrawing()
    {
        if (sketchOrbitMoved)
            return;
        sketchOrbitMoved = true;
        if (PlacementStart is null)
            AddFromGrid(orbitOrigin);
    }

    private void UpdateCursorPreview(Point location)
    {
        if (scale <= 0 || !drawingToolActive)
            return;
        var snap = DrawingSnap(location);
        ShowDrawingSnap(snap);
        if (PlacementStart is null)
            return;
        var point = snap ?? GridPoint(location);
        if (!InRange(point))
        {
            RemovePreview();
            return;
        }
        cursorPoint = point;
        UpdateDimensionValues();
        UpdatePlacementPreview();
    }

    private void DrawPlacementAnchor()
    {
        if (anchor is not SketchPoint start || sketchProjection.Project(WorldPoint(start)) is not Point center)
            return;
        Drawing.Children.Add(new Path
        {
            Data = new GeometryGroup
            {
                Children =
                {
                    new LineGeometry(center - new Vector(5, 0), center + new Vector(5, 0)),
                    new LineGeometry(center - new Vector(0, 5), center + new Vector(0, 5))
                }
            },
            Stroke = Brushes.LightSkyBlue,
            IsHitTestVisible = false
        });
    }

    private void UpdatePlacementPreview(bool showMeasurements = true)
    {
        if (PlacementStart is not SketchPoint start || scale <= 0)
            return;
        if (preview is null)
        {
            preview = IsPolyline || ArcTool || ThreePointTool && secondAnchor is null
                ? new Polyline() : new Polygon { Fill = new SolidColorBrush(Color.FromArgb(35, 135, 206, 250)) };
            preview.Stroke = Brushes.LightSkyBlue;
            preview.StrokeThickness = 2;
            preview.StrokeDashArray = [4, 2];
            preview.IsHitTestVisible = false;
            Drawing.Children.Add(preview);
        }
        bool valid = TryConstrain(start, cursorPoint, out var end);
        if (!valid)
            end = SnapSegment(start, cursorPoint);
        var outline = IsPolyline || ThreePointTool && secondAnchor is null ? new[] { start, end } :
            secondAnchor is SketchPoint middle ? ThreePointOutline(start, middle, end) : Shape(start, end);
        if (tool == SketchTool.Spline)
            outline = FitSpline([.. points, end], closed: false);
        bool open = preview is Polyline;
        var positions = open
            ? outline.Length == 2 && sketchProjection.Segment(WorldPoint(outline[0]), WorldPoint(outline[1])) is { } segment
                ? new PointCollection([segment.Start, segment.End])
                : new PointCollection(outline.Select(point => sketchProjection.Project(WorldPoint(point))).OfType<Point>())
            : new PointCollection(outline.Length >= 3 ? ProjectOutline(outline) : []);
        if (preview is Polyline line)
            line.Points = positions;
        else
            ((Polygon)preview).Points = positions;
        if (showMeasurements)
            UpdateMeasurements(ThreePointTool || tool == SketchTool.Spline ? [] : outline, closed: !open);
    }

    private void ClearPreview(object sender, MouseEventArgs e)
    {
        SketchEdgeHover.Data = null;
        ShowDrawingSnap(null);
        if (awaitingDragConfirmation || DimensionA.IsKeyboardFocused || DimensionB.IsKeyboardFocused || orbitPoint is not null)
            return;
        RemovePreview();
    }

    private void RemovePreview()
    {
        if (preview is not null)
            Drawing.Children.Remove(preview);
        preview = null;
        UpdateMeasurements(points, validOutline);
    }

    private void UpdateMeasurements(IReadOnlyList<SketchPoint> outline, bool closed) =>
        Measurements.Update(outline.All(point => sketchProjection.IsVisible(WorldPoint(point))) ? outline : [],
            ScreenPoint, closed, showDimensions: FaceOn);

    private void Add(SketchPoint point)
    {
        if (points.Count >= SketchGeometry.MaxVertices)
        {
            Feedback.Text = $"A sketch supports up to {SketchGeometry.MaxVertices} vertices.";
            return;
        }
        points.Add(point);
        Refresh(points.Count - 1);
    }

    private void MovePoint(object sender, RoutedEventArgs e)
    {
        if (profileDimensions.ContainsKey(points))
        {
            Feedback.Text = "Clear Profile Dimensions before editing individual vertices.";
            return;
        }
        int index = PointList.SelectedIndex;
        if (index >= 0 && TryReadPoint(out var point, points[index]))
        {
            if (constraints.TryGetValue(points, out var rules))
            {
                var draft = points.ToArray();
                draft[index] = point;
                var solved = SolveConstraints(draft, rules, index);
                if (!ValidConstrainedOutline(solved))
                {
                    Feedback.Text = "That edit would make the constrained drawing invalid.";
                    return;
                }
                RecordSketchEdit(CaptureSketch());
                points.Clear();
                points.AddRange(solved);
                Refresh(index);
                return;
            }
            if (!drawingPolyline && point != points[index])
                RecordSketchEdit(CaptureSketch());
            points[index] = point;
            Refresh(index);
        }
    }

    private void RemovePoint(object sender, RoutedEventArgs e)
    {
        if (constraints.ContainsKey(points))
        {
            Feedback.Text = "Clear the drawing constraints before removing a vertex.";
            return;
        }
        if (profileDimensions.ContainsKey(points))
        {
            Feedback.Text = "Clear Profile Dimensions before removing a vertex.";
            return;
        }
        int index = PointList.SelectedIndex;
        if (index < 0)
            return;
        if (!drawingPolyline)
            RecordSketchEdit(CaptureSketch());
        points.RemoveAt(index);
        Refresh(Math.Min(index, points.Count - 1));
    }

    private bool TryReadPoint(out SketchPoint point, SketchPoint? original = null)
    {
        point = new SketchPoint(units.Read(InputX.Text, original?.X), units.Read(InputY.Text, original?.Y));
        if (InRange(point))
            return true;
        Feedback.Text = $"Enter coordinates within ±{units.FormatLabel(5000)} {units.Symbol()}.";
        return false;
    }

    private void SelectPoint(object sender, SelectionChangedEventArgs e)
    {
        int index = PointList.SelectedIndex;
        if (index >= 0)
        {
            InputX.Text = units.Format(points[index].X);
            InputY.Text = units.Format(points[index].Y);
        }
        Redraw();
    }

    private void Refresh(int index)
    {
        UpdateSketchHistoryButtons();
        EndPolylineButton.Visibility = drawingPolyline ? Visibility.Visible : Visibility.Collapsed;
        refreshingProfiles = true;
        ProfileChoice.ItemsSource = profiles.Select((profile, i) => openProfiles.Contains(profile) ? $"Open polyline {i + 1}" : $"Profile {i + 1}").ToArray();
        ProfileChoice.SelectedIndex = profiles.IndexOf(points);
        ProfileChoice.Visibility = profiles.Count > 1 && PlacementStart is null && moveOriginal is null
            ? Visibility.Visible : Visibility.Collapsed;
        refreshingProfiles = false;
        PointList.ItemsSource = points.Select((point, i) =>
            $"{i + 1}.  {units.FormatLabel(point.X)}, {units.FormatLabel(point.Y)}").ToArray();
        PointList.SelectedIndex = index;
        extent = Math.Max(50, profiles.SelectMany(profile => profile).Concat(points).Concat(faceGuide.SelectMany(edge => new[] { edge.Start, edge.End }))
            .Select(point => Math.Max(Math.Abs(point.X), Math.Abs(point.Y))).DefaultIfEmpty().Max() * 1.1);
        UpdateFeedback();
        Redraw();
    }

    private void SelectProfile(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingProfiles || ProfileChoice.SelectedIndex < 0)
            return;
        points = profiles[ProfileChoice.SelectedIndex];
        selectedDrawings.Clear();
        Refresh(-1);
    }

    private void UpdateFeedback()
    {
        if (openProfiles.Contains(points))
        {
            Feedback.Text = "Open polyline. Start another drawing or finish the sketch.";
            return;
        }
        if (drawingPolyline && tool == SketchTool.OrthogonalPolyline && points.Count >= 3 && !HasOrthogonalEdges())
        {
            Feedback.Text = "Align each edge horizontally or vertically, including the closing edge.";
            return;
        }
        Feedback.Text = points.Count < 3 ? "Add at least three vertices to close a polyline."
            : SketchGeometry.TryTriangulate(points.ToArray(), out _, out _)
                    ? drawingPolyline ? "Click the first vertex to close, or press Enter to end the polyline."
                    : "Draw another profile or select Finish Sketch."
                : "Use distinct corners and avoid crossing edges within each profile.";
    }

    private bool HasOrthogonalEdges()
    {
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (a.X != b.X && a.Y != b.Y)
                return false;
        }
        return true;
    }

    private void RedrawOnResize(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw() => RedrawSketch(navigating: false);

    private void RedrawSketch(bool navigating)
    {
        double width = Drawing.ActualWidth, height = Drawing.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        sketchCameraNavigating = navigating;
        Drawing.Children.Clear();
        SketchEdgeHover.Data = null;
        snapMarker = null;
        snapPrompt = null;
        preview = null;
        UpdateReferenceCamera();
        SketchGrid.Clip = PlateGridClip();
        double baseStep = units == DisplayUnits.Inches ? 0.25 * units.MillimetersPerUnit() : 5;
        double gridStep = baseStep * Math.Pow(10, Math.Max(0, Math.Ceiling(Math.Log10(12 / (baseStep * scale)))));
        var grid = new SolidColorBrush(Color.FromRgb(37, 49, 66));
        var axis = new SolidColorBrush(Color.FromRgb(91, 113, 137));
        double gridRadius = plateCorners.Select(corner => (corner - referenceOrigin).Length).DefaultIfEmpty(extent).Max() * 2;
        if (gridRadius / gridStep > 120)
            gridStep *= Math.Ceiling(gridRadius / (120 * gridStep));
        int gridCount = (int)Math.Ceiling(gridRadius / gridStep);
        var gridLines = new StreamGeometry();
        var axes = new StreamGeometry();
        using (var minor = gridLines.Open())
        using (var major = axes.Open())
        {
            for (int i = -gridCount; i <= gridCount; i++)
            {
                double at = i * gridStep;
                var path = i == 0 ? major : minor;
                DrawProjectedLine(new(at, -gridRadius), new(at, gridRadius), path);
                DrawProjectedLine(new(-gridRadius, at), new(gridRadius, at), path);
            }
        }
        gridLines.Freeze();
        axes.Freeze();
        SketchGridLines.Stroke = grid;
        SketchGridLines.Data = gridLines;
        SketchGridAxes.Stroke = axis;
        SketchGridAxes.Data = axes;
        DrawAlignmentReference(gridRadius);
        if (faceGuide.Length > 0)
        {
            var faceBoundary = new StreamGeometry();
            using (var path = faceBoundary.Open())
                foreach (var (start, end) in faceGuide)
                    DrawProjectedLine(start, end, path);
            faceBoundary.Freeze();
            Drawing.Children.Add(new Path
            {
                Data = faceBoundary, Stroke = axis, StrokeThickness = 2,
                StrokeDashArray = [4, 3], IsHitTestVisible = false
            });
        }

        var outline = new SolidColorBrush(Color.FromRgb(79, 219, 183));
        var selection = Brushes.Orange;
        foreach (var other in profiles.Where(profile => !ReferenceEquals(profile, points)))
        {
            if (openProfiles.Contains(other))
            {
                DrawOpenPath(other, selectedDrawings.Contains(other) ? selection : outline);
                continue;
            }
            Drawing.Children.Add(new Polygon
            {
                Points = new PointCollection(ProjectOutline(other)), Stroke = selectedDrawings.Contains(other) ? selection : outline, StrokeThickness = 1.5,
                Fill = selectedDrawings.Contains(other) ? DrawingSelectionFill : new SolidColorBrush(Color.FromArgb(32, 79, 219, 183)),
                IsHitTestVisible = false
            });
        }
        bool open = drawingPolyline || openProfiles.Contains(points);
        if (!navigating)
            validOutline = !open && points.Count >= 3 && SketchGeometry.TryTriangulate(points.ToArray(), out _, out _);
        if (!open && points.Count >= 3)
        {
            Drawing.Children.Add(new Polygon
            {
                Points = new PointCollection(ProjectOutline(points)), Stroke = validOutline && !selectedDrawings.Contains(points) ? outline : selection,
                Fill = selectedDrawings.Contains(points) ? DrawingSelectionFill : validOutline ? new SolidColorBrush(Color.FromArgb(52, 79, 219, 183)) : Brushes.Transparent,
                StrokeThickness = 2
            });
        }
        else if (points.Count >= 2)
            DrawOpenPath(points, selectedDrawings.Contains(points) ? selection : outline);

        void DrawOpenPath(List<SketchPoint> path, Brush brush)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (int i = 1; i < path.Count; i++)
                    DrawProjectedLine(path[i - 1], path[i], context);
            }
            geometry.Freeze();
            Drawing.Children.Add(new Path { Data = geometry, Stroke = brush, StrokeThickness = 1.5, IsHitTestVisible = false });
        }

        var markers = new GeometryGroup { FillRule = FillRule.Nonzero };
        int selectedPoint = PointList.SelectedIndex;
        double markerRadius = points.Count <= 24 ? 4 : 1.5;
        if (!navigating || points.Count <= 24)
        {
            for (int i = 0; i < points.Count; i++)
                if (i != selectedPoint && sketchProjection.Project(WorldPoint(points[i])) is Point position)
                    markers.Children.Add(new EllipseGeometry(position, markerRadius, markerRadius));
        }
        if (markers.Children.Count > 0)
        {
            markers.Freeze();
            Drawing.Children.Add(new Path { Data = markers, Fill = outline, IsHitTestVisible = false });
        }
        if (selectedPoint >= 0 && selectedPoint < points.Count &&
            sketchProjection.Project(WorldPoint(points[selectedPoint])) is Point selectedPosition)
            Drawing.Children.Add(new Path { Data = new EllipseGeometry(selectedPosition, 4, 4), Fill = Brushes.White, IsHitTestVisible = false });

        DrawPlacementAnchor();
        if (!navigating)
            UpdateMeasurements(points, validOutline);
        UpdatePlacementPreview(showMeasurements: !navigating);

        void DrawProjectedLine(SketchPoint start, SketchPoint end, StreamGeometryContext path)
        {
            if (sketchProjection.Segment(WorldPoint(start), WorldPoint(end)) is not { } segment)
                return;
            var (a, b) = segment;
            path.BeginFigure(a, isFilled: false, isClosed: false);
            path.LineTo(b, isStroked: true, isSmoothJoin: false);
        }
    }

    private void Finish(object sender, RoutedEventArgs e)
    {
        CloseDrawingOptions();
        if (moveOriginal is not null)
        {
            if (!ApplySketchMoveButton.IsEnabled)
                return;
            EndSketchMove(commit: true);
        }
        if (anchor is not null)
        {
            Feedback.Text = "Finish placing the shape or select a tool to cancel its placement.";
            return;
        }
        if (drawingPolyline && !TryClosePolyline())
            StopPolyline();

        var outlines = profiles.Where(profile => !openProfiles.Contains(profile)).Select(profile => profile.ToArray()).ToArray();
        var paths = profiles.Where(openProfiles.Contains).Select(profile => profile.ToArray()).ToArray();
        if ((outlines.Length == 0 && (requiresClosedProfile || !canFinishEmpty && paths.Length == 0)) ||
            outlines.Any(profile => !SketchGeometry.TryTriangulate(profile, out _, out _)) ||
            paths.Any(path => !SketchGeometry.ValidOpenPath(path)))
        {
            UpdateFeedback();
            return;
        }

        Finished?.Invoke(outlines, paths);
    }

    private void Cancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
}

