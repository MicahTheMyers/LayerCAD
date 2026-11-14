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
