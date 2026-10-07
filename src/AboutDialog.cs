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
        private CheckBox _chkEnableSearch;
        private CheckBox _chkFocusSearch;
        private bool _focusSearchManaged;
        private readonly Action _refreshMenu;

        public AboutDialog(Root userRoot, Action refreshMenu)
        {
            _userRoot = userRoot == null ? RootsLib.ResolveUserRootPath() : userRoot.Path;
            _refreshMenu = refreshMenu;

            Text = "About trutility";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            // The window is exactly as big as what is in it. Fixed sizes had to
            // be raised by hand every time a setting or a line of help was
            // added, and clipped the dialog whenever one was missed; they also
            // ignored display scaling, which sizing to content follows for free.
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            Controls.Add(BuildLayout());
        }

        /// <summary>
        /// One column of sections, each as tall as its contents. The column is
        /// as wide as the widest section, and every section stretches to it so
        /// their edges line up.
        /// </summary>
        private TableLayoutPanel BuildLayout()
        {
            Control[] rows =
            {
                BuildHeader(),
                BuildUserGroup(),
                BuildSettingsGroup(),
                BuildHelpGroup(),
                BuildButtonRow(),
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = rows.Length,
                Padding = new Padding(12),
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            for (int i = 0; i < rows.Length; i++)
            {
                Control row = rows[i];
                row.Dock = DockStyle.Fill;
                row.Margin = new Padding(0, 0, 0, i == rows.Length - 1 ? 0 : 10);
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(row, 0, i);
            }

            return layout;
        }

        /// A titled section that grows to fit what is put in it.
        private static GroupBox NewGroup(string title, Control content)
        {
            GroupBox group = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
            };
            content.Dock = DockStyle.Fill;
            group.Controls.Add(content);
            return group;
        }

        // ── Sections ──────────────────────────────────────────────────────────

        private static Control BuildHeader()
        {
            Version ver = Assembly.GetExecutingAssembly().GetName().Version;
            return new Label
            {
                Text =
                    $"trutility - Tray Utility v{ver.Major}.{ver.Minor}.{ver.Build}\n" +
                    "Keep your favourite files, tools or folders within reach.\n\n" +
                    "Author: Vladimirs Kuznecovs   ·   " +
                    "GitHub: https://github.com/vkuz-dev/trutility",
                AutoSize = true,
            };
        }

        private Control BuildUserGroup()
        {
            bool managed = TrayApplication.IsUserRootManagedByPolicy();

            // The path is the one thing that must not set the width: a deep
            // folder would stretch the whole dialog. It asks for 1px, fills
            // whatever width the other sections settle on, and trails off
            // with "…" when it does not fit.
            _userRootLabel = new Label
            {
                Text = _userRoot,
                AutoSize = false,
                AutoEllipsis = true,
                Width = 1,
                Height = 20,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 8f),
            };

            TableLayoutPanel content = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            content.Controls.Add(_userRootLabel, 0, 0);
            content.Controls.Add(BuildUserButtons(managed), 0, 1);

            // Policy may decide where this folder lives. What goes in it is the
            // user's alone, so nothing here is ever disabled by content.
            return NewGroup(
                managed ? "Your folder - location set by your administrator" : "Your folder",
                content);
        }

        /// Change… and Reset follow the location policy; Open never does, because
        /// reaching your own folder is not something policy takes away.
        private FlowLayoutPanel BuildUserButtons(bool managed)
        {
            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
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
                "Show file extensions",
                ToggleSetting.ShowExtensions.IsOn(), ToggleSetting.ShowExtensions.IsManagedByPolicy());

            _chkEnableSearch = NewSetting(
                "Enable search box",
                ToggleSetting.EnableSearch.IsOn(), ToggleSetting.EnableSearch.IsManagedByPolicy());

            _focusSearchManaged = ToggleSetting.FocusSearch.IsManagedByPolicy();
            _chkFocusSearch = NewSetting(
                "Focus search box by default",
                ToggleSetting.FocusSearch.IsOn(), _focusSearchManaged);

            // With no search box there is nothing to focus, so the option is
            // greyed out for as long as search is unticked.
            _chkEnableSearch.CheckedChanged += (s, e) => UpdateFocusSearchAvailability();
            UpdateFocusSearchAvailability();

            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            stack.Controls.AddRange(new Control[]
            {
                _chkShell, _chkStartup, _chkTrayPin, _chkExtensions, _chkEnableSearch, _chkFocusSearch,
            });
            return NewGroup("Settings", stack);
        }

        private void UpdateFocusSearchAvailability()
        {
            _chkFocusSearch.Enabled = !_focusSearchManaged && _chkEnableSearch.Checked;
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
            Label help = new Label
            {
                AutoSize = true,
                Text =
                    "Tools are grouped into Shared (at the top) and Personal (at the bottom).\n\n" +
                    "Append to a name:\n" +
                    "    $^  separator above      $_  separator below      $^_  both sides\n" +
                    "    $F  a shortcut to a folder opens as a submenu, like the folder itself\n" +
                    "    $X  a folder (or shortcut to one) is replaced by its contents, aka - unpack here\n\n" +
                    "    Example: reports$F$^.lnk  →  a folder named \"reports\" shown as a submenu with a separator above.\n\n" +
                    "    File actions:\n" +
                    "    Ctrl + Click  remove from your folder (Recycle Bin)\n" +
                    "    Shift + Click  run as Administrator\n" +
                    "    Middle Click  open the folder containing the file in Explorer\n",
            };
            return NewGroup("Menu organization", help);
        }

        private Control BuildButtonRow()
        {
            FlowLayoutPanel dialogButtons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = Padding.Empty,
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

            TableLayoutPanel row = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Button refresh = BuildRefreshButton();
            refresh.Anchor = AnchorStyles.Left;
            row.Controls.Add(refresh, 0, 0);
            row.Controls.Add(dialogButtons, 1, 0);
            return row;
        }

        /// <summary>
        /// Rescans every folder and reloads every icon. Rarely needed - changes
        /// are picked up on their own - but a share that was offline is only
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

            // Extensions and search change what the menu shows, so the menu is
            // rebuilt. Focus is read each time the menu opens and needs nothing
            // more.
            if (_chkExtensions.Enabled && ToggleSetting.ShowExtensions.SaveIfChanged(_chkExtensions.Checked))
            {
                MenuNeedsRebuild = true;
            }
            if (_chkEnableSearch.Enabled && ToggleSetting.EnableSearch.SaveIfChanged(_chkEnableSearch.Checked))
            {
                MenuNeedsRebuild = true;
            }

            // Saved even while greyed out by search being off: that only means
            // there is nothing to focus right now, and the choice should still
            // be there when search is turned back on. Only policy prevents it.
            if (!_focusSearchManaged)
            {
                ToggleSetting.FocusSearch.SaveIfChanged(_chkFocusSearch.Checked);
            }

            // Reset means "drop my choice and fall back", not "pin the path the
            // fallback currently resolves to" - writing it back would make the
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
        /// without pressing OK leaves the current folder alone - the same way
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
