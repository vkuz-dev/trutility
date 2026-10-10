using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

using trutility.Settings;

namespace trutility
{
    internal sealed class SettingsDialog : Form
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
        private CheckBox _chkDesktop;
        private CheckBox _chkTrayPin;
        private CheckBox _chkExtensions;
        private CheckBox _chkExpand;
        private NumericUpDown _numExpandLimit;
        private bool _expandLimitManaged;
        private RadioButton[] _sortOptions;
        private CheckBox _chkReverseSort;
        private CheckBox _chkFoldersFirst;
        private CheckBox _chkEnableSearch;
        private CheckBox _chkFocusSearch;
        private NumericUpDown _numSearchDelay;
        private bool _searchDelayManaged;
        private bool _focusSearchManaged;
        private CheckBox _chkConfirmFiles;
        private CheckBox _chkConfirmFolders;
        private bool _confirmFilesManaged;
        private bool _confirmFoldersManaged;
        private readonly Action _refreshMenu;
        private readonly Action _rebuildMenu;

        public SettingsDialog(Root userRoot, Action refreshMenu, Action rebuildMenu)
        {
            _userRoot = userRoot == null ? RootsLib.ResolveUserRootPath() : userRoot.Path;
            _refreshMenu = refreshMenu;
            _rebuildMenu = rebuildMenu;

            Version ver = Assembly.GetExecutingAssembly().GetName().Version;
            Text = $"Settings | trutility v{ver.Major}.{ver.Minor}.{ver.Build}";
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

        private TableLayoutPanel BuildLayout()
        {
            TableLayoutPanel layout = NewColumn(BuildTabs(), BuildButtonRow());
            layout.Padding = new Padding(12);
            return layout;
        }

        private TabControl BuildTabs()
        {
            TabControl tabs = new TabControl();
            Size largest = Size.Empty;

            void AddPage(string title, params Control[] rows)
            {

                TableLayoutPanel column = NewColumn(rows);
                column.Padding = new Padding(10);
                column.Dock = DockStyle.Top;

                Size wanted = column.GetPreferredSize(Size.Empty);
                largest = new Size(
                    Math.Max(largest.Width, wanted.Width),
                    Math.Max(largest.Height, wanted.Height));

                TabPage page = new TabPage(title) { UseVisualStyleBackColor = true };
                page.Controls.Add(column);
                tabs.TabPages.Add(page);
            }

            AddPage("General", BuildUserGroup(), BuildSettingsGroup());
            AddPage("Advanced", BuildAdvancedSettings(), BuildSortingGroup());
            AddPage("About", BuildHeader(), BuildHelpGroup());

            // What the tab strip and the border take up around a page.
            Size chrome = tabs.Size - tabs.DisplayRectangle.Size;
            tabs.Size = largest + chrome;
            return tabs;
        }

        /// <summary>
        /// One column of sections, each as tall as its contents. The column is
        /// as wide as the widest section, and every section stretches to it so
        /// their edges line up.
        /// </summary>
        private static TableLayoutPanel NewColumn(params Control[] rows)
        {
            TableLayoutPanel layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = rows.Length,
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

        private const string GitHubUrl = "https://github.com/vkuz-dev/trutility";

        private static Control BuildHeader()
        {
            Label about = new Label
            {
                Text =
                    "Your files, folders and programs, one click away.\n\n" +
                    "trutility is a menu that lives next to the clock. Click its icon and pick what you want to open,\n" +
                    "with no digging through folders or the Start menu.\n\n" +
                    "To add something, create a shortcut(or a file) - in Your folder on the General tab. Turn on\n" +
                    "\"Add to trutility\" there to add things with a right-click in Explorer.\n\n" +
                    "Author: Vladimir Kuznetsov",
                AutoSize = true,
                Margin = Padding.Empty,
            };

            LinkLabel link = new LinkLabel
            {
                Text = GitHubUrl,
                AutoSize = true,
                Margin = Padding.Empty,
            };
            link.LinkClicked += (s, e) => OpenLink(GitHubUrl);

            FlowLayoutPanel header = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            header.Controls.AddRange(new Control[] { about, link });
            return header;
        }

        private static void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Diag.Swallowed("opening " + url, ex);
            }
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

            Button change = new Button { Text = "Change", AutoSize = true, Enabled = !managed };
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

            _chkStartup = NewSetting(
                "Run at startup", ShortcutSetting.Startup.IsEnabled(), ShortcutSetting.Startup.IsGlobal());

            _chkDesktop = NewSetting(
                "Create Desktop shortcut", ShortcutSetting.Desktop.IsEnabled(), ShortcutSetting.Desktop.IsGlobal());

            bool trayPinManaged = SettingsTrayPin.IsManagedByPolicy();
            _chkTrayPin = NewSetting(
                "Pin icon to tray", SettingsTrayPin.IsPinEnabled(), trayPinManaged);

            _chkEnableSearch = NewSetting(
                "Enable search box",
                ToggleSetting.EnableSearch.IsOn(), ToggleSetting.EnableSearch.IsManagedByPolicy());

            _focusSearchManaged = ToggleSetting.FocusSearch.IsManagedByPolicy();
            _chkFocusSearch = NewSetting(
                "Focus search box by default",
                ToggleSetting.FocusSearch.IsOn(), _focusSearchManaged);

            _searchDelayManaged = NumberSetting.SearchDelayMs.IsManagedByPolicy();
            _numSearchDelay = NewNumberBox(NumberSetting.SearchDelayMs, 10000); // max value
            Control delayRow = NewNumberRow(_numSearchDelay, "Search delay after typing, in milliseconds");

            _chkEnableSearch.CheckedChanged += (s, e) => UpdateSearchAvailability();
            UpdateSearchAvailability();

            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            stack.Controls.AddRange(new Control[]
            {
                _chkShell, _chkStartup, _chkDesktop, _chkTrayPin, _chkEnableSearch, _chkFocusSearch, delayRow,
            });
            return NewGroup("Settings", stack);
        }

