using System.Windows.Forms;

namespace trutility
{
    /// <summary>
    /// Thicker, tinted separator that marks the global/user boundary.
    /// </summary>
    internal sealed class SectionDivider : ToolStripSeparator
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int mid = e.ClipRectangle.Height / 2;
            using (var pen = new System.Drawing.Pen(
                       System.Drawing.Color.FromArgb(160, 120, 120, 120), 2f))
            {
                e.Graphics.DrawLine(pen,
                    e.ClipRectangle.Left, mid,
                    e.ClipRectangle.Right, mid);
            }
        }
    }
}
