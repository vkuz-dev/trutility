using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Win32;

namespace trutility
{
    /// <summary>
    /// Security context of a <see cref="Root"/>.
    ///
    /// Global roots are administrator-managed: trutility only ever reads them,
    /// never writes, deletes or reorders their contents.
    ///
    /// The user root belongs to the user and is the single location trutility
    /// writes to (--add drops shortcuts there, Ctrl+Click deletes from there).
    /// An administrator may pin *where* it lives; what goes in it is never managed.
    /// </summary>
    internal enum RootScope
    {
        Global,
        User
    }

    /// <summary>
    /// One scanned folder plus the policy metadata that positions it in the menu.
    ///
    /// Roots never merge. Each root owns its own menu namespace, so a "Tools"
    /// folder present in two roots produces two independent entries rather than
    /// one shadowing the other. There is no collision-resolution anywhere.
    /// </summary>
    internal sealed class Root
    {
        /// Registry subkey name. This is the identity of the root: a
        /// higher-authority source defining the same name shadows the lower one.
        public string Name { get; set; }

        /// Resolved folder path, with environment variables expanded.
        public string Path { get; set; }

        public RootScope Scope { get; set; }

        /// Ascending sort position; ties break on Name.
        public int Order { get; set; }

        /// When set, the contents of the root nest under a submenu of this name.
        /// When null, the contents splice into the menu at top level.
        public string Label { get; set; }

        /// Set to false by policy to switch a root off without deleting its key.
        public bool Enabled { get; set; } = true;

        /// Human-readable origin, surfaced in the About dialog.
        public string Source { get; set; }
    }

    /// <summary>
    /// Resolves the folders trutility scans.
    ///
    /// Global roots are collected from several registry sources and unioned by
    /// root name. Subkeys are used rather than a single list value precisely so
    /// that independent GPOs merge instead of colliding: two policies each
    /// assigning a department folder both apply, and a user in both departments
    /// receives both roots.
    ///
    /// Sources, lowest authority first. A later source shadows an earlier one of
    /// the same name value by value, inheriting the values it does not set:
    ///
    ///   built-in default                            -> root "Default"
    ///   HKLM\SOFTWARE\trutility\GlobalRoot (legacy) -> root "Default"
    ///   HKLM\SOFTWARE\trutility\Roots\                 installer / machine config
    ///   HKCU\SOFTWARE\Policies\trutility\Roots\        user GPO (department targeting)
    ///   HKLM\SOFTWARE\Policies\trutility\Roots\        machine GPO (final authority)
    ///
    /// HKCU\SOFTWARE\trutility is deliberately absent: a user must not be able to
    /// contribute a global root, or "global" stops meaning "administrator-managed".
    /// Policy keys live under Software\Policies because Group Policy owns and
    /// cleans up that subtree, so a department change leaves no stale roots behind.
    /// </summary>
    internal static class RootsLib
    {
        internal const string RegKeyPath = @"SOFTWARE\trutility";
        internal const string RegPolicyPath = @"SOFTWARE\Policies\trutility";
        private const string RootsSubKey = "Roots";

        private const string RegValGlobal = "GlobalRoot";
        private const string RegValUser = "UserRoot";

        /// Well-known name of the root carrying the built-in / legacy global
        /// folder. Administrators switch it off with Enabled=0 under this name.
        internal const string DefaultRootName = "Default";
        internal const string UserRootName = "User";

        private const int DefaultOrder = 100;

        // ── Built-in fallbacks ────────────────────────────────────────────────

        internal static string BuiltInGlobalPath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "trutility");

