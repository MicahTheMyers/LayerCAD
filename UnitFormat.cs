using System.Globalization;

namespace LayerCAD;

public enum DisplayUnits { Millimeters, Inches }

internal static class UnitFormat
{
    public static string Symbol(this DisplayUnits units) => units == DisplayUnits.Inches ? "in" : "mm";

    public static double MillimetersPerUnit(this DisplayUnits units) => units == DisplayUnits.Inches ? 25.4 : 1;

    public static string Format(this DisplayUnits units, double millimeters)
    {
        double value = millimeters / units.MillimetersPerUnit();
        string format = units == DisplayUnits.Millimeters ? "G" :
            Math.Abs(value) is > 0 and < 0.000001 ? "G6" : "0.######";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    public static string FormatLabel(this DisplayUnits units, double millimeters)
    {
        double value = millimeters / units.MillimetersPerUnit();
        double threshold = units == DisplayUnits.Inches ? 0.0001 : 0.001;
        string format = Math.Abs(value) > 0 && Math.Abs(value) < threshold ? "G3" :
            units == DisplayUnits.Inches ? "0.####" : "0.###";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    public static double Read(this DisplayUnits units, string input, double? displayedMillimeters = null)
    {
        string text = input.Trim();
        if (displayedMillimeters is double original && text == units.Format(original))
            return original;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value)
            ? value * units.MillimetersPerUnit() : double.NaN;
    }
}
