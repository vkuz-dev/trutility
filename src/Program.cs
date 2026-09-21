using System;
using System.Windows.Forms;

namespace trutility
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // ── Handle the --add <file> argument from the Explorer shell verb ──
            // When the user right-clicks a file and chooses "Add to trutility",
            // Explorer launches:  trutility.exe --add "C:\path\to\file.exe"
            // We create the shortcut and exit — no tray UI needed.
            if (args.Length == 2 &&
                string.Equals(args[0], "--add", StringComparison.OrdinalIgnoreCase))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                TrayApplication.AddFileToUserFolder(args[1]);
                return;
            }

            // ── Normal single-instance tray run ───────────────────────────────
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "trutility_SingleInstance", out bool isNew))
            {
                if (!isNew)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (TrayApplication app = new TrayApplication())
                {
                    Application.Run();
                }
            }
        }
    }
}