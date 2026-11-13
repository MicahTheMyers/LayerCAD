using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SnapGeometry = System.Windows.Media.Geometry;

namespace LayerCAD;

public partial class SketchEditor
{
    private enum SnapKind { Endpoint, Midpoint, Edge, Alignment }
    private System.Windows.Shapes.Path? snapMarker;
    private Border? snapPrompt;
    private SnapKind snapKind;
    private SketchPoint? alignmentReference;

    private SketchPoint? DrawingSnap(Point pointer)
    {
        snapKind = SnapKind.Alignment;
        if (PlacementStart is not null && fixedDimensions.Any(value => value))
            return null;
        var direct = GeometrySnap(pointer);
        if (direct is not null || alignmentReference is not SketchPoint reference)
            return direct;
        var raw = SketchPointAt(pointer);
        if (!InRange(raw))
            return null;
        var grid = GridPoint(pointer);
        var vertical = new SketchPoint(reference.X, raw.Y);
        var horizontal = new SketchPoint(raw.X, reference.Y);
        double verticalDistance = Distance(vertical), horizontalDistance = Distance(horizontal);
        if (verticalDistance < 100 && horizontalDistance < 100)
            return reference;
        if (verticalDistance < horizontalDistance && verticalDistance < 100)
            return new SketchPoint(reference.X, grid.Y);
        if (horizontalDistance < 100)
            return new SketchPoint(grid.X, reference.Y);
        return null;

        double Distance(SketchPoint point) => sketchProjection.Project(WorldPoint(point)) is Point screen
            ? (screen - pointer).LengthSquared : double.PositiveInfinity;
    }

    private SketchPoint? FindAlignmentVertex(Point pointer)
    {
        double best = 100;
        SketchPoint? nearest = null;
        foreach (var vertex in profiles.SelectMany(profile => profile))
        {
            if (vertex == PlacementStart || sketchProjection.Project(WorldPoint(vertex)) is not Point screen)
                continue;
            double distance = (screen - pointer).LengthSquared;
            if (distance < best)
            {
                best = distance;
                nearest = vertex;
            }
        }
        return nearest;
    }

    private bool SelectAlignmentReference(Point pointer)
    {
        if (FindAlignmentVertex(pointer) is not SketchPoint vertex)
            return false;
        alignmentReference = alignmentReference == vertex ? null : vertex;
        Redraw();
        Feedback.Text = alignmentReference is null ? "Alignment reference cleared." :
            "Alignment point selected. Move near either guide to snap.";
        return true;
    }

    private void DrawAlignmentReference(double radius)
    {
        if (alignmentReference is not SketchPoint reference)
            return;
        Guide(new(reference.X, reference.Y - radius), new(reference.X, reference.Y + radius));
        Guide(new(reference.X - radius, reference.Y), new(reference.X + radius, reference.Y));
        if (sketchProjection.Project(WorldPoint(reference)) is Point screen)
        {
            var dot = new Ellipse { Width = 9, Height = 9, Fill = Brushes.Gold, IsHitTestVisible = false };
            Canvas.SetLeft(dot, screen.X - 4.5);
            Canvas.SetTop(dot, screen.Y - 4.5);
            Drawing.Children.Add(dot);
        }

        void Guide(SketchPoint start, SketchPoint end)
        {
            if (sketchProjection.Segment(WorldPoint(start), WorldPoint(end)) is { } line)
                Drawing.Children.Add(new Line { X1 = line.Start.X, Y1 = line.Start.Y, X2 = line.End.X, Y2 = line.End.Y,
                    Stroke = Brushes.Gold, Opacity = 0.55, StrokeDashArray = [5, 4], IsHitTestVisible = false });
        }
    }

