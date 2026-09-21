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
                return roots.Count > 0 ? roots[0].Path : RootsLib.BuiltInGlobalPath;
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
        private FileSystemWatcher _globalWatcher;
        private FileSystemWatcher _userWatcher;

        // ── Debounce ──────────────────────────────────────────────────────────
        private CancellationTokenSource _debounceCts = new CancellationTokenSource();
        private readonly object _debounceLock = new object();
        private const int DebounceMs = 400;

        // ── Folder access-error tracking ────────────────────────────────────────
        private HashSet<string> _warnedAccessErrorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Watchers for followed shortcut-to-folder targets ──────────────────
        private readonly Dictionary<string, FileSystemWatcher> _followedWatchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);

        // ─────────────────────────────────────────────────────────────────────
        public TrayApplication()
        {
            _invokeHelper = new Control();
            _invokeHelper.CreateControl();

            EnsureFoldersExist();
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

            BuildMenuAsync();
            StartFileWatchers();
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

        private static void EnsureFoldersExist()
        {
            try { Directory.CreateDirectory(GlobalRoot); } catch { }
            try { Directory.CreateDirectory(UserRoot); } catch { }
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

                    var tree = MenuBuilder.BuildTree(GlobalRoot, UserRoot, out var accessErrors, out var followedFolders);

                    _invokeHelper.BeginInvoke((Action)(() =>
                    {
                        ApplyNewMenu(tree);
                        ReportAccessErrors(accessErrors);
                        SyncFollowedWatchers(followedFolders);
                    }));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _invokeHelper.BeginInvoke((Action)(() => ShowBalloon("trutility error", ex.Message))); }
            }, token);
        }

        private void ReportAccessErrors(List<string> accessErrors)
        {
            var current = new HashSet<string>(accessErrors, StringComparer.OrdinalIgnoreCase);
            foreach (var path in current)
            {
                if (!_warnedAccessErrorPaths.Contains(path))
                    ShowBalloon("trutility – folder inaccessible", $"Could not read:\n{path}");
            }
            _warnedAccessErrorPaths = current;
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
                    items.Add(new SectionDivider());
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
            using (var dlg = new AboutDialog(GlobalRoot, UserRoot))
            {
                dlg.ShowDialog();
                // If the user changed UserRoot, restart the watchers and rebuild
                if (dlg.UserRootChanged)
                {
                    EnsureFoldersExist();
                    RestartUserWatcher();
                    BuildMenuAsync();
                }
            }
        }

        // ── FileSystemWatcher ─────────────────────────────────────────────────

        private void StartFileWatchers()
        {
            _globalWatcher = CreateWatcher(GlobalRoot);
            _userWatcher = CreateWatcher(UserRoot);
        }

        private void RestartUserWatcher()
        {
            _userWatcher?.Dispose();
            _userWatcher = CreateWatcher(UserRoot);
        }

        private FileSystemWatcher CreateWatcher(string path)
        {
            if (!Directory.Exists(path)) return null;

            var w = new FileSystemWatcher(path)
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
            return w;
        }

        private void OnFolderChanged(object sender, FileSystemEventArgs e) => BuildMenuAsync();

        /// <summary>
        /// Keeps one FileSystemWatcher per currently-followed shortcut target
        /// folder, so edits there rebuild the menu live, just like a real
        /// subfolder under Global/User root.
        /// </summary>
        private void SyncFollowedWatchers(List<string> followedFolders)
        {
            var current = new HashSet<string>(followedFolders, StringComparer.OrdinalIgnoreCase);

            foreach (var stalePath in _followedWatchers.Keys.Except(current).ToList())
            {
                _followedWatchers[stalePath].Dispose();
                _followedWatchers.Remove(stalePath);
            }

            foreach (var path in current)
            {
                if (_followedWatchers.ContainsKey(path)) continue;
                var watcher = CreateWatcher(path);
                if (watcher != null)
                    _followedWatchers[path] = watcher;
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
            _globalWatcher?.Dispose();
            _userWatcher?.Dispose();
            foreach (var watcher in _followedWatchers.Values)
                watcher.Dispose();
            _followedWatchers.Clear();
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

        private readonly string _globalRoot;
        private string _userRoot;
        private Label _userRootLabel;
        private CheckBox _chkShell;
        private CheckBox _chkStartup;
        private CheckBox _chkTrayPin;

        public AboutDialog(string globalRoot, string userRoot)
        {
            _globalRoot = globalRoot;
            _userRoot = userRoot;

            var ver = Assembly.GetExecutingAssembly().GetName().Version;

            Text = "About trutility";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 550);

            // ── Info label ────────────────────────────────────────────────────
            var lbl = new Label
            {
                Text =
                    $"trutility - Tray Utility v{ver.Major}.{ver.Minor}.{ver.Build}\n\n" +
                    $"Keep your favorite files, tools or folders within reach.\n\n" +
                    $"Author: Vladimirs Kuznecovs\n" +
                    $"Email: vladimir.kuznetsov0706@gmail.com\n" +
                    $"GitHub: https://github.com/vkuz-dev/trutility\n\n" +

                    $"=======================================\n\n" +

                    $"Put your tools into these locations:\n" +
                    $"  Global tools(visible for everyone): {globalRoot}\n" +
                    $"  User tools(visible only to you): {userRoot}\n\n" +
                    $"Menu Organization:\n" +
                    $"Append these suffixes to a filename to insert line separators:\n" +
                    $"  `$^`   -  adds separator ABOVE\n" +
                    $"  `$_`   -  adds separator BELOW\n" +
                    $"  `$^_`  -  adds separator on BOTH SIDES\n" +
                    $"  `$F`   -  follows the shortcut and expands it into a submenu\n" +
                    $"  Example: \"trutility$^_.exe\" - separators on BOTH sides\n\n" +
                    $"Mouse shortcuts (on any tray menu item):\n" +
                    $"  Ctrl  + Click  - remove item from User folder (sent to Recycle Bin)\n" +
                    $"  Shift + Click - run as Administrator\n\n",
                AutoSize = false,
                Size = new Size(520, 310),
                Location = new Point(10, 10),
            };

            // ── Settings checkboxes (grouped) ─────────────────────────────────
            var grpSettings = new GroupBox
            {
                Text = "Settings",
                Size = new Size(500, 100),
                Location = new Point(10, 320),
            };

            bool shellManaged = TrayApplication.IsShellVerbManagedByPolicy();
            _chkShell = new CheckBox
            {
                Text = shellManaged
                               ? "Enable \"Add to trutility\" in Explorer context menu [Managed by Administrator]"
                               : "Enable \"Add to trutility\" in Explorer context menu",
                AutoSize = true,
                Location = new Point(10, 20),
                Checked = TrayApplication.IsShellVerbEnabled(),
                Enabled = !shellManaged,
            };

            bool globalStartup = SettingsStartup.IsGlobalStartupEnabled();
            _chkStartup = new CheckBox
            {
                Text = globalStartup
                               ? "Run at startup [Managed by Administrator]"
                               : "Run at startup",
                AutoSize = true,
                Location = new Point(10, 44),
                Checked = SettingsStartup.IsStartupEnabled(),
                Enabled = !globalStartup,
            };

            bool trayPinManaged = SettingsTrayPin.IsManagedByPolicy();
            _chkTrayPin = new CheckBox
            {
                Text = trayPinManaged
                               ? "Pin icon to tray [Managed by Administrator]"
                               : "Pin icon to tray",
                AutoSize = true,
                Location = new Point(10, 68),
                Checked = SettingsTrayPin.IsPinEnabled(),
                Enabled = !trayPinManaged,
            };

            grpSettings.Controls.AddRange(new Control[] { _chkShell, _chkStartup, _chkTrayPin });

            // ── User folder location ──────────────────────────────────────────
            bool userRootManaged = TrayApplication.IsUserRootManagedByPolicy();
            var grpUser = new GroupBox
            {
                Text = userRootManaged
                               ? "User folder location [Managed by Administrator]"
                               : "User folder location",
                Size = new Size(500, 80),
                Location = new Point(10, 420),
            };

            _userRootLabel = new Label
            {
                Text = _userRoot,
                AutoSize = false,
                Size = new Size(480, 20),
                Location = new Point(10, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 8f),
            };

            var btnChange = new Button
            {
                Text = "Change…",
                Size = new Size(80, 24),
                Location = new Point(10, 48),
                Enabled = !userRootManaged,
            };
            btnChange.Click += OnChangeUserRoot;

            var btnReset = new Button
            {
                Text = "Reset",
                Size = new Size(60, 24),
                Location = new Point(96, 48),
                Enabled = !userRootManaged,
            };
            btnReset.Click += OnResetUserRoot;

            grpUser.Controls.AddRange(new Control[] { _userRootLabel, btnChange, btnReset });

            // ── Bottom buttons ────────────────────────────────────────────────
            var btnGlobal = new Button
            {
                Text = "Open Global folder",
                Size = new Size(140, 28),
                Location = new Point(10, 510),
            };
            btnGlobal.Click += (s, e) => OpenFolder(_globalRoot);

            var btnUser = new Button
            {
                Text = "Open User folder",
                Size = new Size(130, 28),
                Location = new Point(158, 510),
            };
            btnUser.Click += (s, e) => OpenFolder(_userRoot);

            var btnOk = new Button
            {
                Text = "OK",
                Size = new Size(70, 28),
                Location = new Point(440, 510),
            };
            btnOk.Click += OnOkClick;

            AcceptButton = btnOk;
            Controls.AddRange(new Control[] { lbl, grpSettings, grpUser, btnGlobal, btnUser, btnOk });
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

            if (UserRootChanged)
                TrayApplication.SetUserRoot(_userRoot);

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
                UserRootChanged = true;
            }
        }

        private void OnResetUserRoot(object sender, EventArgs e)
        {
            RootsLib.ClearUserRoot();

            _userRoot = TrayApplication.UserRoot;   // re-read default
            _userRootLabel.Text = _userRoot;
            UserRootChanged = true;
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            catch { }
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