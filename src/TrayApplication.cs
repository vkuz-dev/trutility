using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
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
        // ── Registry ──────────────────────────────────────────────────────────
        private const string RegKeyPath = @"SOFTWARE\trutility";
        private const string RegValShell = "AddToShell";

        // ── Folder paths ──────────────────────────────────────────────────────
        //
        // Resolution lives in RootsLib, which is the single place that knows how
        // folders are configured. The two accessors below are the remaining
        // single-root call sites; they are replaced by the full root list when
        // the menu builder and watchers are ported.

        /// First resolved global root. Identical to the previous behaviour while
        /// no Roots keys exist, since the legacy GlobalRoot value and the built-in
        /// default both resolve to the "Default" root at Order 0.
        internal static string GlobalRoot
        {
            get
            {
                IReadOnlyList<Root> roots = RootsLib.GetGlobalRoots();
                return roots.Count > 0 ? roots[0].Path : RootsLib.ResolveBuiltInGlobalPath();
            }
        }

        /// The folder belonging to the user — the only one trutility writes to.
        internal static string UserRoot => RootsLib.ResolveUserRootPath();

        internal static void SetUserRoot(string path) => RootsLib.SetUserRoot(path);

        /// True when HKLM pins the location and HKCU has no explicit override yet.
        internal static bool IsUserRootManagedByPolicy() => RootsLib.IsUserRootManagedByPolicy();

        // ── Shell verb helpers ────────────────────────────────────────────────

        /// HKCU → HKLM → actual verb registration (legacy fallback).
        internal static bool IsShellVerbEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                object val = key?.GetValue(RegValShell);
                if (val != null) return Convert.ToInt32(val) != 0;
            }
            using (var key = Registry.LocalMachine.OpenSubKey(RegKeyPath))
            {
                object val = key?.GetValue(RegValShell);
                if (val != null) return Convert.ToInt32(val) != 0;
            }
            return IsShellVerbRegistered();
        }

        /// True when HKLM carries AddToShell but HKCU has no explicit override yet.
        internal static bool IsShellVerbManagedByPolicy()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
                if (key?.GetValue(RegValShell) != null) return false;
            using (var key = Registry.LocalMachine.OpenSubKey(RegKeyPath))
                return key?.GetValue(RegValShell) != null;
        }

        internal static void SaveShellPref(bool enable)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
                key?.SetValue(RegValShell, enable ? 1 : 0, RegistryValueKind.DWord);
        }

        /// Applies HKLM AddToShell policy on startup when the user has no HKCU override.
        internal static void ApplyShellPolicy()
        {
            using (var hkcu = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                object hkcuVal = hkcu?.GetValue(RegValShell);
                if (hkcuVal != null)
                {
                    if (Convert.ToInt32(hkcuVal) != 0) RegisterShellVerb();
                    else UnregisterShellVerb();
                    return;
                }
            }
            using (var hklm = Registry.LocalMachine.OpenSubKey(RegKeyPath))
            {
                object hklmVal = hklm?.GetValue(RegValShell);
                if (hklmVal != null)
                {
                    if (Convert.ToInt32(hklmVal) != 0) RegisterShellVerb();
                    else UnregisterShellVerb();
                }
            }
        }

        // ── Shell context-menu registration ───────────────────────────────────
        private const string ShellRegBaseFiles = @"Software\Classes\*\shell\AddToTrutility";
        private const string ShellCommandFiles = @"Software\Classes\*\shell\AddToTrutility\command";
        private const string ShellRegBaseFolders = @"Software\Classes\Folder\shell\AddToTrutility";
        private const string ShellCommandFolders = @"Software\Classes\Folder\shell\AddToTrutility\command";

        internal static void RegisterShellVerb()
        {
            var exe = Assembly.GetExecutingAssembly().Location;

            // Register for files
            using (var key = Registry.CurrentUser.CreateSubKey(ShellRegBaseFiles))
            {
                key.SetValue(null, "Add to trutility");
                key.SetValue("Icon", $"\"{exe}\"");
            }
            using (var cmd = Registry.CurrentUser.CreateSubKey(ShellCommandFiles))
                cmd.SetValue(null, $"\"{exe}\" --add \"%1\"");

            // Register for folders
            using (var key = Registry.CurrentUser.CreateSubKey(ShellRegBaseFolders))
            {
                key.SetValue(null, "Add to trutility");
                key.SetValue("Icon", $"\"{exe}\"");
            }
            using (var cmd = Registry.CurrentUser.CreateSubKey(ShellCommandFolders))
                cmd.SetValue(null, $"\"{exe}\" --add \"%1\"");
        }

        internal static void UnregisterShellVerb()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(ShellRegBaseFiles); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(ShellRegBaseFolders); } catch { }
        }

        internal static bool IsShellVerbRegistered()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(ShellRegBaseFiles))
                return k != null;
        }

        // ── UI components ─────────────────────────────────────────────────────
        private readonly NotifyIcon _notifyIcon;
        private ContextMenuStrip _menu;
        private readonly Control _invokeHelper;

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
            // icon exists — probing one would stall the icon for the length of
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

            _notifyIcon.MouseClick += OnTrayClick;

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
                if (_menu != null)
                {
                    var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    mi?.Invoke(_notifyIcon, null);
                }
            }
        }

        // ── Folder setup ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates the folder belonging to the user, and the built-in global
        /// folder when trutility picked that location itself.
        ///
        /// An administrator-provisioned root is never created. Conjuring one
        /// would turn a mistyped policy path into a silently empty folder rather
        /// than a visible mistake, and would scatter stray directories across
        /// whichever share the path pointed at.
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
                try
                {
                    await Task.Delay(DebounceMs, token);
                    token.ThrowIfCancellationRequested();

                    RootsLib.MigrateLegacyUserRoot();

                    IReadOnlyList<Root> globalRoots = RootsLib.GetGlobalRoots();
                    Root userRoot = RootsLib.GetUserRoot();
                    EnsureFoldersExist(globalRoots, userRoot);

                    List<string> accessErrors;
                    List<string> followedFolders;
                    MenuNode tree = MenuBuilder.BuildTree(
                        globalRoots, userRoot, out accessErrors, out followedFolders);

                    List<string> watchPaths = CollectWatchPaths(globalRoots, userRoot, followedFolders);
                    string userRootPath = userRoot.Path;

                    _invokeHelper.BeginInvoke((Action)(() =>
                    {
                        ApplyNewMenu(tree);
                        ReportAccessErrors(accessErrors, userRootPath);
                        SyncWatchers(watchPaths);
                    }));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _invokeHelper.BeginInvoke((Action)(() => ShowBalloon("trutility error", ex.Message))); }
            }, token);
        }

        /// <summary>
        /// Warns only about folders the user can actually do something about.
        ///
        /// A denied or unreachable global root is an administrator matter: the
        /// user cannot change its permissions and cannot bring its share back,
        /// so a balloon would only nag on every rebuild. Those are recorded for
        /// support instead, which is what answers "my department folder is
        /// missing" without a reproduction.
        /// </summary>
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

        private void ApplyNewMenu(MenuNode tree)
        {
            var newMenu = new ContextMenuStrip();
            PopulateMenu(newMenu.Items, tree);
            AddStaticItems(newMenu);

            var old = _menu;
            _menu = newMenu;
            _notifyIcon.ContextMenuStrip = newMenu;
            old?.Dispose();
        }

        private void PopulateMenu(ToolStripItemCollection items, MenuNode node)
        {
            foreach (var child in node.Children)
            {
                if (child.IsSeparator)
                {
                    // The thick rule marks the one boundary between
                    // administrator-managed content and the folder belonging to
                    // the user; thin rules part one global root from the next.
                    if (child.IsSectionBoundary)
                    {
                        items.Add(new SectionDivider());
                    }
                    else
                    {
                        items.Add(new ToolStripSeparator());
                    }
                    continue;
                }

                if (child.SeparatorBefore)
                    items.Add(new ToolStripSeparator());

                if (child.IsFolder)
                {
                    if (child.Children.Count == 0) continue;

                    var subMenu = new ToolStripMenuItem(child.Name);
                    PopulateMenu(subMenu.DropDownItems, child);
                    items.Add(subMenu);
                }
                else
                {
                    var filePath = child.FilePath;
                    var item = new ToolStripMenuItem(child.Name);
                    item.Image = IconsLib.GetIcon(filePath);

                    // Normal left-click: launch
                    // Ctrl+Click: delete from user folder (sent to Recycle Bin)
                    // Shift+Click: Run As (ShellExecute "runas")
                    item.MouseDown += (s, e) =>
                    {
                        if (e.Button == MouseButtons.Left)
                        {
                            if ((Control.ModifierKeys & Keys.Control) != 0)
                            {
                                // ── Ctrl+Click → delete ────────────────────
                                TryDeleteUserFile(filePath);
                                // Close the menu so the deleted item isn't still visible
                                _menu?.Close();
                                return;
                            }
                            if ((Control.ModifierKeys & Keys.Shift) != 0)
                            {
                                // ── Shift+Click → Run As ──────────────────
                                LaunchFile(filePath, runas: true);
                                _menu?.Close();
                                return;
                            }
                        }
                    };

                    item.Click += (s, e) =>
                    {
                        // Guard: if a modifier was held the MouseDown handler
                        // already acted; don't launch again.
                        if ((Control.ModifierKeys & (Keys.Control | Keys.Shift)) == 0)
                            LaunchFile(filePath, runas: false);
                    };

                    items.Add(item);
                }

                if (child.SeparatorAfter)
                    items.Add(new ToolStripSeparator());
            }
        }

        private void AddStaticItems(ContextMenuStrip menu)
        {
            menu.Items.Add(new ToolStripSeparator());

            var about = new ToolStripMenuItem("About / Settings");
            about.Click += OnAbout;
            menu.Items.Add(about);

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => Application.Exit();
            menu.Items.Add(exit);
        }

        // ── File operations ───────────────────────────────────────────────────

        private void LaunchFile(string path, bool runas)
        {
            try
            {
                var psi = new ProcessStartInfo(path)
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

        // ── Add-file entry point (called from --add CLI argument) ─────────────

        internal static void AddFileToUserFolder(string sourcePath)
        {
            try
            {
                var userRoot = UserRoot;
                Directory.CreateDirectory(userRoot);

                var lnkName = Path.GetFileNameWithoutExtension(sourcePath) + ".lnk";
                var dest = Path.Combine(userRoot, lnkName);

                // Create a proper shell shortcut via WScript.Shell progid at runtime
                var wshType = Type.GetTypeFromProgID("WScript.Shell");
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

        // ── About / Settings dialog ───────────────────────────────────────────

        private void OnAbout(object sender, EventArgs e)
        {
            using (AboutDialog dlg = new AboutDialog(RootsLib.GetGlobalRoots(), RootsLib.GetUserRoot()))
            {
                dlg.ShowDialog();
                // The rebuild creates the folder and re-points the watchers.
                if (dlg.UserRootChanged)
                {
                    BuildMenuAsync();
                }
            }
        }

        // ── FileSystemWatcher ─────────────────────────────────────────────────

        /// <summary>
        /// Holds one watcher per folder the last scan actually reached — every
        /// root plus every folder followed through a $F shortcut — so edits
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

        private void OnFolderChanged(object sender, FileSystemEventArgs e) => BuildMenuAsync();

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
