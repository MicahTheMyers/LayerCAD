namespace LayerCAD;

public static class ProfileExtrusion
{
    // Indices 0 through n-1 describe the base loop. The next n indices describe the top loop.
    public static (int A, int B, int C)[] Triangulate(SketchPoint[] profile)
    {
        if (!SketchGeometry.TryTriangulate(profile, out var caps, out bool counterclockwise))
            throw new ArgumentException("The sketch profile cannot be extruded.", nameof(profile));

        int count = profile.Length;
        var triangles = new (int A, int B, int C)[2 * (caps.Count + count)];
        int index = 0;
        foreach (var (a, b, c) in caps)
        {
            triangles[index++] = (c, b, a);
            triangles[index++] = (a + count, b + count, c + count);
        }
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            int a = counterclockwise ? i : next;
            int b = counterclockwise ? next : i;
            triangles[index++] = (a, b, b + count);
            triangles[index++] = (a, b + count, a + count);
        }
        return triangles;
    }
}