        internal static string BuiltInUserPath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "trutility");

        // ── Global roots ──────────────────────────────────────────────────────

        /// <summary>
        /// Every enabled global root, ordered for display. Disabled roots and
        /// roots without a usable Path are dropped. Never returns null.
        /// </summary>
        internal static IReadOnlyList<Root> GetGlobalRoots()
        {
            Dictionary<string, Root> acc =
                new Dictionary<string, Root>(StringComparer.OrdinalIgnoreCase)
                {
                    [DefaultRootName] = BuiltInDefaultRoot()
                };

            List<RegistryKey> hklm = OpenLocalMachineViews();
            try
            {
                foreach (RegistryKey view in hklm)
                {
                    ApplyLegacyGlobalValue(view, acc);
                }
                foreach (RegistryKey view in hklm)
                {
                    ReadRootsFrom(view, RegKeyPath, "HKLM", acc);
                }

                ReadRootsFrom(Registry.CurrentUser, RegPolicyPath, "HKCU", acc);

                foreach (RegistryKey view in hklm)
                {
                    ReadRootsFrom(view, RegPolicyPath, "HKLM", acc);
                }
            }
            finally
            {
                CloseLocalMachineViews(hklm);
            }

            return acc.Values
                .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Path))
                .OrderBy(r => r.Order)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Root BuiltInDefaultRoot()
        {
            return new Root
            {
                Name = DefaultRootName,
                Path = BuiltInGlobalPath,
                Scope = RootScope.Global,
                Order = 0,
                Source = "built-in default",
            };
        }

        /// <summary>
        /// Folds the legacy single-folder GlobalRoot value into the "Default"
        /// root, so old installs need no special-casing: they simply resolve to
        /// a one-element root list.
        /// </summary>
        private static void ApplyLegacyGlobalValue(RegistryKey hive, IDictionary<string, Root> acc)
        {
            string path = ReadValuePath(hive, RegKeyPath, RegValGlobal);
            if (path == null)
            {
                return;
            }

            Root root = GetOrAdd(acc, DefaultRootName, 0);
            root.Path = path;
            root.Source = @"HKLM\" + RegKeyPath + @"\" + RegValGlobal + " (legacy)";
        }

        private static void ReadRootsFrom(RegistryKey hive, string keyPath, string hiveLabel,
                                          IDictionary<string, Root> acc)
        {
            RegistryKey rootsKey = OpenSubKey(hive, keyPath + @"\" + RootsSubKey);
            if (rootsKey == null)
            {
                return;
            }

            using (rootsKey)
            {
                foreach (string name in GetSubKeyNames(rootsKey))
                {
                    try
                    {
                        using (RegistryKey key = rootsKey.OpenSubKey(name))
                        {
                            if (key == null)
                            {
                                continue;
                            }

                            Root root = GetOrAdd(acc, name, DefaultOrder);
                            ApplyRootValues(key, root);
                            root.Source =
                                hiveLabel + @"\" + keyPath + @"\" + RootsSubKey + @"\" + name;
                        }
                    }
                    catch (Exception ex)
                    {
                        // One bad root must not cost the whole menu.
                        Diag.Swallowed("reading root " + name + " under " + keyPath, ex);
                    }
                }
            }
        }

        /// <summary>
        /// Only values actually present override the shadowed root, so Enabled=0
        /// on its own switches a root off without the policy having to restate
        /// its path.
        /// </summary>
        private static void ApplyRootValues(RegistryKey key, Root root)
        {
            string path = CleanPath(key.GetValue("Path") as string);
            if (path != null)
            {
                root.Path = path;
            }

            string label = key.GetValue("Label") as string;
            if (!string.IsNullOrWhiteSpace(label))
            {
                root.Label = label.Trim();
            }

            object order = key.GetValue("Order");
            if (order != null)
            {
                root.Order = ToInt(order, root.Order);
            }

            object enabled = key.GetValue("Enabled");
            if (enabled != null)
            {
                root.Enabled = ToInt(enabled, 1) != 0;
            }
        }

        private static Root GetOrAdd(IDictionary<string, Root> acc, string name, int defaultOrder)
        {
            Root root;
            if (acc.TryGetValue(name, out root))
            {
                return root;
            }

            root = new Root { Name = name, Scope = RootScope.Global, Order = defaultOrder };
            acc[name] = root;
            return root;
        }

        // ── User root ─────────────────────────────────────────────────────────

        /// <summary>
        /// The folder belonging to the user. HKCU wins, then a location pinned by
        /// an administrator in HKLM, then the built-in default. Policy may decide
        /// where this folder lives; what goes in it is the user's alone.
        /// </summary>
        internal static Root GetUserRoot()
        {
            return new Root
            {
                Name = UserRootName,
                Path = ResolveUserRootPath(),
                Scope = RootScope.User,
                Order = int.MaxValue,
                Source = IsUserRootManagedByPolicy()
                    ? @"HKLM\" + RegKeyPath + @"\" + RegValUser + " (location pinned by policy)"
                    : @"HKCU\" + RegKeyPath + @"\" + RegValUser,
            };
        }

        internal static string ResolveUserRootPath()
        {
            string path = ReadValuePath(Registry.CurrentUser, RegKeyPath, RegValUser);
            if (path != null)
            {
                return path;
            }

            List<RegistryKey> hklm = OpenLocalMachineViews();
            try
            {
                foreach (RegistryKey view in hklm)
                {
                    path = ReadValuePath(view, RegKeyPath, RegValUser);
                    if (path != null)
                    {
                        return path;
                    }
                }
            }
            finally
            {
                CloseLocalMachineViews(hklm);
            }

            return BuiltInUserPath;
        }

        internal static void SetUserRoot(string path)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(RegValUser, path);
            }
        }

        /// Drops the explicit choice of the user so the location falls back to
        /// policy or the built-in default.
        internal static void ClearUserRoot()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath, writable: true))
            {
                key?.DeleteValue(RegValUser, throwOnMissingValue: false);
            }
        }

        /// True when HKLM pins the location and HKCU has no override yet.
        internal static bool IsUserRootManagedByPolicy()
        {
            if (ReadValuePath(Registry.CurrentUser, RegKeyPath, RegValUser) != null)
            {
                return false;
            }

            List<RegistryKey> hklm = OpenLocalMachineViews();
            try
            {
                foreach (RegistryKey view in hklm)
                {
                    if (ReadValuePath(view, RegKeyPath, RegValUser) != null)
                    {
                        return true;
                    }
                }
            }
            finally
            {
                CloseLocalMachineViews(hklm);
            }
            return false;
        }

        // ── Path helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Full path with trailing separators removed, or null when the path is
        /// blank or malformed.
        /// </summary>
        internal static string NormalizeDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return System.IO.Path.GetFullPath(path)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar,
                             System.IO.Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                // A configured path can be anything; an unusable one is simply
                // not a folder trutility can scan or compare against.
                Diag.Swallowed("normalising path " + path, ex);
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="directory"/> or
        /// sits beneath it. Both sides are normalised and the comparison lands on
        /// a separator boundary, so "D:\tools" does not contain "D:\tools-shared".
        /// This is the check enforcing the read-only guarantee on global roots.
        /// </summary>
        internal static bool IsUnder(string path, string directory)
        {
            string dir = NormalizeDir(directory);
            string candidate = NormalizeDir(path);
            if (dir == null || candidate == null)
            {
                return false;
            }

            if (string.Equals(candidate, dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return candidate.StartsWith(
                dir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ── Registry helpers ──────────────────────────────────────────────────

        /// <summary>
        /// HKLM views to consult, lowest authority first.
        ///
        /// On 64-bit Windows a 32-bit process is redirected to Wow6432Node, while
        /// Group Policy and regedit write the native view. Reading both — native
        /// last, so it wins — means the keys of an administrator are found
        /// whichever bitness trutility happens to be built as.
        /// </summary>
        private static List<RegistryKey> OpenLocalMachineViews()
        {
            List<RegistryKey> views = new List<RegistryKey> { Registry.LocalMachine };
            if (!Environment.Is64BitOperatingSystem || Environment.Is64BitProcess)
            {
                return views;
            }

            try
            {
                views.Add(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));
            }
            catch (Exception ex)
            {
                // Losing the native view only costs policy visibility, so the
                // redirected view alone is still worth returning.
                Diag.Swallowed("opening the 64-bit registry view", ex);
            }
            return views;
        }

        private static void CloseLocalMachineViews(List<RegistryKey> views)
        {
            // views[0] is the shared Registry.LocalMachine singleton, not ours to close.
            for (int i = 1; i < views.Count; i++)
            {
                try
                {
                    views[i].Dispose();
                }
                catch (Exception ex)
                {
                    Diag.Swallowed("closing a registry view", ex);
                }
            }
        }

        private static RegistryKey OpenSubKey(RegistryKey hive, string keyPath)
        {
            try
            {
                return hive.OpenSubKey(keyPath);
            }
            catch (Exception ex)
            {
                // Typically an ACL denying this user the policy branch.
                Diag.Swallowed("opening " + keyPath, ex);
                return null;
            }
        }

        private static string[] GetSubKeyNames(RegistryKey key)
        {
            try
            {
                return key.GetSubKeyNames();
            }
            catch (Exception ex)
            {
                Diag.Swallowed("enumerating roots under " + key.Name, ex);
                return new string[0];
            }
        }

        private static string ReadValuePath(RegistryKey hive, string keyPath, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(keyPath))
                {
                    return key == null ? null : CleanPath(key.GetValue(valueName) as string);
                }
            }
            catch (Exception ex)
            {
                Diag.Swallowed("reading " + keyPath + @"\" + valueName, ex);
                return null;
            }
        }

        /// <summary>
        /// Trims and expands a configured path. REG_EXPAND_SZ is already expanded
        /// by GetValue; expanding again lets administrators use %USERNAME% and
        /// friends in a plain REG_SZ too, so one policy value can serve every user.
        /// </summary>
        private static string CleanPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                raw = Environment.ExpandEnvironmentVariables(raw);
            }
            catch (Exception ex)
            {
                // Keep the unexpanded form: it may still name a usable folder.
                Diag.Swallowed("expanding environment variables in " + raw, ex);
            }

            raw = raw.Trim();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        private static int ToInt(object value, int fallback)
        {
            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                // A policy value of the wrong type falls back rather than
                // discarding the root it belongs to.
                Diag.Swallowed("converting registry value to an integer", ex);
                return fallback;
            }
        }
    }
}
