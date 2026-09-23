using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Extensions whose icon belongs to the individual file rather than to
        /// the file type: executables carry their own, and a shortcut shows the
        /// icon of whatever it points at. Everything else looks identical for
        /// every file of that type, so it is cached and resolved by extension.
        /// </summary>
        private static readonly HashSet<string> PerFileIconExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".exe", ".lnk", ".url", ".ico", ".dll", ".cpl",
                ".msc", ".msi", ".scr", ".cur", ".ani", ".appref-ms",
            };

        private static readonly Dictionary<string, Bitmap> Cache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        /// Bounded so a very large tree cannot grow the cache without limit.
        /// Dropping everything is crude, but a refill costs one lookup per
        /// visible item and only the menu actually being opened pays it.
        private const int MaxCacheEntries = 4000;

        /// <summary>
        /// Returns a 16x16 bitmap of the shell icon for the given file path, or
        /// null when none can be retrieved.
        ///
        /// Every caller gets its own copy. The cached bitmap is never handed out
        /// directly, because a menu item that disposes its image would empty the
        /// cache entry underneath every other item sharing it.
        /// </summary>
        public static Bitmap GetIcon(string filePath)
        {
            return Copy(Resolve(filePath));
        }

        /// <summary>
        /// Loads an icon into the cache without handing one back.
        ///
        /// Called off the UI thread after a scan, so that by the time a menu is
        /// opened every icon in it is already a cache hit. The lookup has to
        /// happen once per file whatever else changes; this decides that it
        /// happens on a background thread rather than in front of the user.
        /// </summary>
        public static void Prewarm(string filePath)
        {
            Resolve(filePath);
        }

        /// Returns the cached instance itself, which only Copy may hand out.
        private static Bitmap Resolve(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            string key = CacheKey(filePath);

            lock (CacheLock)
            {
                Bitmap hit;
                if (Cache.TryGetValue(key, out hit))
                {
                    return hit;
                }
            }

            Bitmap fetched = Fetch(filePath);

            // A failure is only remembered for type-wide icons. A per-file icon
            // that could not be read may simply be on a share that is down, and
            // caching the miss would keep it blank for the rest of the session.
            if (fetched != null || !IsPerFileIcon(filePath))
            {
                Store(key, fetched);
            }

            return fetched;
        }

        /// <summary>
        /// Forgets every cached icon, so the next lookup asks the shell again.
        /// Items already on screen hold their own copies and are unaffected.
        /// </summary>
        public static void ClearCache()
        {
            lock (CacheLock)
            {
                DisposeAllLocked();
            }
        }

        private static void Store(string key, Bitmap value)
        {
            lock (CacheLock)
            {
                if (Cache.Count >= MaxCacheEntries)
                {
                    DisposeAllLocked();
                }
                Cache[key] = value;
            }
        }

        private static void DisposeAllLocked()
        {
            foreach (Bitmap held in Cache.Values)
            {
                if (held != null) held.Dispose();
            }
            Cache.Clear();
        }

        private static Bitmap Copy(Bitmap source)
        {
            if (source == null)
            {
                return null;
            }

            try
            {
                return (Bitmap)source.Clone();
            }
            catch (Exception ex)
            {
                Diag.Swallowed("copying a cached icon", ex);
                return null;
            }
        }

        /// Type-wide icons share one entry per extension, which collapses most
        /// of a folder to a handful of lookups.
        private static string CacheKey(string filePath)
        {
            if (IsPerFileIcon(filePath))
            {
                return "path:" + filePath;
            }
            return "ext:" + Path.GetExtension(filePath);
        }

        private static bool IsPerFileIcon(string filePath)
        {
            return PerFileIconExtensions.Contains(Path.GetExtension(filePath) ?? "");
        }

        private static Bitmap Fetch(string filePath)
        {
            Bitmap small = FetchIcon(filePath, SHGFI_ICON | SHGFI_SMALLICON);
            if (small != null)
            {
                return small;
            }

            Bitmap large = FetchIcon(filePath, SHGFI_ICON | SHGFI_LARGEICON);
            if (large != null)
            {
                return ResizeBitmap(large, 16, 16);
            }

            return null;
        }

        private static Bitmap FetchIcon(string filePath, uint flags)
        {
            uint attributes = 0;

            if (!IsPerFileIcon(filePath))
            {
                // The shell is told to answer from the extension alone and leave
                // the file shut. Besides being several times faster, it means a
                // document on an unreachable share costs nothing here instead of
                // an SMB round trip.
                flags |= SHGFI_USEFILEATTRIBUTES;
                attributes = FILE_ATTRIBUTE_NORMAL;
            }

            SHFILEINFO info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(
                filePath, attributes,
                ref info, (uint)Marshal.SizeOf(info),
                flags);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                using (Icon icon = Icon.FromHandle(info.hIcon))
                {
                    // Clone immediately - the bitmap must not reference
                    // the HICON after DestroyIcon is called in finally.
                    return new Bitmap(icon.ToBitmap());
                }
            }
            catch (Exception ex)
            {
                // An unreadable icon costs that entry its image, nothing more.
                Diag.Swallowed("reading the icon for " + filePath, ex);
                return null;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        private static Bitmap ResizeBitmap(Bitmap source, int width, int height)
        {
            Bitmap dest = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(dest))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, width, height);
            }
            source.Dispose();
            return dest;
        }
    }
}
