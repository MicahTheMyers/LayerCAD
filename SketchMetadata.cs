namespace LayerCAD;

public sealed record ProfileDimensions(double Width, double Height, bool Centered, bool FollowInnerCircles = false)
{
    internal bool IsValid() => Part.ValidDimension(Width) && Part.ValidDimension(Height);

    internal SketchPoint[] Apply(IReadOnlyList<SketchPoint> profile)
    {
        double minX = profile.Min(p => p.X), minY = profile.Min(p => p.Y);
        double spanX = profile.Max(p => p.X) - minX, spanY = profile.Max(p => p.Y) - minY;
        double x = Centered ? -Width / 2 : minX, y = Centered ? -Height / 2 : minY;
        return profile.Select(p => new SketchPoint(x + (p.X - minX) * Width / spanX,
            y + (p.Y - minY) * Height / spanY)).ToArray();
    }
}


public enum ProfileConstraintKind { Horizontal, Vertical, CoincidentOrigin }

public sealed record ProfileConstraint(ProfileConstraintKind Kind, int Vertex);


public sealed record SketchReferenceImage(byte[] Png, double Width, double Height, double X, double Y, double Opacity)
{
    internal bool IsValid() => Png is { Length: > 0 and <= 8_000_000 } &&
        Part.ValidDimension(Width) && Part.ValidDimension(Height) &&
        Part.ValidOffset(X) && Part.ValidOffset(Y) && Opacity is >= 0 and <= 1;
}

internal static class SketchConstraintSolver
{
    internal static SketchPoint[] Solve(IReadOnlyList<SketchPoint> outline, ProfileConstraint[] rules, int preferred)
    {
        var x = Enumerable.Range(0, outline.Count).ToArray();
        var y = (int[])x.Clone();
        foreach (var rule in rules)
        {
            int next = (rule.Vertex + 1) % outline.Count;
            if (rule.Kind == ProfileConstraintKind.Vertical)
                x[Root(x, next)] = Root(x, rule.Vertex);
            if (rule.Kind == ProfileConstraintKind.Horizontal)
                y[Root(y, next)] = Root(y, rule.Vertex);
        }
        var origin = rules.Where(rule => rule.Kind == ProfileConstraintKind.CoincidentOrigin).Select(rule => rule.Vertex).ToArray();
        return Enumerable.Range(0, outline.Count).Select(i => new SketchPoint(
            Value(x, i, p => p.X), Value(y, i, p => p.Y))).ToArray();

        double Value(int[] groups, int index, Func<SketchPoint, double> coordinate)
        {
            int group = Root(groups, index);
            if (origin.Any(vertex => Root(groups, vertex) == group))
                return 0;
            return coordinate(outline[Root(groups, preferred) == group ? preferred : group]);
        }

        static int Root(int[] parents, int index)
        {
            while (parents[index] != index)
                index = parents[index];
            return index;
        }
    }
}
