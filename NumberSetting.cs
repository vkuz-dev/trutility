using System;

using Microsoft.Win32;

namespace trutility.Settings
{
    public sealed class NumberSetting
    {
        public static readonly NumberSetting AutoExpandLimit = new NumberSetting("AutoExpandLimit", 250); //default value

        /// The order of files within a folder, as a MenuSort value. Name by default.
        public static readonly NumberSetting SortBy = new NumberSetting("SortBy", 0);

        private const string RegKeyPath = @"SOFTWARE\trutility";

        private readonly string _valueName;
        private readonly int _default;

        private NumberSetting(string valueName, int defaultValue)
        {
            _valueName = valueName;
            _default = defaultValue;
        }

        public int Default => _default;

        /// A stored zero or less counts as not set, the same as in the dialog.
        public int Value()
        {
            object val = ReadValue(Registry.CurrentUser) ?? ReadValue(Registry.LocalMachine);
            int number = val == null ? 0 : Convert.ToInt32(val);
            return number > 0 ? number : _default;
        }

        /// Only written when it actually changes: saving the default would pin
        /// it in HKCU and shadow any policy an administrator sets later.
        public bool SaveIfChanged(int value)
        {
            if (value == Value())
            {
                return false;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
            {
                key?.SetValue(_valueName, value, RegistryValueKind.DWord);
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
