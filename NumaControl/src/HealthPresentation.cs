using System;

namespace NumaControl
{
    internal static class HealthPresentation
    {
        internal static int Level(double fraction) { return fraction >= 0.9 ? 2 : fraction >= 0.7 ? 1 : 0; }
        internal static string Role(int level) { return level >= 2 ? "Error" : level == 1 ? "Warning" : "Success"; }
        internal static double Ratio(double value, double limit) { return limit <= 0 ? 0 : Math.Max(0, value) / limit; }
    }
}
