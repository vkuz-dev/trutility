using System;

using Microsoft.Win32;

namespace trutility.Settings
{
    /// <summary>
    /// Whether file extensions appear in the menu. Shortcuts (.lnk, .url) never
    /// show theirs either way; this only decides for everything else.
    ///
    /// On by default, which is how the menu has always looked. The same
    /// HKCU-over-HKLM rule as the other settings: an administrator decides it
    /// for anyone who has not chosen for themselves.
    /// </summary>
    public static class SettingsExtensions
    {
        private const string RegKeyPath = @"SOFTWARE\trutility";
        private const string ValueName = "ShowExtensions";

        public static bool IsShown()
        {
            object val = ReadValue(Registry.CurrentUser) ?? ReadValue(Registry.LocalMachine);
            if (val == null)
            {
                return true;
            }
            return Convert.ToInt32(val) != 0;
        }

        public static void Save(bool show)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(ValueName, show ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        /// True when HKLM carries the value and HKCU has no override yet.
        public static bool IsManagedByPolicy()
        {
            return ReadValue(Registry.CurrentUser) == null
                && ReadValue(Registry.LocalMachine) != null;
        }

        private static object ReadValue(RegistryKey hive)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(RegKeyPath))
                {
                    return key?.GetValue(ValueName);
                }
            }
            catch (Exception ex)
            {
                Diag.Swallowed("reading " + ValueName, ex);
                return null;
            }
        }
    }
}
