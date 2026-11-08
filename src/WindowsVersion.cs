using System;

using Microsoft.Win32;

namespace trutility
{
    /// <summary>
    /// Which Windows this is, for the parts of trutility that only fit one.
    /// Environment.OSVersion cannot tell: without a manifest listing Windows
    /// 10, .NET Framework reports every newer Windows as 8.
    /// </summary>
    internal static class WindowsVersion
    {
        /// Windows 11 and Server 2025 both start at this build; Server 2022 is 20348.
        private const int FirstWindows11Build = 22000;

        public static readonly bool IsWindows11 = Build() >= FirstWindows11Build;

        private static int Build()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    return RootsLib.ToInt(key?.GetValue("CurrentBuildNumber"), 0);
                }
            }
            catch (Exception ex)
            {
                Diag.Swallowed("reading the Windows build", ex);
                return 0;
            }
        }
    }
}
