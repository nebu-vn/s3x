using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace S3Explorer.Services;

/// <summary>
/// Lấy tên loại file và icon của Windows theo phần mở rộng (giống Explorer),
/// không cần file tồn tại trên đĩa. Cache theo extension.
/// </summary>
public static class ShellInfo
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    private static readonly Dictionary<string, (string Type, ImageSource? Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const string FolderKey = "\0folder";

    public static (string Type, ImageSource? Icon) Folder() => Get(FolderKey);

    public static (string Type, ImageSource? Icon) ForExtension(string ext) => Get(ext ?? "");

    private static (string Type, ImageSource? Icon) Get(string key)
    {
        if (Cache.TryGetValue(key, out var hit)) return hit;

        bool isFolder = key == FolderKey;
        string fake = isFolder ? "folder" : "file" + key;
        string type = isFolder ? "Thư mục" : key.Length > 0 ? $"Tệp {key.TrimStart('.').ToUpperInvariant()}" : "Tệp";
        ImageSource? icon = null;

        try
        {
            var info = new SHFILEINFO();
            var r = SHGetFileInfo(fake, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            if (r != IntPtr.Zero)
            {
                if (!string.IsNullOrWhiteSpace(info.szTypeName)) type = info.szTypeName;
                if (info.hIcon != IntPtr.Zero)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    icon = bmp;
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch
        {
            // giữ giá trị mặc định
        }

        return Cache[key] = (type, icon);
    }
}
