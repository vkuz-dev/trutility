using Microsoft.Win32;

namespace trutility.Settings
{
    public sealed class NumberSetting
    {
        public static readonly NumberSetting AutoExpandLimit = new NumberSetting("AutoExpandLimit", 250); //default value

        /// The order of files within a folder, as a MenuSort value. Name by default.
        public static readonly NumberSetting SortBy = new NumberSetting("SortBy", 0);

        /// How long the keyboard has to be quiet before a search runs, in
        /// milliseconds, so a word typed quickly costs one search rather than
        /// one per letter.
        public static readonly NumberSetting SearchDelayMs = new NumberSetting("SearchDelayMs", 1000);

        /// What the extension filter does with the types it lists: 0 hides
        /// them (the default), 1 shows only them.
        public static readonly NumberSetting ExtensionFilterMode = new NumberSetting("ExtensionFilterMode", 0);

        /// Where the hotkey opens the menu, as a MenuPosition value. Bottom right by default.
        public static readonly NumberSetting HotkeyMenuPosition = new NumberSetting("HotkeyMenuPosition", 0);

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
            int number = SettingStore.ToInt(SettingStore.Read(_valueName), 0);
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

            SettingStore.Write(_valueName, value, RegistryValueKind.DWord);
            return true;
        }

        /// True when HKLM carries the value and HKCU has no override yet.
        public bool IsManagedByPolicy() => SettingStore.IsManagedByPolicy(_valueName);
    }
}
