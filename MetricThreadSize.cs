namespace LayerCAD;

internal sealed record MetricThreadSize(string Name, double Diameter, double Pitch)
{
    public double FundamentalHeight => Math.Sqrt(3) * Pitch / 2;
    public double PitchDiameter => Diameter - 3 * FundamentalHeight / 4;
    public double RadialDepth(bool external) => FundamentalHeight * (external ? 17d / 24 : 5d / 8);
    public double MinorDiameter(bool external) => Diameter - 2 * RadialDepth(external);

    public double GrooveDepth(double fraction, bool external)
    {
        double axial = Math.Min(fraction, 1 - fraction);
        if (!external)
            return Math.Clamp((axial - 0.125) * Pitch * Math.Sqrt(3), 0, RadialDepth(false));

        if (axial <= 0.375)
            return Math.Max(0, (axial - 0.0625) * Pitch * Math.Sqrt(3));

        double rootRadius = FundamentalHeight / 6;
        double distance = (0.5 - axial) * Pitch;
        double rootCenter = 7 * FundamentalHeight / 8 - 2 * rootRadius;
        return rootCenter + Math.Sqrt(Math.Max(0, rootRadius * rootRadius - distance * distance));
    }

    // ISO 261 coarse pitches and ISO 724 basic design dimensions.
    public static readonly MetricThreadSize[] Coarse =
    [
        new("M1.6", 1.6, 0.35), new("M2", 2, 0.4), new("M2.5", 2.5, 0.45),
        new("M3", 3, 0.5), new("M3.5", 3.5, 0.6), new("M4", 4, 0.7),
        new("M5", 5, 0.8), new("M6", 6, 1), new("M7", 7, 1),
        new("M8", 8, 1.25), new("M10", 10, 1.5), new("M12", 12, 1.75),
        new("M14", 14, 2), new("M16", 16, 2), new("M18", 18, 2.5),
        new("M20", 20, 2.5), new("M22", 22, 2.5), new("M24", 24, 3),
        new("M27", 27, 3), new("M30", 30, 3.5)
    ];

    public static MetricThreadSize? Find(string? name) =>
        name is null ? null : Coarse.FirstOrDefault(size => size.Name == name);
}
