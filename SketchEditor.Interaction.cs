using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class SketchEditor
{
    private Point3D referenceOrigin;
    private SketchFrame? referenceFrame;
    private bool awaitingDragConfirmation;

    private SketchPoint? PlacementStart => drawingPolyline && points.Count > 0 ? points[^1] : anchor;
    private bool SingleDimension => IsPolyline || tool is SketchTool.Circle or SketchTool.DiameterCircle or SketchTool.CenterDiameterCircle or
        SketchTool.Triangle or SketchTool.Pentagon or
        SketchTool.Hexagon or SketchTool.Octagon;
    private double DimensionFactor => tool is SketchTool.CenteredRectangle or SketchTool.Diamond ? 2 : 1;

    private void BeginDimensions(SketchPoint start)
    {
        awaitingDragConfirmation = false;
        Array.Clear(fixedDimensions);
        cursorPoint = start;
        string first = tool switch
        {
            SketchTool.Polyline or SketchTool.OrthogonalPolyline => "Segment length",
            SketchTool.Circle or SketchTool.Triangle or SketchTool.Pentagon or SketchTool.Hexagon or SketchTool.Octagon => "Radius",
            SketchTool.DiameterCircle or SketchTool.CenterDiameterCircle => "Diameter",
            _ => "Width"
        };
        DimensionALabel.Content = $"{first} ({units.Symbol()})";
        DimensionBLabel.Content = $"Height ({units.Symbol()})";
        DimensionBLabel.Visibility = DimensionB.Visibility = SingleDimension ? Visibility.Collapsed : Visibility.Visible;
        DimensionInputs.Visibility = Visibility.Visible;
        ProfileChoice.Visibility = Visibility.Collapsed;
        UpdateDimensionValues();
        FocusDimension(DimensionA);
        UpdateSketchHistoryButtons();
    }

    private void EndDimensions()
    {
        awaitingDragConfirmation = false;
        DimensionInputs.Visibility = Visibility.Collapsed;
        Array.Clear(fixedDimensions);
    }

    private void CancelPlacement()
    {
        anchor = null;
        secondAnchor = null;
        undoneVertices.Clear();
        if (drawingPolyline)
        {
            drawingPolyline = false;
            points = profiles.LastOrDefault() ?? [];
        }
        EndDimensions();
        SetToolHint();
        Refresh(-1);
    }

    private void SketchKeyDown(object sender, KeyEventArgs e)
    {
        if (calibratingImage && e.Key == Key.Escape)
        {
            calibratingImage = false;
            calibrationStart = null;
            Feedback.Text = "Image calibration canceled.";
            e.Handled = true;
            return;
        }
        if (InlineDimension.IsKeyboardFocusWithin && e.Key is Key.Enter or Key.Escape)
            return;
        if (LayerClipboard.IsShortcut(e))
        {
            if (!trimming)
                HandleSketchClipboard(e.Key);
            e.Handled = true;
            return;
        }
        if (TryHandleHistoryKey(e))
            return;
        if (Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBoxBase &&
            e.Key is Key.R or Key.C or Key.O or Key.D1 or Key.D2 or Key.F6)
        {
            switch (e.Key)
            {
                case Key.R: SelectDefaultTool(RectangleButton, e); break;
                case Key.C: SelectDefaultTool(CircleButton, e); break;
                case Key.O: if (!e.IsRepeat) BeginSketchOffset(sender, e); break;
                case Key.D1: EnterSketchSelection(); break;
                case Key.D2: EnterSketchSelection(lasso: true); break;
                case Key.F6: FitSketchView(); break;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBoxBase)
        {
            BeginSketchTrim(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBoxBase)
        {
            EndSketchMove(commit: false);
            CloseDrawingOptions();
            ActivateTool(PolylineButton, SketchTool.Polyline, "Line");
            Focus();
            e.Handled = true;
            return;
        }
        bool cancel = e.Key == Key.Escape ||
            (e.Key is Key.Delete or Key.Back && e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase);
        if (cancel && drawingSelectionPath is not null)
        {
            orbitPoint = null;
            Drawing.ReleaseMouseCapture();
            ClearDrawingSelectionGesture();
            e.Handled = true;
        }
        else if (cancel && moveOriginal is not null)
        {
            EndSketchMove(commit: false);
            e.Handled = true;
        }
        else if (cancel && (drawingToolActive || trimming))
        {
            if (e.Key == Key.Escape && drawingPolyline)
                StopPolyline();
            EnterSketchSelection();
            e.Handled = true;
        }
        else if (cancel && PlacementStart is null && selectedDrawings.Count > 0)
        {
            if (e.Key == Key.Escape)
                SetDrawingSelection([], extend: false, toggle: false);
            else if (Keyboard.Modifiers == ModifierKeys.None && !e.IsRepeat)
                DeleteProfiles(selectedDrawings.ToArray());
            e.Handled = true;
        }
        else if (PlacementStart is not null)
        {
            if (e.Key == Key.Tab && !ThreePointTool)
                FocusDimension(!SingleDimension && DimensionA.IsKeyboardFocused ? DimensionB : DimensionA);
            else if (e.Key == Key.Enter)
            {
                if (drawingPolyline)
                {
                    if (awaitingDragConfirmation)
                        PlaceConstrained(cursorPoint);
                    StopPolyline();
                }
                else
                    PlaceConstrained(cursorPoint);
            }
            else
                return;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && moveOriginal is not null && e.OriginalSource is not ButtonBase)
        {
            ApplySketchMove(sender, e);
            e.Handled = true;
        }
    }

    private static void FocusDimension(TextBox input)
    {
        input.Focus();
        input.SelectAll();
    }

    private void HighlightDimension(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    private void FocusDimensionFromClick(object sender, MouseButtonEventArgs e)
    {
        FocusDimension((TextBox)sender);
        e.Handled = true;
    }

    private void DimensionChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingDimensions || PlacementStart is null)
            return;
        fixedDimensions[ReferenceEquals(sender, DimensionA) ? 0 : 1] = true;
        UpdatePlacementPreview();
    }

    private SketchPoint SnapSegment(SketchPoint start, SketchPoint end)
    {
        if (tool != SketchTool.OrthogonalPolyline)
            return end;
        return Math.Abs(end.X - start.X) >= Math.Abs(end.Y - start.Y)
            ? end with { Y = start.Y } : end with { X = start.X };
    }

    private void UpdateDimensionValues()
    {
        if (PlacementStart is not SketchPoint start)
            return;
        var end = SnapSegment(start, cursorPoint);
        double dx = Math.Abs(end.X - start.X), dy = Math.Abs(end.Y - start.Y);
        double a = SingleDimension
            ? Math.Sqrt(dx * dx + dy * dy) * (tool == SketchTool.CenterDiameterCircle ? 2 : 1)
            : dx * DimensionFactor;
        updatingDimensions = true;
        SetLive(DimensionA, 0, a);
        SetLive(DimensionB, 1, dy * DimensionFactor);
        updatingDimensions = false;

        void SetLive(TextBox input, int index, double value)
        {
            if (fixedDimensions[index])
                return;
            string text = units.FormatLabel(value);
            if (input.Text == text)
                return;
            input.Text = text;
            if (input.IsKeyboardFocused)
                input.SelectAll();
        }
    }

    private bool TryConstrain(SketchPoint start, SketchPoint cursor, out SketchPoint end)
    {
        end = SnapSegment(start, cursor);
        if (!fixedDimensions[0] && !fixedDimensions[1])
            return InRange(end);
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double a = fixedDimensions[0] ? units.Read(DimensionA.Text) : 0;
        double b = fixedDimensions[1] && !SingleDimension ? units.Read(DimensionB.Text) : 0;
        if (fixedDimensions[0] && !Part.ValidDimension(a) || fixedDimensions[1] && !SingleDimension && !Part.ValidDimension(b))
            return false;
        if (SingleDimension && fixedDimensions[0])
        {
            if (tool == SketchTool.CenterDiameterCircle)
                a /= 2;
            double length = Math.Sqrt(dx * dx + dy * dy);
            dx = length == 0 ? a : dx * (a / length);
            dy = length == 0 ? 0 : dy * (a / length);
        }
        else if (!SingleDimension)
        {
            if (fixedDimensions[0])
                dx = Math.CopySign(a / DimensionFactor, dx);
            if (fixedDimensions[1])
                dy = Math.CopySign(b / DimensionFactor, dy);
        }
        end = new SketchPoint(start.X + dx, start.Y + dy);
        return InRange(end);
    }

    private Point3D[] plateCorners = [];
    private double sketchViewSize = 220;
    private double cameraDistance = ViewportCamera.DefaultDistance(220);
    private Vector3D cameraPan;
    private bool panDrag;
    private ViewportProjection sketchProjection;
    private Rect3D referenceBounds = Rect3D.Empty;

    internal void SetReferences(Model3DGroup scene, Point3D origin, SketchFrame frame, Point3D[] corners,
                                double viewSize, double distance)
    {
        referenceOrigin = origin;
        cameraPan = default;
        referenceFrame = frame;
        plateCorners = corners;
        sketchViewSize = viewSize;
        cameraDistance = distance;
        ReferenceScene.Content = scene;
        referenceBounds = scene.Bounds;
        (orbitYaw, orbitPitch) = ViewportCamera.Angles(frame.Normal, frame.Vertical.Vector);
        Redraw();
    }

    private StreamGeometry? PlateGridClip()
    {
        if (plateCorners.Length == 0)
            return null;
        var points = sketchProjection.Polygon(plateCorners);
        var geometry = new StreamGeometry();
        if (points.Length >= 3)
        {
            using var outline = geometry.Open();
            outline.BeginFigure(points[0], isFilled: true, isClosed: true);
            outline.PolyLineTo(points[1..], isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void UpdateReferenceCamera()
    {
        ViewportCamera.Position(ReferenceCamera, referenceOrigin + cameraPan, orbitYaw, orbitPitch, cameraDistance);
        ViewportCamera.FitDepth(ReferenceCamera, cameraDistance, referenceBounds,
            ReferenceImageModel.Content?.Bounds ?? Rect3D.Empty);
        SketchViewCube.SetOrientation(-ReferenceCamera.LookDirection, ReferenceCamera.UpDirection);
        sketchProjection = new ViewportProjection(ReferenceCamera, Drawing.ActualWidth, Drawing.ActualHeight);
        scale = sketchProjection.PixelsPerUnit(referenceOrigin);
        SketchOriginMarker.Data = sketchProjection.Project(referenceOrigin) is Point origin
            ? new EllipseGeometry(origin, 4, 4) : null;
    }

    private void DragSketchCameraBy(Vector delta)
    {
        if (panDrag)
            cameraPan += ViewportCamera.Pan(ReferenceCamera, Drawing.ActualWidth, cameraDistance, delta);
        else
            (orbitYaw, orbitPitch) = ViewportCamera.Orbit(orbitYaw, orbitPitch, delta);
        bool dragging = orbitPoint is not null && orbitButton == MouseButton.Middle;
        if (dragging && !sketchCameraNavigating)
            UpdateMeasurements([], closed: false);
        RedrawSketch(navigating: dragging);
    }

    private void SetSketchView(Vector3D direction)
    {
        orbitQueue.Cancel();
        (orbitYaw, orbitPitch) = ViewportCamera.Angles(direction);
        Redraw();
    }

    private void ResetSketchView(object sender, RoutedEventArgs e)
    {
        orbitQueue.Cancel();
        cameraPan = default;
        (orbitYaw, orbitPitch) = ViewportCamera.DefaultAngles;
        cameraDistance = ViewportCamera.DefaultDistance(sketchViewSize);
        Redraw();
    }

    private void FitSketchView()
    {
        var outline = profiles.SelectMany(profile => profile).Concat(points).ToArray();
        if (outline.Length == 0)
        {
            ResetOrbit(this, new RoutedEventArgs());
            return;
        }
        var center = WorldPoint(new((outline.Min(p => p.X) + outline.Max(p => p.X)) / 2,
            (outline.Min(p => p.Y) + outline.Max(p => p.Y)) / 2));
        cameraPan = center - referenceOrigin;
        cameraDistance = ViewportCamera.FitDistance(ReferenceCamera, center,
            outline.Select(WorldPoint), Drawing.ActualWidth, Drawing.ActualHeight);
        Redraw();
    }

    private void BeginOrbit(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle) || orbitPoint is not null)
            return;
        if (drawingToolActive && !trimming && e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            SelectAlignmentReference(e.GetPosition(Drawing));
            e.Handled = true;
            return;
        }
        var location = e.GetPosition(Drawing);
        if (e.ChangedButton == MouseButton.Left && PickImageCalibrationPoint(location))
        {
            e.Handled = true;
            return;
        }
        if (!trimming && e.ChangedButton == MouseButton.Left && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            BeginInlineDimension(location))
        {
            e.Handled = true;
            return;
        }
        if (Drawing.CaptureMouse())
        {
            orbitPoint = orbitOrigin = location;
            sketchOrbitMoved = false;
            orbitButton = e.ChangedButton;
            panDrag = orbitButton == MouseButton.Middle && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (orbitButton == MouseButton.Left && !drawingToolActive && !trimming && moveOriginal is null)
                BeginDrawingSelection(location);
            e.Handled = true;
        }
    }

    private void EndOrbit(object sender, MouseButtonEventArgs e)
    {
        if (EndSketchGesture(e.ChangedButton, e.GetPosition(Drawing)))
            e.Handled = true;
    }

    private bool EndSketchGesture(MouseButton button, Point location)
    {
        if (orbitPoint is null || orbitButton != button)
            return false;
        if (button == MouseButton.Middle)
            orbitQueue.Flush();
        bool click = !sketchOrbitMoved && IsSketchGestureClick(location);
        if (drawingSelectionPath is not null)
        {
            orbitPoint = null;
            EndDrawingSelection(location, click);
            return true;
        }
        if (!click && button == MouseButton.Left && drawingToolActive && !awaitingDragConfirmation)
            StartDragDrawing();
        bool previewEnd = button == MouseButton.Left && !click && !awaitingDragConfirmation && PlacementStart is not null;
        orbitPoint = null;
        Drawing.ReleaseMouseCapture();
        if (button == MouseButton.Middle && sketchOrbitMoved)
            Redraw();
        if (trimming && click && button == MouseButton.Left)
        {
            if (TrimAt(location))
                UpdateSketchEdgeHover(location);
            return true;
        }
        if (previewEnd)
        {
            UpdateCursorPreview(location);
            awaitingDragConfirmation = true;
            FocusDimension(DimensionB.IsKeyboardFocused ? DimensionB : DimensionA);
            Feedback.Text = "Edit the dimensions, then click the canvas or press Enter to confirm.";
        }
        else if (click && button == MouseButton.Left && drawingToolActive)
        {
            AddFromGrid(location);
        }
        else if (click && button == MouseButton.Right)
        {
            if (drawingPolyline)
                StopPolyline();
            else
                ShowDrawingMenu(location);
        }
        return true;
    }

    private void LoseOrbit(object sender, MouseEventArgs e)
    {
        bool redraw = orbitPoint is not null && orbitButton == MouseButton.Middle && sketchOrbitMoved;
        ClearDrawingSelectionGesture();
        orbitPoint = null;
        orbitQueue.Cancel();
        panDrag = false;
        if (redraw)
            Redraw();
    }

    private void ZoomSketch(object sender, MouseWheelEventArgs e)
    {
        cameraDistance = ViewportCamera.Zoom(cameraDistance, sketchViewSize, e.Delta);
        Redraw();
        if (PlacementStart is not null && scale > 0)
        {
            UpdateCursorPreview(e.GetPosition(Drawing));
        }
        e.Handled = true;
    }

    private void ResetOrbit(object sender, RoutedEventArgs e)
    {
        orbitQueue.Cancel();
        cameraPan = default;
        if (referenceFrame is { } frame)
            (orbitYaw, orbitPitch) = ViewportCamera.Angles(frame.Normal, frame.Vertical.Vector);
        Redraw();
        if (PlacementStart is not null)
            FocusDimension(DimensionA);
    }
}
