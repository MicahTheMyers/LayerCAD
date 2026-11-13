using System.Runtime.CompilerServices;
using System.Windows.Media.Media3D;

namespace LayerCAD;

internal sealed class SketchSurface
{
    private readonly List<Triangle> triangles;
    private readonly HashSet<int> indices;
    private readonly List<(Point3D Start, Point3D End)> boundary;
    private static readonly ConditionalWeakTable<IReadOnlyList<Triangle>, Dictionary<(Point3D, Point3D), List<int>>> adjacency = new();

    public SketchFrame Frame { get; }
    public Part Part { get; }

    private SketchSurface(Part part, SketchFrame frame, List<Triangle> triangles, HashSet<int> indices,
                          List<(Point3D Start, Point3D End)> boundary)
    {
        Part = part;
        Frame = frame;
        this.triangles = triangles;
        this.indices = indices;
        this.boundary = boundary;
    }

    public bool Contains(int index) => indices.Contains(index);
    internal IEnumerable<int> TriangleIndices => indices;

    public Point3D Center()
    {
        var origin = triangles[0].A;
        var weighted = new Vector3D();
        double area = 0;
        foreach (var (a, b, c) in triangles)
        {
            double weight = Vector3D.CrossProduct(b - a, c - a).Length;
            weighted += ((a - origin) + (b - origin) + (c - origin)) * (weight / 3);
            area += weight;
        }
        return origin + weighted / area;
    }

    public IEnumerable<Triangle> Highlight(double lift)
    {
        var offset = Frame.Normal * lift;
        return triangles.Select(t => new Triangle(t.A + offset, t.B + offset, t.C + offset));
    }

    public (SketchPoint Start, SketchPoint End)[] Guide(Point3D origin) =>
        boundary.Select(edge => (Frame.Project(edge.Start - origin), Frame.Project(edge.End - origin))).ToArray();

    public static SketchSurface? Find(Part part, IReadOnlyList<Triangle> mesh, int index)
    {
        var picked = mesh[index];
        var normal = Normal(picked);
        if (part.Kind is PartKind.Cylinder or PartKind.Tube && Math.Abs(normal.Z) < 0.5)
            return null;

        const double tolerance = 1e-7;
        var matchingEdges = adjacency.GetValue(mesh, BuildAdjacency);

        var indices = new HashSet<int> { index };
        var pending = new Queue<int>();
        pending.Enqueue(index);
        var triangles = new List<Triangle>();
        var boundary = new List<(Point3D, Point3D)>();
        while (pending.TryDequeue(out int current))
        {
            var triangle = mesh[current];
            triangles.Add(triangle);
            foreach (var (start, end) in Edges(triangle))
            {
                int adjacent = matchingEdges.TryGetValue((end, start), out var neighbors)
                    ? neighbors.FindIndex(item => Vector3D.DotProduct(normal, Normal(mesh[item])) >= 1 - tolerance &&
                        Math.Abs(Vector3D.DotProduct(normal, mesh[item].A - picked.A)) <= tolerance)
                    : -1;
                if (adjacent < 0)
                    boundary.Add((start, end));
                else if (indices.Add(neighbors![adjacent]))
                    pending.Enqueue(neighbors[adjacent]);
            }
        }

        return new SketchSurface(part, SketchFrame.FromNormal(normal), triangles, indices, boundary);
    }

    private static Dictionary<(Point3D, Point3D), List<int>> BuildAdjacency(IReadOnlyList<Triangle> mesh)
    {
        var edges = new Dictionary<(Point3D, Point3D), List<int>>();
        for (int index = 0; index < mesh.Count; index++)
            foreach (var edge in Edges(mesh[index]))
            {
                if (!edges.TryGetValue(edge, out var neighbors))
                    edges.Add(edge, neighbors = []);
                neighbors.Add(index);
            }
        return edges;
    }

    private static Vector3D Normal(Triangle triangle)
    {
        var normal = Vector3D.CrossProduct(triangle.B - triangle.A, triangle.C - triangle.A);
        normal.Normalize();
        return normal;
    }

    private static (Point3D, Point3D)[] Edges(Triangle triangle) =>
        [(triangle.A, triangle.B), (triangle.B, triangle.C), (triangle.C, triangle.A)];
}
