using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static KillSwitchLauncher.Services.EtwNative;

namespace KillSwitchLauncher.Services;

/// <summary>Один DNS-запрос, замеченный Windows: кто спросил, что и когда.</summary>
/// <param name="TimeUtc">Время запроса (UTC).</param>
/// <param name="ProcessId">PID процесса, который сделал запрос.</param>
/// <param name="Name">Имя, как его передало приложение (до нормализации).</param>
/// <param name="Type">Тип записи: 1 — A, 28 — AAAA, 65 — HTTPS и т. д.</param>
public readonly record struct DnsQueryEvent(DateTime TimeUtc, int ProcessId, string Name, ushort Type);

/// <summary>
/// Сессия ETW, которая слушает встроенный журнал DNS-клиента Windows (поставщик Microsoft-Windows-DNS-Client)
/// и сообщает о каждом DNS-запросе любого процесса. Нужны права администратора или членство в группе
/// «Пользователи журналов производительности»; без них Start бросает Win32Exception с кодом 5.
///
/// Берутся два события: 3006 — приложение вызвало DNS-запрос, 3008 — запрос завершён. Оба пишутся
/// библиотекой DNS внутри самого приложения, поэтому PID в заголовке события — это PID приложения.
/// Сессия у системы одна на имя; если лаунчер упал и оставил её, следующий запуск её закрывает.
/// </summary>
internal sealed class DnsEtwSession : IDisposable
{
    public const string DefaultSessionName = "SafeLauncher-DNS";
    public const ushort EventQueryStarted = 3006;
    public const ushort EventQueryCompleted = 3008;

    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    private static readonly IntPtr QueryNameProperty = Marshal.StringToHGlobalUni("QueryName");
    private static readonly IntPtr QueryTypeProperty = Marshal.StringToHGlobalUni("QueryType");

    private readonly string _name;
    private readonly Action<DnsQueryEvent> _onEvent;
    private readonly EventRecordCallback _callback; // держим делегат, пока Windows может его вызвать
    private readonly byte[] _buffer = new byte[2048];

    private IntPtr _loggerName;
    private ulong _session;
    private ulong _trace = ulong.MaxValue;
    private Thread? _thread;
    private int _disposed;

    private DnsEtwSession(string name, Action<DnsQueryEvent> onEvent)
    {
        _name = name;
        _onEvent = onEvent;
        _callback = OnEventRecord;
    }

    /// <summary>Сессия перестала получать события (её остановили извне или Windows закрыла её). Любой поток.</summary>
    public event EventHandler? Stopped;

