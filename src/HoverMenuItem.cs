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

        /// <summary>
        /// WinForms drops its own open-on-hover timer when the cursor goes
        /// straight from a folder into a folder of the submenu it opened: the
        /// late mouse-leave from the menu behind cancels it. The folder stayed
        /// highlighted but needed a click. Hover fires once the cursor rests.
        /// </summary>
        protected override void OnMouseHover(EventArgs e)
        {
            base.OnMouseHover(e);
            if (Enabled && Selected && HasDropDownItems && !DropDown.Visible)
            {
                ShowDropDown();
            }
        }
    }
}
