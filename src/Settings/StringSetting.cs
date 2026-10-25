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

        /// The key combination that opens the menu, e.g. "Ctrl+Alt+S", in the form GlobalHotkey.Parse reads. Empty means no hotkey.
        public static readonly StringSetting Hotkey = new StringSetting("Hotkey", "Ctrl+Alt+S");

        private readonly string _valueName;
        private readonly string _default;

        private StringSetting(string valueName, string defaultValue)
        {
            _valueName = valueName;
            _default = defaultValue;
        }

        public string Value()
        {
            object val = SettingStore.Read(_valueName);
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

            SettingStore.Write(_valueName, value, RegistryValueKind.String);
            return true;
        }

        /// True when HKLM carries the value and HKCU has no override yet.
        public bool IsManagedByPolicy() => SettingStore.IsManagedByPolicy(_valueName);
    }
}
