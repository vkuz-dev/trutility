using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;

namespace trutility.Tests
{
    /// Builds menus from real folders, since the name suffixes only mean
    /// something in how the tree comes out.
    public sealed class MenuBuilderTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "trutility-tests-" + Guid.NewGuid().ToString("N"));

        public MenuBuilderTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            Directory.Delete(_dir, recursive: true);
        }

        private void Touch(string relative)
        {
            string path = Path.Combine(_dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
        }

        private MenuNode Build(bool showExtensions = true)
        {
            Root user = new Root { Name = "User", Path = _dir, Scope = RootScope.User };
            return MenuBuilder.BuildTree(new Root[0], user, out _, out _, showExtensions);
        }

        private static List<string> Names(MenuNode node) =>
            node.Children.Where(c => !c.IsSeparator).Select(c => c.Name).ToList();

        [Fact]
        public void SeparatorSuffixesAreParsedAndHidden()
        {
            Touch("a$^.txt");
            Touch("b$_.txt");
            Touch("c$^_.txt");

            List<MenuNode> items = Build().Children.ToList();

            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, items.Select(i => i.Name));
            Assert.True(items[0].SeparatorBefore && !items[0].SeparatorAfter);
            Assert.True(!items[1].SeparatorBefore && items[1].SeparatorAfter);
            Assert.True(items[2].SeparatorBefore && items[2].SeparatorAfter);
        }

        [Fact]
        public void ShortcutsLoseTheirExtensionOtherFilesFollowTheSetting()
        {
            Touch("putty$^_.lnk");
            Touch("eventvwr.msc");

            Assert.Equal(new[] { "eventvwr.msc", "putty" }, Names(Build(showExtensions: true)));
            Assert.Equal(new[] { "eventvwr", "putty" }, Names(Build(showExtensions: false)));
        }

        [Fact]
        public void FlattenedFolderIsReplacedByItsChildren()
        {
            Touch(@"Tools$X\server1.txt");
            Touch(@"Tools$X\Remote\rdp.txt");

            MenuNode root = Build();

            Assert.Equal(new[] { "server1.txt", "Remote" }, Names(root));
            Assert.Equal(new[] { "rdp.txt" }, Names(root.Children.Single(c => c.Name == "Remote")));
        }

        [Fact]
        public void FolderNameIsNeverCutAtADot()
        {
            Touch(@"Finance.2026$X\pay.txt");
            Touch(@"v1.2\notes.txt");

            Assert.Equal(new[] { "pay.txt", "v1.2" }, Names(Build()));
        }

        [Fact]
        public void EmptyFoldersAreLeftOut()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "Empty"));
            Touch("a.txt");

            Assert.Equal(new[] { "a.txt" }, Names(Build()));
        }
    }
}
