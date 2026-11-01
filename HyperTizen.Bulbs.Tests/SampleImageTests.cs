using System.IO;
using System.Linq;
using System.Text.Json;
using HyperTizen.Bulbs;
using Xunit;

namespace HyperTizen.Bulbs.Tests
{
    public class SampleImageTests
    {
        private static Rgb10[] Uniform(int v) =>
            Enumerable.Repeat(new Rgb10(v, v, v), 16).ToArray();

        [Fact]
        public void AllBlack_GivesY0_UV128()
        {
            var (y, uv) = SampleImage.ToNv12(Uniform(0));
            Assert.Equal(SampleImage.Width * SampleImage.Height, y.Length);
            Assert.All(y, b => Assert.Equal(0, b));
            Assert.All(uv, b => Assert.Equal(128, b));
        }

        [Fact]
        public void AllWhite_GivesY255()
        {
            var (y, _) = SampleImage.ToNv12(Uniform(1023));
            // left edge, columns 0-2, all rows
            for (int row = 0; row < SampleImage.Height; row++)
                for (int col = 0; col < 3; col++)
                    Assert.Equal(255, y[row * SampleImage.Width + col]);
        }

        [Fact]
        public void MatchesOriginalEncoder()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("fixtures", "reference.json")));
            var raw = doc.RootElement[0].GetProperty("raw").EnumerateArray()
                .Select(p => new Rgb10(p[0].GetInt32(), p[1].GetInt32(), p[2].GetInt32())).ToArray();
            var golden = File.ReadAllBytes(Path.Combine("fixtures", "nv12-golden.bin"));

            var (y, uv) = SampleImage.ToNv12(raw);

            Assert.Equal(golden, y.Concat(uv).ToArray());
        }
    }
}
