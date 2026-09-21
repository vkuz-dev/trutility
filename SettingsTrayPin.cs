using System;
using System.IO;
using System.Reflection;

using Microsoft.Win32;

namespace trutility.Settings
{
    public static class SettingsTrayPin
    {
        private const string NotifyIconRegPath = @"Control Panel\NotifyIconSettings";
        private const string RegKeyPath = @"SOFTWARE\trutility";
        private const string PinValueName = "PinToTray";

        // ── Public API ────────────────────────────────────────────────────────
        public static bool IsPinEnabled()
        {
            // HKCU
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
            {
                object val = key?.GetValue(PinValueName);
                if (val != null)
                    return Convert.ToInt32(val) != 0;
            }

            // HKLM 
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegKeyPath))
            {
                object val = key?.GetValue(PinValueName);
                if (val != null)
                    return Convert.ToInt32(val) != 0;
            }

            return false;
        }

        public static void SaveTrayPin(bool enable)
        {
            // Persist user preference
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKeyPath))
                key?.SetValue(PinValueName, enable ? 1 : 0, RegistryValueKind.DWord);

            // Apply to the live NotifyIconSettings entries
            ApplyToNotifyIconSettings(enable ? 1 : 0);
        }

        /// <summary>
        /// Returns true when HKLM carries a PinToTray value but HKCU has no explicit
        /// override yet — i.e. the effective state is driven by policy, not by the user.
        /// Writing to HKCU via SaveTrayPin() will shadow the HKLM value.
        /// </summary>
        public static bool IsManagedByPolicy()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKeyPath))
                if (key?.GetValue(PinValueName) != null) return false;

            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegKeyPath))
                return key?.GetValue(PinValueName) != null;
        }

        public static void ApplyTrayPin()
        {
            // Always apply the effective state: 1 if pinned, 0 if not
            ApplyToNotifyIconSettings(IsPinEnabled() ? 1 : 0);
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
