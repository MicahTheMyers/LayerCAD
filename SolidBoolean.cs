using System.Runtime.InteropServices;
using System.Windows.Media.Media3D;
using Microsoft.Win32.SafeHandles;

namespace LayerCAD;

internal static class SolidBoolean
{
    public sealed class Solid : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Solid() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle()
        {
            Free(handle);
            return true;
        }
    }

    public static Solid Create(IReadOnlyList<Triangle> mesh)
    {
        var points = new Dictionary<(double X, double Y, double Z), ulong>();
        var coordinates = new List<double>();
        var indices = new List<ulong>(mesh.Count * 3);
        var origin = mesh[0].A;
        double volume = 0;
        foreach (var (a, b, c) in mesh)
        {
            ulong first = Index(a), second = Index(b), third = Index(c);
            if (first == second || second == third || third == first)
                continue;
            volume += Vector3D.DotProduct(a - origin, Vector3D.CrossProduct(b - origin, c - origin));
            indices.Add(first);
            indices.Add(second);
            indices.Add(third);
        }
        if (indices.Count == 0)
            return new Solid();
        if (volume < 0)
            for (int i = 0; i < indices.Count; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        return CreateNative(coordinates.ToArray(), (nuint)coordinates.Count, indices.ToArray(), (nuint)indices.Count);

        ulong Index(Point3D point)
        {
            var key = (Math.Round(point.X, 9), Math.Round(point.Y, 9), Math.Round(point.Z, 9));
            if (points.TryGetValue(key, out ulong index))
                return index;
            index = (ulong)points.Count;
            points.Add(key, index);
            coordinates.Add(point.X);
            coordinates.Add(point.Y);
            coordinates.Add(point.Z);
            return index;
        }
    }

    public static Triangle[]? ReadMesh(Solid solid, Vector3D origin)
    {
        int count = checked((int)TriangleCount(solid));
        if (count == 0)
            return [];
        var values = new double[checked(count * 9)];
        if (ReadNative(solid, values) == 0)
            return null;
        var triangles = new Triangle[count];
        for (int i = 0; i < count; i++)
            triangles[i] = new(Point(i * 9), Point(i * 9 + 3), Point(i * 9 + 6));
        return triangles;

        Point3D Point(int i) => new Point3D(values[i], values[i + 1], values[i + 2]) - origin;
    }

    [DllImport("LayerCadManifold", EntryPoint = "layercad_solid", CallingConvention = CallingConvention.Cdecl)]
    private static extern Solid CreateNative(double[] vertices, nuint coordinates, ulong[] triangles, nuint indices);

    [DllImport("LayerCadManifold", EntryPoint = "layercad_boolean", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Apply(Solid stock, Solid tool, int merge, out Solid result);

    [DllImport("LayerCadManifold", EntryPoint = "layercad_triangle_count", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint TriangleCount(Solid solid);

    [DllImport("LayerCadManifold", EntryPoint = "layercad_read", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadNative(Solid solid, [Out] double[] coordinates);

    [DllImport("LayerCadManifold", EntryPoint = "layercad_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Free(IntPtr solid);
}
