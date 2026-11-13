using System.Windows;
using System.Windows.Media.Media3D;

namespace LayerCAD;

internal static class ViewportCamera
{
    public static (double Yaw, double Pitch) DefaultAngles => (-Math.PI / 3, Math.PI / 4);
    public static double DefaultDistance(double size) => size * 2.18;

    public static double FitDistance(PerspectiveCamera camera, Point3D center, IEnumerable<Point3D> points,
        double width, double height)
    {
        var forward = camera.LookDirection;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double horizontal = Math.Tan(camera.FieldOfView * Math.PI / 360) * 0.85;
        double vertical = horizontal * Math.Max(1, height) / Math.Max(1, width);
        return points.Select(point =>
        {
            var offset = point - center;
            return Math.Max(Math.Abs(Vector3D.DotProduct(offset, right)) / horizontal,
                Math.Abs(Vector3D.DotProduct(offset, up)) / vertical) - Vector3D.DotProduct(offset, forward);
        }).Append(1).Max();
    }

    public static (double Yaw, double Pitch) Angles(Vector3D direction, Vector3D? poleUp = null)
    {
        direction.Normalize();
        double yaw = Math.Atan2(direction.Y, direction.X);
        if (direction.X == 0 && direction.Y == 0)
        {
            var up = poleUp ?? new Vector3D(0, 1, 0);
            double sign = direction.Z > 0 ? -1 : 1;
            yaw = Math.Atan2(up.Y * sign, up.X * sign);
        }
        return (yaw, Math.Asin(Math.Clamp(direction.Z, -1, 1)));
    }

    public static (double Yaw, double Pitch) Orbit(double yaw, double pitch, Vector delta) =>
        (yaw - delta.X * 0.012, Math.Clamp(pitch + delta.Y * 0.012, -Math.PI / 2, Math.PI / 2));

    public static double Zoom(double distance, double size, int wheelDelta) =>
        Math.Clamp(distance * Math.Pow(0.85, wheelDelta / 120.0), size * 0.06, size * 7);

    public static Vector3D Pan(PerspectiveCamera camera, double width, double distance, Vector delta)
    {
        if (width <= 0)
            return default;
        var right = Vector3D.CrossProduct(camera.LookDirection, camera.UpDirection);
        right.Normalize();
        var up = camera.UpDirection;
        up.Normalize();
        double unitsPerPixel = 2 * distance * Math.Tan(camera.FieldOfView * Math.PI / 360) / width;
        return (-delta.X * right + delta.Y * up) * unitsPerPixel;
    }

    public static void Position(PerspectiveCamera camera, Point3D pivot, double yaw, double pitch, double distance)
    {
        var offset = new Vector3D(Math.Cos(yaw) * Math.Cos(pitch), Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch)) * distance;
        camera.Position = pivot + offset;
        camera.LookDirection = -offset;
        camera.UpDirection = new Vector3D(-Math.Cos(yaw) * Math.Sin(pitch), -Math.Sin(yaw) * Math.Sin(pitch), Math.Cos(pitch));
    }

    public static void FitDepth(PerspectiveCamera camera, double distance, params Rect3D[] bounds)
    {
        var forward = camera.LookDirection;
        forward.Normalize();
        double nearest = distance;
        double farthest = distance;
        foreach (var box in bounds)
        {
            if (box.IsEmpty)
                continue;
            var center = new Point3D(box.X + box.SizeX / 2, box.Y + box.SizeY / 2, box.Z + box.SizeZ / 2);
            double spread = (Math.Abs(forward.X) * box.SizeX + Math.Abs(forward.Y) * box.SizeY +
                Math.Abs(forward.Z) * box.SizeZ) / 2;
            double depth = Vector3D.DotProduct(center - camera.Position, forward);
            if (depth + spread <= 0)
                continue;
            nearest = Math.Min(nearest, depth - spread);
            farthest = Math.Max(farthest, depth + spread);
        }
        // Leave room for overlays and nearby geometry while keeping the depth range tight.
        double near = nearest > 0 ? nearest / 4 : distance / 1000;
        camera.NearPlaneDistance = Math.Max(0.001, Math.Min(distance / 10, near));
        camera.FarPlaneDistance = Math.Max(distance * 2, farthest + Math.Max(1, distance / 10));
    }
}

internal readonly struct ViewportProjection
{
    private readonly Point center;
    private readonly Point3D eye;
    private readonly Vector3D forward, right, up;
    private readonly double focal, near;

    public ViewportProjection(PerspectiveCamera camera, double width, double height)
    {
        center = new Point(width / 2, height / 2);
        eye = camera.Position;
        var direction = camera.LookDirection;
        direction.Normalize();
        forward = direction;
        var horizontal = Vector3D.CrossProduct(forward, camera.UpDirection);
        horizontal.Normalize();
        right = horizontal;
        up = Vector3D.CrossProduct(right, forward);
        focal = width / (2 * Math.Tan(camera.FieldOfView * Math.PI / 360));
        near = camera.NearPlaneDistance;
    }

    public double PixelsPerUnit(Point3D point) => focal / Depth(point);
    public double Depth(Point3D point) => Vector3D.DotProduct(point - eye, forward);
    public bool IsVisible(Point3D point) => focal > 0 && Depth(point) >= near;
    public Point? Project(Point3D point) => IsVisible(point) ? ProjectVisible(point) : null;
    public Vector3D? Ray(Point point) => focal > 0
        ? forward + right * ((point.X - center.X) / focal) + up * ((center.Y - point.Y) / focal) : null;

    public (Point Start, Point End)? Segment(Point3D start, Point3D end)
    {
        bool a = IsVisible(start), b = IsVisible(end);
        if (!a && !b)
            return null;
        if (!a)
            start = NearIntersection(start, end);
        if (!b)
            end = NearIntersection(start, end);
        return (ProjectVisible(start), ProjectVisible(end));
    }

    public Point[] Polygon(IReadOnlyList<Point3D> points)
    {
        if (points.Count == 0 || focal <= 0)
            return [];
        var result = new List<Point>(points.Count);
        var previous = points[^1];
        foreach (var point in points)
        {
            if (IsVisible(previous) != IsVisible(point))
                result.Add(ProjectVisible(NearIntersection(previous, point)));
            if (IsVisible(point))
                result.Add(ProjectVisible(point));
            previous = point;
        }
        return result.ToArray();
    }

    private Point3D NearIntersection(Point3D start, Point3D end)
    {
        double depth = Depth(start);
        return start + (end - start) * ((near - depth) / (Depth(end) - depth));
    }

    private Point ProjectVisible(Point3D point)
    {
        var relative = point - eye;
        double scale = focal / Math.Max(near, Depth(point));
        return new Point(center.X + scale * Vector3D.DotProduct(relative, right), center.Y - scale * Vector3D.DotProduct(relative, up));
    }
}
