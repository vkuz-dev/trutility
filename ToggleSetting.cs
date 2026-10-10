using System;

using Microsoft.Win32;

namespace trutility.Settings
{
    /// <summary>
    /// An on/off preference kept under SOFTWARE\trutility, with the same rule
    /// as the other settings: the user's HKCU value wins, and an HKLM value
    /// decides it for anyone who has not chosen for themselves.
    /// </summary>
    public sealed class ToggleSetting
    {
        /// Whether file extensions appear in the menu. Shortcuts (.lnk, .url)
        /// never show theirs either way. On by default, which is how the menu
        /// has always looked.
        public static readonly ToggleSetting ShowExtensions = new ToggleSetting("ShowExtensions", true);

        /// Whether the menu has a search box at all. On by default. Turned off,
        /// the menu is the plain tree it was before search existed, and the
        /// search index is not built.
        public static readonly ToggleSetting EnableSearch = new ToggleSetting("EnableSearch", true);

        /// <summary>
        /// Whether the search box takes the keyboard when the menu opens. Off by
        /// default: unfocused, the menu behaves like any other - arrows move the
        /// highlight, Enter launches it, a letter jumps to an entry. Focused, all
        /// of that goes to the box instead, which suits someone who always
        /// searches and surprises someone who navigates.
        /// </summary>
        public static readonly ToggleSetting FocusSearch = new ToggleSetting("FocusSearch", false);

        /// <summary>
        /// Whether a shortcut to a folder opens as a submenu without $F in its
        /// name. On by default, up to the size NumberSetting.AutoExpandLimit
        /// allows. Turned off, only shortcuts marked $F or $X expand
        /// </summary>
        public static readonly ToggleSetting ExpandFolderShortcuts = new ToggleSetting("ExpandFolderShortcuts", true);

        private const string RegKeyPath = @"SOFTWARE\trutility";

        private readonly string _valueName;
        private readonly bool _default;

        private ToggleSetting(string valueName, bool defaultValue)
        {
            _valueName = valueName;
            _default = defaultValue;
        }

        public bool IsOn()
        {
            object val = ReadValue(Registry.CurrentUser) ?? ReadValue(Registry.LocalMachine);
            if (val == null)
            {
                return _default;
            }
            return Convert.ToInt32(val) != 0;
        }

        /// Only written when it actually changes: saving the default would pin
        /// it in HKCU and shadow any policy an administrator sets later.
        public bool SaveIfChanged(bool on)
        {
            if (on == IsOn())
            {
                return false;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(_valueName, on ? 1 : 0, RegistryValueKind.DWord);
            }
            return true;
        }

        /// True when HKLM carries the value and HKCU has no override yet.
        public bool IsManagedByPolicy()
        {
            return ReadValue(Registry.CurrentUser) == null
                && ReadValue(Registry.LocalMachine) != null;
        }

        private object ReadValue(RegistryKey hive)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(RegKeyPath))
                {
                    return key?.GetValue(_valueName);
                }
            }
            catch (Exception ex)
            {
                Diag.Swallowed("reading " + _valueName, ex);
                return null;
            }
        }
    }
}
