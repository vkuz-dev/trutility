using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.Win32;

using trutility.Settings;

namespace trutility
{
    /// <summary>
    /// Core application class. Owns the NotifyIcon, ContextMenuStrip,
    /// FileSystemWatchers, and the async menu-rebuild pipeline.
    /// </summary>
    internal sealed class TrayApplication : IDisposable
    {
        /// The folder belonging to the user - the only one trutility writes to.
        internal static string UserRoot => RootsLib.ResolveUserRootPath();

        // ── Shell verb helpers ────────────────────────────────────────────────

        /// HKCU → HKLM → actual verb registration (legacy fallback).
        internal static bool IsShellVerbEnabled()
        {
            return ToggleSetting.AddToShell.IsSet() ? ToggleSetting.AddToShell.IsOn() : IsShellVerbRegistered();
        }

        /// Brings the registration in line with the stored preference or HKLM
        /// policy at startup. With neither, the registration is left as it is.
        internal static void ApplyShellPolicy()
        {
            if (!ToggleSetting.AddToShell.IsSet()) return;

            if (ToggleSetting.AddToShell.IsOn()) RegisterShellVerb();
            else UnregisterShellVerb();
        }

        private static void RefreshShortcuts()
        {
            // The package has no Startup shortcut of its own, only a startup task.
            ShortcutSetting[] shortcuts = PackageInfo.IsPackaged
                ? new[] { ShortcutSetting.Desktop }
                : new[] { ShortcutSetting.Startup, ShortcutSetting.Desktop };
            foreach (ShortcutSetting setting in shortcuts)
            {
                try
                {
                    setting.Refresh();
                }
                catch (Exception ex)
                {
                    Diag.Swallowed("refreshing a shortcut", ex);
                }
            }
        }

        // ── Shell context-menu registration ───────────────────────────────────
        private const string ShellRegBaseFiles = @"Software\Classes\*\shell\AddToTrutility";
        private const string ShellCommandFiles = @"Software\Classes\*\shell\AddToTrutility\command";
        private const string ShellRegBaseFolders = @"Software\Classes\Folder\shell\AddToTrutility";
        private const string ShellCommandFolders = @"Software\Classes\Folder\shell\AddToTrutility\command";

        /// The packaged build's verb is declared in its manifest (shellext\)
        /// and shows only while this file exists: Explorer cannot see the
        /// package's own HKCU writes, but it can see its LocalState folder.
        private static string ShellVerbFlagPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", PackageInfo.FamilyName, "LocalState", "shell-verb.on");

        internal static void RegisterShellVerb()
        {
            if (PackageInfo.IsPackaged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ShellVerbFlagPath));
                File.WriteAllBytes(ShellVerbFlagPath, new byte[0]);
                return;
            }

            // The icon comes from the real exe: an execution alias has none.
            string exe = Assembly.GetExecutingAssembly().Location;
            string launch = PackageInfo.LaunchPath;

