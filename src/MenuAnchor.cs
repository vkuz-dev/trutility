using System;
using System.Drawing;

namespace trutility
{
    /// <summary>
    /// The corner of an open menu that should stay where it is when the menu
    /// changes size — the one it was opened from.
    ///
    /// Windows opens a tray menu above and to the left of the cursor, so with
    /// the taskbar at the bottom that corner is the bottom-right one. With the
    /// taskbar at the top or on a side the menu opens the other way, and the
    /// nearest corner to the cursor is still the right one to keep.
    /// </summary>
    internal sealed class MenuAnchor
    {
        public Point Corner { get; private set; }
        public bool KeepsBottom { get; private set; }
        public bool KeepsRight { get; private set; }

        /// Takes the corner nearest the cursor at the moment the menu opened.
        public static MenuAnchor Detect(Rectangle menu, Point cursor)
        {
            bool bottom = Math.Abs(cursor.Y - menu.Bottom) < Math.Abs(cursor.Y - menu.Top);
            bool right = Math.Abs(cursor.X - menu.Right) < Math.Abs(cursor.X - menu.Left);

            return new MenuAnchor
            {
                KeepsBottom = bottom,
                KeepsRight = right,
                Corner = new Point(right ? menu.Right : menu.Left, bottom ? menu.Bottom : menu.Top),
            };
        }

        /// <summary>
        /// Where a menu of the given size goes so its kept corner stays put,
        /// pulled back onto the screen if it would not fit.
        ///
        /// Constrained to the whole screen rather than the working area,
        /// because a tray menu is placed that way: its bottom sits on the
        /// taskbar. Using the working area would lift it by the height of the
        /// taskbar on the first change.
        /// </summary>
        public Point PlaceMenu(Size size, Rectangle screen)
        {
            int x = KeepsRight ? Corner.X - size.Width : Corner.X;
            int y = KeepsBottom ? Corner.Y - size.Height : Corner.Y;

            x = Math.Max(screen.Left, Math.Min(x, screen.Right - size.Width));
            y = Math.Max(screen.Top, Math.Min(y, screen.Bottom - size.Height));
            return new Point(x, y);
        }
    }
}
