using System;

namespace BlackScholesApp.Helpers;

public static class MathHelpers
{
    public static bool IsPositive(double v)    => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0;
    public static bool IsNonNegative(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;
    public static double SafeDiv(double n, double d) => Math.Abs(d) < 1e-12 ? 0.0 : n / d;
    public static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    public static string FormatDiff(double diff, double reference, string unit = "")
    {
        string sign = diff >= 0 ? "+" : "";
        if (Math.Abs(reference) < 1e-9) return $"{sign}{diff:F2}{unit}";
        double pct = diff / reference * 100.0;
        return $"{sign}{diff:F2}{unit} ({sign}{pct:F1}%)";
    }
}
