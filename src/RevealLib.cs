using System;
using System.IO;

namespace trutility
{
    internal enum RevealKind
    {
        /// Select the path in its Explorer folder.
        Select,
        /// Open the path as a folder.
        OpenFolder,
        /// The shortcut points at something that is no longer there.
        Missing,
    }

    internal static class RevealLib
    {
        /// <summary>
        /// Decides what middle-click shows for a menu file. A .lnk with a path on
        /// disk shows that target; anything else shows the file itself. Only a
        /// .lnk is resolved, so a .url is never sent to the shortcut COM object.
        /// </summary>
        public static RevealKind Plan(string path, Func<string, string> resolveTarget, out string revealPath)
        {
            revealPath = path;
            if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return RevealKind.Select;
            }

            // A target with no filesystem path (a shell item, a URL, an
            // installer-advertised shortcut) falls back to the .lnk itself.
            string target = resolveTarget(path);
            if (target == null || !Path.IsPathRooted(target))
            {
                return RevealKind.Select;
            }

            revealPath = target;
            if (Directory.Exists(target)) return RevealKind.OpenFolder;
            if (File.Exists(target)) return RevealKind.Select;
            return RevealKind.Missing;
        }
    }
}
