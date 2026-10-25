using System;

using Microsoft.Win32;

namespace trutility.Settings
{
    /// <summary>
    /// The one rule every preference under SOFTWARE\trutility follows: the
    /// user's HKCU value wins, and an HKLM value decides it for anyone who has
    /// not chosen for themselves.
    /// </summary>
    internal static class SettingStore
    {
        private const string RegKeyPath = @"SOFTWARE\trutility";

        public static object Read(string valueName)
        {
            return ReadFrom(Registry.CurrentUser, valueName) ?? ReadFrom(Registry.LocalMachine, valueName);
        }

        /// True when HKLM carries the value and HKCU has no override yet.
        public static bool IsManagedByPolicy(string valueName)
        {
            return ReadFrom(Registry.CurrentUser, valueName) == null
                && ReadFrom(Registry.LocalMachine, valueName) != null;
        }

        public static void Write(string valueName, object value, RegistryValueKind kind)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(valueName, value, kind);
            }
        }

        /// <summary>
        /// A value of the wrong type falls back rather than throwing. These are
        /// read at logon and on every menu open, so a policy typed as REG_SZ
        /// "true" would otherwise crash trutility for everyone it reaches.
        /// </summary>
        public static int ToInt(object value, int fallback)
        {
            return value == null ? fallback : RootsLib.ToInt(value, fallback);
        }

        private static object ReadFrom(RegistryKey hive, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(RegKeyPath))
                {
                    return key?.GetValue(valueName);
                }
            }
            catch (Exception ex)
            {
                Diag.Swallowed("reading " + valueName, ex);
                return null;
            }
        }
    }
}
