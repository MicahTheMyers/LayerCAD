using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private static Func<Point3D, Vector3D, Vector3D>? RoundedBevelNormals(Part part)
    {
        if (part.BevelCuts is not { } cuts || !cuts.Any(cut => cut.Rounded))
            return null;
        if (RoundBevel.Surface(part) is { } round)
            return RoundNormals(part, round, cuts);
        if (part.BevelBox() is not { } box)
            return null;

        var byTarget = cuts.ToDictionary(cut => cut.Target);
        var radii = cuts.Where(cut => cut.Rounded).ToDictionary(cut => cut.Target, cut => cut.Amount);
        foreach (var corner in BoxBevelTarget.Corners)
        {
            if (byTarget.ContainsKey(corner))
                continue;
            var neighbors = AdjacentEdges(corner).Select(edge => byTarget.GetValueOrDefault(edge)).ToArray();
            if (neighbors.All(cut => cut.Rounded && cut.Amount == neighbors[0].Amount))
                radii.Add(corner, neighbors[0].Amount);
        }

        var center = new Point3D(box.X, box.Y, box.Z + box.Height / 2);
        return (point, facet) =>
        {
            var target = BoxBevelTarget.FromNormal(facet);
            if (!radii.TryGetValue(target, out double radius))
                return facet;
            var local = point - center;
            if (target.AxisCount == 2 && (!InArc(local.X, target.X, box.Width / 2 - radius) ||
                !InArc(local.Y, target.Y, box.Depth / 2 - radius) || !InArc(local.Z, target.Z, box.Height / 2 - radius)))
                return facet;
            var normal = new Vector3D(
                Component(local.X, target.X, box.Width / 2 - radius),
                Component(local.Y, target.Y, box.Depth / 2 - radius),
                Component(local.Z, target.Z, box.Height / 2 - radius));
            return NormalizeOrFacet(normal, facet);
        };

        static double Component(double coordinate, int sign, double center) =>
            sign * Math.Max(0, sign * coordinate - center);
        static bool InArc(double coordinate, int sign, double center) => sign == 0 || sign * coordinate >= center - 1e-6;
    }

    private static Func<Point3D, Vector3D, Vector3D> RoundNormals(Part part, ThreadSurface round, BoxBevelCut[] cuts)
    {
        var frame = round.Prism.Frame;
        var axis = frame.Normal;
        var origin = round.Prism.Vertex(round.Center, round.Low, new Vector3D(part.X, part.Y, part.Z));
        var top = cuts.FirstOrDefault(cut => cut.Target == RoundBevel.Rim(true));
        var bottom = cuts.FirstOrDefault(cut => cut.Target == RoundBevel.Rim(false));
        return (point, facet) =>
        {
            double facing = Vector3D.DotProduct(facet, axis);
            if (Math.Abs(facing) > 0.999999)
                return facet;
            var local = point - origin;
            double depth = Vector3D.DotProduct(local, axis);
            var radial = local - axis * depth;
            double length = radial.Length;
            if (length < 1e-8 || Vector3D.DotProduct(radial, facet) <= 0)
                return facet;
            if (Math.Abs(facing) < 1e-6)
                return radial / length;
            var cut = facing > 0 ? top : bottom;
            if (!cut.Rounded)
                return facet;
            double axial = facing > 0 ? Math.Max(0, depth - (round.Length - cut.Amount)) : Math.Min(0, depth - cut.Amount);
            var normal = radial / length * Math.Max(0, length - (round.Radius - cut.Amount)) + axis * axial;
            return NormalizeOrFacet(normal, facet);
        };
    }

    private static Vector3D NormalizeOrFacet(Vector3D normal, Vector3D facet)
    {
        if (normal.LengthSquared < 1e-16)
            return facet;
        normal.Normalize();
        return normal;
    }
}
