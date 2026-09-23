using System;
using System.Collections.Generic;
using System.Linq;

namespace trutility
{
    /// <summary>
    /// One launchable entry in the menu, flattened out of the tree so it can be
    /// found without opening the submenus it lives in.
    /// </summary>
    internal sealed class SearchEntry
    {
        public MenuNode Node { get; set; }
        public string Name { get; set; }

        /// The submenus leading to it, e.g. "Windows Tools ▸ Remote". Empty at
        /// the top level.
        public string Location { get; set; }

        /// Below the thick line: the entry came from the user's own folder.
        public bool IsYours { get; set; }
    }

    /// <summary>
    /// Finds menu entries by name, across every level of the menu at once.
    ///
    /// Every word typed must appear somewhere in the entry's location or name,
    /// in any order, so "fin pay" finds payroll inside Finance. Only files are
    /// indexed: a folder is already one click away, and what people look for
    /// is the thing they want to run.
    /// </summary>
    internal static class MenuSearch
    {
        private const string LocationSeparator = " ▸ ";

        /// Built off the UI thread with the menu, so typing only filters.
        public static IReadOnlyList<SearchEntry> Index(MenuNode root)
        {
            List<SearchEntry> entries = new List<SearchEntry>();
            foreach (MenuNode child in root.Children)
            {
                Collect(child, "", child.BelongsToUser, entries);
            }
            return entries;
        }

        private static void Collect(MenuNode node, string location, bool yours, List<SearchEntry> entries)
        {
            if (node.IsSeparator)
            {
                return;
            }

            if (node.IsFolder)
            {
                string inner = location.Length == 0 ? node.Name : location + LocationSeparator + node.Name;
                foreach (MenuNode child in node.Children)
                {
                    Collect(child, inner, yours, entries);
                }
                return;
            }

            entries.Add(new SearchEntry
            {
                Node = node,
                Name = node.Name,
                Location = location,
                IsYours = yours,
            });
        }

        /// <summary>
        /// Every entry matching the query, best first — but shared entries always
        /// come before the user's own, so results keep the same line the menu
        /// has. Within each side, a name that starts with the query beats one
        /// that merely contains it, which beats a match found only in the
        /// location.
        /// </summary>
        public static List<SearchEntry> Find(IReadOnlyList<SearchEntry> index, string query)
        {
            string trimmed = (query ?? "").Trim();
            if (trimmed.Length == 0)
            {
                return new List<SearchEntry>();
            }

            string[] words = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            return index
                .Where(e => words.All(w => Contains(e.Location + " " + e.Name, w)))
                .OrderBy(e => e.IsYours)
                .ThenBy(e => Rank(e, trimmed, words))
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int Rank(SearchEntry entry, string query, string[] words)
        {
            if (entry.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (Contains(entry.Name, query)) return 1;
            if (words.All(w => Contains(entry.Name, w))) return 2;
            return 3;
        }

        private static bool Contains(string text, string word)
        {
            return text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
