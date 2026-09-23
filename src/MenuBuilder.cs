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

        /// Thick tinted rule. It marks the single boundary between
        /// administrator-managed content and the folder belonging to the user,
        /// so exactly one of these appears in a menu.
        public bool IsSectionBoundary { get; }

        /// Marked $X: this folder is a container on disk only. It is never shown
        /// itself — its children take its place in the parent menu.
        public bool IsFlattened { get; set; }

        private readonly List<MenuNode> _children = new List<MenuNode>();

        public IReadOnlyList<MenuNode> Children => _children;
        public bool IsFolder => FilePath == null && !IsSeparator;

        public MenuNode(string name, bool separatorBefore, bool separatorAfter,
                        bool isSeparator = false, bool isSectionBoundary = false)
        {
            Name = name;
            SeparatorBefore = separatorBefore;
            SeparatorAfter = separatorAfter;
            IsSeparator = isSeparator;
            IsSectionBoundary = isSectionBoundary;
        }

        /// <summary>
        /// Appends. There is deliberately no keyed replace.
        ///
        /// Children used to be held in a dictionary as well as a list, so adding
        /// one whose key already existed replaced it in place. Nothing ever
        /// looked a child up by key — the index only existed to power that
        /// replacement, and the replacement only ever lost entries: two folders
        /// called "Tools" from different places are two entries, and making that
        /// impossible to get wrong is the whole point of keeping them separate.
        /// </summary>
        public void AddChild(MenuNode node)
        {
            _children.Add(node);
        }

        /// Separates one global root from the next.
        public static MenuNode NewThinDivider() =>
            new MenuNode("", false, false, isSeparator: true);

        /// Separates administrator-managed content from user-owned content.
        public static MenuNode NewSectionBoundary() =>
            new MenuNode("", false, false, isSeparator: true, isSectionBoundary: true);
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

        /// <summary>
        /// State threaded through one scan. ForeignRoots carries the other roots
        /// so a $F shortcut cannot reach across a root boundary.
        /// </summary>
        private sealed class ScanContext
        {
            public List<string> AccessErrors;
            public List<string> FollowedFolders;
            public HashSet<string> VisitedFolders;
            public IReadOnlyList<string> ForeignRoots;
        }

        /// <summary>
        /// Builds the menu from the administrator-managed global roots followed
        /// by the folder belonging to the user.
        ///
        /// Roots never merge. A "Tools" folder present in two roots yields two
        /// independent entries: children are only ever appended, so one can
        /// never replace another. There is no collision-resolution anywhere and
        /// no notion of one root winning over another.
        ///
        /// A root carrying a Label nests under a submenu of that name; without
        /// one its contents splice in at top level. Consecutive global roots are
        /// parted by a thin separator, and the single thick section boundary
        /// marks where administrator-managed content ends.
        /// </summary>
        public static MenuNode BuildTree(IReadOnlyList<Root> globalRoots, Root userRoot,
                                         out List<string> accessErrors, out List<string> followedFolders)
        {
            accessErrors = new List<string>();
            followedFolders = new List<string>();

            List<Root> allRoots = new List<Root>(globalRoots ?? new Root[0]);
            if (userRoot != null)
            {
                allRoots.Add(userRoot);
            }

            HashSet<string> visited = SeedVisitedFolders(allRoots);
            MenuNode root = new MenuNode("root", false, false);
            bool anyGlobal = false;

            foreach (Root globalRoot in allRoots.Where(r => r.Scope == RootScope.Global))
            {
                MenuNode section = ScanRoot(globalRoot, allRoots, visited, accessErrors, followedFolders);
                if (section == null)
                {
                    continue;
                }

                if (anyGlobal)
                {
                    root.AddChild(MenuNode.NewThinDivider());
                }
                AppendSection(root, globalRoot, section);
                anyGlobal = true;
            }

            AppendUserSection(root, userRoot, allRoots, visited, accessErrors, followedFolders,
                              anyGlobal);
            return root;
        }

        private static void AppendUserSection(MenuNode root, Root userRoot, List<Root> allRoots,
                                              HashSet<string> visited, List<string> accessErrors,
                                              List<string> followedFolders, bool anyGlobal)
        {
            if (userRoot == null)
            {
                return;
            }

            MenuNode section = ScanRoot(userRoot, allRoots, visited, accessErrors, followedFolders);
            if (section == null)
            {
                return;
            }

            if (anyGlobal)
            {
                root.AddChild(MenuNode.NewSectionBoundary());
            }
            AppendSection(root, userRoot, section);
        }

        /// <summary>
        /// Seeding every root means a $F shortcut cannot re-expand a root that is
        /// already on the menu, and following cannot loop back on itself.
        /// </summary>
        private static HashSet<string> SeedVisitedFolders(IReadOnlyList<Root> allRoots)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Root root in allRoots)
            {
                string normalized = RootsLib.NormalizeDir(root.Path);
                if (normalized != null)
                {
                    visited.Add(normalized);
                }
            }
            return visited;
        }

        /// Returns the populated section, or null when the root is unreachable
        /// or contributes nothing.
        private static MenuNode ScanRoot(Root root, IReadOnlyList<Root> allRoots,
                                         HashSet<string> visited, List<string> accessErrors,
                                         List<string> followedFolders)
        {
            if (string.IsNullOrWhiteSpace(root.Path) || !Directory.Exists(root.Path))
            {
                return null;
            }

            ScanContext context = new ScanContext
            {
                AccessErrors = accessErrors,
                FollowedFolders = followedFolders,
                VisitedFolders = visited,
                ForeignRoots = ForeignRootPaths(root, allRoots),
            };

            MenuNode section = new MenuNode(SectionName(root), false, false);
            ScanIntoTree(section, root.Path, context);
            return section.Children.Count > 0 ? section : null;
        }

        private static IReadOnlyList<string> ForeignRootPaths(Root current, IReadOnlyList<Root> allRoots)
        {
            return allRoots
                .Where(r => !ReferenceEquals(r, current))
                .Select(r => RootsLib.NormalizeDir(r.Path))
                .Where(p => p != null)
                .ToList();
        }

        private static string SectionName(Root root) =>
            string.IsNullOrWhiteSpace(root.Label) ? root.Name : root.Label;

        /// <summary>
        /// Adds a scanned root to the menu: as one submenu when it carries a
        /// Label, otherwise by splicing its contents in at the top level.
        /// </summary>
        private static void AppendSection(MenuNode target, Root root, MenuNode section)
        {
            if (!string.IsNullOrWhiteSpace(root.Label))
            {
                target.AddChild(section);
                return;
            }

            foreach (MenuNode child in section.Children)
            {
                target.AddChild(child);
            }
        }

        private static void ScanIntoTree(MenuNode node, string directory, ScanContext context)
        {
            List<MenuNode> fileNodes = new List<MenuNode>();
            List<MenuNode> folderNodes = new List<MenuNode>();

            ScanFiles(directory, context, fileNodes, folderNodes);
            ScanSubdirectories(directory, context, folderNodes);

            // Followed shortcuts are discovered during the files pass, so re-sort
            // to keep folder ordering alphabetical regardless of which pass found them.
            folderNodes = folderNodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // ── Populate node: files → [divider] → folders ────────────────────
            List<MenuNode> rendered = new List<MenuNode>();
            rendered.AddRange(fileNodes);

            int firstFolder = rendered.Count;
            foreach (MenuNode d in folderNodes)
            {
                AppendFolder(rendered, d);
            }

            // The rule is "a divider between files and folders", so it belongs on
            // whatever ends up first among them — which for a flattened folder is
            // one of its promoted children, not the folder itself.
            if (fileNodes.Count > 0 && rendered.Count > firstFolder)
            {
                rendered[firstFolder].SeparatorBefore = true;
            }

            foreach (MenuNode child in rendered)
            {
                node.AddChild(child);
            }
        }

        /// <summary>
        /// Adds a folder, or — when it is marked $X — the children it stands in
        /// for, at the position the folder itself would have taken.
        ///
        /// Promoted children are keyed under the folder they came from, so a
        /// name that already exists alongside them stays a separate entry. The
        /// same rule as everywhere else: nothing merges.
        /// </summary>
        private static void AppendFolder(List<MenuNode> rendered, MenuNode folder)
        {
            if (!folder.IsFlattened)
            {
                rendered.Add(folder);
                return;
            }

            rendered.AddRange(folder.Children);
        }

        private static void ScanFiles(string directory, ScanContext context,
                                      List<MenuNode> fileNodes, List<MenuNode> folderNodes)
        {
            try
            {
                foreach (string file in Directory.GetFiles(directory)
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(file);
                    if (IgnoredFileNames.Contains(fileName) || string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    ParsedName parsed = ParseName(fileName, isFolder: false);

                    // $X on a shortcut implies $F: promoting the children of a
                    // target means following it first, so it never needs both.
                    if ((parsed.Follow || parsed.Flatten)
                        && LinkExtensions.Contains(Path.GetExtension(fileName)))
                    {
                        // A $F link is a folder expansion, not an entry. When the
                        // target cannot be expanded — offline share, denied ACL,
                        // already on the menu, or inside another root — nothing is
                        // shown, rather than a dead item that errors when clicked.
                        MenuNode followed = TryFollow(file, parsed, context);
                        if (followed != null)
                        {
                            folderNodes.Add(followed);
                        }
                        continue;
                    }

                    fileNodes.Add(new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter)
                    {
                        FilePath = file
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                context.AccessErrors.Add(directory);
            }
        }

        private static void ScanSubdirectories(string directory, ScanContext context,
                                               List<MenuNode> folderNodes)
        {
            try
            {
                foreach (string subDir in Directory.GetDirectories(directory)
                             .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    ParsedName parsed = ParseName(Path.GetFileName(subDir), isFolder: true);
                    MenuNode folderNode =
                        new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter)
                        {
                            IsFlattened = parsed.Flatten,
                        };

                    ScanIntoTree(folderNode, subDir, context);

                    if (folderNode.Children.Count > 0)   // skip empty folders
                    {
                        folderNodes.Add(folderNode);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                context.AccessErrors.Add(directory);
            }
        }

        /// <summary>
        /// Expands a $F shortcut into a submenu, or returns null when it must not
        /// be followed.
        /// </summary>
        private static MenuNode TryFollow(string linkPath, ParsedName parsed, ScanContext context)
        {
            string target;
            if (!ShortcutLib.TryResolveTarget(linkPath, out target) || !Directory.Exists(target))
            {
                return null;
            }

            string normalizedTarget = RootsLib.NormalizeDir(target);
            if (normalizedTarget == null)
            {
                return null;
            }

            // Reaching into another root would carry its content across the
            // boundary between administrator-managed and user space, into a
            // namespace where it could no longer be told apart from this root.
            if (IsInsideForeignRoot(normalizedTarget, context))
            {
                return null;
            }

            if (!context.VisitedFolders.Add(normalizedTarget))
            {
                return null;
            }

            MenuNode node = new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter)
            {
                IsFlattened = parsed.Flatten,
            };
            ScanIntoTree(node, target, context);
            context.FollowedFolders.Add(target);

            return node.Children.Count > 0 ? node : null;
        }

        private static bool IsInsideForeignRoot(string normalizedTarget, ScanContext context)
        {
            foreach (string foreign in context.ForeignRoots)
            {
                if (RootsLib.IsUnder(normalizedTarget, foreign))
                {
                    return true;
                }
            }
            return false;
        }

        private struct ParsedName
        {
            public string DisplayName;
            public bool SepBefore;
            public bool SepAfter;
            public bool Follow;
            public bool Flatten;
        }

        /// <summary>
        /// Parses separator suffixes first (on raw filename), then strips
        /// link-only extensions from the display name. The "$F" follow-folder
        /// marker is checked both before and after the separator suffix so
        /// either ordering works (e.g. "Name$F$^.lnk" or "Name$^$F.lnk").
        /// </summary>
        private static ParsedName ParseName(string raw, bool isFolder)
        {
            // A folder name is never split on a dot: "Finance.2026$X" is a folder
            // called "Finance.2026", not one called "Finance" with an extension.
            // Splitting it hid every marker behind the dot.
            string ext = isFolder ? "" : Path.GetExtension(raw);
            string stem = isFolder ? raw : Path.GetFileNameWithoutExtension(raw);

            bool follow = TryStripSuffix(ref stem, "$F");
            bool flatten = TryStripSuffix(ref stem, "$X");

            bool both = stem.EndsWith("$^_") || stem.EndsWith("$_^");
            bool before = both || stem.EndsWith("$^");
            bool after = both || stem.EndsWith("$_");

            if (both)
            {
                stem = stem.Substring(0, stem.Length - 3);
            }
            else if (before || after)
            {
                stem = stem.Substring(0, stem.Length - 2);
            }

            if (!follow)
            {
                follow = TryStripSuffix(ref stem, "$F");
            }
            if (!flatten)
            {
                flatten = TryStripSuffix(ref stem, "$X");
            }

            string displayExt = LinkExtensions.Contains(ext) ? "" : ext;

            return new ParsedName
            {
                DisplayName = stem.Trim() + displayExt,
                SepBefore = before,
                SepAfter = after,
                Follow = follow,
                Flatten = flatten
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
