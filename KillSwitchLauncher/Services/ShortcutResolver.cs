using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Цель ярлыка: путь к .exe, аргументы и рабочая папка.
/// У ярлыков приложений Microsoft Store пути к файлу нет: TargetPath пустой, а приложение задаёт AppUserModelId
/// (вида «Publisher.App_8wekyb3d8bbwe!App»).
/// </summary>
public sealed record ShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory, string? AppUserModelId = null);

/// <summary>
/// Извлекает реальный путь из ярлыка .lnk через системный COM-объект ShellLink (интерфейс IShellLinkW).
/// Это тот же механизм, которым пользуется Проводник, поэтому не нужна ссылка на
/// Windows Script Host (IWshRuntimeLibrary) и не нужен разбор бинарного формата .lnk вручную.
/// </summary>
public static class ShortcutResolver
{
    private const int MaxPath = 32767;          // поддержка длинных путей
    private const int StgmRead = 0x00000000;

    // Флаги IShellLink::Resolve
    private const uint SlrNoUi = 0x0001;        // никаких диалогов
    private const uint SlrNoUpdate = 0x0008;    // не перезаписывать сам .lnk
    private const uint SlrNoSearch = 0x0010;    // не искать по диску долго
    private const uint ResolveTimeoutMs = 1000; // при SLR_NO_UI старшее слово флагов = таймаут

    public static ShortcutTarget Resolve(string lnkPath)
    {
        if (!File.Exists(lnkPath))
            throw new FileNotFoundException("Ярлык не найден.", lnkPath);

        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            // 1. Загружаем файл ярлыка через IPersistFile.
            ((IPersistFile)link).Load(lnkPath, StgmRead);

            // 2. Пытаемся «разрешить» ярлык: если цель была перемещена, Windows найдёт её
            //    по Distributed Link Tracking. Ошибку игнорируем — путь всё равно попробуем прочитать.
            link.Resolve(IntPtr.Zero, SlrNoUi | SlrNoUpdate | SlrNoSearch | (ResolveTimeoutMs << 16));

            // 3. Читаем путь к цели, аргументы и рабочую папку.
            var buffer = new StringBuilder(MaxPath);
            try
            {
                link.GetPath(buffer, buffer.Capacity, out _, 0);
            }
            catch (COMException)
            {
                // Ярлыки на элементы оболочки (приложения Microsoft Store) пути к файлу не имеют.
                buffer.Clear();
            }
            var target = Environment.ExpandEnvironmentVariables(buffer.ToString());

            // «Объявленный» ярлык установщика MSI указывает не на .exe, а на компонент установленного продукта.
            if (TryResolveMsiShortcut(lnkPath) is { } msiTarget)
                target = msiTarget;

            buffer.Clear();
            link.GetArguments(buffer, buffer.Capacity);
            var args = buffer.ToString();

            buffer.Clear();
            link.GetWorkingDirectory(buffer, buffer.Capacity);
            var workDir = Environment.ExpandEnvironmentVariables(buffer.ToString());

            // Ярлык приложения Microsoft Store: внутри не путь, а элемент папки «Приложения» (shell:AppsFolder).
            string? appUserModelId = null;
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                appUserModelId = TryGetAppUserModelId(link);
                if (appUserModelId is not null)
                    target = string.Empty;
            }

            return new ShortcutTarget(target, args, workDir, appUserModelId);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static string? TryResolveMsiShortcut(string lnkPath)
    {
        try
        {
            var product = new StringBuilder(39);
            var feature = new StringBuilder(39);
            var component = new StringBuilder(39);
            if (MsiGetShortcutTargetW(lnkPath, product, feature, component) != 0 || component.Length == 0)
                return null;

            var length = 32767u;
            var path = new StringBuilder((int)length + 1);
            var state = MsiGetComponentPathW(product.ToString(), component.ToString(), path, ref length);
            return state is InstallStateLocal or InstallStateSource && File.Exists(path.ToString()) ? path.ToString() : null;
        }
        catch (Exception)
        {
            return null; // msi.dll недоступна или ярлык обычный
        }
    }

    private static string? TryGetAppUserModelId(IShellLinkW link)
    {
        var pidl = IntPtr.Zero;
        try
        {
            link.GetIDList(out pidl);
            if (pidl == IntPtr.Zero)
                return null;

            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromIDList(pidl, ref iid, out var item);
            try
            {
                // У элемента папки «Приложения» имя для разбора и есть AppUserModelId: «Семейство_пакета!App».
                item.GetDisplayName(SigdnParentRelativeParsing, out var name);
                return !string.IsNullOrWhiteSpace(name) && name.Contains('!') && !name.Contains('\\') ? name : null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    // ---- COM interop ----------------------------------------------------------------

    private const uint SigdnParentRelativeParsing = 0x80018001;
    private const int InstallStateLocal = 3;
    private const int InstallStateSource = 4;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetShortcutTargetW(string shortcutPath, StringBuilder productCode, StringBuilder featureId, StringBuilder componentCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPathW(string product, string component, StringBuilder path, ref uint length);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")] // CLSID_ShellLink
    private class ShellLinkCoClass
    {
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindDataW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    /// <summary>IShellLinkW. Порядок методов обязан совпадать с vtable из ShObjIdl_core.h.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, out Win32FindDataW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        [PreserveSig]
        int Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
