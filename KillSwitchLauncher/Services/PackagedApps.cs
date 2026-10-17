using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Запуск приложений Microsoft Store. У них нет .exe, который можно запустить напрямую: Windows запускает
/// их сама по AppUserModelId через ApplicationActivationManager и сообщает PID запущенного процесса.
/// </summary>
internal static class PackagedApps
{
    /// <summary>Запускает приложение (или выводит на передний план уже запущенное) и возвращает его PID.</summary>
    public static int Activate(string appUserModelId, string? arguments)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManagerCoClass();
        try
        {
            var hr = manager.ActivateApplication(appUserModelId, string.IsNullOrWhiteSpace(arguments) ? null : arguments, ActivateOptionsNone, out var pid);
            Marshal.ThrowExceptionForHR(hr);
            return (int)pid;
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    private const int ActivateOptionsNone = 0;

    private static readonly ConcurrentDictionary<string, string?> Executables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Программа приложения Store до его запуска: папка пакета и Executable из AppxManifest.xml. Нужна, чтобы фильтры
    /// Windows и ядро знали её заранее: иначе первые секунды после запуска приложение шло бы без них. null — не нашли.
    /// Запоминается; если файла больше нет (пакет обновился и переехал в папку новой версии), ищется заново.
    /// </summary>
    public static string? ExecutablePath(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return null;
        if (Executables.TryGetValue(appUserModelId, out var known) && (known is null || File.Exists(known)))
            return known;
        var found = FindExecutable(appUserModelId);
        Executables[appUserModelId] = found;
        return found;
    }

    private static string? FindExecutable(string appUserModelId)
    {
        try
        {
            var bang = appUserModelId.IndexOf('!');
            if (bang <= 0 || bang == appUserModelId.Length - 1)
                return null;
            var family = appUserModelId[..bang];
            var appId = appUserModelId[(bang + 1)..];
            foreach (var fullName in PackagesOf(family))
            {
                if (PackagePath(fullName) is not { } root)
                    continue;
                var manifest = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifest))
                    continue;
                var application = XDocument.Load(manifest).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Application" && string.Equals((string?)e.Attribute("Id"), appId, StringComparison.OrdinalIgnoreCase));
                if ((string?)application?.Attribute("Executable") is not { Length: > 0 } executable ||
                    !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                var path = Path.GetFullPath(Path.Combine(root, executable));
                if (File.Exists(path))
                    return path;
            }
        }
        catch (Exception)
        {
            // нет API (старая Windows), нет доступа к манифесту, манифест не разобрать: программа станет известна после запуска
        }

        return null;
    }

    private static string[] PackagesOf(string familyName)
    {
        uint count = 0, length = 0;
        var result = GetPackagesByPackageFamily(familyName, ref count, null, ref length, null);
        if (result != ErrorInsufficientBuffer || count == 0 || length == 0)
            return Array.Empty<string>();
        var names = new IntPtr[count];
        var buffer = new char[length];
        if (GetPackagesByPackageFamily(familyName, ref count, names, ref length, buffer) != 0)
            return Array.Empty<string>();
        // Полные имена пакетов лежат в буфере подряд, каждое заканчивается нулём.
        return new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? PackagePath(string fullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != ErrorInsufficientBuffer || length == 0)
            return null;
        var buffer = new char[length];
        return GetPackagePathByFullName(fullName, ref length, buffer) == 0 ? new string(buffer, 0, (int)length).TrimEnd('\0') : null;
    }

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, [Out] IntPtr[]? packageFullNames,
        ref uint bufferLength, [Out] char[]? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] // CLSID_ApplicationActivationManager
    private class ApplicationActivationManagerCoClass
    {
    }

    /// <summary>IApplicationActivationManager. Порядок методов обязан совпадать с vtable из ShObjIdl_core.h.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);

        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }
}
