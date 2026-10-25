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
        public string FolderPath { get; set; }

        /// The shortcut this folder was expanded from; null for a real folder.
        public string LinkPath { get; set; }
        public bool SeparatorBefore { get; set; }
        public bool SeparatorAfter { get; set; }
        public bool IsSeparator { get; }

        /// Thick tinted rule. It marks the single boundary between
        /// administrator-managed content and the folder belonging to the user,
        /// so exactly one of these appears in a menu.
        public bool IsSectionBoundary { get; }

        /// Marked $X: this folder is a container on disk only. It is never shown
        /// itself - its children take its place in the parent menu.
        public bool IsFlattened { get; set; }

        /// Set on top-level entries that came from the user's own folder; their
        /// descendants belong to the same side. The thick line only exists when
        /// both sides have content, so it cannot be relied on to tell them apart.
        public bool BelongsToUser { get; set; }

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
        /// looked a child up by key - the index only existed to power that
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

    /// The order files take within a folder. The numbers are what is stored
    /// in the registry, so they are never reused or reordered.
    internal enum MenuSort
    {
        Name = 0,
        Date = 1,
        Extension = 2,
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
        /// The file types the extension filter never removes: what this tool
        /// exists to launch.
        /// </summary>
        private static readonly HashSet<string> UnfilteredExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".lnk", ".exe", ".msc", ".cpl", ".rdp"
            };

        /// <summary>
        /// State threaded through one build. ForeignRoots is set per root as each
        /// one is scanned, so a $F shortcut cannot reach across a root boundary.
        /// </summary>
        private sealed class ScanContext
        {
            public List<string> AccessErrors;
            public List<string> FollowedFolders;
            public HashSet<string> VisitedFolders;

            /// UNC share roots that did not answer during this scan.
            public HashSet<string> DeadShares = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyList<Root> AllRoots;
            public IReadOnlyList<string> ForeignRoots;
            public bool ShowExtensions;

            /// The most items a folder may hold and still be expanded through an
            /// unmarked shortcut. Zero means unmarked shortcuts never expand.
            public int AutoExpandLimit;
            public MenuSort Sort;
            public bool ReverseSort;
            public bool FoldersFirst;

            /// The extensions the filter lists, or null for no filter.
            public HashSet<string> FilterExtensions;

            /// True: only the listed types are shown. False: they are hidden.
            public bool FilterShowsOnlyListed;
        }

        /// <summary>
        /// Reads an extension list as typed: separated by semicolons, commas or
        /// spaces, with or without the dot, and "*.png" taken as ".png".
        /// </summary>
        public static HashSet<string> ParseExtensions(string list)
        {
            return new HashSet<string>(
                (list ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Trim().TrimStart('*', '.'))
                    .Where(e => e.Length > 0)
                    .Select(e => "." + e),
                StringComparer.OrdinalIgnoreCase);
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
                                         out List<string> accessErrors, out List<string> followedFolders,
                                         bool showExtensions = true, int autoExpandLimit = 0,
                                         MenuSort sort = MenuSort.Name, bool reverseSort = false,
                                         bool foldersFirst = false,
                                         HashSet<string> filterExtensions = null,
                                         bool filterShowsOnlyListed = false)
        {
            List<Root> allRoots = new List<Root>(globalRoots ?? new Root[0]);
            if (userRoot != null)
            {
                allRoots.Add(userRoot);
            }

            ScanContext context = new ScanContext
            {
                AccessErrors = new List<string>(),
                FollowedFolders = new List<string>(),
                VisitedFolders = SeedVisitedFolders(allRoots),
                AllRoots = allRoots,
                ShowExtensions = showExtensions,
                AutoExpandLimit = autoExpandLimit,
                Sort = sort,
                ReverseSort = reverseSort,
                FoldersFirst = foldersFirst,
                FilterExtensions = filterExtensions != null && filterExtensions.Count > 0 ? filterExtensions : null,
                FilterShowsOnlyListed = filterShowsOnlyListed,
            };
            accessErrors = context.AccessErrors;
            followedFolders = context.FollowedFolders;

            MenuNode root = new MenuNode("root", false, false);
            bool anyGlobal = false;

            foreach (Root globalRoot in allRoots.Where(r => r.Scope == RootScope.Global))
            {
                MenuNode section = ScanRoot(globalRoot, context);
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

            AppendUserSection(root, userRoot, context, anyGlobal);
            return root;
        }

        private static void AppendUserSection(MenuNode root, Root userRoot, ScanContext context,
                                              bool anyGlobal)
        {
            if (userRoot == null)
            {
                return;
            }

            MenuNode section = ScanRoot(userRoot, context);
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
        private static MenuNode ScanRoot(Root root, ScanContext context)
        {
            if (string.IsNullOrWhiteSpace(root.Path) || !Directory.Exists(root.Path))
            {
                return null;
            }

            // Roots are scanned one after another, so the context can carry the
            // boundaries of whichever root is being scanned right now.
            context.ForeignRoots = ForeignRootPaths(root, context.AllRoots);

            MenuNode section = new MenuNode(SectionName(root), false, false)
            {
                FolderPath = root.Path,
            };
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
            bool yours = root.Scope == RootScope.User;

            if (!string.IsNullOrWhiteSpace(root.Label))
            {
                section.BelongsToUser = yours;
                target.AddChild(section);
                return;
            }

            foreach (MenuNode child in section.Children)
            {
                child.BelongsToUser = yours;
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
            if (context.ReverseSort)
            {
                folderNodes.Reverse();
            }

            List<MenuNode> folderEntries = new List<MenuNode>();
            foreach (MenuNode d in folderNodes)
            {
                AppendFolder(folderEntries, d);
            }

            // ── Populate node: files → [divider] → folders, or the other way ──
            List<MenuNode> first = context.FoldersFirst ? folderEntries : fileNodes;
            List<MenuNode> second = context.FoldersFirst ? fileNodes : folderEntries;

            // The rule is "a divider between files and folders", so it belongs on
            // whatever starts the second group - which for a flattened folder is
            // one of its promoted children, not the folder itself.
            if (first.Count > 0 && second.Count > 0)
            {
                second[0].SeparatorBefore = true;
            }

            foreach (MenuNode child in first.Concat(second))
            {
                node.AddChild(child);
            }
        }

        /// <summary>
        /// Adds a folder, or - when it is marked $X - the children it stands in
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
                IEnumerable<string> files = SortFiles(Directory.GetFiles(directory), context.Sort);
                foreach (string file in context.ReverseSort ? files.Reverse() : files)
                {
                    string fileName = Path.GetFileName(file);
                    if (IgnoredFileNames.Contains(fileName) || string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    ParsedName parsed = ParseName(fileName, isFolder: false, context.ShowExtensions);

                    // $X on a shortcut implies $F: promoting the children of a
                    // target means following it first, so it never needs both.
                    bool isLink = LinkExtensions.Contains(Path.GetExtension(fileName));
                    if ((parsed.Follow || parsed.Flatten) && isLink)
                    {
                        // A $F link is a folder expansion, not an entry. When the
                        // target cannot be expanded - offline share, denied ACL,
                        // already on the menu, or inside another root - nothing is
                        // shown, rather than a dead item that errors when clicked.
                        MenuNode followed = TryFollow(file, parsed, context, limit: 0);
                        if (followed != null)
                        {
                            folderNodes.Add(followed);
                        }
                        continue;
                    }

                    if (isLink && context.AutoExpandLimit > 0)
                    {
                        MenuNode expanded = TryFollow(file, parsed, context, context.AutoExpandLimit);
                        if (expanded != null)
                        {
                            folderNodes.Add(expanded);
                            continue;
                        }
                    }

                    // Checked only now, so a shortcut that expanded into a
                    // folder above is never filtered as a file.
                    if (IsFilteredOut(fileName, context))
                    {
                        continue;
                    }

                    fileNodes.Add(new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter)
                    {
                        FilePath = file
                    });
                }
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                context.AccessErrors.Add(directory);
            }
        }

        private static bool IsFilteredOut(string fileName, ScanContext context)
        {
            if (context.FilterExtensions == null)
            {
                return false;
            }

            string ext = Path.GetExtension(fileName);
            if (UnfilteredExtensions.Contains(ext))
            {
                return false;
            }
            return context.FilterExtensions.Contains(ext) != context.FilterShowsOnlyListed;
        }

        /// <summary>
        /// Puts the files of one folder in the chosen order. Submenus are not
        /// affected: they stay in name order whatever the setting.
        ///
        /// Date means last modified, newest first, and for a shortcut it is the
        /// shortcut's own date, not its target's - so what was added to the
        /// folder most recently is at the top. Extension is likewise the
        /// file's own, so shortcuts group together rather than by what they
        /// point at. Name settles every tie, which keeps the order fixed from
        /// one rebuild to the next.
        /// </summary>
        private static IEnumerable<string> SortFiles(string[] files, MenuSort sort)
        {
            IOrderedEnumerable<string> ordered;
            switch (sort)
            {
                case MenuSort.Date:
                    ordered = files.OrderByDescending(f => File.GetLastWriteTimeUtc(f));
                    break;
                case MenuSort.Extension:
                    ordered = files.OrderBy(f => Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);
                    break;
                default:
                    return files.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            }
            return ordered.ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        }

        private static void ScanSubdirectories(string directory, ScanContext context,
                                               List<MenuNode> folderNodes)
        {
            try
            {
                foreach (string subDir in Directory.GetDirectories(directory)
                             .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    ParsedName parsed = ParseName(Path.GetFileName(subDir), isFolder: true, context.ShowExtensions);
                    MenuNode folderNode =
                        new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter)
                        {
                            FolderPath = subDir,
                            IsFlattened = parsed.Flatten,
                        };

                    ScanIntoTree(folderNode, subDir, context);

                    if (folderNode.Children.Count > 0)   // skip empty folders
                    {
                        folderNodes.Add(folderNode);
                    }
                }
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                context.AccessErrors.Add(directory);
            }
        }

        /// <summary>
        /// Denied, or gone mid-scan: a share that dropped, a folder renamed or
        /// deleted while it was being read. Either costs that one folder, not
        /// the whole rebuild.
        /// </summary>
        private static bool IsUnreadable(Exception ex) =>
            ex is UnauthorizedAccessException || ex is IOException;

        /// <summary>
        /// Expands a shortcut to a folder into a submenu, or returns null when it
        /// must not be followed. A limit above zero also refuses a folder holding
        /// more items than that; zero, for a shortcut marked $F, follows any size.
        /// </summary>
        private static MenuNode TryFollow(string linkPath, ParsedName parsed, ScanContext context, int limit)
        {
            string target;
            if (!ShortcutLib.TryResolveTarget(linkPath, out target) || !TargetExists(target, context))
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

            // Checked before the folder is marked as visited, so one that is
            // turned down here can still be reached by a $F shortcut elsewhere.
            if (limit > 0 && !IsWithinLimit(target, limit))
            {
                return null;
            }

            if (!context.VisitedFolders.Add(normalizedTarget))
            {
                return null;
            }

            MenuNode node = new MenuNode(parsed.DisplayName, parsed.SepBefore, parsed.SepAfter)
            {
                FolderPath = target,
                LinkPath = linkPath,
                IsFlattened = parsed.Flatten,
            };
            ScanIntoTree(node, target, context);
            context.FollowedFolders.Add(target);

            return node.Children.Count > 0 ? node : null;
        }

        /// <summary>
        /// Directory.Exists on a share whose server is gone waits out an SMB
        /// timeout, and every shortcut into it would pay that again, one after
        /// another, on every rebuild. A share found unreachable once is skipped
        /// for the rest of the scan. Only the share root itself is remembered,
        /// so a missing folder on a live share costs nothing elsewhere.
        /// </summary>
        private static bool TargetExists(string target, ScanContext context)
        {
            string share = UncShare(target);
            if (share != null && context.DeadShares.Contains(share))
            {
                return false;
            }

            if (Directory.Exists(target))
            {
                return true;
            }

            if (share != null && !Directory.Exists(share))
            {
                context.DeadShares.Add(share);
            }
            return false;
        }

        /// "\\server\share" out of a UNC path, or null for anything else.
        private static string UncShare(string path)
        {
            if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            string[] parts = path.Substring(2).Split(new[] { '\\' }, 3);
            return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0
                ? @"\\" + parts[0] + @"\" + parts[1]
                : null;
        }

        /// <summary>
        /// Counts everything under a folder, giving up one past the limit so a
        /// whole drive costs no more to turn down than a small folder does to
        /// accept. A folder that cannot be read all the way through is turned
        /// down too.
        /// </summary>
        private static bool IsWithinLimit(string folder, int limit)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                    .Take(limit + 1).Count() <= limit;
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                return false;
            }
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
        private static ParsedName ParseName(string raw, bool isFolder, bool showExtensions)
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

            // A shortcut never shows its extension; everything else follows the
            // setting.
            string displayExt = !showExtensions || LinkExtensions.Contains(ext) ? "" : ext;

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
