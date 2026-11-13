using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public sealed class ViewCube : FrameworkElement
{
    private sealed record Face(string Label, Vector3D Normal, Brush Fill);
    private sealed record ProjectedFace(Face Face, Vector3D[] Corners, Point[] Points, StreamGeometry Outline, double Facing);
    private sealed record Target(Vector3D Direction, Point? Corner = null, (Point Start, Point End)? Edge = null);

    private static readonly Vector3D Vertical = new(0, 0, 1);
    private static readonly Brush TextColor = Solid(232, 240, 249);
    private static readonly Brush Highlight = Solid(54, 119, 120);
    private static readonly Brush Accent = Solid(98, 224, 197);
    private static readonly Pen Outline = MakePen(Solid(138, 157, 181), 1);
    private static readonly Pen EdgeHighlight = MakePen(Accent, 2.5);
    private static readonly Typeface LabelFont = new("Segoe UI Semibold");
    private static readonly Face[] Faces =
    [
        new("Top", new Vector3D(0, 0, 1), Solid(78, 94, 118)),
        new("Bottom", new Vector3D(0, 0, -1), Solid(62, 77, 99)),
        new("Front", new Vector3D(0, -1, 0), Solid(59, 73, 94)),
        new("Back", new Vector3D(0, 1, 0), Solid(59, 73, 94)),
        new("Right", new Vector3D(1, 0, 0), Solid(45, 57, 75)),
        new("Left", new Vector3D(-1, 0, 0), Solid(45, 57, 75))
    ];

    private Vector3D outward = new(1, -1, 1);
    private Vector3D up = Vertical;
    private Target? hover;
    private Vector3D? pressedDirection;
    private Point pressedAt;
    private Point dragPosition;
    private bool dragging;
    private MouseButton? pressedButton;

    public event Action<Vector3D>? ViewSelected;
    public event Action<Vector>? OrbitDragged;

    public ViewCube()
    {
        Focusable = true;
        ContextMenu = new ContextMenu { Background = Solid(32, 40, 56) };
        foreach (var face in Faces)
            AddMenuView(face.Label, face.Normal);
        ContextMenu.Items.Add(new Separator());
        AddMenuView("Isometric", new Vector3D(1, -1, 1));
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    public void SetOrientation(Vector3D cameraOffset, Vector3D cameraUp)
    {
        outward = cameraOffset;
        outward.Normalize();
        up = cameraUp;
        hover = null;
        InvalidateVisual();
    }

    private void AddMenuView(string label, Vector3D direction)
    {
        var item = new MenuItem { Header = label, Foreground = TextColor };
        item.Click += (_, _) => ViewSelected?.Invoke(direction);
        ContextMenu.Items.Add(item);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var area = new Rect(RenderSize);
        drawing.DrawRectangle(Brushes.Transparent, null, area);
        if (IsKeyboardFocused)
            drawing.DrawRoundedRectangle(null, Outline, new Rect(1, 1, area.Width - 2, area.Height - 2), 4, 4);

        foreach (var face in ProjectFaces())
        {
            bool active = hover is { Corner: null, Edge: null } && hover.Direction == face.Face.Normal;
            drawing.DrawGeometry(active ? Highlight : face.Face.Fill, Outline, face.Outline);
            if (face.Facing < 0.22)
                continue;

            var label = new FormattedText(face.Face.Label.ToUpperInvariant(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFont, face.Facing < 0.42 ? 8 : 10, TextColor,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var center = Project(face.Face.Normal);
            drawing.PushClip(face.Outline);
            drawing.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
            drawing.Pop();
        }

        if (hover?.Corner is Point corner)
            drawing.DrawEllipse(Accent, null, corner, 3, 3);
        else if (hover?.Edge is { } edge)
            drawing.DrawLine(EdgeHighlight, edge.Start, edge.End);
    }

    private IEnumerable<ProjectedFace> ProjectFaces()
    {
        foreach (var face in Faces)
        {
            var normal = face.Normal;
            double facing = Vector3D.DotProduct(normal, outward);
            if (facing < 0.001)
                continue;

            var u = normal.Z == 0 ? Vector3D.CrossProduct(Vertical, normal) : new Vector3D(1, 0, 0);
            var v = Vector3D.CrossProduct(normal, u);
            Vector3D[] corners = [normal - u - v, normal + u - v, normal + u + v, normal - u + v];
            var points = corners.Select(Project).ToArray();
            var outline = new StreamGeometry();
            using (var path = outline.Open())
            {
                path.BeginFigure(points[0], isFilled: true, isClosed: true);
                for (int i = 1; i < points.Length; i++)
                    path.LineTo(points[i], isStroked: true, isSmoothJoin: false);
            }
            yield return new ProjectedFace(face, corners, points, outline, facing);
        }
    }

    private Point Project(Vector3D point)
    {
        var right = Vector3D.CrossProduct(up, outward);
        right.Normalize();
        var screenUp = Vector3D.CrossProduct(outward, right);
        double scale = Math.Min(ActualWidth, ActualHeight) * 0.28;
        return new Point(ActualWidth / 2 + Vector3D.DotProduct(point, right) * scale,
                         ActualHeight / 2 - Vector3D.DotProduct(point, screenUp) * scale);
    }

    private Target? HitTarget(Point point)
    {
        var faces = ProjectFaces().ToArray();
        Target? target = null;
        double closest = 5;
        foreach (var face in faces)
        {
            for (int i = 0; i < face.Points.Length; i++)
            {
                double distance = (point - face.Points[i]).Length;
                if (distance >= closest)
                    continue;

                closest = distance;
                target = new Target(face.Corners[i], Corner: face.Points[i]);
            }
        }
        if (target is not null)
            return target;

        closest = 4;
        foreach (var face in faces)
        {
            for (int i = 0; i < face.Points.Length; i++)
            {
                int next = (i + 1) % face.Points.Length;
                var a = face.Points[i];
                var b = face.Points[next];
                var line = b - a;
                double along = Math.Clamp(Vector.Multiply(point - a, line) / line.LengthSquared, 0, 1);
                double distance = (point - (a + line * along)).Length;
                if (distance >= closest)
                    continue;

                closest = distance;
                target = new Target((face.Corners[i] + face.Corners[next]) / 2, Edge: (a, b));
            }
        }
        if (target is not null)
            return target;

        var body = faces.FirstOrDefault(face => face.Outline.FillContains(point));
        return body is null ? null : new Target(body.Face.Normal);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (pressedButton is not null && IsMouseCaptured)
        {
            if (!dragging && (position - pressedAt).Length >= 4)
            {
                dragging = true;
                hover = null;
                Cursor = pressedButton == MouseButton.Middle ? Cursors.SizeAll : Cursors.Arrow;
            }
            if (pressedButton == MouseButton.Middle && dragging && position != dragPosition)
            {
                OrbitDragged?.Invoke(position - dragPosition);
                dragPosition = position;
            }
            e.Handled = true;
            return;
        }
        hover = HitTarget(position);
        Cursor = hover is null ? Cursors.Arrow : Cursors.Hand;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle) || pressedButton is not null)
            return;
        pressedAt = dragPosition = e.GetPosition(this);
        pressedDirection = HitTarget(pressedAt)?.Direction;
        dragging = false;
        if ((pressedDirection is not null || e.ChangedButton == MouseButton.Middle) && CaptureMouse())
            pressedButton = e.ChangedButton;
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (pressedButton != e.ChangedButton)
            return;
        var position = e.GetPosition(this);
        var selected = e.ChangedButton == MouseButton.Left && !dragging && (position - pressedAt).LengthSquared < 16
            ? HitTarget(position)?.Direction : null;
        var pressed = pressedDirection;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (selected is Vector3D direction && pressed == direction)
            ViewSelected?.Invoke(direction);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        pressedDirection = null;
        pressedButton = null;
        dragging = false;
        Cursor = Cursors.Arrow;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space))
            return;
        ContextMenu.PlacementTarget = this;
        ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private static Brush Solid(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
