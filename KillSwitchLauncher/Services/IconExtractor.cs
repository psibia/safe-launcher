using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillSwitchLauncher.Services;

/// <summary>Достаёт иконку из .exe в виде WPF-изображения. Возвращает null, если иконки нет.</summary>
public static class IconExtractor
{
    private const int IconSize = 64;

    public static ImageSource? TryGetIcon(string exePath)
    {
        try
        {
            return FromExeResources(exePath) ?? FromShell(exePath);
        }
        catch
        {
            return null; // в UI будет показана иконка по умолчанию
        }
    }

    /// <summary>
    /// Иконка ярлыка так, как её рисует Проводник (48×48, без стрелки ярлыка).
    /// Нужна для приложений Microsoft Store: у них нет .exe, из которого можно достать иконку.
    /// </summary>
    public static ImageSource? TryGetShortcutIcon(string lnkPath)
    {
        IntPtr imageList = IntPtr.Zero;
        try
        {
            var info = new ShFileInfo();
            if (SHGetFileInfo(lnkPath, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiSysIconIndex) == IntPtr.Zero)
                return null;

            var iid = typeof(IImageList).GUID;
            if (SHGetImageList(ShilExtraLarge, ref iid, out imageList) != 0 || imageList == IntPtr.Zero)
                return FromShell(lnkPath);

            var list = (IImageList)Marshal.GetObjectForIUnknown(imageList);
            try
            {
                return list.GetIcon(info.iIcon, IldTransparent, out var hIcon) == 0 && hIcon != IntPtr.Zero
                    ? ToImageSource(hIcon)
                    : FromShell(lnkPath);
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (imageList != IntPtr.Zero)
                Marshal.Release(imageList);
        }
    }

    /// <summary>Первая иконка из ресурсов .exe нужного размера (чётче, чем 32×32 из оболочки).</summary>
    private static ImageSource? FromExeResources(string path)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        var count = PrivateExtractIcons(path, 0, IconSize, IconSize, handles, ids, 1, 0);
        if (count == 0 || count == uint.MaxValue || handles[0] == IntPtr.Zero)
            return null;

        return ToImageSource(handles[0]);
    }

    /// <summary>Иконка, которую показывает Проводник (для exe без своей иконки — стандартная).</summary>
    private static ImageSource? FromShell(string path)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        return ToImageSource(info.hIcon);
    }

    private static ImageSource ToImageSource(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSysIconIndex = 0x000004000;
    private const int ShilExtraLarge = 2;
    private const int IldTransparent = 0x00000001;

    /// <summary>Системный список иконок. Порядок методов обязан совпадать с vtable из CommonControls.h; вызывается только GetIcon.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppvObj);
    private const uint ShgfiLargeIcon = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, int[] piconid, uint nIcons, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
