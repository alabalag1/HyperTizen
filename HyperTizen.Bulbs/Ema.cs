using System;

namespace HyperTizen.Bulbs
{
    /// <summary>The bridge's fade (~/git/bulb-sync/bridge.mjs send loop), one step.</summary>
    public static class Ema
    {
        public static Rgb Step(Rgb current, Rgb target, double dtMs, int smoothMs)
        {
            double a = smoothMs > 0 ? 1 - Math.Exp(-dtMs / smoothMs) : 1;
            return new Rgb(current.R + (target.R - current.R) * a,
                           current.G + (target.G - current.G) * a,
                           current.B + (target.B - current.B) * a);
        }
    }
}
