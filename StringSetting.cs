using System;

using Microsoft.Win32;

namespace trutility.Settings
{
    /// <summary>
    /// A text preference kept under SOFTWARE\trutility, with the same rule as
    /// the other settings: the user's HKCU value wins, and an HKLM value
    /// decides it for anyone who has not chosen for themselves.
    /// </summary>
    public sealed class StringSetting
    {
        /// The file types the extension filter lists, e.g. ".png;.jpg". Empty by
        /// default, which means no filter at all.
        public static readonly StringSetting ExtensionFilter = new StringSetting("ExtensionFilter", "");

        private const string RegKeyPath = @"SOFTWARE\trutility";

        private readonly string _valueName;
        private readonly string _default;

        private StringSetting(string valueName, string defaultValue)
        {
            _valueName = valueName;
            _default = defaultValue;
        }

        public string Value()
        {
            object val = ReadValue(Registry.CurrentUser) ?? ReadValue(Registry.LocalMachine);
            return val == null ? _default : val.ToString();
        }

        /// Only written when it actually changes: saving the default would pin
        /// it in HKCU and shadow any policy an administrator sets later.
        public bool SaveIfChanged(string value)
        {
            if (value == Value())
            {
                return false;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(_valueName, value, RegistryValueKind.String);
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
