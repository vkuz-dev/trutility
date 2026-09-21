using System;

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
            try
            {
                Type wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType == null) return false;

                dynamic shell = Activator.CreateInstance(wshType);
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string target = shortcut.TargetPath;

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
        }
    }
}