        private Control BuildAdvancedSettings()
        {
            _chkExtensions = NewSetting(
                "Show file extensions",
                ToggleSetting.ShowExtensions.IsOn(), ToggleSetting.ShowExtensions.IsManagedByPolicy());

            _chkExpand = NewSetting(
                "Automatically expand folder shortcuts",
                ToggleSetting.ExpandFolderShortcuts.IsOn(), ToggleSetting.ExpandFolderShortcuts.IsManagedByPolicy());

            _expandLimitManaged = NumberSetting.AutoExpandLimit.IsManagedByPolicy();
            _numExpandLimit = NewNumberBox(NumberSetting.AutoExpandLimit, 100000); //max value
            Control limitRow = NewNumberRow(_numExpandLimit, "Auto-expand folders up to this many items ($F ignores this)");

            // The limit only means something while shortcuts expand on their own.
            _chkExpand.CheckedChanged += (s, e) => UpdateExpandLimitAvailability();
            UpdateExpandLimitAvailability();

            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            stack.Controls.AddRange(new Control[] { _chkExtensions, _chkExpand, limitRow });
            AddConfirmSettings(stack);
            return stack;
        }

        private void AddConfirmSettings(FlowLayoutPanel stack)
        {
            _confirmFilesManaged = ToggleSetting.ConfirmDeleteFiles.IsManagedByPolicy();
            _chkConfirmFiles = NewSetting(
                "Ask for confirmation when deleting files",
                ToggleSetting.ConfirmDeleteFiles.IsOn(), _confirmFilesManaged);

            _confirmFoldersManaged = ToggleSetting.ConfirmDeleteFolders.IsManagedByPolicy();
            _chkConfirmFolders = NewSetting(
                "Ask for confirmation when deleting folders",
                ToggleSetting.ConfirmDeleteFolders.IsOn(), _confirmFoldersManaged);

            stack.Controls.AddRange(new Control[] { _chkConfirmFiles, _chkConfirmFolders });
        }

        private Control BuildSortingGroup()
        {
            // In MenuSort order, so a button's position is the value it stands for.
            _sortOptions = new[]
            {
                new RadioButton { Text = "Sort by name (A to Z)", AutoSize = true },
                new RadioButton { Text = "Sort by date (newest first)", AutoSize = true },
                new RadioButton { Text = "Sort by file extension", AutoSize = true },
            };

            // A stored value that is none of the three is shown as name, which
            // is also how the menu treats it.
            int current = NumberSetting.SortBy.Value();
            _sortOptions[current < _sortOptions.Length ? current : 0].Checked = true;

            // The three choices are managed as one, so they share a panel that
            // is greyed out whole. The checkboxes under them each follow their
            // own policy and sit outside it.
            FlowLayoutPanel choices = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = Padding.Empty,
                Enabled = !NumberSetting.SortBy.IsManagedByPolicy(),
            };
            choices.Controls.AddRange(_sortOptions);

            _chkReverseSort = NewSetting(
                "Reverse order",
                ToggleSetting.ReverseSort.IsOn(), ToggleSetting.ReverseSort.IsManagedByPolicy());

