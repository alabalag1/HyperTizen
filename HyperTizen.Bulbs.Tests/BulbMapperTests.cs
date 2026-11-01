using HyperTizen.Bulbs;
using Xunit;

namespace HyperTizen.Bulbs.Tests
{
    public class BulbMapperTests
    {
        static MatterColour Map(double r, double g, double b) => BulbMapper.ToMatter(new Rgb(r, g, b), Tuning.Default);

        [Fact]
        public void Black_HasNoColour_AndMinLevel()
        {
            var c = Map(0, 0, 0);
            Assert.Null(c.Hue);
            Assert.Null(c.Sat);
            Assert.Equal(5, c.Level);
        }

        [Fact]
        public void AtBlackThreshold_NoLevelJump() => Assert.Equal(5, Map(10, 0, 0).Level);

        [Fact]
        public void Red_IsFullySaturatedFullLevel()
        {
            var c = Map(255, 0, 0);
            Assert.Equal((0, 254, 254), (c.Hue.Value, c.Sat.Value, c.Level));
        }

        [Fact]
        public void Blue_Hue169() => Assert.Equal(169, Map(0, 0, 255).Hue);

        [Fact]
        public void White_Sat0() => Assert.Equal(0, Map(255, 255, 255).Sat);

        [Fact]
        public void HueDiff_Wraps() => Assert.Equal(4, BulbMapper.HueDiff(2, 252));

        [Fact]
        public void Gamma_BelowOne_BrightensMidtones()
        {
            var lifted = BulbMapper.ToMatter(new Rgb(128, 0, 0), Tuning.Default with { Gamma = 0.7 });
            Assert.True(lifted.Level > Map(128, 0, 0).Level);
        }

        [Fact]
        public void ColourChanged_NullHue_IsFalse()
        {
            var black = Map(0, 0, 0);
            Assert.False(BulbMapper.ColourChanged(null, black));
            Assert.True(BulbMapper.ColourChanged(null, Map(255, 0, 0)));
            Assert.False(BulbMapper.ColourChanged(Map(255, 0, 0), Map(255, 0, 0)));
        }

        [Fact]
        public void LevelChanged_Threshold()
        {
            Assert.True(BulbMapper.LevelChanged(null, 5));
            Assert.False(BulbMapper.LevelChanged(10, 13));
            Assert.True(BulbMapper.LevelChanged(10, 14));
        }

        [Theory]
        [InlineData("bulbMinLevel", "0")]
        [InlineData("bulbMinLevel", "300")]
        [InlineData("bulbGamma", "0")]
        [InlineData("bulbGamma", "abc")]
        [InlineData("bulbGamma", "NaN")]
        [InlineData("bulbBlack", "255")]
        [InlineData("nonsense", "1")]
        public void Tuning_RejectsBadValues(string key, string value)
        {
            var cur = Tuning.Default;
            Assert.False(Tuning.TryParse(key, value, cur, out var updated));
            Assert.Equal(cur, updated);
        }

        [Fact]
        public void Tuning_AcceptsGoodValues_InvariantCulture()
        {
            Assert.True(Tuning.TryParse("bulbGamma", "0.7", Tuning.Default, out var t));
            Assert.Equal(0.7, t.Gamma);
            Assert.True(Tuning.TryParse("bulbSmoothMs", "500", t, out t));
            Assert.Equal(500, t.SmoothMs);
            Assert.True(Tuning.TryParse("bulbMinLevel", "1", t, out t));
            Assert.True(Tuning.TryParse("bulbBlack", "0", t, out t));
            Assert.Equal((1, 0), (t.MinLevel, t.Black));
        }
    }
}
