using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HyperTizen.Bulbs;
using Xunit;

namespace HyperTizen.Bulbs.Tests
{
    public class ColourChainTests
    {
        private static Rgb10[] Points(JsonElement arr) =>
            arr.EnumerateArray().Select(p => new Rgb10(p[0].GetInt32(), p[1].GetInt32(), p[2].GetInt32())).ToArray();

        private static void AssertClose(JsonElement expected, Rgb actual, double tol, int row)
        {
            double[] e = { expected[0].GetInt32(), expected[1].GetInt32(), expected[2].GetInt32() };
            double[] a = { actual.R, actual.G, actual.B };
            for (int c = 0; c < 3; c++)
                Assert.True(Math.Abs(e[c] - a[c]) <= tol,
                    $"row {row} channel {c}: expected {e[c]}, got {a[c]}");
        }

        [Fact]
        public void ReproducesHyperHdrReference()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("fixtures", "reference.json")));
            int i = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var (y, uv) = SampleImage.ToNv12(Points(row.GetProperty("raw")));
                var (lower, upper) = ColourChain.LeftZones(y, uv, SampleImage.Width, SampleImage.Height);
                AssertClose(row.GetProperty("led0"), ColourChain.ToDevice8Bit(lower), 2, i);
                AssertClose(row.GetProperty("led1"), ColourChain.ToDevice8Bit(upper), 2, i);
                i++;
            }
        }

        [Fact]
        public void Black_MatchesReference()
        {
            // HyperHDR's backlightThreshold 0.0039 (colored) lifts black to 1, not 0.
            var (y, uv) = SampleImage.ToNv12(Enumerable.Repeat(new Rgb10(0, 0, 0), 16).ToArray());
            var (lower, upper) = ColourChain.LeftZones(y, uv, SampleImage.Width, SampleImage.Height);
            foreach (var z in new[] { ColourChain.ToDevice8Bit(lower), ColourChain.ToDevice8Bit(upper) })
            {
                Assert.Equal(1.0, z.R);
                Assert.Equal(1.0, z.G);
                Assert.Equal(1.0, z.B);
            }
        }

        [Fact]
        public void Pipeline_SmoothThenRound_MatchesReferenceAtSteadyState()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("fixtures", "reference.json")));
            int i = 0;
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var (y, uv) = SampleImage.ToNv12(Points(row.GetProperty("raw")));
                var (lower, upper) = ColourChain.LeftZones(y, uv, SampleImage.Width, SampleImage.Height);
                var s = new HybridSmoother();
                var black = new Rgb(0, 0, 0);
                s.SetTarget(black, black, 0);     // the first target snaps; step from black instead
                s.Tick(0);
                s.SetTarget(lower, upper, 0);
                (Rgb Lower, Rgb Upper) o = default;
                for (int t = 20; t <= 3000; t += 20) o = s.Tick(t);
                AssertClose(row.GetProperty("led0"), ColourChain.ToDevice8Bit(o.Lower), 3, i);
                AssertClose(row.GetProperty("led1"), ColourChain.ToDevice8Bit(o.Upper), 3, i);
                i++;
            }
        }

        [Fact]
        public void ZoneSplit_UpperAndLowerDiffer()
        {
            var raw = Enumerable.Repeat(new Rgb10(0, 0, 0), 16).ToArray();
            raw[15] = raw[14] = new Rgb10(1023, 0, 0); // left edge, y 0.2 / 0.4
            raw[13] = raw[12] = new Rgb10(0, 0, 1023); // left edge, y 0.6 / 0.8
            var (y, uv) = SampleImage.ToNv12(raw);
            var (lower, upper) = ColourChain.LeftZones(y, uv, SampleImage.Width, SampleImage.Height);
            Assert.True(upper.R > 2 * upper.B && upper.R > 2 * upper.G, $"upper {upper.R},{upper.G},{upper.B}");
            Assert.True(lower.B > 2 * lower.R && lower.B > 2 * lower.G, $"lower {lower.R},{lower.G},{lower.B}");
        }
    }
}
