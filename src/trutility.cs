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

    // ═════════════════════════════════════════════════════════════════════════
    //  About / Settings dialog
    // ═════════════════════════════════════════════════════════════════════════

    internal sealed class AboutDialog : Form
    {
        public bool UserRootChanged { get; private set; }

        private const string StatusChecking = "Checking…";
        private const int StatusColumn = 4;

        private readonly IReadOnlyList<Root> _globalRoots;
        private string _userRoot;

        /// True when Change… picked a path, false when Reset asked to fall back.
        /// Only meaningful while UserRootChanged is set.
        private bool _userRootExplicitlyChosen;
        private Label _userRootLabel;
        private ListView _rootsList;
        private Button _btnOpenSelected;
        private CheckBox _chkShell;
        private CheckBox _chkStartup;
        private CheckBox _chkTrayPin;

        public AboutDialog(IReadOnlyList<Root> globalRoots, Root userRoot)
        {
            _globalRoots = globalRoots ?? new Root[0];
            _userRoot = userRoot == null ? RootsLib.ResolveUserRootPath() : userRoot.Path;

            Text = "About trutility";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(780, 700);
            MinimumSize = new Size(640, 520);

            // A single column that grows with the window. The folder list is the
            // only row given the slack, because it is the one whose length is
            // decided by policy rather than by this layout.
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(12),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Control[] rows =
            {
                BuildHeader(),
                BuildGlobalGroup(),
                BuildUserGroup(),
                BuildSettingsGroup(),
                BuildHelpGroup(),
                BuildButtonRow(),
            };

            foreach (Control row in rows)
            {
                row.Dock = DockStyle.Fill;
                row.Margin = new Padding(0, 0, 0, 10);
                layout.RowStyles.Add(row == rows[1]
                    ? new RowStyle(SizeType.Percent, 100f)
                    : new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(row);
            }

            Controls.Add(layout);
            ProbeRootStatusesAsync();
        }

        // ── Sections ──────────────────────────────────────────────────────────

        private static Control BuildHeader()
        {
            Version ver = Assembly.GetExecutingAssembly().GetName().Version;
            return new Label
            {
                Text =
                    $"trutility — Tray Utility v{ver.Major}.{ver.Minor}.{ver.Build}\n" +
                    "Keep your favourite files, tools or folders within reach.\n\n" +
                    "Author: Vladimirs Kuznecovs   ·   " +
                    "GitHub: https://github.com/vkuz-dev/trutility",
                AutoSize = true,
            };
        }

        /// <summary>
        /// Lists the folders an administrator assigned, with where each one came
        /// from and whether it resolved.
        ///
        /// This is the support surface: "why do I see this" and "why is my
        /// department folder missing" are both answerable from a screenshot of
        /// this list, without a remote session.
        /// </summary>
        private Control BuildGlobalGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Global folders — managed by your administrator (read-only)",
                Padding = new Padding(10, 6, 10, 10),
                // The one section whose length policy decides, so it keeps a
                // usable minimum however many roots arrive.
                MinimumSize = new Size(0, 190),
            };

            _rootsList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                UseCompatibleStateImageBehavior = false,
            };
            _rootsList.Columns.Add("Name", 100);
            _rootsList.Columns.Add("Shown as", 100);
            _rootsList.Columns.Add("Location", 210);
            _rootsList.Columns.Add("Configured in", 210);
            _rootsList.Columns.Add("Status", 90);

            foreach (Root root in _globalRoots)
            {
                ListViewItem item = new ListViewItem(new[]
                {
                    root.Name,
                    string.IsNullOrWhiteSpace(root.Label) ? "(top level)" : root.Label,
                    root.Path,
                    root.Source ?? "",
                    StatusChecking,
                })
                { Tag = root.Path };
                _rootsList.Items.Add(item);
            }

            _rootsList.SelectedIndexChanged += (s, e) =>
                _btnOpenSelected.Enabled = _rootsList.SelectedItems.Count > 0;
            _rootsList.DoubleClick += (s, e) => OpenSelectedRoot();

            _btnOpenSelected = new Button
            {
                Text = "Open selected folder",
                AutoSize = true,
                Enabled = false,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 0, 0),
            };
            _btnOpenSelected.Click += (s, e) => OpenSelectedRoot();

            // A table rather than Fill-over-Bottom docking: docking order inside
            // a GroupBox let the button overlap the last row of the list.
            TableLayoutPanel inner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
            };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.Controls.Add(_rootsList, 0, 0);
            inner.Controls.Add(_btnOpenSelected, 0, 1);

            group.Controls.Add(inner);
            return group;
        }

        private Control BuildUserGroup()
        {
            bool managed = TrayApplication.IsUserRootManagedByPolicy();
            GroupBox group = new GroupBox
            {
                // Policy may decide where this folder lives. What goes in it is
                // the user's alone, so nothing here is ever disabled by content.
                Text = managed
                    ? "Your folder — location set by your administrator"
                    : "Your folder",
                Height = 86,
                Padding = new Padding(10, 6, 10, 10),
            };

            _userRootLabel = new Label
            {
                Text = _userRoot,
                Dock = DockStyle.Top,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 8f),
                AutoEllipsis = true,
            };

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                FlowDirection = FlowDirection.LeftToRight,
            };

            Button change = new Button { Text = "Change…", AutoSize = true, Enabled = !managed };
            change.Click += OnChangeUserRoot;

            Button reset = new Button { Text = "Reset", AutoSize = true, Enabled = !managed };
            reset.Click += OnResetUserRoot;

            Button open = new Button { Text = "Open", AutoSize = true };
            open.Click += (s, e) => OpenFolder(_userRoot, createIfMissing: true);

            buttons.Controls.AddRange(new Control[] { change, reset, open });
            group.Controls.Add(_userRootLabel);
            group.Controls.Add(buttons);
            return group;
        }

        private Control BuildSettingsGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Settings",
                Height = 112,
                Padding = new Padding(10, 6, 10, 6),
            };

            bool shellManaged = TrayApplication.IsShellVerbManagedByPolicy();
            _chkShell = NewSetting(
                "Enable \"Add to trutility\" in Explorer context menu",
                TrayApplication.IsShellVerbEnabled(), shellManaged);

            bool globalStartup = SettingsStartup.IsGlobalStartupEnabled();
            _chkStartup = NewSetting(
                "Run at startup", SettingsStartup.IsStartupEnabled(), globalStartup);

            bool trayPinManaged = SettingsTrayPin.IsManagedByPolicy();
            _chkTrayPin = NewSetting(
                "Pin icon to tray", SettingsTrayPin.IsPinEnabled(), trayPinManaged);

            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            stack.Controls.AddRange(new Control[] { _chkShell, _chkStartup, _chkTrayPin });
            group.Controls.Add(stack);
            return group;
        }

        private static CheckBox NewSetting(string text, bool value, bool managed)
        {
            return new CheckBox
            {
                Text = managed ? text + "   [Managed by Administrator]" : text,
                AutoSize = true,
                Checked = value,
                Enabled = !managed,
            };
        }

        private static Control BuildHelpGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Menu organization",
                Height = 104,
                Padding = new Padding(10, 6, 10, 6),
            };

            group.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text =
                    "Global folders appear first, one block each, then your own below the " +
                    "thicker line.\nNothing merges: a Tools folder in two places stays two " +
                    "separate entries.\n\n" +
                    "Append to a filename:     " +
                    "$^  separator above     $_  separator below     " +
                    "$^_  both sides     $F  expand shortcut as a submenu\n" +
                    "On any menu item:         " +
                    "Ctrl + Click  remove from your folder (Recycle Bin)     " +
                    "Shift + Click  run as Administrator",
            });
            return group;
        }

        private Control BuildButtonRow()
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                Height = 34,
                FlowDirection = FlowDirection.RightToLeft,
            };

            Button ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(80, 26) };
            ok.Click += OnOkClick;

            Button cancel = new Button
            {
                Text = "Cancel",
                AutoSize = true,
                MinimumSize = new Size(80, 26),
                DialogResult = DialogResult.Cancel,
            };

            row.Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
            return row;
        }

        // ── Root status ───────────────────────────────────────────────────────

        /// <summary>
        /// Probes the folders off the UI thread, one task each.
        ///
        /// A root on a share that is down blocks for the length of an SMB
        /// timeout, so probing inline would hang the dialog on open and probing
        /// them in sequence would leave every healthy folder reading "Checking…"
        /// behind the one dead share. Each row settles on its own.
        /// </summary>
        private void ProbeRootStatusesAsync()
        {
            for (int i = 0; i < _globalRoots.Count; i++)
            {
                int index = i;
                string path = _globalRoots[i].Path;
                Task.Run(() => ReportStatus(index, DescribeStatus(path)));
            }
        }

        private void ReportStatus(int index, string status)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated)
                {
                    return;
                }

                BeginInvoke((Action)(() =>
                {
                    if (index < _rootsList.Items.Count)
                    {
                        _rootsList.Items[index].SubItems[StatusColumn].Text = status;
                    }
                }));
            }
            catch (Exception ex)
            {
                // The dialog was closed while the probe was still running.
                Diag.Swallowed("reporting folder status", ex);
            }
        }

        /// <summary>
        /// "Empty" is called out separately from "OK" on purpose: a reachable
        /// folder with nothing in it contributes no menu entries, which is the
        /// usual answer to a department folder that appears to be missing.
        /// </summary>
        private static string DescribeStatus(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return "Unreachable";
                }

                using (IEnumerator<string> entries =
                       Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                {
                    return entries.MoveNext() ? "OK" : "Empty";
                }
            }
            catch (UnauthorizedAccessException)
            {
                return "Access denied";
            }
            catch (Exception ex)
            {
                Diag.Swallowed("checking " + path, ex);
                return "Error";
            }
        }

        private void OpenSelectedRoot()
        {
            if (_rootsList.SelectedItems.Count == 0)
            {
                return;
            }

            // Never created on demand: these belong to the administrator, and an
            // empty folder conjured here would mask a mistyped policy path.
            OpenFolder(_rootsList.SelectedItems[0].Tag as string, createIfMissing: false);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            if (_chkShell.Enabled)
            {
                if (_chkShell.Checked) { TrayApplication.RegisterShellVerb(); TrayApplication.SaveShellPref(true); }
                else { TrayApplication.UnregisterShellVerb(); TrayApplication.SaveShellPref(false); }
            }

            SettingsStartup.SetStartup(_chkStartup.Checked);

            if (_chkTrayPin.Enabled)
                SettingsTrayPin.SaveTrayPin(_chkTrayPin.Checked);

            // Reset means "drop my choice and fall back", not "pin the path the
            // fallback currently resolves to" — writing it back would make the
            // default permanent and survive a later policy change.
            if (UserRootChanged)
            {
                if (_userRootExplicitlyChosen)
                {
                    RootsLib.SetUserRoot(_userRoot);
                }
                else
                {
                    RootsLib.ClearUserRoot();
                }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnChangeUserRoot(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = "Select the folder where trutility will store your personal shortcuts.";
            dlg.SelectedPath = _userRoot;
            dlg.ShowNewFolderButton = true;

            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _userRoot = dlg.SelectedPath;
                _userRootLabel.Text = _userRoot;
                _userRootExplicitlyChosen = true;
                UserRootChanged = true;
            }
        }

        /// Previews the fallback without applying it, so closing the dialog
        /// without pressing OK leaves the current folder alone — the same way
        /// Change… behaves.
        private void OnResetUserRoot(object sender, EventArgs e)
        {
            _userRoot = RootsLib.ResolveDefaultUserRootPath();
            _userRootLabel.Text = _userRoot;
            _userRootExplicitlyChosen = false;
            UserRootChanged = true;
        }

        private static void OpenFolder(string path, bool createIfMissing)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (createIfMissing)
                {
                    Directory.CreateDirectory(path);
                }
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Diag.Swallowed("opening " + path, ex);
                MessageBox.Show($"Could not open:\n{path}\n\n{ex.Message}",
                    "trutility", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Visual helpers
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Thicker, tinted separator that marks the global/user boundary.
    /// </summary>
    internal sealed class SectionDivider : ToolStripSeparator
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int mid = e.ClipRectangle.Height / 2;
            using (var pen = new System.Drawing.Pen(
                       System.Drawing.Color.FromArgb(160, 120, 120, 120), 2f))
            {
                e.Graphics.DrawLine(pen,
                    e.ClipRectangle.Left, mid,
                    e.ClipRectangle.Right, mid);
            }
        }
    }
}