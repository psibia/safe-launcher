using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KillSwitchLauncher.Services;

/// <summary>Дескриптор объекта ядра (задание, процесс, снимок процессов), закрываемый через CloseHandle.</summary>
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle() : base(ownsHandle: true)
    {
    }

    public SafeKernelHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>
/// Функции Windows для контроля процессов: задания (Job Objects), создание процесса в приостановленном
/// состоянии, перечисление процессов. В .NET для этого нет готовых классов.
/// </summary>
internal static class NativeMethods
{
    // ---- права на процесс
    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessSetQuota = 0x0100;
    public const uint ProcessSuspendResume = 0x0800;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x00100000;

    // ---- флаги CreateProcess
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateDefaultErrorMode = 0x04000000;
    public const uint CreateUnicodeEnvironment = 0x00000400;

    // ---- задания
    public const int JobInfoBasicAccounting = 1;
    public const int JobInfoBasicProcessIdList = 3;
    public const int JobInfoExtendedLimit = 9;
    public const int JobInfoAssociateCompletionPort = 7;
    public const uint JobMsgNewProcess = 6;
    public const uint JobMsgExitProcess = 7;
    public const uint JobMsgAbnormalExitProcess = 8;
    public const uint JobObjectLimitActiveProcess = 0x00000008;
    public const uint JobObjectLimitBreakawayOk = 0x00000800;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;

    // ---- прочее
    public const uint StillActive = 259;
    public const uint Th32csSnapProcess = 0x00000002;
    public const int ErrorAccessDenied = 5;
    public const int ErrorElevationRequired = 740;
    public const int ErrorAbandonedWait0 = 735;

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicLimitInformation
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
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectAssociateCompletionPort
    {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ---- задания

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeKernelHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(SafeKernelHandle hJob, int infoClass,
        ref JobObjectExtendedLimitInformation info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(SafeKernelHandle hJob, int infoClass,
        ref JobObjectAssociateCompletionPort info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(SafeKernelHandle hJob, int infoClass,
        out JobObjectBasicAccountingInformation info, uint infoLength, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryInformationJobObject(SafeKernelHandle hJob, int infoClass,
        [Out] UIntPtr[] info, uint infoLength, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(SafeKernelHandle hJob, SafeHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(SafeKernelHandle hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessInJob(SafeHandle processHandle, SafeKernelHandle jobHandle, out bool result);

    // ---- порт уведомлений заданий (какие процессы появились и завершились)

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeKernelHandle CreateIoCompletionPort(IntPtr fileHandle, IntPtr existingPort,
        UIntPtr completionKey, uint concurrentThreads);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetQueuedCompletionStatus(SafeKernelHandle port, out uint numberOfBytes,
        out UIntPtr completionKey, out IntPtr overlapped, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PostQueuedCompletionStatus(SafeKernelHandle port, uint numberOfBytes,
        UIntPtr completionKey, IntPtr overlapped);

    // ---- процессы

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeKernelHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(SafeHandle hProcess, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(SafeHandle hProcess, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(SafeHandle hProcess, uint flags,
        [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeKernelHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32FirstW(SafeKernelHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Process32NextW(SafeKernelHandle snapshot, ref ProcessEntry32 entry);

    /// <summary>Приостановить все потоки процесса (без новых соединений, пока перезапускается ядро). 0 — успех.</summary>
    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(SafeHandle process);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(SafeHandle process);

    /// <summary>Порядок завершения при выходе из Windows: 0x100 — последними среди программ.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessShutdownParameters(uint level, uint flags);

    // ---- пути

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint bufferLength, uint flags);
}
