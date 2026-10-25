using Xunit;

namespace trutility.Tests
{
    /// IsUnder is the only check keeping Ctrl+Click from deleting files in
    /// an administrator's folder.
    public class RootsLibTests
    {
        [Theory]
        [InlineData(@"D:\tools\a.lnk", @"D:\tools", true)]
        [InlineData(@"D:\tools", @"D:\tools", true)]
        [InlineData(@"D:\tools\", @"D:\tools", true)]
        [InlineData(@"d:\TOOLS\sub\a.lnk", @"D:\tools\", true)]
        [InlineData(@"D:\tools-shared\a.lnk", @"D:\tools", false)]
        [InlineData(@"D:\toolsx", @"D:\tools", false)]
        [InlineData(@"D:\tools\..\other\a.lnk", @"D:\tools", false)]
        [InlineData(@"D:\a.lnk", @"D:\tools", false)]
        [InlineData("", @"D:\tools", false)]
        [InlineData(@"D:\tools\a.lnk", "", false)]
        public void IsUnder(string path, string directory, bool expected)
        {
            Assert.Equal(expected, RootsLib.IsUnder(path, directory));
        }
    }
}
