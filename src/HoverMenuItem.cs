using System;
using System.Windows.Forms;

namespace trutility
{
    /// <summary>
    /// A menu item that still highlights under the mouse while the search box
    /// has the keyboard.
    ///
    /// WinForms stops hot-tracking a menu as soon as a control inside it holds
    /// focus, so with the cursor in the search box the top level showed no
    /// highlight and its folders opened only on a click. Submenus were never
    /// affected: each is its own window, with nothing focused inside it.
    /// </summary>
    internal sealed class HoverMenuItem : ToolStripMenuItem
    {
        public HoverMenuItem(string text) : base(text)
        {
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            // Before base: it only starts the timer that opens a submenu on
            // hover if the item is already selected.
            if (Enabled)
            {
                Select();
            }
            base.OnMouseEnter(e);
        }
    }
}
