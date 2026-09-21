using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace trutility
{
    internal static class IconsLib
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001; // 16x16
        private const uint SHGFI_LARGEICON = 0x000000000; // 32x32
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Returns a 16x16 bitmap of the shell icon for the given file path.
        /// Falls back to the large icon (resized) if no small icon is available.
        /// Returns null if no icon can be retrieved.
        /// </summary>
        public static Bitmap GetIcon(string filePath)
        {
            // First attempt: ask for the native 16x16 small icon
            Bitmap bmp = FetchIcon(filePath, SHGFI_ICON | SHGFI_SMALLICON);
            if (bmp != null)
                return bmp;

            // Fallback: get the 32x32 large icon and scale it down
            bmp = FetchIcon(filePath, SHGFI_ICON | SHGFI_LARGEICON);
            if (bmp != null)
                return ResizeBitmap(bmp, 16, 16);

            return null;
        }

        private static Bitmap FetchIcon(string filePath, uint flags)
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(
                filePath, 0,
                ref info, (uint)Marshal.SizeOf(info),
                flags);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                using (var icon = Icon.FromHandle(info.hIcon))
                {
                    // Clone immediately — the bitmap must not reference
                    // the HICON after DestroyIcon is called in finally.
                    return new Bitmap(icon.ToBitmap());
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        private static Bitmap ResizeBitmap(Bitmap source, int width, int height)
        {
            var dest = new Bitmap(width, height);
            using (var g = Graphics.FromImage(dest))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, width, height);
            }
            source.Dispose();
            return dest;
        }
    }
}