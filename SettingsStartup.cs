using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace trutility.Settings
{
    public static class SettingsStartup
    {
        // ── Folder helpers ────────────────────────────────────────────────────

        private static string UserStartupFolder =>
            Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        private static string GlobalStartupFolder =>
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

        private static string AppProcessPath =>
            Process.GetCurrentProcess().MainModule.FileName;

        private static string AppBaseName =>
            Path.GetFileNameWithoutExtension(AppProcessPath);

        // ── Public API ────────────────────────────────────────────────────────

        public static bool IsStartupEnabled()
        {
            return FindExistingShortcut(UserStartupFolder) != null
                || FindExistingShortcut(GlobalStartupFolder) != null;
        }

        public static bool IsGlobalStartupEnabled()
        {
            return FindExistingShortcut(GlobalStartupFolder) != null;
        }

        public static void SetStartup(bool enable)
        {
            if (IsGlobalStartupEnabled())
            {
                RemoveShortcutsIn(UserStartupFolder);
                return;
            }

            if (enable)
            {
                RemoveShortcutsIn(UserStartupFolder);   // clean up stale entries
                string lnkPath = Path.Combine(UserStartupFolder, AppBaseName + ".lnk");
                CreateShortcut(lnkPath, AppProcessPath);
            }
            else
            {
                RemoveShortcutsIn(UserStartupFolder);
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        // Returns the path of the first matching .lnk
        private static string FindExistingShortcut(string folder)
        {
            if (!Directory.Exists(folder)) return null;

            foreach (string file in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                string baseName = Path.GetFileNameWithoutExtension(file);

                if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    baseName = baseName.Substring(0, baseName.Length - ".exe".Length);

                if (string.Equals(baseName, AppBaseName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

        private static void RemoveShortcutsIn(string folder)
        {
            string found = FindExistingShortcut(folder);
            if (found != null && File.Exists(found))
                File.Delete(found);
        }

        // Creates a Windows Shell shortcut .lnk
        private static void CreateShortcut(string shortcutPath, string targetPath)
        {
            object sc = null;
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);

            try
            {
                sc = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, shell,
                    new object[] { shortcutPath });

                Type scType = sc.GetType();

                scType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty,
                    null, sc, new object[] { targetPath });

                scType.InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty,
                    null, sc, new object[] { Path.GetDirectoryName(targetPath) });

                scType.InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, sc, null);
            }
            finally
            {
                if (sc != null && Marshal.IsComObject(sc))
                    Marshal.ReleaseComObject(sc);
                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.ReleaseComObject(shell);
            }
        }
    }
}
