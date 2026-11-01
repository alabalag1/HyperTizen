using System;

namespace HyperTizen.Bulbs
{
    /// <summary>Matter colour: Hue/Sat 0-254, null when the screen counts as black (keep the last colour, only dim).</summary>
    public readonly struct MatterColour
    {
        public readonly int? Hue, Sat;
        public readonly int Level;
        public MatterColour(int? hue, int? sat, int level) { Hue = hue; Sat = sat; Level = level; }
    }

    /// <summary>Port of rgbToMatter / colourChanged / levelChanged / hueDiff from bulb-sync/bridge.mjs.</summary>
    public static class BulbMapper
    {
        // JS Math.round: halves go toward +infinity (not banker's rounding)
        static int Round(double x) => (int)Math.Floor(x + 0.5);

        public static MatterColour ToMatter(Rgb rgb, Tuning t)
        {
            double r = rgb.R, g = rgb.G, b = rgb.B;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double v = Math.Min(1, Math.Max(0, (max - t.Black) / (255.0 - t.Black)));   // 0 at the black threshold
            int level = Round(t.MinLevel + (254 - t.MinLevel) * Math.Pow(v, t.Gamma));
            if (max < t.Black) return new MatterColour(null, null, level);
            double h = 0;
            if (d != 0)
            {
                if (max == r) h = ((g - b) / d) % 6;
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h *= 60; if (h < 0) h += 360;
            }
            // max == 0 only when Black == 0 and pixel is pure black; JS yields NaN there, use sat 0
            int sat = max > 0 ? Round(d / max * 254) : 0;
            return new MatterColour(Round(h / 360 * 254), sat, level);
        }

        public static int HueDiff(int a, int b) { int d = Math.Abs(a - b) % 254; return Math.Min(d, 254 - d); }

        public static bool ColourChanged(MatterColour? prev, MatterColour next) =>
            next.Hue != null &&
            (prev == null || prev.Value.Hue == null || HueDiff(prev.Value.Hue.Value, next.Hue.Value) > 3
                || Math.Abs(prev.Value.Sat.Value - next.Sat.Value) > 6);

        public static bool LevelChanged(int? prev, int next) => prev == null || Math.Abs(prev.Value - next) > 3;
    }
}
