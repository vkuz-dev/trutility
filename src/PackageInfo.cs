using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace trutility
{
    /// <summary>
    /// Whether trutility runs from an MSIX package. A packaged exe lives under
    /// WindowsApps in a folder named after its version, so a shortcut or verb
    /// pointing at it breaks on the next update; the execution alias declared
    /// in packaging\AppxManifest.xml stays at the same path.
    /// </summary>
    internal static class PackageInfo
    {
        private const int AppModelErrorNoPackage = 15700;

        public static readonly bool IsPackaged = DetectPackaged();

        /// The exe that shortcuts and the Explorer verb should start.
        public static string LaunchPath => IsPackaged
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\trutility.exe")
            : Process.GetCurrentProcess().MainModule.FileName;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref int length, StringBuilder fullName);

        private static bool DetectPackaged()
        {
            try
            {
                int length = 0;
                return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
            }
            catch (EntryPointNotFoundException)
            {
                return false;   // Windows 7 has no packages
            }
        }
    }
}
