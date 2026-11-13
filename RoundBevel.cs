using System.Windows.Media.Media3D;

namespace LayerCAD;

internal static class RoundBevel
{
    public static BoxBevelTarget Rim(bool top) => new(1, 0, top ? 1 : -1);
    public static bool IsRim(BoxBevelTarget target) => target == Rim(false) || target == Rim(true);

    public static ThreadSurface? Surface(Part part) =>
        part.Kind is PartKind.Cylinder or PartKind.Extrusion && part.Bevel == 0 &&
        part.Threads?.Any(thread => thread.Surface < 0) != true
            ? ThreadSurface.Get(part with { BevelCuts = null, Holes = null, Threads = null }, ThreadSurface.Outer) : null;

    public static double Limit(ThreadSurface surface) => Math.Min(surface.Radius, surface.Length / 2);
    public static double Depth(BoxBevelCut cut) => cut.Rounded ? cut.Amount : cut.Amount / cut.Slope;

    public static List<Triangle> Build(Part part, ThreadSurface surface, bool positioned)
    {
        var bounds = part.LocalBounds();
        var offset = positioned ? new Vector3D(part.X, part.Y, part.Z) :
            new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
        var bottom = part.BevelCuts!.FirstOrDefault(cut => cut.Target == Rim(false));
        var top = part.BevelCuts!.FirstOrDefault(cut => cut.Target == Rim(true));
        var levels = new List<(double Z, double Inset)>();
        AddEnd(bottom, top: false);
        AddEnd(top, top: true);
        var rings = levels.Select(level => surface.Ring.Select(point =>
        {
            double factor = 1 - level.Inset / surface.Radius;
            return new SketchPoint(surface.Center.X + (point.X - surface.Center.X) * factor,
                surface.Center.Y + (point.Y - surface.Center.Y) * factor);
        }).ToArray()).ToArray();
        var triangles = new List<Triangle>();
        var a = surface.Ring[0];
        var b = surface.Ring[1];
        bool ccw = (a.X - surface.Center.X) * (b.Y - surface.Center.Y) > (a.Y - surface.Center.Y) * (b.X - surface.Center.X);
        for (int level = 1; level < levels.Count; level++)
        {
            for (int i = 0; i < surface.Ring.Length; i++)
            {
                int next = (i + 1) % surface.Ring.Length;
                int first = ccw ? i : next, second = ccw ? next : i;
                var lowA = surface.Prism.Vertex(rings[level - 1][first], levels[level - 1].Z, offset);
                var lowB = surface.Prism.Vertex(rings[level - 1][second], levels[level - 1].Z, offset);
                var highA = surface.Prism.Vertex(rings[level][first], levels[level].Z, offset);
                var highB = surface.Prism.Vertex(rings[level][second], levels[level].Z, offset);
                triangles.Add(new Triangle(lowA, lowB, highB));
                triangles.Add(new Triangle(lowA, highB, highA));
            }
        }
        HoleGeometry.AddCap(triangles, surface.Prism, [rings[0]], surface.Low, false, offset);
        HoleGeometry.AddCap(triangles, surface.Prism, [rings[^1]], surface.High, true, offset);
        return triangles;

        void AddEnd(BoxBevelCut cut, bool top)
        {
            if (cut.Amount == 0)
            {
                levels.Add((top ? surface.High : surface.Low, 0));
                return;
            }
            int steps = cut.Rounded ? 24 : 1;
            for (int index = 0; index <= steps; index++)
            {
                int step = top ? steps - index : index;
                double angle = step * Math.PI / (2 * steps);
                double depth = step == 0 ? 0 : step == steps ? Depth(cut) : cut.Amount * (1 - Math.Cos(angle));
                double inset = step == steps ? 0 : cut.Amount * (1 - Math.Sin(angle));
                levels.Add((top ? surface.High - depth : surface.Low + depth, inset));
            }
        }
    }
}
