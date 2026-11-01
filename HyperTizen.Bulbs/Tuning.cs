using System.Globalization;

namespace HyperTizen.Bulbs
{
    /// <summary>User-adjustable colour/brightness tuning, parsed from TV-app settings.</summary>
    public sealed record Tuning(double Gamma = 1, int SmoothMs = 300, int MinLevel = 5, int Black = 10)
    {
        public static readonly Tuning Default = new Tuning();

        /// <summary>Applies one setting. Returns false (and <paramref name="updated"/> = current) on unknown key or bad value.</summary>
        public static bool TryParse(string key, string value, Tuning current, out Tuning updated)
        {
            updated = current;
            var ci = CultureInfo.InvariantCulture;
            switch (key)
            {
                case "bulbGamma":
                    // written as !(in range) so NaN is rejected too
                    if (!double.TryParse(value, NumberStyles.Float, ci, out var g) || !(g >= 0.2 && g <= 5)) return false;
                    updated = current with { Gamma = g };
                    return true;
                case "bulbSmoothMs":
                    if (!TryInt(value, 0, 5000, out var s)) return false;
                    updated = current with { SmoothMs = s };
                    return true;
                case "bulbMinLevel":
                    if (!TryInt(value, 1, 254, out var m)) return false;
                    updated = current with { MinLevel = m };
                    return true;
                case "bulbBlack":
                    if (!TryInt(value, 0, 254, out var b)) return false;
                    updated = current with { Black = b };
                    return true;
                default:
                    return false;
            }
        }

        static bool TryInt(string v, int lo, int hi, out int n) =>
            int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= lo && n <= hi;
    }
}
