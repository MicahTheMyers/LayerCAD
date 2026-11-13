namespace LayerCAD;

public partial class SketchEditor
{
    private SketchPoint? secondAnchor;
    private bool ThreePointTool => tool is SketchTool.AngledRectangle or SketchTool.ThreePointCircle or SketchTool.ThreePointArc or SketchTool.Slot;
    private bool ArcTool => tool == SketchTool.ThreePointArc;

    private SketchPoint[] ThreePointOutline(SketchPoint a, SketchPoint b, SketchPoint c)
    {
        double x = b.X - a.X, y = b.Y - a.Y;
        double length = Math.Sqrt(x * x + y * y);
        if (!Part.ValidDimension(length))
            return [];
        if (tool is SketchTool.AngledRectangle or SketchTool.Slot)
        {
            double height = (x * (c.Y - a.Y) - y * (c.X - a.X)) / length;
            if (!Part.ValidDimension(Math.Abs(height)))
                return [];
            if (tool == SketchTool.AngledRectangle)
            {
                double u = -y / length * height, v = x / length * height;
                return [a, b, new(b.X + u, b.Y + v), new(a.X + u, a.Y + v)];
            }
            double direction = Math.Atan2(y, x), radius = Math.Abs(height);
            return Enumerable.Range(0, 49).Select(i => At(b, radius, direction - Math.PI / 2 + Math.PI * i / 48))
                .Concat(Enumerable.Range(0, 49).Select(i => At(a, radius, direction + Math.PI / 2 + Math.PI * i / 48))).ToArray();
        }

        double u2 = c.X - a.X, v2 = c.Y - a.Y;
        double determinant = 2 * (x * v2 - y * u2);
        if (Math.Abs(determinant) < 1e-8)
            return [];
        double thirdLengthSquared = u2 * u2 + v2 * v2;
        var center = new SketchPoint(a.X + (length * length * v2 - thirdLengthSquared * y) / determinant,
            a.Y + (thirdLengthSquared * x - length * length * u2) / determinant);
        double radiusX = a.X - center.X, radiusY = a.Y - center.Y;
        double radiusOfCircle = Math.Sqrt(radiusX * radiusX + radiusY * radiusY);
        if (!Part.ValidDimension(radiusOfCircle))
            return [];
        double start = Angle(a), end = Positive(Angle(b) - start), via = Positive(Angle(c) - start);
        if (!ArcTool)
            return Enumerable.Range(0, SketchGeometry.CurveSegments)
                .Select(i => At(center, radiusOfCircle, start + Math.Tau * i / SketchGeometry.CurveSegments)).ToArray();

        double sweep = via <= end ? end : end - Math.Tau;
        double through = sweep > 0 ? via : via - Math.Tau;
        return SampleArc(start, through, a, c).SkipLast(1)
            .Concat(SampleArc(start + through, sweep - through, c, b)).ToArray();

        IEnumerable<SketchPoint> SampleArc(double angle, double delta, SketchPoint first, SketchPoint last)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / Math.Tau * SketchGeometry.CurveSegments));
            return Enumerable.Range(0, steps + 1).Select(i => i == 0 ? first : i == steps ? last :
                At(center, radiusOfCircle, angle + delta * i / steps));
        }

        double Angle(SketchPoint point) => Math.Atan2(point.Y - center.Y, point.X - center.X);
        static double Positive(double angle) => (angle + Math.Tau) % Math.Tau;
        static SketchPoint At(SketchPoint point, double radius, double angle) =>
            new(point.X + radius * Math.Cos(angle), point.Y + radius * Math.Sin(angle));
    }
}
