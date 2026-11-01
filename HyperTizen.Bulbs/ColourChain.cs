using System;

namespace HyperTizen.Bulbs
{
    /// <summary>
    /// NV12 frame -> two left-edge LED colours, reproducing HyperHDR 22.0.0 with the
    /// Mac's config: flatbuffer NV12 (HDR off), "advanced" mapping, gamma 1.5,
    /// backlightThreshold 0.0039 coloured, every other adjustment identity.
    /// Values are the 8-bit numbers HyperHDR sends over udpraw, before smoothing.
    /// </summary>
    public static class ColourChain
    {
        const int LutSize = 512;              // InfiniteProcessing::LUT_SIZE
        const float Gamma = 1.5f;
        const float MinBacklight = 0.0039f;

        static readonly ushort[] SrgbToLinear16 = new ushort[256];
        static readonly float[] LinearToSrgb = new float[LutSize];
        static readonly float[] GammaLut = new float[LutSize];

        static ColourChain()
        {
            for (int i = 0; i < 256; i++)
            {
                float s = i / 255.0f;
                float lin = s <= 0.04045f ? s / 12.92f : (float)Math.Pow((s + 0.055f) / 1.055f, 2.4f);
                SrgbToLinear16[i] = (ushort)(lin * 65535.0f + 0.5f);
            }
            for (int i = 0; i < LutSize; i++)
            {
                float v = (float)i / (LutSize - 1);
                LinearToSrgb[i] = v <= 0.0031308f ? v * 12.92f : 1.055f * (float)Math.Pow(v, 1.0f / 2.4f) - 0.055f;
                GammaLut[i] = (float)Math.Pow(v, Gamma);
            }
        }

        public static (Rgb Lower, Rgb Upper) LeftZones(byte[] y, byte[] uv, int width, int height)
        {
            // ImageColorAveraging ctor, borders 0: leds[0] = x 0-0.05, y 0.5-1; leds[1] = x 0-0.05, y 0-0.5
            int x1 = QRound(width * 0.05);
            int mid = QRound(height * 0.5);
            return (Zone(y, uv, width, 0, x1, mid, height), Zone(y, uv, width, 0, x1, 0, mid));
        }

        static Rgb Zone(byte[] yPlane, byte[] uv, int width, int x0, int x1, int y0, int y1)
        {
            long sr = 0, sg = 0, sb = 0;
            for (int row = y0; row < y1; row++)
                for (int col = x0; col < x1; col++)
                {
                    int uvIdx = (row / 2) * width + (col & ~1);
                    var (r, g, b) = Bt601Limited(yPlane[row * width + col], uv[uvIdx], uv[uvIdx + 1]);
                    sr += SrgbToLinear16[r]; sg += SrgbToLinear16[g]; sb += SrgbToLinear16[b];
                }
            float n = (y1 - y0) * (x1 - x0);
            return new Rgb(Output(sr / n / 65535.0f), Output(sg / n / 65535.0f), Output(sb / n / 65535.0f));
        }

        /// <summary>
        /// lut_lin_tables.3d segment 2 (YUV, no tone mapping) is exactly BT.601 limited range,
        /// rounded and clamped: verified against all 16.7M entries of the installed table.
        /// </summary>
        static (int R, int G, int B) Bt601Limited(int y, int u, int v)
        {
            const double kr = 0.299, kb = 0.114, kg = 1 - kr - kb;
            double Y = (y - 16) * 255 / 219.0, U = (u - 128) * 255 / 224.0, V = (v - 128) * 255 / 224.0;
            return (To8(Y + 2 * (1 - kr) * V),
                    To8(Y - 2 * kb * (1 - kb) / kg * U - 2 * kr * (1 - kr) / kg * V),
                    To8(Y + 2 * (1 - kb) * U));
        }

        static int To8(double x) => (int)Math.Max(0, Math.Min(255, Math.Floor(x + 0.5)));

        /// <summary>applyyAllProcessingSteps (non-identity steps only), steady-state smoothing, LedDevice::write.</summary>
        static double Output(float linear)
        {
            float c = Lerp(GammaLut, Lerp(LinearToSrgb, Clamp01(linear)));
            c = Math.Max(c, MinBacklight);                       // applyMinimalBacklight, coloured
            c = Math.Min(Math.Max(c, MinBacklight), 1f);         // interpolator getCurrentColors clamp
            return Math.Round(Math.Max(0f, Math.Min(255f, c * 255.0f)), MidpointRounding.AwayFromZero); // std::lround
        }

        static float Lerp(float[] lut, float x)
        {
            float pos = x * (LutSize - 1);
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, LutSize - 1);
            float f = pos - i0;
            return lut[i0] + (lut[i1] - lut[i0]) * f;
        }

        static float Clamp01(float x) => Math.Max(0f, Math.Min(x, 1f));

        static int QRound(double x) => (int)Math.Floor(x + 0.5);
    }
}
