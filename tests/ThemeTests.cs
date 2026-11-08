using Xunit;

namespace trutility.Tests
{
    public class ThemeTests
    {
        [Theory]
        [InlineData(1, true, "Classic")]
        [InlineData(2, false, "Win11Light")]
        [InlineData(3, true, "Win11Dark")]
        [InlineData(4, true, "Win11Light")]
        [InlineData(4, false, "Win11Dark")]
        [InlineData(99, true, "Classic")]
        public void SettingPicksTheme(int setting, bool systemIsLight, string expected)
        {
            Theme theme = Theme.For(setting, systemIsLight);

            Assert.Same(typeof(Theme).GetField(expected).GetValue(null), theme);
        }
    }
}
