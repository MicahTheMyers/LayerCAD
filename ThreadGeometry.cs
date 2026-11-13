using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public sealed record ThreadSpec(int Surface, double Pitch, double Depth, double Length, bool LeftHanded, bool FromEnd,
                               [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MetricSize = null)
{
    internal MetricThreadSize? Metric => MetricThreadSize.Find(MetricSize);
    internal double InnerRadius(double stock) => stock - Depth;
    internal double OuterRadius(double stock) => ThreadGeometry.BoreCrestRadius(stock, Metric) + Depth;
}

internal sealed record ThreadSurface(HolePrism Prism, SketchPoint Center, SketchPoint[] Ring, double Radius,
                                     double Low, double High, bool External)
{
    public const int Outer = -1;
    public const int TubeBore = -2;
    public double Length => High - Low;

    public static ThreadSurface? Get(Part part, int surface)
    {
        if (surface >= 0)
        {
            if (part.Holes is not { } holes || surface >= holes.Length ||
                HolePrism.Create(part, holes[surface].Axis) is not { } prism)
                return null;
            var hole = holes[surface];
            var (low, high) = hole.Shaft(prism);
            return new ThreadSurface(prism, hole.Center, HolePrism.Circle(hole.Center, hole.Diameter / 2),
                hole.Diameter / 2, low, high, false);
        }

        if (part.Holes is { Length: > 0 } holesOnPart && holesOnPart[0].Axis != HoleAxis.Profile ||
            part.Bevel > 0 || part.BevelCuts is not null ||
            HolePrism.Create(part, HoleAxis.Profile) is not { } body)
            return null;
        var ring = surface switch { Outer => body.Outer, TubeBore => body.Inner, _ => null };
        if (ring is not { Length: 96 or SketchGeometry.CurveSegments } || !CircleInfo(ring, out var center, out double radius))
            return null;
        return new ThreadSurface(body, center, ring, radius, 0, body.Thickness, surface == Outer);
    }

    private static bool CircleInfo(SketchPoint[] ring, out SketchPoint center, out double radius)
    {
        center = new SketchPoint(ring.Average(point => point.X), ring.Average(point => point.Y));
        radius = HolePrism.Distance(center, ring[0]);
        if (radius < 0.005)
            return false;
        double expectedCosine = Math.Cos(Math.Tau / ring.Length);
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            if (Math.Abs(HolePrism.Distance(center, a) - radius) > Math.Max(1e-8, radius * 1e-7))
                return false;
            double dot = (a.X - center.X) * (b.X - center.X) + (a.Y - center.Y) * (b.Y - center.Y);
            if (Math.Abs(dot / (radius * radius) - expectedCosine) > 1e-7)
                return false;
        }
        return true;
    }
}

internal sealed record ThreadPlacement(Part Part, int Index, ThreadSurface Surface, bool FromEnd)
{
    internal static ThreadPlacement[] BoreOpenings(Part part)
    {
        var openings = new List<ThreadPlacement>();
        if (part.Holes is { } holes)
        {
            for (int i = 0; i < holes.Length; i++)
            {
                if (ThreadSurface.Get(part, i) is not { } surface)
                    continue;
                if (holes[i].Through || holes[i].FromEnd)
                    openings.Add(new ThreadPlacement(part, i, surface, true));
                if (holes[i].Through || !holes[i].FromEnd)
                    openings.Add(new ThreadPlacement(part, i, surface, false));
            }
        }
        if (part.Kind == PartKind.Tube && ThreadSurface.Get(part, ThreadSurface.TubeBore) is { } bore)
        {
            openings.Add(new ThreadPlacement(part, ThreadSurface.TubeBore, bore, true));
            openings.Add(new ThreadPlacement(part, ThreadSurface.TubeBore, bore, false));
        }
        return openings.ToArray();
    }

    public static ThreadPlacement? Find(Part part, Point3D point, Vector3D normal)
    {
        var indices = Enumerable.Range(0, part.Holes?.Length ?? 0).Append(ThreadSurface.Outer).Append(ThreadSurface.TubeBore);
        foreach (int index in indices)
        {
            if (ThreadSurface.Get(part, index) is not { } surface)
                continue;
            var prism = surface.Prism;
            var offset = new Vector3D(part.X, part.Y, part.Z);
            var relative = point - (prism.Origin + offset);
            double depth = Vector3D.DotProduct(relative, prism.Frame.Normal);
            if (depth < surface.Low - 1e-6 || depth > surface.High + 1e-6)
                continue;

            var uv = prism.Frame.Project(relative);
            var radial = prism.Frame.Horizontal.Vector * (uv.X - surface.Center.X) +
                         prism.Frame.Vertical.Vector * (uv.Y - surface.Center.Y);
            var existing = part.Threads?.FirstOrDefault(thread => thread.Surface == index);
            double expected = surface.Radius;
            double tolerance = Math.Max(1e-6, expected * 0.003);
            double minimum = (surface.External && existing is not null ? existing.InnerRadius(expected) : expected) - tolerance;
            double maximum = (!surface.External && existing is not null ? existing.OuterRadius(expected) : expected) + tolerance;
            if (radial.Length < minimum || radial.Length > maximum)
                continue;
            radial.Normalize();
            double facing = Vector3D.DotProduct(normal, radial) * (surface.External ? 1 : -1);
            if (facing > 0.06)
                return new ThreadPlacement(part, index, surface, depth > (surface.Low + surface.High) / 2);
        }
        return null;
    }

