using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace LayerCAD;

public partial class MainWindow
{
    private static readonly Vector3D[] MoveDirections = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
    private readonly (Point Start, Point End)[] moveHandles = new (Point, Point)[3];
    private Point? movePivotScreen;
    private sealed record MoveDrag(Point Start, Point3D Pivot, Vector3D Offset, int Axis, Vector PixelsPerMillimeter,
                                   Vector3D PlaneNormal);
    private MoveDrag? moveDrag;

    private Point3D MoveCenter()
    {
        var bounds = Rect3D.Empty;
        foreach (var part in moveSources)
            bounds.Union(PartWorldBounds(part));
        return new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
    }

    private bool TryReadMoveOffset(out Vector3D offset)
    {
        offset = new Vector3D(units.Read(MoveX.Text), units.Read(MoveY.Text), units.Read(MoveZ.Text));
        var delta = offset;
        return moveSources.All(part => Part.ValidOffset(part.X + delta.X) && Part.ValidOffset(part.Y + delta.Y) &&
            Part.ValidOffset(part.Z + delta.Z));
    }

    private void UpdateMoveGizmo()
    {
        Path[] arrows = [MoveXAxis, MoveYAxis, MoveZAxis];
        movePivotScreen = null;
        MovePivot.Data = null;
        foreach (var arrow in arrows)
            arrow.Data = null;
        if (!featureMove || moveSources.Length == 0)
            return;
        TryReadMoveOffset(out var offset);
        var pivot = MoveCenter() + (featureChange?.MovedParts is not null ? offset : default);
        if (ProjectViewportPoint(pivot) is not Point start)
            return;

        movePivotScreen = start;
        MovePivot.Data = new EllipseGeometry(start, 6, 6);
        for (int i = 0; i < arrows.Length; i++)
        {
            var direction = MoveAxisPixels(pivot, start, i);
            direction.Normalize();
            var end = start + direction * 59;
            moveHandles[i] = (start, end);
            var side = new Vector(-direction.Y, direction.X);
            var geometry = new GeometryGroup();
            geometry.Children.Add(new LineGeometry(start + direction * 7, end));
            geometry.Children.Add(new LineGeometry(end, end - direction * 11 + side * 5));
            geometry.Children.Add(new LineGeometry(end, end - direction * 11 - side * 5));
            arrows[i].Data = geometry;
        }
    }

    private Vector MoveAxisPixels(Point3D pivot, Point start, int axis)
    {
        var direction = ProjectViewportPoint(pivot + MoveDirections[axis]) is Point end ? end - start : default;
        double depth = Math.Max(1, (Camera.Position - pivot).Length);
        double minimum = Math.Max(0.01, ModelViewport.ActualWidth /
            (2 * Math.Tan(Camera.FieldOfView * Math.PI / 360) * depth) * 0.33);
        if (direction.Length < minimum * 0.3)
            direction = new Vector(0.66, -0.75);
        return direction.Length >= minimum ? direction : direction * (minimum / direction.Length);
    }

    private int MoveAxisAt(Point point)
    {
        if (movePivotScreen is not Point pivot || (point - pivot).LengthSquared <= 81)
            return -1;
        int nearest = -1;
        double limit = 8;
        for (int i = 0; i < moveHandles.Length; i++)
        {
            var (start, end) = moveHandles[i];
            var segment = end - start;
            double fraction = Math.Clamp(Vector.Multiply(point - start, segment) / segment.LengthSquared, 0, 1);
            double gap = (point - (start + segment * fraction)).Length;
            if (gap < limit)
            {
                nearest = i;
                limit = gap;
            }
        }
        return nearest;
    }

    private bool CanStartMoveDrag(Point point) => featureMove && (MoveAxisAt(point) >= 0 || IsMoveBodyAt(point));

