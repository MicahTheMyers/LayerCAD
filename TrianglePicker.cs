using System.Windows.Media.Media3D;

namespace LayerCAD;

internal sealed class TrianglePicker
{
    private const int LeafSize = 12;
    private readonly IReadOnlyList<Triangle> triangles;
    private readonly Primitive[] primitives;
    private readonly Node? root;

    private readonly record struct Primitive(int Index, Rect3D Bounds);
    private sealed record Node(Rect3D Bounds, int Start, int Count, Node? Left = null, Node? Right = null);

    public TrianglePicker(IReadOnlyList<Triangle> triangles)
    {
        this.triangles = triangles;
        primitives = new Primitive[triangles.Count];
        for (int index = 0; index < triangles.Count; index++)
        {
            var (a, b, c) = triangles[index];
            var bounds = new Rect3D(a, new Size3D());
            bounds.Union(b);
            bounds.Union(c);
            primitives[index] = new Primitive(index, bounds);
        }
        if (primitives.Length > 0)
            root = Build(0, primitives.Length);
    }

    public int Find(Point3D origin, Vector3D ray, double near, bool bothSides, ref double nearest)
    {
        if (root is null)
            return -1;
        int index = -1;
        double best = nearest;
        Visit(root);
        nearest = best;
        return index;

        void Visit(Node node)
        {
            if (!Intersects(node.Bounds, origin, ray, near, best))
                return;
            if (node.Left is { } left && node.Right is { } right)
            {
                Visit(left);
                Visit(right);
                return;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int candidate = primitives[i].Index;
                double distance = Distance(triangles[candidate], origin, ray, bothSides);
                if (distance >= near && distance < best)
                {
                    index = candidate;
                    best = distance;
                }
            }
        }
    }

    private Node Build(int start, int count)
    {
        var bounds = Rect3D.Empty;
        for (int i = start; i < start + count; i++)
            bounds.Union(primitives[i].Bounds);
        if (count <= LeafSize)
            return new Node(bounds, start, count);

        int axis = bounds.SizeX >= bounds.SizeY && bounds.SizeX >= bounds.SizeZ ? 0 : bounds.SizeY >= bounds.SizeZ ? 1 : 2;
        double middle = Center(bounds, axis);
        int split = start;
        for (int i = start; i < start + count; i++)
        {
            if (Center(primitives[i].Bounds, axis) < middle)
            {
                (primitives[split], primitives[i]) = (primitives[i], primitives[split]);
                split++;
            }
        }
        int leftCount = split - start;
        if (leftCount < count / 8 || leftCount > count - count / 8)
            leftCount = count / 2;
        return new Node(bounds, start, count, Build(start, leftCount), Build(start + leftCount, count - leftCount));
    }

    private static double Center(Rect3D bounds, int axis) => axis switch
    {
        0 => bounds.X + bounds.SizeX / 2,
        1 => bounds.Y + bounds.SizeY / 2,
        _ => bounds.Z + bounds.SizeZ / 2
    };

    internal static bool Intersects(Rect3D bounds, Point3D origin, Vector3D ray, double near, double far) =>
        !bounds.IsEmpty && Slab(bounds.X, bounds.SizeX, origin.X, ray.X, ref near, ref far) &&
        Slab(bounds.Y, bounds.SizeY, origin.Y, ray.Y, ref near, ref far) &&
        Slab(bounds.Z, bounds.SizeZ, origin.Z, ray.Z, ref near, ref far);

    private static bool Slab(double start, double length, double origin, double direction, ref double near, ref double far)
    {
        const double tolerance = 1e-8;
        if (Math.Abs(direction) < 1e-12)
            return origin >= start - tolerance && origin <= start + length + tolerance;
        double a = (start - tolerance - origin) / direction;
        double b = (start + length + tolerance - origin) / direction;
        near = Math.Max(near, Math.Min(a, b));
        far = Math.Min(far, Math.Max(a, b));
        return near <= far;
    }

    private static double Distance(Triangle triangle, Point3D origin, Vector3D ray, bool bothSides)
    {
        var (a, b, c) = triangle;
        var ab = b - a;
        var ac = c - a;
        var cross = Vector3D.CrossProduct(ray, ac);
        double determinant = Vector3D.DotProduct(ab, cross);
        if ((bothSides ? Math.Abs(determinant) : determinant) < 1e-14)
            return double.PositiveInfinity;
        var offset = origin - a;
        double u = Vector3D.DotProduct(offset, cross) / determinant;
        if (u < -1e-9 || u > 1 + 1e-9)
            return double.PositiveInfinity;
        var q = Vector3D.CrossProduct(offset, ab);
        double v = Vector3D.DotProduct(ray, q) / determinant;
        return v < -1e-9 || u + v > 1 + 1e-9 ? double.PositiveInfinity : Vector3D.DotProduct(ac, q) / determinant;
    }
}