            _chkFoldersFirst = NewSetting(
                "Folders before files",
                ToggleSetting.FoldersFirst.IsOn(), ToggleSetting.FoldersFirst.IsManagedByPolicy());

            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            stack.Controls.AddRange(new Control[] { choices, _chkReverseSort, _chkFoldersFirst });
            return NewGroup(
                choices.Enabled ? "Sorting" : "Sorting   [Managed by Administrator]", stack);
        }

        /// A box for a numeric setting, starting at its current value.
        private static NumericUpDown NewNumberBox(NumberSetting setting, int maximum)
        {
            NumericUpDown box = new NumericUpDown
            {
                Minimum = 0,
                Maximum = maximum,
                Width = 50,
                Anchor = AnchorStyles.Left,
            };
            box.Value = Math.Min(box.Maximum, setting.Value());

            // Shown as soon as the box is left, so what OK will save is never
            // a surprise.
            box.Leave += (s, e) =>
            {
                int number = NumberOrDefault(box, setting);
                box.Value = number;
                box.Text = number.ToString();
            };
            return box;
        }

        /// A number box with its caption to the right of it.
        private static Control NewNumberRow(NumericUpDown box, string caption)
        {
            // Anchored to neither top nor bottom, which is what centres it on
            // the box beside it.
            Label label = new Label
            {
                Text = caption,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            };

            FlowLayoutPanel row = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty,
            };
            row.Controls.AddRange(new Control[] { box, label });
            return row;
        }

        /// Treat zero or empty as "not set" and fall back to the default
        private static int NumberOrDefault(NumericUpDown box, NumberSetting setting)
        {
            bool unset = box.Text.Trim().Length == 0 || box.Value == 0;
            return unset ? setting.Default : (int)box.Value;
        }

        private void UpdateExpandLimitAvailability()
        {
            _numExpandLimit.Enabled = !_expandLimitManaged && _chkExpand.Checked;
        }

        /// With no search box there is nothing to focus and nothing to delay,
        /// so both options are greyed out for as long as search is unticked.
        private void UpdateSearchAvailability()
        {
            _chkFocusSearch.Enabled = !_focusSearchManaged && _chkEnableSearch.Checked;
            _numSearchDelay.Enabled = !_searchDelayManaged && _chkEnableSearch.Checked;
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
                    "    $F  a shortcut to a folder opens as a submenu even above the item limit (Advanced tab)\n" +
                    "    $X  a folder (or shortcut to one) is replaced by its contents, aka - unpack here\n\n" +
                    "    Example: reports$F$^.lnk  →  a folder named \"reports\" shown as an expandable submenu\n" + 
                    "    with a separator above.\n\n" +
                    "    File actions:\n" +
                    "    Ctrl + Click  remove from your folder (Recycle Bin)\n" +
                    "    Shift + Click  run as Administrator\n" +
                    "    Middle Click  open the folder location in Explorer\n",
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
                FlowDirection = FlowDirection.LeftToRight,
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

            // Saves like OK but leaves the dialog open, to try one combination
            // of settings after another against the real menu.
            Button apply = new Button { Text = "Apply", AutoSize = true, MinimumSize = new Size(80, 26) };
            apply.Click += OnApplyClick;

            dialogButtons.Controls.AddRange(new Control[] { ok, cancel, apply });
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
            ApplySettings();
            DialogResult = DialogResult.OK;
            Close();
        }

        /// The dialog stays open, so the rebuild that OK leaves to whoever
        /// opened it has to be asked for here.
        private void OnApplyClick(object sender, EventArgs e)
        {
            ApplySettings();
            if (MenuNeedsRebuild)
            {
                _rebuildMenu?.Invoke();
                MenuNeedsRebuild = false;
            }
        }

        private void ApplySettings()
        {
            if (_chkShell.Enabled)
            {
                if (_chkShell.Checked) { TrayApplication.RegisterShellVerb(); TrayApplication.SaveShellPref(true); }
                else { TrayApplication.UnregisterShellVerb(); TrayApplication.SaveShellPref(false); }
            }

            ShortcutSetting.Startup.Set(_chkStartup.Checked);
            ShortcutSetting.Desktop.Set(_chkDesktop.Checked);

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

            if (_chkExpand.Enabled && ToggleSetting.ExpandFolderShortcuts.SaveIfChanged(_chkExpand.Checked))
            {
                MenuNeedsRebuild = true;
            }

            // The limit is saved while greyed out too, for the same reason as
            // the focus choice below: it should still be there when expanding
            // is turned back on.
            if (!_expandLimitManaged && NumberSetting.AutoExpandLimit.SaveIfChanged(NumberOrDefault(_numExpandLimit, NumberSetting.AutoExpandLimit)))
            {
                MenuNeedsRebuild = true;
            }

            if (!NumberSetting.SortBy.IsManagedByPolicy()
                && NumberSetting.SortBy.SaveIfChanged(Array.FindIndex(_sortOptions, o => o.Checked)))
            {
                MenuNeedsRebuild = true;
            }
            if (_chkReverseSort.Enabled && ToggleSetting.ReverseSort.SaveIfChanged(_chkReverseSort.Checked))
            {
                MenuNeedsRebuild = true;
            }
            if (_chkFoldersFirst.Enabled && ToggleSetting.FoldersFirst.SaveIfChanged(_chkFoldersFirst.Checked))
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

            // Read at the moment of deleting, so no rebuild.
            if (!_confirmFilesManaged)
            {
                ToggleSetting.ConfirmDeleteFiles.SaveIfChanged(_chkConfirmFiles.Checked);
            }
            if (!_confirmFoldersManaged)
            {
                ToggleSetting.ConfirmDeleteFolders.SaveIfChanged(_chkConfirmFolders.Checked);
            }

            // The same goes for the delay, which is also read as the menu opens.
            if (!_searchDelayManaged)
            {
                NumberSetting.SearchDelayMs.SaveIfChanged(NumberOrDefault(_numSearchDelay, NumberSetting.SearchDelayMs));
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

                // Applied once: a later Apply or OK has nothing new to save.
                _userRootChanged = false;
            }
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