    /// <summary>Создаёт сессию и начинает получать события в фоновом потоке.</summary>
    public static DnsEtwSession Start(Action<DnsQueryEvent> onEvent, string name = DefaultSessionName)
    {
        var session = new DnsEtwSession(name, onEvent);
        try
        {
            session.Open();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Останавливает сессию с этим именем, если она осталась от прошлого запуска.</summary>
    public static void StopStale(string name = DefaultSessionName) => Control(0, name, EventTraceControlStop);

    private void Open()
    {
        var error = Control(0, _name, 0, start: true, out _session);
        if (error == ErrorAlreadyExists)
        {
            // Осталась от аварийно закрытого лаунчера: закрываем и создаём заново.
            StopStale(_name);
            error = Control(0, _name, 0, start: true, out _session);
        }

        if (error != ErrorSuccess)
            throw new Win32Exception(error);

        error = EnableProvider();
        if (error != ErrorSuccess)
            throw new Win32Exception(error);

        _loggerName = Marshal.StringToHGlobalUni(_name);
        var logfile = new EventTraceLogfile
        {
            LoggerName = _loggerName,
            ProcessTraceMode = ProcessTraceModeRealTime | ProcessTraceModeEventRecord,
            EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
        };

        var trace = OpenTraceW(ref logfile);
        if (IsInvalidTraceHandle(trace))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _trace = trace;

        _thread = new Thread(Pump) { IsBackground = true, Name = "Журнал DNS (ETW)" };
        _thread.Start();
    }

    /// <summary>
    /// Подписывает сессию на DNS-клиент. Сначала с фильтром по номерам событий (меньше нагрузки: поставщик
    /// пишет много служебных событий), а если Windows фильтр не поддерживает — без него.
    /// </summary>
    private int EnableProvider()
    {
        var provider = DnsClientProvider;

        // EVENT_FILTER_EVENT_ID: FilterIn (1 байт), Reserved (1), Count (2), затем номера событий по 2 байта.
        var filter = Marshal.AllocHGlobal(8);
        var descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<EventFilterDescriptor>());
        var parameters = Marshal.AllocHGlobal(Marshal.SizeOf<EnableTraceParameters>());
        try
        {
            Marshal.WriteByte(filter, 0, 1);
            Marshal.WriteByte(filter, 1, 0);
            Marshal.WriteInt16(filter, 2, 2);
            Marshal.WriteInt16(filter, 4, unchecked((short)EventQueryStarted));
            Marshal.WriteInt16(filter, 6, unchecked((short)EventQueryCompleted));

            Marshal.StructureToPtr(new EventFilterDescriptor
            {
                Ptr = (ulong)filter.ToInt64(),
                Size = 8,
                Type = EventFilterTypeEventId
            }, descriptor, false);

            Marshal.StructureToPtr(new EnableTraceParameters
            {
                Version = EnableTraceParametersVersion2,
                EnableFilterDesc = descriptor,
                FilterDescCount = 1
            }, parameters, false);

            var error = EnableTraceEx2(_session, ref provider, EventControlCodeEnableProvider, TraceLevelVerbose,
                ulong.MaxValue, 0, 0, parameters);
            if (error == ErrorSuccess)
                return error;
        }
        finally
        {
            Marshal.FreeHGlobal(parameters);
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(filter);
        }

        return EnableTraceEx2(_session, ref provider, EventControlCodeEnableProvider, TraceLevelVerbose,
            ulong.MaxValue, 0, 0, IntPtr.Zero);
    }

    private void Pump()
    {
        try
        {
            // Возвращается, когда сессию закрыли (CloseTrace) или остановили.
            ProcessTrace(new[] { _trace }, 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        if (Volatile.Read(ref _disposed) == 0)
            Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnEventRecord(IntPtr record)
    {
        // Исключение, вылетевшее в код Windows, уронило бы процесс.
        try
        {
            var header = Marshal.PtrToStructure<EventRecord>(record).EventHeader;
            if (header.ProviderId != DnsClientProvider || header.Id is not (EventQueryStarted or EventQueryCompleted))
                return;

            if (!TryReadQuery(record, out var name, out var type))
                return;

            var time = header.TimeStamp > 0 ? DateTime.FromFileTimeUtc(header.TimeStamp) : DateTime.UtcNow;
            if (time.Year < 2000)
                time = DateTime.UtcNow; // на случай, если время пришло не в формате FILETIME

            _onEvent(new DnsQueryEvent(time, (int)header.ProcessId, name, type));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>Имя и тип запроса: по описанию события в Windows, а если оно недоступно — по порядку полей.</summary>
    private bool TryReadQuery(IntPtr record, out string name, out ushort type)
    {
        name = string.Empty;
        type = 0;

        var nameSize = ReadProperty(record, QueryNameProperty);
        if (nameSize >= 2)
        {
            name = Encoding.Unicode.GetString(_buffer, 0, nameSize).TrimEnd('\0');
            var typeSize = ReadProperty(record, QueryTypeProperty);
            if (typeSize >= 2)
                type = BitConverter.ToUInt16(_buffer, 0);
            return name.Length > 0;
        }

        // Запасной путь: оба события начинаются с QueryName (строка UTF-16 с нулём) и QueryType (UInt32).
        var data = Marshal.PtrToStructure<EventRecord>(record);
        int length = data.UserDataLength;
        if (data.UserData == IntPtr.Zero || length < 6)
            return false;

        var chars = new StringBuilder();
        var offset = 0;
        while (offset + 1 < length)
        {
            var ch = (char)Marshal.ReadInt16(data.UserData, offset);
            offset += 2;
            if (ch == '\0')
                break;
            chars.Append(ch);
        }

        if (chars.Length == 0)
            return false;

        name = chars.ToString();
        if (offset + 4 <= length)
            type = (ushort)Marshal.ReadInt32(data.UserData, offset);
        return true;
    }

    /// <summary>Читает свойство события в буфер; возвращает размер или -1.</summary>
    private int ReadProperty(IntPtr record, IntPtr propertyName)
    {
        var descriptor = new PropertyDataDescriptor { PropertyName = (ulong)propertyName.ToInt64(), ArrayIndex = uint.MaxValue };
        if (TdhGetPropertySize(record, 0, IntPtr.Zero, 1, ref descriptor, out var size) != ErrorSuccess ||
            size == 0 || size > _buffer.Length)
            return -1;

        return TdhGetProperty(record, 0, IntPtr.Zero, 1, ref descriptor, size, _buffer) == ErrorSuccess ? (int)size : -1;
    }

    /// <summary>StartTrace или ControlTrace с буфером EVENT_TRACE_PROPERTIES, в котором лежит имя сессии.</summary>
    private static int Control(ulong handle, string name, uint code, bool start, out ulong session)
    {
        session = 0;
        var structSize = Marshal.SizeOf<EventTraceProperties>();
        var total = structSize + (name.Length + 1) * 2 + 1024; // запас под имя файла, которое Windows может дописать
        var buffer = Marshal.AllocHGlobal(total);
        try
        {
            for (var i = 0; i < total; i++)
                Marshal.WriteByte(buffer, i, 0);

            Marshal.StructureToPtr(new EventTraceProperties
            {
                Wnode = new WnodeHeader
                {
                    BufferSize = (uint)total,
                    Flags = WnodeFlagTracedGuid,
                    ClientContext = 1 // время по QueryPerformanceCounter
                },
                BufferSize = 64, // КБ
                MinimumBuffers = 4,
                MaximumBuffers = 64,
                LogFileMode = EventTraceRealTimeMode,
                FlushTimer = 1, // секунда: события доходят почти сразу
                LoggerNameOffset = (uint)structSize
            }, buffer, false);

            return start ? StartTraceW(out session, name, buffer) : ControlTraceW(handle, name, buffer, code);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Control(ulong handle, string name, uint code) => Control(handle, name, code, start: false, out _);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!IsInvalidTraceHandle(_trace))
            CloseTrace(_trace);
        if (_session != 0)
            Control(_session, _name, EventTraceControlStop);

        // Имя сессии освобождаем, только когда поток чтения точно закончил.
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(3)))
        {
            if (_loggerName != IntPtr.Zero)
                Marshal.FreeHGlobal(_loggerName);
            _loggerName = IntPtr.Zero;
        }
    }
}
