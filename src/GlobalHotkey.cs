using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace trutility
{
    internal sealed class GlobalHotkey : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 1;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        // Holding the keys down fires once instead of opening the menu over and over.
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private static readonly KeysConverter Converter = new KeysConverter();

        private bool _registered;

        public event EventHandler Pressed;

        public GlobalHotkey()
        {
            // A message-only window: it receives WM_HOTKEY and is never shown.
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        /// <summary>
        /// Replaces whatever combination was registered. Keys.None just clears
        /// it. False when another program already owns the combination, which
        /// Windows gives to whoever asked first.
        /// </summary>
        public bool Register(Keys keys)
        {
            Unregister();
            if (keys == Keys.None)
            {
                return true;
            }

            _registered = RegisterHotKey(Handle, HotkeyId, ToModifiers(keys) | MOD_NOREPEAT, (uint)(keys & Keys.KeyCode));
            if (!_registered)
            {
                Diag.Swallowed("registering hotkey " + Format(keys), new Win32Exception(Marshal.GetLastWin32Error()));
            }
            return _registered;
        }

        public void Unregister()
        {
            if (!_registered)
            {
                return;
            }
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        /// <summary>
        /// Reads a stored combination such as "Ctrl+Alt+S". Empty, unreadable
        /// or without Ctrl or Alt all mean none: a plain letter or Shift+letter
        /// registered system-wide would stop it being typed anywhere.
        /// </summary>
        public static Keys Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Keys.None;
            }

            try
            {
                Keys keys = (Keys)Converter.ConvertFromInvariantString(text.Trim());
                return IsUsable(keys) ? keys : Keys.None;
            }
            catch (Exception ex)
            {
                Diag.Swallowed("reading hotkey \"" + text + "\"", ex);
                return Keys.None;
            }
        }

        public static string Format(Keys keys)
        {
            return keys == Keys.None ? "" : Converter.ConvertToInvariantString(keys);
        }

        /// A key that is not itself a modifier, held with Ctrl or Alt.
        public static bool IsUsable(Keys keys)
        {
            Keys code = keys & Keys.KeyCode;
            bool isModifierKey = code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu
                || code == Keys.LWin || code == Keys.RWin;
            bool hasModifier = (keys & (Keys.Control | Keys.Alt)) != 0;
            return code != Keys.None && !isModifierKey && hasModifier;
        }

        private static uint ToModifiers(Keys keys)
        {
            uint mods = 0;
            if ((keys & Keys.Alt) != 0) mods |= MOD_ALT;
            if ((keys & Keys.Control) != 0) mods |= MOD_CONTROL;
            if ((keys & Keys.Shift) != 0) mods |= MOD_SHIFT;
            return mods;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
