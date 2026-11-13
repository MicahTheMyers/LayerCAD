using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace LayerCAD;

public partial class MainWindow
{
    private BoxBevelTarget? FindPolygonBevelTarget(Part part, PolygonBevel polygon, Point pointer, SceneHit? hit)
    {
        BoxBevelTarget? nearest = null;
        double best = 64;
        foreach (var corner in polygon.Corners())
        {
            var point = polygon.Corner(corner);
            if (ProjectViewportPoint(point) is not Point screen)
                continue;
            double gap = (screen - pointer).LengthSquared;
            if (gap < best && IsDetailVisible(point, screen))
            {
                best = gap;
                nearest = corner;
            }
        }
        if (nearest is not null)
            return nearest;
        best = 121;
        foreach (var edge in polygon.Edges())
        {
            var (a, b) = polygon.Segment(edge);
            if (ProjectViewportPoint(a) is not Point start || ProjectViewportPoint(b) is not Point end)
                continue;
            var line = end - start;
            if (line.LengthSquared < 1e-8)
                continue;
            double along = Math.Clamp(Vector.Multiply(pointer - start, line) / line.LengthSquared, 0, 1);
            var point = start + line * along;
            double gap = (point - pointer).LengthSquared;
            if (gap < best && IsDetailVisible(a + (b - a) * along, point))
            {
                best = gap;
                nearest = edge;
            }
        }
        if (nearest is not null || hit is null || !ReferenceEquals(pickableParts[hit.ModelHit].Part, part))
            return nearest;
        return polygon.Face(BevelFacetNormal(pickableParts[hit.ModelHit].Triangles[hit.TriangleIndex]));
    }

    private void DrawPolygonTargets(PolygonBevel polygon, IEnumerable<BoxBevelTarget> targets, Path edges, Path corners)
    {
        var lines = new GeometryGroup();
        var dots = new GeometryGroup();
        foreach (var target in targets)
        {
            if (target.ProfileKind == ProfileBevelKind.Corner)
            {
                if (ProjectViewportPoint(polygon.Corner(target)) is Point screen)
                    dots.Children.Add(new EllipseGeometry(screen, 5, 5));
            }
            else
            {
                var (start, end) = polygon.Segment(target);
                AddBevelLine(lines, start, end);
            }
        }
        edges.Data = lines;
        corners.Data = dots;
    }
}
