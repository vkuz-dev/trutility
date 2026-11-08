using System;
using System.Drawing;
using System.Windows.Forms;

using trutility.Settings;

namespace trutility
{
    /// <summary>
    /// Shown once, on the first run: where the user's folder is, and how to
    /// start trutility with Windows and keep its icon visible. The package
    /// cannot do either of the last two itself, so it links to Windows
    /// Settings where the unpackaged build offers a checkbox.
    /// </summary>
    internal sealed class WelcomeDialog : Form
    {
        /// Set when Finish saved a different folder, which needs a menu rebuild.
        public bool UserRootChanged { get; private set; }

        /// Set when the user asked to carry on in the Settings dialog.
        public bool OpenSettings { get; private set; }

        private string _userRoot;
        private bool _userRootPicked;
        private Label _userRootLabel;
        private CheckBox _chkStartup;
        private CheckBox _chkTrayPin;
        private CheckBox _chkShell;

        public WelcomeDialog()
        {
            _userRoot = RootsLib.ResolveUserRootPath();

            Text = "Welcome to trutility";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            TableLayoutPanel layout = SettingsDialog.NewColumn(
                BuildIntro(), BuildFolderGroup(), BuildStartupGroup(), BuildTrayGroup(), BuildShellGroup(), BuildButtons());
            layout.Padding = new Padding(12);
            Controls.Add(layout);
        }

        private static Label NewText(string text) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 6) };

        private static LinkLabel NewSettingsLink(string text, string uri)
        {
            LinkLabel link = new LinkLabel { Text = text, AutoSize = true };
            link.LinkClicked += (s, e) => SettingsDialog.OpenLink(uri);
            return link;
        }

        private static FlowLayoutPanel NewStack(params Control[] controls)
        {
            FlowLayoutPanel stack = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            stack.Controls.AddRange(controls);
            return stack;
        }

        private static Control BuildIntro()
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
            };
            row.Controls.AddRange(new Control[]
            {
                SettingsDialog.BuildLogo(),
                NewText(
                    "Your files, folders and programs, one click away.\n\n" +
                    "trutility adds a menu next to the clock that mirrors a folder on your PC. " +
                    "Each subfolder becomes a submenu, and each file or shortcut becomes a menu item.\n\n" +
                    "A few things to set up before you start:"),
            });
            return row;
        }

        private Control BuildFolderGroup()
        {
            bool managed = RootsLib.IsUserRootManagedByPolicy();

            _userRootLabel = new Label
            {
                Text = _userRoot,
                AutoSize = false,
                AutoEllipsis = true,
                Width = 460,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 8f),
            };

            Button change = new Button { Text = "Change", AutoSize = true, Visible = !managed };
            change.Click += (s, e) =>
            {
                string picked = SettingsDialog.PickUserRoot(this, _userRoot);
                if (picked == null) return;
                _userRoot = picked;
                _userRootLabel.Text = picked;
                _userRootPicked = true;
            };

            Button open = new Button { Text = "Open", AutoSize = true };
            open.Click += (s, e) => SettingsDialog.OpenFolder(_userRoot, createIfMissing: true);

            FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { change, open });

            return SettingsDialog.NewGroup(
                managed ? "1. Your folder - location set by your administrator" : "1. Your folder",
                NewStack(
                    NewText("Put files, shortcuts and folders here, and they show up in the menu right away."),
                    _userRootLabel,
                    buttons));
        }

        private Control BuildStartupGroup()
        {
            if (PackageInfo.IsPackaged)
            {
                return SettingsDialog.NewGroup("2. Start with Windows", NewStack(
                    NewText("Switch trutility on in Windows Settings › Apps › Startup."),
                    NewSettingsLink("Open Startup apps", "ms-settings:startupapps")));
            }

            _chkStartup = SettingsDialog.NewSetting(
                "Run at startup", ShortcutSetting.Startup.IsEnabled(), ShortcutSetting.Startup.IsGlobal());
            return SettingsDialog.NewGroup("2. Start with Windows", NewStack(_chkStartup));
        }

        private Control BuildTrayGroup()
        {
            const string where = "Windows hides new tray icons behind the ^ arrow next to the clock. ";
            if (PackageInfo.IsPackaged)
            {
                return SettingsDialog.NewGroup("3. Keep the icon visible", NewStack(
                    NewText(where + "Drag the trutility icon from there onto the taskbar, or switch it on in " +
                            "Settings › Personalization › Taskbar › Other system tray icons."),
                    NewSettingsLink("Open Taskbar settings", "ms-settings:taskbar")));
            }

            _chkTrayPin = SettingsDialog.NewSetting(
                "Pin icon to tray", ToggleSetting.PinToTray.IsOn(), ToggleSetting.PinToTray.IsManagedByPolicy());
            return SettingsDialog.NewGroup("3. Keep the icon visible", NewStack(NewText(where), _chkTrayPin));
        }

        private Control BuildShellGroup()
        {
            _chkShell = SettingsDialog.NewSetting(
                "Enable \"Add to trutility\" in Explorer context menu",
                TrayApplication.IsShellVerbEnabled(), ToggleSetting.AddToShell.IsManagedByPolicy());
            return SettingsDialog.NewGroup("4. Add to context menu", NewStack(
                NewText("Right-click a file or folder in Explorer and choose \"Add to trutility\" " +
                        "to put a shortcut to it in your folder."),
                _chkShell));
        }

        private Control BuildButtons()
        {
            Button settings = new Button { Text = "Go to settings", AutoSize = true, MinimumSize = new Size(100, 26) };
            settings.Click += (s, e) => Finish(openSettings: true);

            Button finish = new Button { Text = "Finish", AutoSize = true, MinimumSize = new Size(80, 26) };
            finish.Click += (s, e) => Finish(openSettings: false);
            AcceptButton = finish;

            FlowLayoutPanel row = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
            };
            row.Controls.AddRange(new Control[] { settings, finish });
            return row;
        }

        private void Finish(bool openSettings)
        {
            Apply();
            OpenSettings = openSettings;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// Only Finish saves; closing the window keeps every default.
        private void Apply()
        {
            if (_userRootPicked && !string.Equals(_userRoot, RootsLib.ResolveUserRootPath(), StringComparison.OrdinalIgnoreCase))
            {
                RootsLib.SetUserRoot(_userRoot);
                UserRootChanged = true;
            }

            if (_chkStartup != null && _chkStartup.Enabled)
            {
                SettingsDialog.ApplyShortcutSetting(ShortcutSetting.Startup, _chkStartup.Checked, "the Startup shortcut");
            }

            if (_chkShell.Enabled)
            {
                if (_chkShell.Checked) TrayApplication.RegisterShellVerb();
                else TrayApplication.UnregisterShellVerb();
                ToggleSetting.AddToShell.SaveIfChanged(_chkShell.Checked);
            }

            if (_chkTrayPin != null && _chkTrayPin.Enabled)
            {
                SettingsTrayPin.SaveTrayPin(_chkTrayPin.Checked);
            }
        }
    }
}