    private bool IsMoveBodyAt(Point point)
    {
        if (movePivotScreen is Point pivot && (point - pivot).LengthSquared <= 81)
            return true;
        var hit = HitSceneCore(point, includeSketches: false).Part;
        return hit is not null && moveSources.Any(part => ReferenceEquals(part, pickableParts[hit.ModelHit].Part));
    }

    private bool BeginMoveDrag(Point point)
    {
        if (!featureMove)
            return false;
        int axis = MoveAxisAt(point);
        if ((axis < 0 && !IsMoveBodyAt(point)) || !OrbitSurface.CaptureMouse())
            return false;

        if (!TryReadMoveOffset(out var offset))
            offset = default;
        var pivot = MoveCenter() + offset;
        var pixels = axis >= 0 ? MoveAxisPixels(pivot, movePivotScreen!.Value, axis) : default;
        var normal = Camera.LookDirection;
        normal.Normalize();
        moveDrag = new MoveDrag(point, pivot, offset, axis, pixels, normal);
        OrbitSurface.Cursor = Cursors.SizeAll;
        return true;
    }

    private void UpdateMoveDrag(Point point)
    {
        if (moveDrag is not { } drag)
            return;
        var movement = drag.Axis >= 0
            ? MoveDirections[drag.Axis] * (Vector.Multiply(point - drag.Start, drag.PixelsPerMillimeter) / drag.PixelsPerMillimeter.LengthSquared)
            : MoveAlongBuildPlate(point, drag);
        var offset = drag.Offset + movement;
        string x = drag.Axis is -1 or 0 ? units.Format(ClampOffset(offset.X, part => part.X)) : Unchanged(MoveX.Text, drag.Offset.X);
        string y = drag.Axis is -1 or 1 ? units.Format(ClampOffset(offset.Y, part => part.Y)) : Unchanged(MoveY.Text, drag.Offset.Y);
        string z = drag.Axis == 2 ? units.Format(ClampOffset(offset.Z, part => part.Z)) : Unchanged(MoveZ.Text, drag.Offset.Z);
        if (MoveX.Text == x && MoveY.Text == y && MoveZ.Text == z)
            return;
        loadingFeatureInputs = true;
        MoveX.Text = x;
        MoveY.Text = y;
        MoveZ.Text = z;
        loadingFeatureInputs = false;
        UpdateBuildPreview();

        string Unchanged(string input, double value) => units.Read(input) == value ? input : units.Format(value);
    }

    private Vector3D MoveAlongBuildPlate(Point point, MoveDrag drag)
    {
        var up = new Vector3D(0, 0, 1);
        var movement = Math.Abs(drag.PlaneNormal.Z) >= 0.15 &&
            OnMovePlane(point, drag.Pivot, up) is Point3D current &&
            OnMovePlane(drag.Start, drag.Pivot, up) is Point3D start
            ? current - start
            : (OnMovePlane(point, drag.Pivot, drag.PlaneNormal) ?? drag.Pivot) -
              (OnMovePlane(drag.Start, drag.Pivot, drag.PlaneNormal) ?? drag.Pivot);
        return new Vector3D(movement.X, movement.Y, 0);
    }

    private Point3D? OnMovePlane(Point point, Point3D pivot, Vector3D normal)
    {
        if (ViewportRay(point) is not Vector3D ray)
            return null;
        double approach = Vector3D.DotProduct(ray, normal);
        if (Math.Abs(approach) < 0.01)
            return null;
        double depth = Vector3D.DotProduct(pivot - Camera.Position, normal) / approach;
        if (depth <= Camera.NearPlaneDistance)
            return null;
        return Camera.Position + ray * depth;
    }

    private double ClampOffset(double offset, Func<Part, double> position) =>
        Math.Clamp(Math.Round(offset, 4), -10000 - moveSources.Min(position), 10000 - moveSources.Max(position));

    private void EndMoveDrag()
    {
        if (moveDrag is null)
            return;
        moveDrag = null;
        OrbitSurface.ReleaseMouseCapture();
        OrbitSurface.Cursor = null;
    }
}
