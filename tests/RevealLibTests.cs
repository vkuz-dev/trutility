using System;
using System.IO;

using Xunit;

namespace trutility.Tests
{
    public sealed class RevealLibTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "trutility-reveal-" + Guid.NewGuid().ToString("N"));

        public RevealLibTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            Directory.Delete(_dir, recursive: true);
        }

        private string Touch(string name)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, "");
            return path;
        }

        private static Func<string, string> Target(string target) => _ => target;

        [Fact]
        public void ShortcutToFileSelectsTheTarget()
        {
            string target = Touch("app.exe");
            string link = Touch("app.lnk");

            RevealKind kind = RevealLib.Plan(link, Target(target), out string reveal);

            Assert.Equal(RevealKind.Select, kind);
            Assert.Equal(target, reveal);
        }

        [Fact]
        public void ShortcutToFolderOpensTheTarget()
        {
            string link = Touch("tools.lnk");

            RevealKind kind = RevealLib.Plan(link, Target(_dir), out string reveal);

            Assert.Equal(RevealKind.OpenFolder, kind);
            Assert.Equal(_dir, reveal);
        }

        [Fact]
        public void ShortcutToMissingTargetReportsIt()
        {
            string link = Touch("gone.lnk");
            string missing = Path.Combine(_dir, "removed.exe");

            RevealKind kind = RevealLib.Plan(link, Target(missing), out string reveal);

            Assert.Equal(RevealKind.Missing, kind);
            Assert.Equal(missing, reveal);
        }

        [Fact]
        public void ShortcutWithoutFilesystemTargetRevealsItself()
        {
            string link = Touch("store.lnk");

            RevealKind kind = RevealLib.Plan(link, Target(null), out string reveal);

            Assert.Equal(RevealKind.Select, kind);
            Assert.Equal(link, reveal);
        }

        [Fact]
        public void UrlRevealsItselfWithoutResolving()
        {
            string url = Touch("site.url");

            RevealKind kind = RevealLib.Plan(url, _ => throw new InvalidOperationException("resolved a .url"), out string reveal);

            Assert.Equal(RevealKind.Select, kind);
            Assert.Equal(url, reveal);
        }
    }
}
