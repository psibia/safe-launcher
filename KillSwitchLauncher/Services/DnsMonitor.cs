using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>Какому приложению лаунчера принадлежит процесс (см. ProcessTracker.TryGetOwner).</summary>
public delegate bool DnsOwnerResolver(int processId, out Guid appId, out string processName);

/// <summary>
/// Журналы DNS по приложениям. Источник событий видит DNS-запросы всей системы; монитор оставляет только
/// запросы процессов тех приложений, у которых журнал включён, и раскладывает их по журналам.
/// Чужие запросы сразу отбрасываются и нигде не хранятся.
///
/// Источник запускается по требованию (StartAsync) и останавливается, когда журнал выключен у всех приложений.
/// Журнал приложения хранится в памяти до закрытия лаунчера, даже если его выключили: записи можно
/// дочитать и скопировать.
/// </summary>
public sealed class DnsMonitor : IDisposable
{
    /// <summary>Одинаковый запрос того же процесса чаще этого считается одним (Windows пишет начало и конец запроса).</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1);

    /// <summary>Сколько помнить, что процесс не относится к приложениям лаунчера.</summary>
    private static readonly TimeSpan StrangerKeep = TimeSpan.FromSeconds(2);

    private readonly DnsOwnerResolver _resolveOwner;
    private readonly Func<Action<DnsQueryEvent>, Task<IDnsEventSource>> _startSource;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DnsJournal> _journals = new();
    private readonly HashSet<Guid> _tracked = new();
    private readonly Dictionary<int, DateTime> _strangers = new();
    private readonly Dictionary<(int Pid, string Domain, ushort Type), DateTime> _recent = new();

    private IDnsEventSource? _source;
    private Task? _starting;
    private bool _disposed;

    /// <param name="resolveOwner">Чей процесс.</param>
    /// <param name="startSource">Как запустить источник событий; по умолчанию — DnsSources.StartAsync. Тесты передают свой.</param>
    /// <param name="capacity">Сколько записей хранит журнал одного приложения.</param>
    public DnsMonitor(DnsOwnerResolver resolveOwner, Func<Action<DnsQueryEvent>, Task<IDnsEventSource>>? startSource = null,
        int capacity = DnsJournal.DefaultCapacity)
    {
        _resolveOwner = resolveOwner;
        _startSource = startSource ?? DnsSources.StartAsync;
        _capacity = capacity;
    }

    /// <summary>Источник запустился или остановился. Любой поток.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Источник остановился сам, хотя журнал включён (например, помощник закрыли). Любой поток.</summary>
    public event EventHandler? SourceLost;

