using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

using trutility.Settings;

namespace trutility
{
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

            Controls.Add(BuildLayout());
        }

        /// <summary>
        /// The folder probe starts here rather than in the constructor.
        ///
        /// Results are delivered with BeginInvoke, which needs a window handle,
        /// and the handle does not exist until the form is shown. Started from
        /// the constructor it was a race: once the thread pool is warm the probe
        /// finishes first, finds no handle to post to, and the column stays on
        /// "Checking..." for as long as the dialog is open. Losing that race was
        /// the normal case in a process that had been running for a while.
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ProbeRootStatusesAsync();
        }

        /// <summary>
        /// A single column that grows with the window. The folder list is the
        /// only row given the slack, because it is the one whose length policy
        /// decides rather than this layout.
        /// </summary>
        private TableLayoutPanel BuildLayout()
        {
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
            Control stretched = rows[1];

            foreach (Control row in rows)
            {
                row.Dock = DockStyle.Fill;
                row.Margin = new Padding(0, 0, 0, 10);
                layout.RowStyles.Add(row == stretched
                    ? new RowStyle(SizeType.Percent, 100f)
                    : new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(row);
            }

            return layout;
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

            _rootsList = BuildRootsList();

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

        private ListView BuildRootsList()
        {
            ListView list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                UseCompatibleStateImageBehavior = false,
            };
            list.Columns.Add("Name", 100);
            list.Columns.Add("Shown as", 100);
            list.Columns.Add("Location", 210);
            list.Columns.Add("Configured in", 210);
            list.Columns.Add("Status", 90);

            foreach (Root root in _globalRoots)
            {
                list.Items.Add(NewRootRow(root));
            }

            list.SelectedIndexChanged += (s, e) =>
                _btnOpenSelected.Enabled = list.SelectedItems.Count > 0;
            list.DoubleClick += (s, e) => OpenSelectedRoot();
            return list;
        }

        /// "Shown as" is where Label becomes visible: a labelled root reads as
        /// its label, an unlabelled one splices in at the top of the menu.
        private static ListViewItem NewRootRow(Root root)
        {
            return new ListViewItem(new[]
            {
                root.Name,
                string.IsNullOrWhiteSpace(root.Label) ? "(top level)" : root.Label,
                root.Path,
                root.Source ?? "",
                StatusChecking,
            })
            { Tag = root.Path };
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

            group.Controls.Add(_userRootLabel);
            group.Controls.Add(BuildUserButtons(managed));
            return group;
        }

        /// Change… and Reset follow the location policy; Open never does, because
        /// reaching your own folder is not something policy takes away.
        private FlowLayoutPanel BuildUserButtons(bool managed)
        {
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
            return buttons;
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

                // A dedicated thread rather than the pool. These probes can block
                // for the length of an SMB timeout, which is what the pool is
                // worst at: it would hold a worker hostage and make the healthy
                // roots wait behind its slow thread injection.
                Task.Factory.StartNew(
                    () => ReportStatus(index, DescribeStatus(path)),
                    System.Threading.CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
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
            using FolderBrowserDialog dlg = new FolderBrowserDialog();
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
}
