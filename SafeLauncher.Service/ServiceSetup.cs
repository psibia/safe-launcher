using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace SafeLauncher.Service;

/// <summary>
/// Регистрация службы: SafeLauncher.Service.exe --install / --uninstall (нужны права администратора;
/// так делают установщик и проверка RouteCheck). Служба запускается вместе с Windows от имени системы
/// и после сбоя перезапускается.
/// </summary>
internal static class ServiceSetup
{
    private const string Description =
        "Запускает ядро встроенного VPN по команде Безопасного лаунчера. Без лаунчера ничего не делает.";

    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 0x2;
    private const uint ServiceErrorNormal = 0x1;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceDoesNotExist = 1060;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigFailureActions = 2;
    private const int ScActionRestart = 1;

    /// <summary>Регистрирует (или перенастраивает) службу на этот exe и запускает её.</summary>
    public static void Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь службы.");
        CheckLocation(exe);

        using var manager = OpenManager();
        var binPath = $"\"{exe}\"";
        var service = CreateServiceW(manager, ControlProtocol.ServiceName, ControlProtocol.DisplayName, ServiceAllAccess,
            ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, binPath, null, IntPtr.Zero, null, null, null);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            if (error != ErrorServiceExists)
                throw new Win32Exception(error);

            // Уже есть (обновление): останавливаем и направляем на новый exe.
            Stop();
            service = OpenServiceW(manager, ControlProtocol.ServiceName, ServiceAllAccess);
            if (service.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!ChangeServiceConfigW(service, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, binPath,
                    null, IntPtr.Zero, null, null, null, ControlProtocol.DisplayName))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (service)
        {
            var description = new ServiceDescription { Description = Description };
            ChangeServiceConfig2W(service, ServiceConfigDescription, ref description);

            // После сбоя — перезапуск через 5 секунд; счётчик сбоев сбрасывается раз в сутки.
            var actions = new[]
            {
                new ScAction { Type = ScActionRestart, Delay = 5000 },
                new ScAction { Type = ScActionRestart, Delay = 5000 },
                new ScAction { Type = ScActionRestart, Delay = 30000 }
            };
            var handle = GCHandle.Alloc(actions, GCHandleType.Pinned);
            try
            {
                var failure = new ServiceFailureActions
                {
                    ResetPeriod = 86400,
                    ActionCount = actions.Length,
                    Actions = handle.AddrOfPinnedObject()
                };
                ChangeServiceConfig2W(service, ServiceConfigFailureActions, ref failure);
            }
            finally
            {
                handle.Free();
            }
        }

        // Источник для записей службы в журнале Windows «Приложение».
        try
        {
            if (!EventLog.SourceExists(ControlProtocol.ServiceName))
                EventLog.CreateEventSource(ControlProtocol.ServiceName, "Application");
        }
        catch (Exception)
        {
            // без журнала служба работает
        }

        using var controller = new ServiceController(ControlProtocol.ServiceName);
        if (controller.Status != ServiceControllerStatus.Running)
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
    }

    public static void Uninstall()
    {
        Stop();
        using var manager = OpenManager();
        using var service = OpenServiceW(manager, ControlProtocol.ServiceName, ServiceAllAccess);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
                return;
            throw new Win32Exception(error);
        }

        if (!DeleteService(service))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            if (EventLog.SourceExists(ControlProtocol.ServiceName))
                EventLog.DeleteEventSource(ControlProtocol.ServiceName);
        }
        catch (Exception)
        {
            // не страшно
        }
    }

    private static void Stop()
    {
        try
        {
            using var controller = new ServiceController(ControlProtocol.ServiceName);
            if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                return;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        catch (InvalidOperationException)
        {
            // службы нет
        }
    }

    /// <summary>
    /// Служба работает от имени системы, поэтому её файлы должны лежать там, куда пишут только администраторы,
    /// и в пути не должно быть русских букв (иначе ломается DNS при строгой маршрутизации ядра).
    /// </summary>
    internal static void CheckLocation(string exe)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(exe);
        if (!full.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Службу можно установить только из папки {programFiles}, а она в {Path.GetDirectoryName(full)}.");
        if (full.Any(c => c > 127))
            throw new InvalidOperationException($"В пути службы не должно быть русских букв: {full}");
    }

    private static ScHandle OpenManager()
    {
        var manager = OpenSCManagerW(null, null, ScManagerAllAccess);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return manager;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescription
    {
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public string? RebootMessage;
        public string? Command;
        public int ActionCount;
        public IntPtr Actions;
    }

    private sealed class ScHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public ScHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ScHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ScHandle CreateServiceW(ScHandle manager, string name, string displayName, uint access, uint serviceType,
        uint startType, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ScHandle OpenServiceW(ScHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(ScHandle service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName,
        string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2W(ScHandle service, uint infoLevel, ref ServiceDescription info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2W(ScHandle service, uint infoLevel, ref ServiceFailureActions info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(ScHandle service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
