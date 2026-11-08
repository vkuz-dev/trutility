using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace trutility
{
    /// <summary>
    /// Draws menus the way Windows 11 does: flat, with a rounded highlight
    /// inside the row and rounded window corners. The corners are the system's
    /// to draw, so on Windows 10 they stay square.
    /// </summary>
    internal sealed class Win11Renderer : ToolStripProfessionalRenderer
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;
        private const int HighlightRadius = 4;

        /// How far icons and text sit in from where WinForms puts them, so they
        /// clear the rounded highlight. Theme.RowPadding leaves room for it.
        internal const int ContentIndent = 6;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private readonly Theme _theme;

        /// The handle and theme each menu was last styled for. A menu's window can
        /// be recreated, which loses the attributes, so the handle is compared.
        /// Shared by both themes: light then dark on one window must restyle it.
        private static readonly ConditionalWeakTable<ToolStrip, StrongBox<Tuple<IntPtr, Theme>>> Styled =
            new ConditionalWeakTable<ToolStrip, StrongBox<Tuple<IntPtr, Theme>>>();

        public Win11Renderer(Theme theme)
        {
            _theme = theme;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(_theme.Back))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
            foreach (ToolStripItem item in e.ToolStrip.Items)
            {
                if (item is ToolStripTextBox box && box.Visible)
                {
                    DrawInputFrame(e.Graphics, e.ToolStrip, box);
                }
            }
            RoundWindow(e.ToolStrip);
        }

        /// <summary>
        /// The box itself is borderless (see MenuSearchBox.ApplyTheme); the
        /// rounded field around it is drawn here, as wide as a row highlight,
        /// with a magnifier where the entries have their icons.
        /// </summary>
        private void DrawInputFrame(Graphics g, ToolStrip strip, ToolStripTextBox box)
        {
            // The same edges as a row highlight. DisplayRectangle would not do:
            // on a menu it starts after the icon column.
            Rectangle r = Rectangle.FromLTRB(4, box.Bounds.Top - InputInset,
                                             strip.Width - 5, box.Bounds.Bottom + InputInset - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(r, HighlightRadius))
            using (SolidBrush fill = new SolidBrush(_theme.Input))
            using (Pen pen = new Pen(_theme.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            Rectangle glyph = Rectangle.FromLTRB(r.Left, r.Top, box.Bounds.Left, r.Bottom);
            TextRenderer.DrawText(g, "", GlyphFont, glyph, _theme.Disabled,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        /// Windows 11's icon font, or the Windows 10 one it replaced; both have
        /// the magnifier at U+E721.
        private static readonly Font GlyphFont = new Font(
            FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 10f);

        /// The gap between the search text and the rounded field drawn around it.
        internal const int InputInset = 6;

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen pen = new Pen(_theme.Border))
            {
                Rectangle r = new Rectangle(Point.Empty, e.ToolStrip.Size);
                e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // The classic gutter behind the icons has no place in Windows 11.
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripItem item = e.Item;
            if (!item.Enabled || !(item.Selected || item.Pressed)) return;

            // Inset from the menu's edges, not the item's: an item runs closer
            // to the right edge than the left, so its own bounds gave the
            // highlight a gap on one side only.
            int left = -item.Bounds.Left;
            int width = item.Owner == null ? item.Width : item.Owner.Width;
            // 5 on the right: the anti-aliased edge spills one pixel past it.
            Rectangle r = Rectangle.FromLTRB(left + 4, 1, left + width - 5, item.Height - 1);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(r, HighlightRadius))
            using (SolidBrush brush = new SolidBrush(_theme.Hover))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int mid = e.Item.Height / 2;
            using (Pen pen = new Pen(_theme.Separator))
            {
                e.Graphics.DrawLine(pen, 4, mid, e.Item.Width - 5, mid);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _theme.Text : _theme.Disabled;
            if (e.Item.IsOnDropDown)
            {
                // The whole row, so the text is centred on the same line as the icon.
                e.TextRectangle = new Rectangle(e.TextRectangle.X + ContentIndent,
                                                TextNudge(e.Graphics, e.TextFont),
                                                e.TextRectangle.Width, e.Item.Height);
                e.TextFormat |= TextFormatFlags.VerticalCenter;
            }
            base.OnRenderItemText(e);
        }

        /// <summary>
        /// GDI centres a font's whole line box, but the eye centres from the
        /// tops of the capitals to the baseline. Segoe UI Variable keeps a lot
        /// of space above its capitals, so centred text sat a few pixels high
        /// next to its icon. This is how far down that band has to move.
        /// </summary>
        private static int TextNudge(Graphics g, Font font)
        {
            const float CapHeight = 0.7f;   // of the em, close enough for Segoe UI and its Variable cut
            FontFamily family = font.FontFamily;
            float em = font.SizeInPoints * g.DpiY / 72f;
            float unit = em / family.GetEmHeight(font.Style);
            float ascent = family.GetCellAscent(font.Style) * unit;
            float descent = family.GetCellDescent(font.Style) * unit;
            return (int)Math.Round((descent - ascent) / 2f + CapHeight * em / 2f);
        }

        /// <summary>
        /// The menu places icons from the top of the row, so the extra height
        /// of a Windows 11 row all ended up below them. Centred here instead,
        /// and moved in from the highlight's edge along with the text.
        /// </summary>
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (e.Item.IsOnDropDown)
            {
                r.X += ContentIndent;
                r.Y = (e.Item.Height - r.Height) / 2;
            }
            base.OnRenderItemImage(new ToolStripItemImageRenderEventArgs(e.Graphics, e.Item, e.Image, r));
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? _theme.Text : _theme.Disabled;
            base.OnRenderArrow(e);
        }

        private void RoundWindow(ToolStrip strip)
        {
            if (!(strip is ToolStripDropDown) || !strip.IsHandleCreated) return;

            StrongBox<Tuple<IntPtr, Theme>> done = Styled.GetValue(strip, s => new StrongBox<Tuple<IntPtr, Theme>>());
            if (done.Value != null && done.Value.Item1 == strip.Handle && done.Value.Item2 == _theme) return;
            done.Value = Tuple.Create(strip.Handle, _theme);

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(strip.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int border = ColorTranslator.ToWin32(_theme.Border);
            DwmSetWindowAttribute(strip.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
