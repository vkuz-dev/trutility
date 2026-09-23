using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

using trutility.Settings;

namespace trutility
{
    internal sealed class AboutDialog : Form
    {
        /// Set once OK saves a change that affects what the menu shows.
        public bool MenuNeedsRebuild { get; private set; }

        private bool _userRootChanged;
        private string _userRoot;

        /// True when Change… picked a path, false when Reset asked to fall back.
        /// Only meaningful while _userRootChanged is set.
        private bool _userRootExplicitlyChosen;
        private Label _userRootLabel;
        private CheckBox _chkShell;
        private CheckBox _chkStartup;
        private CheckBox _chkTrayPin;
        private CheckBox _chkExtensions;
        private CheckBox _chkFocusSearch;
        private readonly Action _refreshMenu;

        public AboutDialog(Root userRoot, Action refreshMenu)
        {
            _userRoot = userRoot == null ? RootsLib.ResolveUserRootPath() : userRoot.Path;
            _refreshMenu = refreshMenu;

            Text = "About trutility";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(700, 580);
            MinimumSize = new Size(600, 430);

            Controls.Add(BuildLayout());
        }

        /// <summary>
        /// Everything here is something the user can act on, so every section
        /// sizes to its content and a spacer takes any slack. That keeps the
        /// buttons on the bottom edge when the window is resized.
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
                BuildUserGroup(),
                BuildSettingsGroup(),
                BuildHelpGroup(),
                new Panel { Height = 0 },   // spacer
                BuildButtonRow(),
            };
            Control spacer = rows[4];

            foreach (Control row in rows)
            {
                row.Dock = DockStyle.Fill;
                row.Margin = new Padding(0, 0, 0, row == spacer ? 0 : 10);
                layout.RowStyles.Add(row == spacer
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
                Height = 160,
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

            _chkExtensions = NewSetting(
                "Show file extensions in the menu (shortcuts never show theirs)",
                ToggleSetting.ShowExtensions.IsOn(), ToggleSetting.ShowExtensions.IsManagedByPolicy());

            _chkFocusSearch = NewSetting(
                "Put the cursor in the search box when the menu opens",
                ToggleSetting.FocusSearch.IsOn(), ToggleSetting.FocusSearch.IsManagedByPolicy());

            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            stack.Controls.AddRange(new Control[]
            {
                _chkShell, _chkStartup, _chkTrayPin, _chkExtensions, _chkFocusSearch,
            });
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
                Height = 160,
                Padding = new Padding(10, 6, 10, 6),
            };

            group.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                // Explains the divider the user can see, without a lecture on
                // where the entries above it came from — that is not theirs to
                // manage and knowing would not help them.
                Text =
                    "Shared tools sit above the thicker line, your own below it.\n" +
                    "Folders with the same name stay separate instead of merging.\n\n" +
                    // Each marker on its own line with a plain indent, rather
                    // than columns padded with spaces — those drift apart at a
                    // different DPI or font size.
                    "Append to a name:\n" +
                    "    $^  separator above      $_  separator below      $^_  both sides\n" +
                    "    $F  a shortcut to a folder opens as a submenu, like the folder itself\n" +
                    "    $X  a folder (or shortcut to one) is replaced by its contents\n\n" +
                    "On any menu item:\n" +
                    "    Ctrl + Click  remove from your folder (Recycle Bin)      " +
                    "Shift + Click  run as Administrator",
            });
            return group;
        }

        /// Refresh sits on the left, apart from OK and Cancel: it acts at once
        /// and is not part of saving or discarding the dialog.
        private Control BuildButtonRow()
        {
            FlowLayoutPanel dialogButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
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

            dialogButtons.Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;

            // In a flow panel of its own, like OK and Cancel, so it gets the
            // same size and margins instead of stretching to the row height.
            FlowLayoutPanel toolButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                WrapContents = false,
            };
            toolButtons.Controls.Add(BuildRefreshButton());

            Panel row = new Panel { Height = 34 };
            row.Controls.Add(dialogButtons);
            row.Controls.Add(toolButtons);
            return row;
        }

        /// <summary>
        /// Rescans every folder and reloads every icon. Rarely needed — changes
        /// are picked up on their own — but a share that was offline is only
        /// re-checked every few minutes, and an updated program can keep its old
        /// icon until the cache is cleared.
        /// </summary>
        private Button BuildRefreshButton()
        {
            Button refresh = new Button
            {
                Text = "Refresh menu",
                AutoSize = true,
                MinimumSize = new Size(100, 26),
                Enabled = _refreshMenu != null,
            };

            refresh.Click += (s, e) =>
            {
                _refreshMenu();

                // The rebuild runs in the background and finishes in moments;
                // this is just so the click visibly did something.
                refresh.Text = "Menu refreshed";
                refresh.Enabled = false;
            };
            return refresh;
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

            // Extensions change what the menu shows, so the menu is rebuilt.
            // Focus is read each time the menu opens and needs nothing more.
            if (_chkExtensions.Enabled && ToggleSetting.ShowExtensions.SaveIfChanged(_chkExtensions.Checked))
            {
                MenuNeedsRebuild = true;
            }
            if (_chkFocusSearch.Enabled)
            {
                ToggleSetting.FocusSearch.SaveIfChanged(_chkFocusSearch.Checked);
            }

            // Reset means "drop my choice and fall back", not "pin the path the
            // fallback currently resolves to" — writing it back would make the
            // default permanent and survive a later policy change.
            if (_userRootChanged)
            {
                MenuNeedsRebuild = true;
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
                _userRootChanged = true;
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
            _userRootChanged = true;
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
