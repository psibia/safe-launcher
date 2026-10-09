using System;
using System.Runtime.InteropServices;

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
