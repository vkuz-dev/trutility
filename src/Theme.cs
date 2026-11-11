using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using Microsoft.Win32;

using trutility.Settings;

namespace trutility
{
    /// <summary>
    /// How the tray menu looks. Classic leaves WinForms alone; the two Windows
    /// 11 themes swap in Win11Renderer, a taller row and the newer font.
    /// </summary>
    internal sealed class Theme
    {
        /// The setting's values, in the order the dialog lists them. They start
        /// at 1 because a stored 0 means "not set" to NumberSetting, so Classic
        /// could not otherwise be chosen over a Follow system default.
        public const int ClassicId = 1;
        public const int Win11LightId = 2;
        public const int Win11DarkId = 3;
        public const int FollowSystemId = 4;

        /// Follow system where the Windows 11 look matches the rest of Windows,
        /// Classic everywhere else.
        public static int DefaultId => WindowsVersion.IsWindows11 ? FollowSystemId : ClassicId;

        /// Captured before anything replaces it, so Classic can put it back.
        private static readonly ToolStripRenderer ClassicRenderer = ToolStripManager.Renderer;

        public static readonly Theme Classic = new Theme();

        public static readonly Theme Win11Light = new Theme
        {
            Back = Color.FromArgb(249, 249, 249),
            Text = Color.FromArgb(27, 27, 27),
            Disabled = Color.FromArgb(160, 160, 160),
            Hover = Color.FromArgb(236, 236, 236),
            Separator = Color.FromArgb(229, 229, 229),
            Border = Color.FromArgb(213, 213, 213),
            Input = Color.White,
            RowPadding = new Padding(0, 5, 0, 5),
        };

        public static readonly Theme Win11Dark = new Theme
        {
            Back = Color.FromArgb(44, 44, 44),
            Text = Color.White,
            Disabled = Color.FromArgb(122, 122, 122),
            Hover = Color.FromArgb(61, 61, 61),
            Separator = Color.FromArgb(72, 72, 72),
            Border = Color.FromArgb(69, 69, 69),
            Input = Color.FromArgb(32, 32, 32),
            RowPadding = new Padding(0, 5, 0, 5),
        };

        public Color Back, Text, Disabled, Hover, Separator, Border, Input;

        /// The space a Windows 11 menu keeps above its first row and below its last.
        public const int MenuEdge = 4;

        /// Null for Classic, which keeps the menu's own row height.
        public Padding? RowPadding;

        private ToolStripRenderer _renderer;
        private Font _font;

        /// What the menu in front of the user is using right now.
        public static Theme Active { get; private set; } = Classic;

        public ToolStripRenderer Renderer
        {
            get
            {
                if (RowPadding == null) return ClassicRenderer;
                return _renderer ?? (_renderer = new Win11Renderer(this));
            }
        }

        /// Null for Classic, which keeps the menu's own font.
        public Font Font
        {
            get
            {
                if (RowPadding == null) return null;
                if (_font == null)
                {
                    bool variable = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text");
                    // 14px, the body size of Windows 11 menus.
                    _font = new Font(variable ? "Segoe UI Variable Text" : "Segoe UI", 10.5f);
                }
                return _font;
            }
        }

        /// A stored value that is none of the known ones is Classic.
        public static Theme For(int setting, bool systemIsLight)
        {
            switch (setting)
            {
                case Win11LightId: return Win11Light;
                case Win11DarkId: return Win11Dark;
                case FollowSystemId: return systemIsLight ? Win11Light : Win11Dark;
                default: return Classic;
            }
        }

        /// Read each time the menu opens, so following the system needs no
        /// restart. Before Windows 11 the menu is always Classic: the Windows 11
        /// look would be out of place there, whatever a policy asks for.
        public static void Apply()
        {
            Active = WindowsVersion.IsWindows11 ? For(NumberSetting.Theme.Value(), SystemIsLight()) : Classic;
            if (ToolStripManager.Renderer != Active.Renderer)
            {
                ToolStripManager.Renderer = Active.Renderer;
            }
        }

        private static bool SystemIsLight()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                // Missing means an older Windows, which has only the light look.
                return SettingStore.ToInt(key?.GetValue("AppsUseLightTheme"), 1) != 0;
            }
        }
    }
}
