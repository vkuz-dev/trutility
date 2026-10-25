using System;
using System.IO;
using System.Reflection;

using Microsoft.Win32;

namespace trutility.Settings
{
    public static class SettingsTrayPin
    {
        private const string NotifyIconRegPath = @"Control Panel\NotifyIconSettings";

        // ── Public API ────────────────────────────────────────────────────────

        /// The preference lives in ToggleSetting.PinToTray; this only pushes
        /// it into the live NotifyIconSettings entries.
        public static void SaveTrayPin(bool enable)
        {
            ToggleSetting.PinToTray.SaveIfChanged(enable);
            ApplyToNotifyIconSettings(enable ? 1 : 0);
        }

        public static void ApplyTrayPin()
        {
            // Always apply the effective state: 1 if pinned, 0 if not
            ApplyToNotifyIconSettings(ToggleSetting.PinToTray.IsOn() ? 1 : 0);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static void ApplyToNotifyIconSettings(int promoted)
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            string exeFile = Path.GetFileName(exePath).ToLowerInvariant();

            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(
                    NotifyIconRegPath, writable: true))
                {
                    if (root == null) return;   // key won't exist until the app has been run at least once

                    foreach (string subName in root.GetSubKeyNames())
                    {
                        using (RegistryKey sub = root.OpenSubKey(subName, writable: true))
                        {
                            if (sub == null) continue;

                            string regExePath = sub.GetValue("ExecutablePath") as string;
                            if (string.IsNullOrEmpty(regExePath)) continue;

                            if (string.Equals(
                                    Path.GetFileName(regExePath),
                                    exeFile,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                sub.SetValue("IsPromoted", promoted, RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // NotifyIconSettings is always HKCU so this should not happen,
                // and a tray icon that failed to pin is not worth taking the
                // application down for.
                Diag.Swallowed("applying the tray pin state", ex);
            }
        }
    }
}
