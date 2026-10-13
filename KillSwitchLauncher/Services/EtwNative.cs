using System;
using System.Runtime.InteropServices;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Функции Windows для журнала событий ETW (Event Tracing for Windows): создать сессию, подписать её на
/// поставщика событий и читать события в реальном времени. В .NET для этого нет готовых классов,
/// а сторонняя библиотека не нужна: используется только малая часть API.
/// </summary>
internal static class EtwNative
{
    public const uint WnodeFlagTracedGuid = 0x00020000;
    public const uint EventTraceRealTimeMode = 0x00000100;
    public const uint EventTraceControlStop = 1;
    public const uint EventControlCodeEnableProvider = 1;
    public const byte TraceLevelVerbose = 5;
    public const uint ProcessTraceModeRealTime = 0x00000100;
    public const uint ProcessTraceModeEventRecord = 0x10000000;
    public const uint EnableTraceParametersVersion2 = 2;
    public const uint EventFilterTypeEventId = 0x80000200;

    public const int ErrorSuccess = 0;
    public const int ErrorAccessDenied = 5;
    public const int ErrorAlreadyExists = 183;

    /// <summary>OpenTrace вернул ошибку (на 64-битной системе — все единицы, на 32-битной — младшие 32 бита).</summary>
    public static bool IsInvalidTraceHandle(ulong handle) => handle == ulong.MaxValue || handle == 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    /// <summary>EVENT_TRACE_PROPERTIES. Сразу за структурой в том же буфере лежит имя сессии.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    /// <summary>TIME_ZONE_INFORMATION: содержимое не нужно, важен только размер (172 байта).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 172)]
    public struct TimeZoneBlob
    {
        public int Bias;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TimeZoneBlob TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    /// <summary>EVENT_TRACE_LOGFILEW: что читать (имя сессии) и куда отдавать события.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceLogfile
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventRecord
    {
        public EventHeader EventHeader;
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventFilterDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnableTraceParameters
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public IntPtr EnableFilterDesc;
        public uint FilterDescCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyDataDescriptor
    {
        public ulong PropertyName;
        public uint ArrayIndex;
        public uint Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void EventRecordCallback(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int StartTraceW(out ulong traceHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int ControlTraceW(ulong traceHandle, string? instanceName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    public static extern int EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll")]
    public static extern int ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    public static extern int CloseTrace(ulong traceHandle);

    [DllImport("tdh.dll")]
    public static extern int TdhGetPropertySize(IntPtr eventRecord, uint tdhContextCount, IntPtr tdhContext,
        uint propertyDataCount, ref PropertyDataDescriptor propertyData, out uint propertySize);

    [DllImport("tdh.dll")]
    public static extern int TdhGetProperty(IntPtr eventRecord, uint tdhContextCount, IntPtr tdhContext,
        uint propertyDataCount, ref PropertyDataDescriptor propertyData, uint bufferSize, byte[] buffer);
}
