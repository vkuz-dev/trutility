using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace trutility
{
    internal class MenuNode
    {
        public string Name { get; }
        public string FilePath { get; set; }
        public bool SeparatorBefore { get; set; }
        public bool SeparatorAfter { get; set; }
        public bool IsSeparator { get; }

        private readonly Dictionary<string, MenuNode> _childIndex =
            new Dictionary<string, MenuNode>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MenuNode> _childOrder = new List<MenuNode>();

        public IReadOnlyList<MenuNode> Children => _childOrder;
        public bool IsFolder => FilePath == null && !IsSeparator;

        public MenuNode(string name, bool separatorBefore, bool separatorAfter, bool isSeparator = false)
        {
            Name = name;
            SeparatorBefore = separatorBefore;
            SeparatorAfter = separatorAfter;
            IsSeparator = isSeparator;
        }

        public void AddChild(string key, MenuNode node)
        {
            if (_childIndex.ContainsKey(key))
            {
                var idx = _childOrder.IndexOf(_childIndex[key]);
                _childOrder[idx] = node;
            }
            else
            {
                _childOrder.Add(node);
            }
            _childIndex[key] = node;
        }

        public void ClearChildren()
        {
            _childIndex.Clear();
            _childOrder.Clear();
        }

        public bool TryGetChild(string key, out MenuNode node) =>
            _childIndex.TryGetValue(key, out node);

        public static readonly MenuNode Divider = new MenuNode("", false, false, isSeparator: true);
    }

    internal static class MenuBuilder
    {
        private static readonly HashSet<string> IgnoredFileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "desktop.ini", "thumbs.db"
            };

        /// <summary>
        /// Extensions whose display name should have the extension stripped.
        /// Separator-suffix parsing always runs first (on the raw filename),
        /// so e.g. "tool$^_.lnk" is handled correctly.
        /// </summary>
        private static readonly HashSet<string> LinkExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".lnk", ".url"
            };

        public static MenuNode BuildTree(string globalRoot, string userRoot, out List<string> accessErrors, out List<string> followedFolders)
        {
            accessErrors = new List<string>();
            followedFolders = new List<string>();

            var visitedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(globalRoot)) visitedFolders.Add(NormalizePath(globalRoot));
            if (Directory.Exists(userRoot)) visitedFolders.Add(NormalizePath(userRoot));

            var globalNode = new MenuNode("global", false, false);
            var userNode = new MenuNode("user", false, false);

            if (Directory.Exists(globalRoot))
                ScanIntoTree(globalNode, globalRoot, accessErrors, followedFolders, visitedFolders);

            if (Directory.Exists(userRoot))
                ScanIntoTree(userNode, userRoot, accessErrors, followedFolders, visitedFolders);

            var root = new MenuNode("root", false, false);

            foreach (var child in globalNode.Children)
                root.AddChild("global:" + child.Name, child);

            if (globalNode.Children.Count > 0 && userNode.Children.Count > 0)
                root.AddChild("~~~divider~~~", MenuNode.Divider);

            foreach (var child in userNode.Children)
                root.AddChild("user:" + child.Name, child);

            return root;
        }

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static void ScanIntoTree(MenuNode node, string directory, List<string> accessErrors,
            List<string> followedFolders, HashSet<string> visitedFolders)
        {
            var fileNodes = new List<MenuNode>();
            var folderNodes = new List<MenuNode>();

            // ── Files ─────────────────────────────────────────────────────────
            try
            {
                foreach (var file in Directory.GetFiles(directory)
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(file);
                    if (IgnoredFileNames.Contains(fileName)) continue;
                    if (string.IsNullOrWhiteSpace(fileName)) continue;

                    var parsed = ParseName(fileName);

                    // ── Optional: follow a .lnk shortcut into its target folder ──
                    if (parsed.Follow && LinkExtensions.Contains(Path.GetExtension(fileName)) &&
                        ShortcutLib.TryResolveTarget(file, out var target) &&
                        Directory.Exists(target))
                    {
                        var normalizedTarget = NormalizePath(target);
                        if (visitedFolders.Add(normalizedTarget))
                        {
                            var followedNode = new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter);
                            ScanIntoTree(followedNode, target, accessErrors, followedFolders, visitedFolders);
                            followedFolders.Add(target);

                            if (followedNode.Children.Count > 0)   // skip empty folders
                                folderNodes.Add(followedNode);
                            continue;
                        }
                    }

                    var fileNode = new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter)
                    { FilePath = file };
                    fileNodes.Add(fileNode);
                }
            }
            catch (UnauthorizedAccessException) { accessErrors.Add(directory); }

            // ── Subdirectories ────────────────────────────────────────────────
            try
            {
                foreach (var subDir in Directory.GetDirectories(directory)
                             .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    var rawName = Path.GetFileName(subDir);
                    var parsed = ParseName(rawName);
                    var folderNode = new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter);

                    ScanIntoTree(folderNode, subDir, accessErrors, followedFolders, visitedFolders);

                    if (folderNode.Children.Count > 0)   // skip empty folders
                        folderNodes.Add(folderNode);
                }
            }
            catch (UnauthorizedAccessException) { accessErrors.Add(directory); }

            // Followed shortcuts are discovered during the files pass, so re-sort
            // to keep folder ordering alphabetical regardless of which pass found them.
            folderNodes = folderNodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // ── Populate node: files → [divider] → folders ────────────────────
            foreach (var f in fileNodes)
                node.AddChild("file:" + f.Name, f);

            if (fileNodes.Count > 0 && folderNodes.Count > 0)
                folderNodes[0].SeparatorBefore = true;

            foreach (var d in folderNodes)
                node.AddChild("folder:" + d.Name, d);
        }

        private struct ParsedName
        {
            public string DisplayName;
            public bool SepBefore;
            public bool SepAfter;
            public bool Follow;
        }

        /// <summary>
        /// Parses separator suffixes first (on raw filename), then strips
        /// link-only extensions from the display name. The "$F" follow-folder
        /// marker is checked both before and after the separator suffix so
        /// either ordering works (e.g. "Name$F$^.lnk" or "Name$^$F.lnk").
        /// </summary>
        private static ParsedName ParseName(string raw)
        {
            var ext = Path.GetExtension(raw);
            var stem = Path.GetFileNameWithoutExtension(raw);

            var follow = TryStripSuffix(ref stem, "$F");

            var both = stem.EndsWith("$^_") || stem.EndsWith("$_^");
            var before = both || stem.EndsWith("$^");
            var after = both || stem.EndsWith("$_");

            if (both) stem = stem.Substring(0, stem.Length - 3);
            else if (before || after) stem = stem.Substring(0, stem.Length - 2);

            if (!follow) follow = TryStripSuffix(ref stem, "$F");

            var displayExt = LinkExtensions.Contains(ext) ? "" : ext;

            return new ParsedName
            {
                DisplayName = stem.Trim() + displayExt,
                SepBefore = before,
                SepAfter = after,
                Follow = follow
            };
        }

        private static bool TryStripSuffix(ref string stem, string token)
        {
            if (stem.EndsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - token.Length);
                return true;
            }
            return false;
        }
    }
}