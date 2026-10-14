using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SafeLauncher.Service;

/// <summary>Функции Windows: задание для ядра, консоль для мягкой остановки, сведения о клиенте канала.</summary>
internal static class Native
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    public const uint CtrlCEvent = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
        ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint FileNameNormalized = 0x0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>Полный путь к exe процесса или null (процесс завершился, защищён и т. п.).</summary>
    /// <param name="native">
    /// true — путь вида \Device\HarddiskVolume3\…, который Windows запомнила при запуске процесса (по нему узнают
    /// программу фильтры WFP), даже если файл с тех пор переименовали.
    /// </param>
    public static string? ProcessImagePath(int processId, bool native = false)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            return null;
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, native ? 1u : 0u, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TokenQuery = 0x0008;

    /// <summary>SID пользователя, от имени которого работает процесс (клиент канала), или null.</summary>
    public static string? ProcessUserSid(int processId)
    {
        try
        {
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
                return null;
            try
            {
                using var identity = new System.Security.Principal.WindowsIdentity(token);
                return identity.User?.Value;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Настоящий путь к файлу: без коротких имён (PROGRA~1), ссылок и точек соединения — под ним Windows и запустит
    /// программу. null — файла нет или его не открыть.
    /// </summary>
    public static string? FinalPath(string path) => FinalPath(path, out _);

    /// <param name="missing">
    /// true — файла нет на диске, который видит служба (программа удалена). false при null — файл не проверить: диск
    /// виден только пользователю (сетевой, subst), сетевая папка или нет доступа.
    /// </param>
    public static string? FinalPath(string path, out bool missing)
    {
        missing = false;
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0)
            return null;

        // Только чтение атрибутов: так открывается и файл, содержимое которого службе читать нельзя (EFS и т. п.).
        using var file = CreateFileW(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            missing = error is ErrorFileNotFound or ErrorPathNotFound && RootVisible(path);
            return null;
        }

        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Length, FileNameNormalized);
        if (length == 0 || length >= buffer.Length)
            return null;
        var final = new string(buffer, 0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + final[8..]; // \\?\UNC\server\share → \\server\share
        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    /// <summary>Диск (C:\) или сетевая папка из пути видны службе.</summary>
    private static bool RootVisible(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCacheNative();

    /// <summary>
    /// Очистить кэш DNS Windows (как ipconfig /flushdns): после ядра в нём не должно остаться ответов, полученных
    /// через него (подменных адресов), — программы сразу получат обычные ответы, как будто ядра и не было.
    /// </summary>
    public static void FlushDnsCache()
    {
        try
        {
            DnsFlushResolverCacheNative();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // нет функции — кэш истечёт сам (ответы ядра живут 1 секунду)
        }
    }

    /// <summary>Задание, процессы которого Windows закроет вместе со службой, даже если она упадёт.</summary>
    public static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return job;
    }

    public static void Assign(SafeFileHandle job, SafeProcessHandle process)
    {
        if (!AssignProcessToJobObject(job, process))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