    public BuildChange? Preview(double pitch, double depth, double length, bool leftHanded, bool fromEnd,
                                MetricThreadSize? metric = null)
    {
        var thread = new ThreadSpec(Index, metric?.Pitch ?? pitch, metric?.RadialDepth(Surface.External) ?? depth,
            length, leftHanded, fromEnd, metric?.Name);
        var updated = (Part.Threads ?? []).Where(item => item.Surface != Index).Append(thread).ToArray();
        var result = Part with { Threads = updated };
        return result.IsValid() ? new BuildChange(result, [], true) : null;
    }
}

internal static class ThreadGeometry
{
    public const int MaxTurns = 64;

    public static bool IsValid(Part part)
    {
        var threads = part.Threads!;
        if (threads.Length == 0 || threads.Any(thread => thread is null) ||
            threads.Select(thread => thread.Surface).Distinct().Count() != threads.Length)
            return false;

        foreach (var thread in threads)
        {
            if (ThreadSurface.Get(part, thread.Surface) is not { } surface ||
                !Part.ValidDimension(thread.Pitch) || !Part.ValidDimension(thread.Depth) ||
                !Part.ValidDimension(thread.Length) || thread.Length > surface.Length ||
                thread.Length / thread.Pitch > MaxTurns)
                return false;
            if (thread.MetricSize is not null)
            {
                if (thread.Metric is not { } size || thread.Pitch != size.Pitch ||
                    thread.Depth != size.RadialDepth(surface.External) || !FitsStock(surface, size))
                    return false;
            }
            else if (thread.Depth >= surface.Radius / 2)
                return false;

            if (thread.Surface >= 0)
            {
                double outer = thread.OuterRadius(surface.Radius);
                if (surface.Prism.Clearance(surface.Center) <= outer + 1e-6)
                    return false;
                for (int index = 0; index < part.Holes!.Length; index++)
                {
                    if (index == thread.Surface)
                        continue;
                    var neighbor = part.Holes[index];
                    double neighborRadius = Math.Max(neighbor.OpeningDiameter / 2,
                        threads.FirstOrDefault(other => other.Surface == index)?.OuterRadius(neighbor.Diameter / 2) ?? 0);
                    if (HoleGeometry.HoleDistance(surface.Prism, part.Holes[thread.Surface], HolePrism.Create(part, neighbor.Axis)!, neighbor) <=
                        outer + neighborRadius + 1e-6)
                        return false;
                }
            }
        }

        var exteriorThread = threads.FirstOrDefault(thread => thread.Surface == ThreadSurface.Outer);
        var tubeThread = threads.FirstOrDefault(thread => thread.Surface == ThreadSurface.TubeBore);
        if (part.Holes is { } cuts && (exteriorThread is not null || tubeThread is not null))
        {
            var surface = ThreadSurface.Get(part, exteriorThread is null ? ThreadSurface.TubeBore : ThreadSurface.Outer)!;
            for (int index = 0; index < cuts.Length; index++)
            {
                var hole = cuts[index];
                double centerDistance = HolePrism.Distance(surface.Center, hole.Center);
                double holeRadius = Math.Max(hole.OpeningDiameter / 2,
                    threads.FirstOrDefault(thread => thread.Surface == index)?.OuterRadius(hole.Diameter / 2) ?? 0);
                if (exteriorThread is not null && centerDistance + holeRadius >= exteriorThread.InnerRadius(surface.Radius) - 1e-6 ||
                    tubeThread is not null && centerDistance - holeRadius <= tubeThread.OuterRadius(part.Depth / 2) + 1e-6)
                    return false;
            }
        }

        if (part.Kind == PartKind.Tube)
        {
            double outerRoot = exteriorThread?.InnerRadius(part.Width / 2) ?? part.Width / 2;
            double boreRoot = tubeThread?.OuterRadius(part.Depth / 2) ?? part.Depth / 2;
            return outerRoot > boreRoot + 1e-6;
        }
        return true;
    }

    internal static bool FitsStock(ThreadSurface surface, MetricThreadSize size) =>
        !surface.External || surface.Radius - size.RadialDepth(true) >= 0.005;

    internal static double BoreCrestRadius(double stock, MetricThreadSize? size) =>
        size is null ? stock : Math.Max(stock, size.MinorDiameter(false) / 2);

