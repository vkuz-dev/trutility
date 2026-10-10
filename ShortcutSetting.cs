using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace trutility.Settings
{
    /// <summary>
    /// A shortcut to trutility in one of the user's shell folders. The
    /// shortcut being there is the setting - nothing is kept in the registry.
    /// The same shortcut in the matching all-users folder is the
    /// administrator's, and decides it for everyone on the machine.
    /// </summary>
    public sealed class ShortcutSetting
    {
        public static readonly ShortcutSetting Startup = new ShortcutSetting(
            Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup);

        private readonly Environment.SpecialFolder _userFolder;
        private readonly Environment.SpecialFolder _globalFolder;

        private ShortcutSetting(Environment.SpecialFolder userFolder, Environment.SpecialFolder globalFolder)
        {
            _userFolder = userFolder;
            _globalFolder = globalFolder;
        }

        // ── Folder helpers ────────────────────────────────────────────────────

        private string UserFolder => Environment.GetFolderPath(_userFolder);

        private string GlobalFolder => Environment.GetFolderPath(_globalFolder);

        private static string AppProcessPath =>
            Process.GetCurrentProcess().MainModule.FileName;

        private static string AppBaseName =>
            Path.GetFileNameWithoutExtension(AppProcessPath);

        // ── Public API ────────────────────────────────────────────────────────

        public bool IsEnabled()
        {
            return FindExistingShortcut(UserFolder) != null
                || FindExistingShortcut(GlobalFolder) != null;
        }

        /// True when the administrator has put the shortcut in the all-users
        /// folder, which the user can neither remove nor needs to duplicate.
        public bool IsGlobal()
        {
            return FindExistingShortcut(GlobalFolder) != null;
        }

        public void Set(bool enable)
        {
            if (IsGlobal())
            {
                RemoveShortcutsIn(UserFolder);
                return;
            }

            if (enable)
            {
                RemoveShortcutsIn(UserFolder);   // clean up stale entries
                string lnkPath = Path.Combine(UserFolder, AppBaseName + ".lnk");
                CreateShortcut(lnkPath, AppProcessPath);
            }
            else
            {
                RemoveShortcutsIn(UserFolder);
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
