using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private static double InitialHoleDiameter(HolePlacement hole) => Math.Min(5, Math.Max(0.01, hole.Clearance));

    private GeometryGroup HoleTarget(HolePlacement hole, double diameter)
    {
        var offset = new Vector3D(hole.Source.X, hole.Source.Y, hole.Source.Z);
        double depth = (hole.FromEnd ? hole.Prism.Thickness : 0) + (hole.FromEnd ? FaceHighlightLift : -FaceHighlightLift);
        var projection = BuildProjection;
        var points = projection.Polygon(HolePrism.Circle(hole.Center, diameter / 2)
            .Select(point => hole.Prism.Vertex(point, depth, offset)).ToArray());
        var result = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (points.Length < 3)
            return result;
        var ring = new StreamGeometry();
        using (var outline = ring.Open())
        {
            outline.BeginFigure(points[0], true, true);
            outline.PolyLineTo(points[1..], true, false);
        }
        result.Children.Add(ring);
        if (projection.Project(hole.Prism.Vertex(hole.Center, depth, offset)) is Point center)
            result.Children.Add(new EllipseGeometry(center, 2.5, 2.5));
        return result;
    }

    private static BoxBevelTarget RoundRimAt(Part part, ThreadSurface surface, Point3D point)
    {
        var offset = new Vector3D(part.X, part.Y, part.Z);
        var center = surface.Prism.Vertex(surface.Center, (surface.Low + surface.High) / 2, offset);
        return RoundBevel.Rim(Vector3D.DotProduct(point - center, surface.Prism.Frame.Normal) >= 0);
    }

    private GeometryGroup RoundRimOutline(Part part, ThreadSurface surface, BoxBevelTarget rim)
    {
        var geometry = new GeometryGroup();
        var projection = BuildProjection;
        var offset = new Vector3D(part.X, part.Y, part.Z);
        var center = surface.Prism.Vertex(surface.Center, rim.Z > 0 ? surface.High : surface.Low, offset);
        var view = Camera.Position - center;
        bool faceVisible = Vector3D.DotProduct(view, surface.Prism.Frame.Normal) * rim.Z > 0;
        foreach (var (a, b) in RimSegments(part, surface, rim))
        {
            var radial = a + (b - a) * 0.5 - center;
            if ((faceVisible || Vector3D.DotProduct(view - radial, radial) > 0) && projection.Segment(a, b) is { } segment)
                geometry.Children.Add(new LineGeometry(segment.Start, segment.End));
        }
        return geometry;
    }

    private BoxBevelTarget? FindRoundBevelTarget(Part part, ThreadSurface surface, Point pointer, SceneHit? hit)
    {
        if (hit is not null && pickableParts[hit.ModelHit] is var entry && ReferenceEquals(entry.Part, part) &&
            Math.Abs(Vector3D.DotProduct(BevelFacetNormal(entry.Triangles[hit.TriangleIndex]), surface.Prism.Frame.Normal)) > 1e-6)
            return RoundRimAt(part, surface, hit.PointHit);

        BoxBevelTarget? closest = null;
        double distance = EdgePickRadius * EdgePickRadius;
        var projection = BuildProjection;
        foreach (bool top in new[] { true, false })
        {
            var rim = RoundBevel.Rim(top);
            foreach (var (a, b) in RimSegments(part, surface, rim))
            {
                if (projection.Project(a) is not Point start || projection.Project(b) is not Point end)
                    continue;
                var line = end - start;
                if (line.LengthSquared < 1e-8)
                    continue;
                double along = Math.Clamp(Vector.Multiply(pointer - start, line) / line.LengthSquared, 0, 1);
                var screen = start + line * along;
                double gap = (pointer - screen).LengthSquared;
                if (gap < distance && IsDetailVisible(a + (b - a) * along, screen))
                {
                    distance = gap;
                    closest = rim;
                }
            }
        }
        return closest;
    }

    private static IEnumerable<(Point3D Start, Point3D End)> RimSegments(Part part, ThreadSurface surface, BoxBevelTarget rim)
    {
        var offset = new Vector3D(part.X, part.Y, part.Z);
        double depth = rim.Z > 0 ? surface.High : surface.Low;
        for (int i = 0; i < surface.Ring.Length; i++)
            yield return (surface.Prism.Vertex(surface.Ring[i], depth, offset),
                surface.Prism.Vertex(surface.Ring[(i + 1) % surface.Ring.Length], depth, offset));
    }

    private SceneHit? SketchBoundaryHit(Point pointer)
    {
        var projection = BuildProjection;
        SceneHit? best = null;
        double distance = 49;
        foreach (var (model, entry) in pickableParts)
        {
            var sketch = entry.Part;
            if (sketch.Kind != PartKind.Sketch)
                continue;
            var offset = new Vector3D(sketch.X, sketch.Y, sketch.Z);
            int triangleIndex = 0;
            foreach (var profile in sketch.SketchProfiles)
            {
                for (int i = 0; i < profile.Length; i++)
                {
                    var a = sketch.SketchVertex(profile[i], 0) + offset;
                    var b = sketch.SketchVertex(profile[(i + 1) % profile.Length], 0) + offset;
                    if (projection.Project(a) is not Point start || projection.Project(b) is not Point end)
                        continue;
                    var line = end - start;
                    if (line.LengthSquared < 1e-8)
                        continue;
                    double along = Math.Clamp(Vector.Multiply(pointer - start, line) / line.LengthSquared, 0, 1);
                    double gap = (pointer - (start + line * along)).LengthSquared;
                    if (gap < distance)
                    {
                        distance = gap;
                        best = new SceneHit(model, a + (b - a) * along, triangleIndex);
                    }
                }
                triangleIndex += profile.Length - 2;
            }
        }
        return best;
    }
}