            // Register for files
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ShellRegBaseFiles))
            {
                key.SetValue(null, "Add to trutility");
                key.SetValue("Icon", $"\"{exe}\"");
            }
            using (RegistryKey cmd = Registry.CurrentUser.CreateSubKey(ShellCommandFiles))
                cmd.SetValue(null, $"\"{launch}\" --add \"%1\"");

            // Register for folders
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ShellRegBaseFolders))
            {
                key.SetValue(null, "Add to trutility");
                key.SetValue("Icon", $"\"{exe}\"");
            }
            using (RegistryKey cmd = Registry.CurrentUser.CreateSubKey(ShellCommandFolders))
                cmd.SetValue(null, $"\"{launch}\" --add \"%1\"");
        }

        internal static void UnregisterShellVerb()
        {
            if (PackageInfo.IsPackaged)
            {
                try
                {
                    File.Delete(ShellVerbFlagPath);
                }
                catch (Exception ex)
                {
                    Diag.Swallowed("removing the shell verb flag", ex);
                }
                return;
            }

            DeleteShellKey(ShellRegBaseFiles);
            DeleteShellKey(ShellRegBaseFolders);
        }

        /// <summary>
        /// DeleteSubKeyTree throws when the key is absent, which is the ordinary
        /// state whenever the verb was never registered. Absence is checked
        /// rather than caught, so only a real failure is reported.
        /// </summary>
        private static void DeleteShellKey(string path)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (key == null) return;
                }
                Registry.CurrentUser.DeleteSubKeyTree(path);
            }
            catch (Exception ex)
            {
                Diag.Swallowed("removing the shell verb at " + path, ex);
            }
        }

        internal static bool IsShellVerbRegistered()
        {
            if (PackageInfo.IsPackaged) return File.Exists(ShellVerbFlagPath);

            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ShellRegBaseFiles))
                return k != null;
        }

        // ── UI components ─────────────────────────────────────────────────────
        private readonly NotifyIcon _notifyIcon;
        private ContextMenuStrip _menu;
        private MenuSearchBox _search;
        private readonly Control _invokeHelper;
        private readonly GlobalHotkey _hotkey;

        // ── File watchers ─────────────────────────────────────────────────────
        // One watcher per folder currently on the menu: every root plus every
        // folder reached through a $F shortcut. The set is rebuilt from what the
        // last scan actually found, so roots arriving and leaving by policy need
        // no separate bookkeeping.
        private readonly Dictionary<string, FileSystemWatcher> _watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);

        // ── Debounce ──────────────────────────────────────────────────────────
        private CancellationTokenSource _debounceCts = new CancellationTokenSource();
        private readonly object _debounceLock = new object();
        private const int DebounceMs = 400;

        // ── Recovery polling ──────────────────────────────────────────────────
        // A folder that could not be watched costs live updates: a department
        // share behind a VPN that is not up yet, or a watcher killed by a network
        // blip. Rescanning periodically recovers both. It runs only while
        // something is actually unwatched, so a healthy setup never polls.
        private System.Windows.Forms.Timer _recoveryTimer;
        private const int RecoveryPollMs = 5 * 60 * 1000;

        // ── Folder access-error tracking ────────────────────────────────────────
        private HashSet<string> _warnedAccessErrorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ─────────────────────────────────────────────────────────────────────
        public TrayApplication()
        {
            _invokeHelper = new Control();
            _invokeHelper.CreateControl();

            // Nothing here may touch a configured folder. A root can sit on a
            // share that is slow or unreachable, and this runs before the tray
            // icon exists - probing one would stall the icon for the length of
            // an SMB timeout at every logon. Folder work happens on the
            // background rebuild instead.
            ApplyShellPolicy();

            _notifyIcon = new NotifyIcon
            {
                Text = "trutility",
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                Visible = true
            };

            // ApplyTrayPin must run AFTER the NotifyIcon is created and visible,
            // because Windows creates the NotifyIconSettings registry key only
            // once the icon is shown via Shell_NotifyIcon(NIM_ADD).
            SettingsTrayPin.ApplyTrayPin();

            // Off the UI thread: a redirected Desktop can sit on a slow share.
            Task.Run(() => RefreshShortcuts());

            _notifyIcon.MouseClick += OnTrayClick;

            _hotkey = new GlobalHotkey();
            _hotkey.Pressed += (s, e) => ShowMenuFromHotkey();
            ApplyHotkey(tellUser: false);

            _recoveryTimer = new System.Windows.Forms.Timer { Interval = RecoveryPollMs };
            _recoveryTimer.Tick += (s, e) => BuildMenuAsync();

            // Watchers are established by the rebuild, from the folders it found.
            BuildMenuAsync();
        }

        // ── Tray click ────────────────────────────────────────────────────────

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                ShowMenu();
            }
        }

        // NotifyIcon's own way of opening its menu, because it also brings
        // trutility to the foreground so the menu closes on a click elsewhere.
        private void ShowMenu()
        {
            if (_menu == null) return;

            MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                BindingFlags.Instance | BindingFlags.NonPublic);
            mi?.Invoke(_notifyIcon, null);
        }

        // ── Hotkey ────────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Opens the menu in the chosen corner, growing away from it. A value
        /// that is none of the positions counts as bottom right, the default.
        /// </summary>
        private void ShowMenuFromHotkey()
        {
            MenuPosition position = (MenuPosition)NumberSetting.HotkeyMenuPosition.Value();
            if (position == MenuPosition.Cursor)
            {
                ShowMenu();
                return;
            }
            if (_menu == null) return;

            bool right = position != MenuPosition.BottomLeft && position != MenuPosition.TopLeft;
            bool bottom = position != MenuPosition.TopRight && position != MenuPosition.TopLeft;

            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Point corner = new Point(right ? area.Right : area.Left, bottom ? area.Bottom : area.Top);
            ToolStripDropDownDirection direction = bottom
                ? (right ? ToolStripDropDownDirection.AboveLeft : ToolStripDropDownDirection.AboveRight)
                : (right ? ToolStripDropDownDirection.BelowLeft : ToolStripDropDownDirection.BelowRight);

            BringToForeground();
            if (_search != null) _search.OpenedFrom = corner;
            _menu.Show(corner, direction);
        }

        /// <summary>
        /// What NotifyIcon does before opening its own menu. Without it the menu
        /// stays open after a click in another program, and the search box
        /// cannot take the keyboard. Windows allows it because the hotkey was
        /// the last input.
        /// </summary>
        private void BringToForeground()
        {
            FieldInfo fi = typeof(NotifyIcon).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi?.GetValue(_notifyIcon) is NativeWindow window)
            {
                SetForegroundWindow(window.Handle);
            }
            else
            {
                Diag.Swallowed("finding the tray icon window");
            }
        }

        /// <summary>
        /// Registers the stored combination. A clash is only reported after the
        /// user picked the combination: at logon it would be a balloon every
        /// day about something they did not just do.
        /// </summary>
        private void ApplyHotkey(bool tellUser)
        {
            Keys keys = GlobalHotkey.Parse(StringSetting.Hotkey.Value());
            if (_hotkey.Register(keys) || !tellUser) return;

            _notifyIcon.ShowBalloonTip(4000, "trutility",
                GlobalHotkey.Format(keys) + " is already used by another program. Pick a different hotkey in Settings.",
                ToolTipIcon.Warning);
        }

        // ── Folder setup ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates the folder belonging to the user, and the built-in global
        /// folder when trutility picked that location itself.

        /// </summary>
        private static void EnsureFoldersExist(IReadOnlyList<Root> globalRoots, Root userRoot)
        {
            foreach (Root root in globalRoots)
            {
                if (root.IsBuiltIn)
                {
                    TryCreateDirectory(root.Path);
                }
            }

            if (userRoot != null)
            {
                TryCreateDirectory(userRoot.Path);
            }
        }

        private static void TryCreateDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                // Missing rights or an unreachable share. The folder simply
                // contributes nothing to the menu.
                Diag.Swallowed("creating " + path, ex);
            }
        }

        // ── Menu building ─────────────────────────────────────────────────────

        private void BuildMenuAsync()
        {
            CancellationToken token;
            lock (_debounceLock)
            {
                _debounceCts.Cancel();
                _debounceCts.Dispose();
                _debounceCts = new CancellationTokenSource();
                token = _debounceCts.Token;
            }

            Task.Run(async () =>
            {
                // Being superseded is the ordinary outcome during a burst of file
                // events, not a failure, so the token is read rather than passed
                // to Task.Delay - otherwise the common path would throw.
                await Task.Delay(DebounceMs);
                if (token.IsCancellationRequested) return;

                try
                {
                    RebuildMenu(token);
                }
                catch (Exception ex)
                {
                    _invokeHelper.BeginInvoke((Action)(() => ShowBalloon("trutility error", ex.Message)));
                }
            }, token);
        }

        /// Resolves the folders, scans them, and hands the result back to the UI
        /// thread. Runs entirely off the UI thread: a root may be unreachable.
        private void RebuildMenu(CancellationToken token)
        {
            RootsLib.MigrateLegacyUserRoot();

            IReadOnlyList<Root> globalRoots = RootsLib.GetGlobalRoots();
            Root userRoot = RootsLib.GetUserRoot();
            EnsureFoldersExist(globalRoots, userRoot);

            // Categories are hide lists, and mean nothing once only the custom
            // list is shown.
            bool showOnlyListed = NumberSetting.ExtensionFilterMode.Value() == 1;
            HashSet<string> filter = MenuBuilder.ParseExtensions(StringSetting.ExtensionFilter.Value()
                + (showOnlyListed ? "" : ", " + FilterCategory.HiddenExtensions()));
            bool filterMenu = filter.Count > 0 && ToggleSetting.FilterMenu.IsOn();
            bool filterSearch = filter.Count > 0 && ToggleSetting.FilterSearch.IsOn();

            List<string> accessErrors;
            List<string> followedFolders;
            MenuNode tree = ScanTree(globalRoots, userRoot, filterMenu ? filter : null, showOnlyListed,
                                     out accessErrors, out followedFolders);

            // Read once here so the index and the box always agree: no index
            // means no box, and a menu without search costs nothing for it.
            // shortcut: filtering only one of menu and search scans every folder
            // twice; prune a copy of one tree instead if that ever shows up as slow.
            IReadOnlyList<SearchEntry> searchIndex = null;
            if (ToggleSetting.EnableSearch.IsOn())
            {
                MenuNode searchTree = filterSearch == filterMenu
                    ? tree
                    : ScanTree(globalRoots, userRoot, filterSearch ? filter : null, showOnlyListed, out _, out _);
                searchIndex = MenuSearch.Index(searchTree);
            }
            List<string> watchPaths = CollectWatchPaths(globalRoots, userRoot, followedFolders);
            string userRootPath = userRoot.Path;

            // A newer rebuild started while this one scanned. Its result is the
            // current one, even if this slower scan finishes after it.
            if (token.IsCancellationRequested) return;

            _invokeHelper.BeginInvoke((Action)(() =>
            {
                if (token.IsCancellationRequested) return;

                ApplyNewMenu(tree, searchIndex);
                ReportAccessErrors(accessErrors, userRootPath);
                SyncWatchers(watchPaths);
            }));

            // The menu is already usable at this point; this only fills the
            // cache the next open will read from.
            WarmIconCache(tree, token);
        }

        /// Scans every folder with the current settings, passing on only those
        /// files the extension filter keeps, or all of them for a null filter.
        private static MenuNode ScanTree(IReadOnlyList<Root> globalRoots, Root userRoot,
                                         HashSet<string> filter, bool showOnlyListed,
                                         out List<string> accessErrors, out List<string> followedFolders)
        {
            return MenuBuilder.BuildTree(
                globalRoots, userRoot, out accessErrors, out followedFolders,
                ToggleSetting.ShowExtensions.IsOn(),
                ToggleSetting.ExpandFolderShortcuts.IsOn() ? NumberSetting.AutoExpandLimit.Value() : 0,
                (MenuSort)NumberSetting.SortBy.Value(),
                ToggleSetting.ReverseSort.IsOn(), ToggleSetting.FoldersFirst.IsOn(),
                filter, showOnlyListed);
        }

        /// <summary>
        /// Loads every icon in the tree into the cache, on the thread that just
        /// finished the scan.
        /// </summary>
        private static void WarmIconCache(MenuNode root, CancellationToken token)
        {
            Queue<MenuNode> pending = new Queue<MenuNode>();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested) return;

                foreach (MenuNode child in pending.Dequeue().Children)
                {
                    if (token.IsCancellationRequested) return;

                    if (child.IsFolder)
                    {
                        pending.Enqueue(child);
                    }
                    else if (child.FilePath != null)
                    {
                        IconsLib.Prewarm(child.FilePath);
                    }
                }
            }
        }

        private void ReportAccessErrors(List<string> accessErrors, string userRootPath)
        {
            HashSet<string> current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in accessErrors)
            {
                if (RootsLib.IsUnder(path, userRootPath))
                {
                    current.Add(path);
                }
                else
                {
                    Diag.Swallowed("reading administrator-managed folder " + path);
                }
            }

            foreach (string path in current)
            {
                if (!_warnedAccessErrorPaths.Contains(path))
                    ShowBalloon("trutility – folder inaccessible", $"Could not read:\n{path}");
            }
            _warnedAccessErrorPaths = current;
        }

        private static List<string> CollectWatchPaths(IReadOnlyList<Root> globalRoots, Root userRoot,
                                                      List<string> followedFolders)
        {
            List<string> paths = new List<string>();
            foreach (Root root in globalRoots)
            {
                paths.Add(root.Path);
            }
            if (userRoot != null)
            {
                paths.Add(userRoot.Path);
            }
            paths.AddRange(followedFolders);
            return paths;
        }

        private void ApplyNewMenu(MenuNode tree, IReadOnlyList<SearchEntry> searchIndex)
        {
            ContextMenuStrip newMenu = new ContextMenuStrip();

            // With search turned off there is no index, and the menu is the
            // plain tree with nothing above it.
            MenuSearchBox search = searchIndex == null
                ? null
                : new MenuSearchBox(newMenu, searchIndex, NewFileItem, FillIcons, LaunchFile);
            PopulateMenu(newMenu.Items, tree);
            search?.CaptureTree();
            AddStaticItems(newMenu);

            // Every open starts from the full tree. The top level is the one part
            // guaranteed to be shown, and it is still only filled when the menu
            // opens rather than on every rebuild.
            newMenu.Opening += (s, e) =>
            {
                Theme.Apply();
                if (Theme.Active.Font != null && newMenu.Font != Theme.Active.Font)
                {
                    newMenu.Font = Theme.Active.Font;
                }
                search?.ApplyTheme();
                search?.Reset();
                FillIcons(newMenu.Items);
            };

            ContextMenuStrip old = _menu;
            _menu = newMenu;
            _search = search;
            _notifyIcon.ContextMenuStrip = newMenu;
            old?.Dispose();
        }

        private void PopulateMenu(ToolStripItemCollection items, MenuNode node)
        {
            foreach (MenuNode child in node.Children)
            {
                if (child.IsSeparator)
                {
                    items.Add(NewSeparator(child));
                    continue;
                }

                if (child.SeparatorBefore)
                    items.Add(new ToolStripSeparator());

                if (child.IsFolder)
                {
                    AddFolderItem(items, child);
                }
                else
                {
                    items.Add(NewFileItem(child));
                }

                if (child.SeparatorAfter)
                    items.Add(new ToolStripSeparator());
            }
        }

        /// The thick rule marks the one boundary between administrator-managed
        /// content and the folder belonging to the user; thin rules part one
        /// global root from the next.
        private static ToolStripSeparator NewSeparator(MenuNode child)
        {
            if (child.IsSectionBoundary)
            {
                return new SectionDivider();
            }
            return new ToolStripSeparator();
        }

        private void AddFolderItem(ToolStripItemCollection items, MenuNode child)
        {
            if (child.Children.Count == 0) return;

            ToolStripMenuItem subMenu = new HoverMenuItem(child.Name);
            PopulateMenu(subMenu.DropDownItems, child);
            subMenu.DropDownOpening += (s, e) => FillIcons(subMenu.DropDownItems);

            string folderPath = child.FolderPath;
            string linkPath = child.LinkPath;
            if (folderPath != null)
            {
                subMenu.MouseDown += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left && (Control.ModifierKeys & Keys.Control) != 0)
                    {
                        _menu?.Close();

                        // An expanded shortcut loses only the shortcut; the
                        // folder it points at is never touched.
                        if (linkPath != null) TryDeleteUserFile(linkPath);
                        else TryDeleteUserFolder(folderPath);
                        return;
                    }

                    if (e.Button != MouseButtons.Middle) return;

                    OpenFolder(folderPath);
                    _menu?.Close();
                };
            }
            items.Add(subMenu);
        }

        /// <summary>
        /// Loads the icons for one menu level, just before it is shown.
        ///
        /// A shell icon lookup costs a millisecond or more per file, and the
        /// whole tree used to be resolved while the menu was built - on the UI
        /// thread, for every rebuild. That is seconds of freeze on a large
        /// folder, nearly all of it spent on submenus nobody opened. Nothing can
        /// be seen inside a closed submenu, so the work waits until it opens.
        ///
        /// DropDownOpening runs before the drop-down is displayed, so the images
        /// are in place for the first paint and no layout shifts.
        /// </summary>
        private static void FillIcons(ToolStripItemCollection items)
        {
            // Setting an item's Image makes its menu lay itself out again, so
            // filling a level one item at a time relaid the whole level once per
            // item - quadratic. With every icon already cached, a 250-entry
            // submenu took over five seconds to open, freezing the highlight and
            // swallowing clicks. Suspended, the level is laid out once: ~10 ms.
            ToolStrip owner = items.Count > 0 ? items[0].Owner : null;
            owner?.SuspendLayout();
            try
            {
                // A menu keeps only 2px above its first row and below its last,
                // less than beside a highlight, and ignores a Padding set on it.
                // The end rows make up the rest. They stay the end rows: the
                // search box is always first, Exit always last, and a submenu
                // never changes.
                if (Theme.Active.RowPadding != null && items.Count > 0)
                {
                    int extra = Theme.MenuEdge - 2;
                    ToolStripItem first = items[0];
                    ToolStripItem last = items[items.Count - 1];
                    if (first is ToolStripMenuItem) first.Margin = new Padding(0, extra, 0, first.Margin.Bottom);
                    if (last is ToolStripMenuItem) last.Margin = new Padding(0, last.Margin.Top, 0, extra);
                }

                foreach (ToolStripItem item in items)
                {
                    // Here because this runs for every level just before it shows,
                    // and for search results as they are added.
                    if (Theme.Active.RowPadding is Padding row && item is ToolStripMenuItem && item.Padding != row)
                    {
                        item.Padding = row;
                    }

                    string filePath = item.Tag as string;
                    if (filePath == null || item.Image != null) continue;

                    item.Image = IconsLib.GetIcon(filePath);
                }
            }
            finally
            {
                owner?.ResumeLayout(true);
            }
        }

        /// <summary>
        /// Plain left-click launches. Ctrl+Click removes the file from the folder
        /// belonging to the user, Shift+Click launches elevated. Both modifiers
        /// act on MouseDown, so Click has to check that none was held or the file
        /// would launch a second time.
        /// </summary>
        private ToolStripMenuItem NewFileItem(MenuNode child)
        {
            string filePath = child.FilePath;

            // The path rides along so FillIcons can resolve the image when this
            // level of the menu is actually opened.
            ToolStripMenuItem item = new HoverMenuItem(child.Name)
            {
                Tag = filePath,
            };

            item.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Middle)
                {
                    OpenFileLocation(filePath);
                    _menu?.Close();
                    return;
                }

                if (e.Button != MouseButtons.Left) return;

                if ((Control.ModifierKeys & Keys.Control) != 0)
                {
                    // Closed first: the deleted entry is not left on screen,
                    // and the confirmation is not shown over an open menu.
                    _menu?.Close();
                    TryDeleteUserFile(filePath);
                    return;
                }

                if ((Control.ModifierKeys & Keys.Shift) != 0)
                {
                    LaunchFile(filePath, runas: true);
                    _menu?.Close();
                }
            };

            item.Click += (s, e) =>
            {
                if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) == 0)
                    LaunchFile(filePath, runas: false);
            };

            return item;
        }

        private void AddStaticItems(ContextMenuStrip menu)
        {
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new HoverMenuItem("Settings");
            settings.Click += OnSettings;
            menu.Items.Add(settings);

            ToolStripMenuItem exit = new HoverMenuItem("Exit");
            exit.Click += (s, e) => Application.Exit();
            menu.Items.Add(exit);
        }

        // ── File operations ───────────────────────────────────────────────────

        private void LaunchFile(string path, bool runas)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = runas ? "runas" : ""
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not launch:\n{path}\n\n{ex.Message}",
                    "trutility", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenFileLocation(string path)
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open the location of:\n{path}\n\n{ex.Message}",
                    "trutility", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open:\n{path}\n\n{ex.Message}",
                    "trutility", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void TryDeleteUserFile(string path)
        {
            // The user root is the only location trutility ever writes to; global
            // roots are administrator-managed and strictly read-only. IsUnder
            // normalises both sides and compares on a separator boundary, so a
            // global root sitting next to the user root (D:\tools-shared beside
            // D:\tools) cannot be deleted through the menu.
            if (!RootsLib.IsUnder(path, UserRoot))
            {
                ShowBalloon("trutility", "Only files in your User folder can be deleted from the menu.");
                return;
            }

            if (!ConfirmDelete(ToggleSetting.ConfirmDeleteFiles, "Delete this file?", path))
                return;

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                // Watcher will trigger a rebuild automatically
            }
            catch (Exception ex)
            {
                ShowBalloon("trutility – delete failed", ex.Message);
            }
        }

        /// True when the delete may go ahead: either nothing asks for this kind
        /// of entry, or the user said yes.
        private static bool ConfirmDelete(ToggleSetting kind, string question, string path)
        {
            if (!kind.IsOn()) return true;

            return MessageBox.Show(
                $"{question}\n\n{path}\n\nIt will be sent to the Recycle Bin.",
                "trutility", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void TryDeleteUserFolder(string path)
        {
            // Same boundary as TryDeleteUserFile, and the user root itself stays.
            if (!RootsLib.IsUnder(path, UserRoot) || RootsLib.IsUnder(UserRoot, path))
            {
                ShowBalloon("trutility", "Only folders inside your User folder can be deleted from the menu.");
                return;
            }

            if (!ConfirmDelete(ToggleSetting.ConfirmDeleteFolders, "Delete this folder and everything in it?", path))
                return;

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                // Watcher will trigger a rebuild automatically
            }
            catch (Exception ex)
            {
                ShowBalloon("trutility – delete failed", ex.Message);
            }
        }

        // ── Add-file entry point (called from --add CLI argument) ─────────────

        internal static void AddFileToUserFolder(string sourcePath)
        {
            try
            {
                string userRoot = UserRoot;
                Directory.CreateDirectory(userRoot);

                string dest = UniqueShortcutPath(userRoot, sourcePath);

                // Create a proper shell shortcut via WScript.Shell progid at runtime
                Type wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType == null)
                    throw new InvalidOperationException("WScript.Shell is not available on this system.");

                dynamic shell = Activator.CreateInstance(wshType);
                dynamic shortcut = shell.CreateShortcut(dest);
                shortcut.TargetPath = sourcePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(sourcePath);
                shortcut.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not add shortcut:\n{ex.Message}",
                    "trutility", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// A folder keeps its whole name - "v1.2" is not "v1" - and a drive
        /// root is named after its letter. A name already taken gets " (2)",
        /// " (3)"..., since saving over it would silently replace a shortcut
        /// the user made earlier.
        /// </summary>
        private static string UniqueShortcutPath(string userRoot, string sourcePath)
        {
            string trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Directory.Exists(sourcePath)
                ? Path.GetFileName(trimmed)
                : Path.GetFileNameWithoutExtension(trimmed);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Drive " + trimmed.TrimEnd(':');
            }

            string dest = Path.Combine(userRoot, name + ".lnk");
            for (int i = 2; File.Exists(dest); i++)
            {
                dest = Path.Combine(userRoot, $"{name} ({i}).lnk");
            }
            return dest;
        }

        // ── Settings dialog ───────────────────────────────────────────────────

        private void OnSettings(object sender, EventArgs e)
        {
            // Released while the dialog is open, so pressing the current
            // combination in the hotkey box reaches the box instead of
            // opening the menu.
            _hotkey.Unregister();

            using (SettingsDialog dlg = new SettingsDialog(RootsLib.GetUserRoot(), RefreshMenu, BuildMenuAsync))
            {
                dlg.ShowDialog();
                ApplyHotkey(tellUser: true);
                // A new folder location or a display change both need a rebuild;
                // for a new location it also creates the folder and re-points
                // the watchers.
                if (dlg.MenuNeedsRebuild)
                {
                    BuildMenuAsync();
                }
            }
        }

        /// <summary>
        /// A full refresh: rescan every folder and forget every cached icon.
        /// Watchers make this rarely necessary, so the usual reason to ask is
        /// something they could not see - a share that came back online, or a
        /// program updated in place whose old icon is still cached.
        /// </summary>
        private void RefreshMenu()
        {
            IconsLib.ClearCache();
            BuildMenuAsync();
        }

        // ── FileSystemWatcher ─────────────────────────────────────────────────

        /// <summary>
        /// Holds one watcher per folder the last scan actually reached - every
        /// root plus every folder followed through a $F shortcut - so edits
        /// anywhere on the menu rebuild it live.
        ///
        /// Anything that could not be watched turns on recovery polling, which
        /// is what eventually picks up a department share that was offline when
        /// trutility started.
        /// </summary>
        private void SyncWatchers(List<string> paths)
        {
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string normalized = RootsLib.NormalizeDir(path);
                if (normalized != null)
                {
                    wanted.Add(normalized);
                }
            }

            foreach (string stale in _watchers.Keys.Except(wanted).ToList())
            {
                _watchers[stale].Dispose();
                _watchers.Remove(stale);
            }

            int unwatched = 0;
            foreach (string path in wanted)
            {
                if (_watchers.ContainsKey(path))
                {
                    continue;
                }

                FileSystemWatcher watcher = CreateWatcher(path);
                if (watcher == null)
                {
                    unwatched++;
                    continue;
                }
                _watchers[path] = watcher;
            }

            SetRecoveryPolling(unwatched > 0);
        }

        private FileSystemWatcher CreateWatcher(string path)
        {
            if (!Directory.Exists(path)) return null;

            try
            {
                FileSystemWatcher w = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter =
                        NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                w.Created += OnFolderChanged;
                w.Deleted += OnFolderChanged;
                w.Renamed += OnFolderChanged;
                w.Changed += OnFolderChanged;
                w.Error += OnWatcherError;
                return w;
            }
            catch (Exception ex)
            {
                // The folder went away between the scan and here, or the path is
                // one FileSystemWatcher will not accept. Recovery polling retries.
                Diag.Swallowed("watching " + path, ex);
                return null;
            }
        }

        /// <summary>
        /// A write to a file changes nothing on the menu unless files are
        /// ordered by date. Without this, a tool logging next to itself on a
        /// watched share rescanned every folder every few hundred milliseconds.
        /// </summary>
        private void OnFolderChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed
                && (MenuSort)NumberSetting.SortBy.Value() != MenuSort.Date)
            {
                return;
            }
            BuildMenuAsync();
        }

        /// <summary>
        /// A watcher over SMB dies on a network blip, and any watcher dies when
        /// its internal buffer overflows during a burst of changes. Neither
        /// recovers on its own, so the dead watcher is dropped and a rebuild
        /// re-establishes it.
        /// </summary>
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            FileSystemWatcher watcher = sender as FileSystemWatcher;
            Diag.Swallowed("watching " + (watcher == null ? "?" : watcher.Path), e.GetException());

            try
            {
                // Fired on a pool thread; _watchers belongs to the UI thread.
                _invokeHelper.BeginInvoke((Action)(() =>
                {
                    DropWatcher(watcher);
                    SetRecoveryPolling(true);
                    BuildMenuAsync();
                }));
            }
            catch (Exception ex)
            {
                // Shutting down: the helper control is already gone.
                Diag.Swallowed("scheduling watcher recovery", ex);
            }
        }

        private void DropWatcher(FileSystemWatcher watcher)
        {
            if (watcher == null)
            {
                return;
            }

            foreach (string key in _watchers.Where(p => ReferenceEquals(p.Value, watcher))
                                            .Select(p => p.Key).ToList())
            {
                _watchers.Remove(key);
            }

            try
            {
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                Diag.Swallowed("disposing a watcher", ex);
            }
        }

        private void SetRecoveryPolling(bool enabled)
        {
            if (_recoveryTimer != null && _recoveryTimer.Enabled != enabled)
            {
                _recoveryTimer.Enabled = enabled;
            }
        }

        // ── Balloon helper ────────────────────────────────────────────────────

        private void ShowBalloon(string title, string text) =>
            _notifyIcon?.ShowBalloonTip(4000, title, text, ToolTipIcon.Error);

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _recoveryTimer?.Stop();
            _recoveryTimer?.Dispose();
            _hotkey?.Dispose();
            foreach (FileSystemWatcher watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu?.Dispose();
            _invokeHelper?.Dispose();
        }
    }
}