    private SketchPoint? GeometrySnap(Point pointer)
    {
        if (moveOriginal is not null)
            return null;

        double best = 100;
        SketchPoint? target = null;
        var last = PlacementStart;
        bool orthogonal = tool == SketchTool.OrthogonalPolyline && last is not null;
        var draft = drawingPolyline ? points.Take(points.Count - 1).ToArray() : [];
        var circles = profiles.Where(profile => !openProfiles.Contains(profile) && IsCircularProfile(profile)).ToHashSet();
        foreach (var point in profiles.Where(profile => !circles.Contains(profile)).SelectMany(profile => profile).Concat(draft)
                     .Concat(faceGuide.SelectMany(edge => new[] { edge.Start, edge.End })).Prepend(default))
            Consider(point, SnapKind.Endpoint);
        if (target is not null)
            return target;

        var segments = new List<(SketchPoint A, SketchPoint B, bool Midpoint)>();
        foreach (var profile in profiles)
        {
            for (int i = 0; i < profile.Count - (openProfiles.Contains(profile) ? 1 : 0); i++)
                segments.Add((profile[i], profile[(i + 1) % profile.Count], !circles.Contains(profile)));
        }
        for (int i = 1; i < draft.Length; i++)
            segments.Add((draft[i - 1], draft[i], true));
        foreach (var (a, b) in faceGuide)
            segments.Add((a, b, true));
        best = 64;
        foreach (var (a, b, midpoint) in segments)
            if (midpoint)
                Consider(new((a.X + b.X) / 2, (a.Y + b.Y) / 2), SnapKind.Midpoint);
        if (target is not null)
            return target;
        best = 100;
        foreach (var (a, b, _) in segments)
            ConsiderSegment(a, b);
        return target;

        void Consider(SketchPoint point, SnapKind kind = SnapKind.Edge)
        {
            if (point == last || !InRange(point) ||
                (orthogonal && point.X != last!.Value.X && point.Y != last.Value.Y) ||
                sketchProjection.Project(WorldPoint(point)) is not Point screen)
                return;
            double distance = (pointer - screen).LengthSquared;
            if (distance < best)
            {
                best = distance;
                target = point;
                snapKind = kind;
            }
        }

        void ConsiderSegment(SketchPoint a, SketchPoint b)
        {
            if (sketchProjection.Segment(WorldPoint(a), WorldPoint(b)) is not { } segment)
                return;
            var line = segment.End - segment.Start;
            if (line.LengthSquared < 1e-8)
                return;
            double fraction = Math.Clamp(Vector.Multiply(pointer - segment.Start, line) / line.LengthSquared, 0, 1);
            var nearestScreen = segment.Start + line * fraction;
            if ((nearestScreen - pointer).LengthSquared >= best)
                return;

            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (orthogonal)
            {
                var start = last!.Value;
                if (dx != 0 && (start.X - a.X) / dx is >= 0 and <= 1)
                    Consider(new(start.X, a.Y + dy * ((start.X - a.X) / dx)));
                if (dy != 0 && (start.Y - a.Y) / dy is >= 0 and <= 1)
                    Consider(new(a.X + dx * ((start.Y - a.Y) / dy), start.Y));
                if ((dx != 0 || start.X != a.X) && (dy != 0 || start.Y != a.Y))
                    return;
            }
            var planePoint = SketchPointAt(nearestScreen);
            double ratio = Math.Clamp(((planePoint.X - a.X) * dx + (planePoint.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            Consider(new(a.X + dx * ratio, a.Y + dy * ratio));
        }
    }

    private void ShowDrawingSnap(SketchPoint? target)
    {
        if (target is not SketchPoint point || sketchProjection.Project(WorldPoint(point)) is not Point screen)
        {
            if (snapMarker is not null)
                Drawing.Children.Remove(snapMarker);
            if (snapPrompt is not null)
                Drawing.Children.Remove(snapPrompt);
            snapMarker = null;
            snapPrompt = null;
            return;
        }
        if (snapMarker is null)
        {
            snapMarker = new System.Windows.Shapes.Path { Stroke = Brushes.LightGreen,
                StrokeThickness = 1.5, IsHitTestVisible = false };
            Panel.SetZIndex(snapMarker, 2);
            Drawing.Children.Add(snapMarker);
            snapPrompt = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 40, 56)),
                Padding = new Thickness(4, 2, 4, 2),
                Child = new TextBlock { Foreground = Brushes.WhiteSmoke, FontSize = 11 },
                IsHitTestVisible = false
            };
            Panel.SetZIndex(snapPrompt, 2);
            Drawing.Children.Add(snapPrompt);
        }
        snapMarker.Data = SnapSymbols[(int)snapKind];
        Canvas.SetLeft(snapMarker, screen.X - 8);
        Canvas.SetTop(snapMarker, screen.Y - 8);
        string action = PlacementStart is null ? "Place first point" : "Place next point";
        string feature = snapKind switch
        {
            SnapKind.Endpoint => "Endpoint",
            SnapKind.Midpoint => "Midpoint",
            SnapKind.Edge => "On edge",
            _ => "Aligned"
        };
        ((TextBlock)snapPrompt!.Child).Text = $"{action} · {feature}";
        snapPrompt.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(snapPrompt, Math.Clamp(screen.X + 16, 0, Math.Max(0, Drawing.ActualWidth - snapPrompt.DesiredSize.Width)));
        Canvas.SetTop(snapPrompt, Math.Clamp(screen.Y + 12, 0, Math.Max(0, Drawing.ActualHeight - snapPrompt.DesiredSize.Height)));
    }

    private static readonly SnapGeometry[] SnapSymbols = new[]
    {
        "M 1,1 H 15 V 15 H 1 Z",
        "M 8,1 L 15,14 H 1 Z M 5,8 H 11",
        "M 3,3 L 13,13 M 13,3 L 3,13",
        "M 8,1 L 15,8 L 8,15 L 1,8 Z"
    }.Select(path =>
    {
        var symbol = SnapGeometry.Parse(path);
        symbol.Freeze();
        return symbol;
    }).ToArray();
}