    internal static double MajorDiameter(ThreadSurface surface, MetricThreadSize size) =>
        surface.External ? surface.Radius * 2 : 2 * (BoreCrestRadius(surface.Radius, size) + size.RadialDepth(false));

    public static List<Triangle> BuildRound(Part part, bool positioned)
    {
        var prism = HolePrism.Create(part, HoleAxis.Profile)!;
        var offset = positioned ? new Vector3D(part.X, part.Y, part.Z) : ExportOffset(part);
        var triangles = new List<Triangle>();
        var contours = new List<SketchPoint[]> { prism.Outer };
        Wall(ThreadSurface.Outer, prism.Outer, true);
        if (prism.Inner is { } inner)
        {
            contours.Add(inner);
            Wall(ThreadSurface.TubeBore, inner, false);
        }
        HoleGeometry.AddCap(triangles, prism, contours, 0, false, offset);
        HoleGeometry.AddCap(triangles, prism, contours, prism.Thickness, true, offset);
        return triangles;

        void Wall(int index, SketchPoint[] ring, bool outward)
        {
            if (part.Threads!.FirstOrDefault(thread => thread.Surface == index) is { } thread)
                AddWalls(triangles, ThreadSurface.Get(part, index)!, thread, offset);
            else
                HoleGeometry.AddWalls(triangles, prism, ring, 0, prism.Thickness, outward, offset);
        }
    }

    private static Vector3D ExportOffset(Part part)
    {
        var bounds = part.LocalBounds();
        return new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
    }

    internal static void AddWalls(List<Triangle> triangles, ThreadSurface surface, ThreadSpec thread, Vector3D offset)
    {
        double start = thread.FromEnd ? surface.High - thread.Length : surface.Low;
        double end = thread.FromEnd ? surface.High : surface.Low + thread.Length;
        double tolerance = Math.Max(1e-8, surface.Length * 1e-7);
        if (start - surface.Low < tolerance)
            start = surface.Low;
        if (surface.High - end < tolerance)
            end = surface.High;
        var levels = new List<double> { surface.Low };
        if (start > surface.Low)
            levels.Add(start);
        double step = Math.Min(thread.Pitch / 16, (end - start) / 4);
        for (int i = 1; start + i * step < end - tolerance; i++)
            levels.Add(start + i * step);
        levels.Add(end);
        if (end < surface.High)
            levels.Add(surface.High);

        var ring = surface.Ring;
        var radial = ring.Select(point => new SketchPoint(point.X - surface.Center.X, point.Y - surface.Center.Y)).ToArray();
        var phase = radial.Select(point => Math.Atan2(point.Y, point.X) / Math.Tau).ToArray();
        var metric = thread.Metric;
        double referencePhase = metric is null ? 0 : phase[0];
        double baseRadius = surface.External ? surface.Radius : BoreCrestRadius(surface.Radius, metric);
        bool ccw = radial[0].X * radial[1].Y - radial[0].Y * radial[1].X > 0;
        bool forward = ccw == surface.External;
        var lower = Section(levels[0]);
        foreach (double level in levels.Skip(1))
        {
            var upper = Section(level);
            for (int i = 0; i < ring.Length; i++)
            {
                int next = (i + 1) % ring.Length;
                int a = forward ? i : next;
                int b = forward ? next : i;
                triangles.Add(new Triangle(lower[a], lower[b], upper[b]));
                triangles.Add(new Triangle(lower[a], upper[b], upper[a]));
            }
            lower = upper;
        }

        Point3D[] Section(double level)
        {
            var points = new Point3D[ring.Length];
            bool isThreaded = level > start && level < end;
            double lead = Math.Min(thread.Pitch * 0.55, (end - start) * 0.24);
            double taper = isThreaded ? Math.Min(1, Math.Min(level - start, end - level) / lead) : 0;
            double travel = (level - start) / thread.Pitch;
            for (int i = 0; i < ring.Length; i++)
            {
                if (!isThreaded)
                {
                    points[i] = surface.Prism.Vertex(ring[i], level, offset);
                    continue;
                }
                double angle = phase[i] - referencePhase;
                double turn = travel + (thread.LeftHanded ? angle : -angle);
                double fraction = turn - Math.Floor(turn);
                double groove = metric?.GrooveDepth(fraction, surface.External) ??
                    thread.Depth * Math.Clamp((0.43 - Math.Abs(fraction - 0.5)) / 0.31, 0, 1);
                double radius = baseRadius + groove * (surface.External ? -1 : 1);
                double factor = 1 + taper * (radius - surface.Radius) / surface.Radius;
                var point = new SketchPoint(surface.Center.X + radial[i].X * factor,
                    surface.Center.Y + radial[i].Y * factor);
                points[i] = surface.Prism.Vertex(point, level, offset);
            }
            return points;
        }
    }
}
