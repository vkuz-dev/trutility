using System;
using System.Runtime.InteropServices;

namespace trutility
{
    internal static class ShortcutLib
    {
        /// <summary>
        /// Resolves the target path of an existing .lnk shortcut via the
        /// WScript.Shell COM object (the same mechanism used to create
        /// shortcuts in TrayApplication.AddFileToUserFolder).
        /// Returns false on any failure (missing file, broken COM, empty target).
        /// </summary>
        public static bool TryResolveTarget(string lnkPath, out string targetPath)
        {
            targetPath = null;
            object shell = null;
            object shortcut = null;
            try
            {
                Type wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType == null) return false;

                shell = Activator.CreateInstance(wshType);
                shortcut = ((dynamic)shell).CreateShortcut(lnkPath);
                string target = ((dynamic)shortcut).TargetPath;

                if (string.IsNullOrWhiteSpace(target)) return false;

                targetPath = target;
                return true;
            }
            catch (Exception ex)
            {
                // A broken or dangling shortcut costs that one menu entry, not
                // the scan it was found in.
                Diag.Swallowed("resolving shortcut " + lnkPath, ex);
                return false;
            }
            finally
            {
                // Released now rather than left to the finalizer: every scan
                // resolves every shortcut in the tree.
                Release(shortcut);
                Release(shell);
            }
        }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                Marshal.ReleaseComObject(com);
            }
        }
    }
}
