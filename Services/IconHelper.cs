using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSearcher.Services;

public static class IconHelper
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

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private static readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? GetIcon(string path, bool largeIcon = true)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Use extension as cache key for files, full path for folders/specific items
        string cacheKey = path;
        bool isDirectory = System.IO.Directory.Exists(path);

        if (!isDirectory && System.IO.File.Exists(path))
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            // Cache .exe and .lnk by full path (each has a unique icon).
            // Generic types like .pdf, .docx, .txt share one icon per extension.
            bool uniqueIcon = ext is ".exe" or ".lnk" or ".url";
            cacheKey = uniqueIcon ? path : ext;
        }

        if (_iconCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var icon = GetIconInternal(path, isDirectory, largeIcon);
        _iconCache[cacheKey] = icon;
        return icon;
    }

    private static BitmapSource? GetIconInternal(string path, bool isDirectory, bool largeIcon)
    {
        try
        {
            var shfi = new SHFILEINFO();
            uint flags = SHGFI_ICON | (largeIcon ? SHGFI_LARGEICON : SHGFI_SMALLICON);
            uint fileAttributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

            // Try with actual path first
            var result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            {
                // Fallback: use file attributes
                result = SHGetFileInfo(path, fileAttributes, ref shfi,
                    (uint)Marshal.SizeOf(shfi), flags | SHGFI_USEFILEATTRIBUTES);
            }

            if (shfi.hIcon != IntPtr.Zero)
            {
                try
                {
                    var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmapSource.Freeze();
                    return bitmapSource;
                }
                finally
                {
                    DestroyIcon(shfi.hIcon);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"IconHelper error for {path}: {ex.Message}");
        }

        return null;
    }

    public static void ClearCache()
    {
        _iconCache.Clear();
    }
}
