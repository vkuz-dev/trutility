using System.Windows.Forms;

namespace trutility
{
    /// <summary>
    /// A box that records a key combination instead of text: press the keys
    /// and it shows them. Backspace or Delete on their own clear it, which
    /// means no hotkey.
    /// </summary>
    internal sealed class HotkeyBox : TextBox
    {
        private Keys _keys;

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
        }

        public Keys Keys
        {
            get => _keys;
            set
            {
                _keys = value;
                Text = value == Keys.None ? "None" : GlobalHotkey.Format(value);
            }
        }

        // Alt combinations arrive as system keys and would otherwise go to the
        // dialog's mnemonics, so everything is taken here before that happens.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            bool bare = (keyData & Keys.Modifiers) == Keys.None;

            // Tab, Enter and Escape still move focus, accept and cancel.
            if (bare && (code == Keys.Tab || code == Keys.Enter || code == Keys.Escape))
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (bare && (code == Keys.Back || code == Keys.Delete))
            {
                Keys = Keys.None;
            }
            else if (GlobalHotkey.IsUsable(keyData))
            {
                Keys = keyData;
            }
            return true;
        }
    }
}