    /// <summary>Запросы сейчас записываются.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _source is not null;
        }
    }

    public bool IsTracking(Guid appId)
    {
        lock (_gate)
            return _tracked.Contains(appId);
    }

    public bool HasTracked
    {
        get
        {
            lock (_gate)
                return _tracked.Count > 0;
        }
    }

    public DnsJournal? FindJournal(Guid appId)
    {
        lock (_gate)
            return _journals.GetValueOrDefault(appId);
    }

    /// <summary>Журнал приложения (создаётся при первом обращении).</summary>
    public DnsJournal Journal(Guid appId, string appName)
    {
        lock (_gate)
        {
            if (!_journals.TryGetValue(appId, out var journal))
                _journals[appId] = journal = new DnsJournal(appName, _capacity);
            journal.AppName = appName;
            return journal;
        }
    }

    /// <summary>Включить или выключить журнал приложения. Источник не запускает: для этого StartAsync.</summary>
    public void SetTracking(Guid appId, string appName, bool on)
    {
        lock (_gate)
        {
            var journal = Journal(appId, appName);
            if (on)
            {
                if (_tracked.Add(appId) && !journal.IsEmpty)
                    journal.AddMarker(DateTime.Now, "Журнал снова включён");
            }
            else if (_tracked.Remove(appId) && !journal.IsEmpty)
            {
                journal.AddMarker(DateTime.Now, "Журнал выключен");
            }
        }
    }

    /// <summary>Приложение запущено из лаунчера: в непустом журнале ставится разделитель.</summary>
    public void NoteLaunch(Guid appId)
    {
        lock (_gate)
        {
            // Новые процессы могли получить PID, который недавно был «чужим».
            _strangers.Clear();

            if (_tracked.Contains(appId) && _journals.TryGetValue(appId, out var journal) && !journal.IsEmpty)
                journal.AddMarker(DateTime.Now, "Новый запуск");
        }
    }

    /// <summary>Приложение убрали из лаунчера: журнал больше не нужен.</summary>
    public void Forget(Guid appId)
    {
        lock (_gate)
        {
            _tracked.Remove(appId);
            _journals.Remove(appId);
        }

        StopIfIdle();
    }

    /// <summary>
    /// Запускает источник, если он ещё не работает. Синхронная часть (в том числе окно UAC для помощника)
    /// выполняется в вызывающем потоке. Ошибку запуска пробрасывает: Win32Exception 1223 — отказ в UAC.
    /// </summary>
    public async Task StartAsync()
    {
        Task? pending;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || _source is not null)
                return;

            pending = _starting;
            if (pending is null)
                _starting = done.Task;
        }

        if (pending is not null)
        {
            // Запуск уже идёт (например, второй клик): ждём его же.
            await pending;
            return;
        }

        try
        {
            var source = await _startSource(OnQuery);
            var started = false;
            lock (_gate)
            {
                if (!_disposed && _tracked.Count > 0)
                {
                    _source = source;
                    source.Stopped += OnSourceStopped;
                    started = true;
                }
            }

            if (started)
                StateChanged?.Invoke(this, EventArgs.Empty);
            else
                source.Dispose(); // пока запускали, журнал успели выключить

            done.SetResult();
        }
        catch (Exception ex)
        {
            done.SetException(ex);
            _ = done.Task.Exception; // ошибку получит вызывающий; второму ожидающему она тоже придёт
            throw;
        }
        finally
        {
            lock (_gate)
                _starting = null;
        }
    }

    /// <summary>Журнал выключен у всех приложений: источник больше не нужен.</summary>
    public void StopIfIdle()
    {
        IDnsEventSource? source;
        lock (_gate)
        {
            if (_tracked.Count > 0 || _source is null)
                return;
            source = _source;
            _source = null;
            _recent.Clear();
            _strangers.Clear();
        }

        source.Stopped -= OnSourceStopped;
        source.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceStopped(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _source))
                return;
            _source = null;
        }

        (sender as IDisposable)?.Dispose();
        StateChanged?.Invoke(this, EventArgs.Empty);
        SourceLost?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Запрос от источника. Вызывается из его фонового потока.</summary>
    internal void OnQuery(DnsQueryEvent e)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_tracked.Count == 0)
                return;
            if (_strangers.TryGetValue(e.ProcessId, out var until) && until > now)
                return;
        }

        // Вне блокировки: проверка может открывать процесс.
        if (!_resolveOwner(e.ProcessId, out var appId, out var processName))
        {
            lock (_gate)
            {
                if (_strangers.Count > 4096)
                    _strangers.Clear();
                _strangers[e.ProcessId] = now + StrangerKeep;
            }

            return;
        }

        var domain = DnsNames.Normalize(e.Name);
        if (domain is null)
            return;

        lock (_gate)
        {
            if (!_tracked.Contains(appId) || !_journals.TryGetValue(appId, out var journal))
                return;

            var key = (e.ProcessId, domain, e.Type);
            if (_recent.TryGetValue(key, out var last) && (e.TimeUtc - last).Duration() < DuplicateWindow)
                return;

            if (_recent.Count > 4096)
            {
                foreach (var old in _recent.Where(r => (e.TimeUtc - r.Value).Duration() > DuplicateWindow).Select(r => r.Key).ToList())
                    _recent.Remove(old);
            }

            _recent[key] = e.TimeUtc;
            journal.AddQuery(e.TimeUtc.ToLocalTime(), domain, e.Type, e.ProcessId, processName);
        }
    }

    public void Dispose()
    {
        IDnsEventSource? source;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            source = _source;
            _source = null;
        }

        if (source is not null)
        {
            source.Stopped -= OnSourceStopped;
            source.Dispose();
        }
    }
}
