using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Что показать в окне выгрузки: варианты периода, выбранный, состояние скрытия и журналов DNS.</summary>
/// <param name="AddressesHidden">Журналы уже пишутся со скрытием адресов: галочка отмечена и неактивна.</param>
/// <param name="ServiceProblem">Почему журналов службы нет (по последней попытке), иначе null.</param>
public sealed record ExportChoices(IReadOnlyList<ExportPeriod> Periods, string DefaultPreset, bool AddressesHidden, bool HasDns, string? ServiceProblem);

/// <summary>Действия с журналами для окон (выгрузка, технический журнал, журнал событий).</summary>
public interface ILogActions
{
    ExportChoices GetExportChoices(string? requestedPreset);

    /// <summary>Собрать все для архива за период. Вызывать из UI-потока: снимки состояния берутся сразу, чтение журналов в фоне.</summary>
    Task<ExportInput> GatherExportAsync(ExportOptions options, IProgress<string>? progress, CancellationToken cancellation);

    /// <summary>Идет перемаскировка сохраненных журналов (скрытие адресов только что включили).</summary>
    bool IsRemasking { get; }

    /// <summary>Архив сохранен: уведомление «Показать в папке».</summary>
    void OnExported(string path, string? warning);

    /// <summary>Отметка проблемы во всех журналах. Возвращает id отметки.</summary>
    string Mark(string? text);

    bool IsDetailedLog { get; set; }

    DateTime? DetailedLogUntilUtc { get; }

    /// <summary>Имена тегов подключений и приложений (для показа строк ядра в окне).</summary>
    IReadOnlyDictionary<string, string> TagNames();

    void ShowExport(string? preset);

    void ShowTechLog();
}

/// <summary>Журналы: настройки раздела «Журналы», отметка проблемы, выгрузка для анализа, запись изменений настроек.</summary>
public sealed partial class MainViewModel
{
    private DateTime? _lastMarkUtc;
    private bool _markSearched;
    private string? _logUsageText;
    private string? _logRemaskText;
    private long _logUsageBytes;

    public ICommand ShowTechLogCommand { get; private set; } = null!;
    public ICommand ShowExportCommand { get; private set; } = null!;
    public ICommand DeleteAllLogsCommand { get; private set; } = null!;

    private void CreateLogCommands()
    {
        ShowTechLogCommand = new RelayCommand(ShowTechLog);
        ShowExportCommand = new RelayCommand(p => ShowExport(p as string));
        DeleteAllLogsCommand = new RelayCommand(() => _ = DeleteAllLogsAsync());
    }

    // ---------------------------------------------------------------- настройки журналов в силу

    /// <summary>При запуске: журналы по сохраненным настройкам, адреса серверов и подписок — маскировщику.</summary>
    private void ApplyLogSettings()
    {
        _settings.Logs = (_settings.Logs ?? new LogSettings()).Normalized();
        TechLog.Configure(_settings.Logs);
        _log.Persist = _settings.Logs.Storage == LogStorage.Files;
        RefreshLogMasks();
        _settingsSnapshot = SettingsSnapshot.Flatten(_settings);
        ResetLogDraft();
    }

    /// <summary>Серверы подключений и адреса подписок для маскировщика (меняются вместе с настройками).</summary>
    private void RefreshLogMasks()
    {
        try
        {
            TechLog.SetServers(SettingsSnapshot.Servers(_settings).ToList());
            TechLog.SetSecrets(SettingsSnapshot.Secrets(_settings).ToList());
        }
        catch (Exception ex)
        {
            TechLog.Warn(LogAreas.Settings, LogCodes.SettingsChange, "Не удалось обновить список скрываемых адресов: " + ex.Message);
        }
    }

    /// <summary>Перед сохранением настроек: что поменялось — в технический журнал.</summary>
    private void LogSettingsChanges()
    {
        try
        {
            var now = SettingsSnapshot.Flatten(_settings);
            var changes = SettingsSnapshot.Diff(_settingsSnapshot, now);
            _settingsSnapshot = now;
            if (changes.Count == 0)
                return;
            RefreshLogMasks();
            TechLog.Info(LogAreas.Settings, LogCodes.SettingsChange, "Изменены настройки: " + string.Join("; ", changes));
        }
        catch (Exception ex)
        {
            TechLog.Warn(LogAreas.Settings, LogCodes.SettingsChange, "Не удалось сравнить настройки: " + ex.Message);
        }
    }

    /// <summary>Строка о запуске приложения: условия и что уже работает (порядок запусков важен для разбора).</summary>
    private string DescribeLaunch(AppEntry entry)
    {
        var plan = PlanFor(entry);
        var running = Apps.Where(a => a.IsRunning && a.Entry.Id != entry.Id)
            .Select(a => $"«{a.Name}» ({ProcessTracker.ProcessesText(a.RunningCount)}{(_network.IsViaCore(a.Entry.Id) ? ", через ядро" : string.Empty)})")
            .ToList();
        return $"{DescribeApp(entry)}; план {plan.Kind} ({plan.Source})" +
               (plan.Connection is { } c ? $", подключение «{c.Name}» [{CorePlan.ConnectionTag(c.Id)}]" : string.Empty) +
               $", тег {CorePlan.AppTag(entry.Id)}, закрывать при обрыве {(plan.CloseOnDrop ? "да" : "нет")}" +
               (plan.UsesLauncherCore || plan.OutsideCopies == OutsideCopies.Launcher ? $", копии мимо лаунчера {plan.OutsideCopies}" : string.Empty) +
               $"; уже работают: {(running.Count == 0 ? "ничего" : string.Join(", ", running))}";
    }

    /// <summary>Когда окно уже показано: настройки журналов службе, таймер подробного журнала, сообщение о прошлом сбое.</summary>
    private void OnStartupLogs()
    {
        _logTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => OnLogTimer(), _dispatcher);
        _logTimer.Start();
        if (TechLog.IsInitialized)
            _ = RefreshLogUsageAsync();

        if (TechLog.Previous is not null)
        {
            if (_settings.Logs.Storage == LogStorage.Memory)
                Toast("Лаунчер в прошлый раз закрылся с ошибкой", "Журналы прошлого сеанса не сохранились: включено хранение только пока лаунчер открыт",
                    GlyphInfo, ToastKind.Danger);
            else
                Toast("Лаунчер в прошлый раз закрылся с ошибкой", "Описание ошибки сохранено", GlyphInfo, ToastKind.Danger,
                    "Выгрузить журналы", () => ShowExport(ExportPeriods.Previous));
        }
    }

    private void OnLogTimer()
    {
        if (TechLog.ExpireDetailed())
        {
            _settings.Logs.DetailedUntilUtc = null;
            SaveSettings();
            RaiseLogProperties();
        }
    }

    // ---------------------------------------------------------------- раздел «Журналы»

    // Черновик раздела «Журналы»: как остальные настройки, вступает в силу по «Применить» (ApplySettings).
    // Подробный журнал в черновик не входит: он временный и включается сразу.
    private LogStorage _draftLogStorage;
    private int _draftLogRetention = LogSettings.DefaultRetentionDays;
    private bool _draftHideLogAddresses;

    /// <summary>В разделе «Журналы» есть неприменённые изменения.</summary>
    private bool HasLogDraftChanges =>
        _settings.Logs is { } logs &&
        (_draftLogStorage != logs.Storage ||
         _draftLogRetention != logs.RetentionDays ||
         _draftHideLogAddresses != logs.HideAddresses);

    private void ResetLogDraft()
    {
        var logs = _settings.Logs ?? new LogSettings();
        _draftLogStorage = logs.Storage;
        _draftLogRetention = logs.RetentionDays;
        _draftHideLogAddresses = logs.HideAddresses;
        RaiseLogProperties();
    }

    /// <summary>Перед применением: переход на «только в памяти» удаляет журналы на диске, спрашиваем. false — не применять ничего.</summary>
    private bool ConfirmLogDraft()
    {
        if (_draftLogStorage != LogStorage.Memory || _settings.Logs.Storage == LogStorage.Memory)
            return true;
        return _dialogs.Confirm("Удалить журналы, уже сохраненные на диске?",
            "Журналы будут храниться только пока лаунчер открыт. Сохраненные на диске журналы лаунчера, службы и подключений будут удалены.",
            "Удалить", destructive: true);
    }

    /// <summary>Применить черновик журналов (сохраняет настройки вызывающий).</summary>
    private void ApplyLogDraft()
    {
        if (!HasLogDraftChanges)
            return;
        var shorter = _draftLogRetention < _settings.Logs.RetentionDays;
        ChangeLogs(s =>
        {
            s.Storage = _draftLogStorage;
            s.RetentionDays = _draftLogRetention;
            s.HideAddresses = _draftHideLogAddresses;
        }, save: false, freedFrom: shorter ? _logUsageBytes : null);
    }

    private void SetLogDraft<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
        RaiseLogProperties();
        OnDraftChanged();
    }

    /// <summary>«Только пока лаунчер открыт» (черновик).</summary>
    public bool LogsInMemory
    {
        get => _draftLogStorage == LogStorage.Memory;
        set => SetLogDraft(ref _draftLogStorage, value ? LogStorage.Memory : LogStorage.Files);
    }

    public bool LogsInFiles
    {
        get => !LogsInMemory;
        set
        {
            if (value)
                LogsInMemory = false;
        }
    }

    public IReadOnlyList<int> LogRetentionChoices => LogSettings.RetentionChoices;

    /// <summary>Срок хранения в днях (черновик).</summary>
    public int LogRetentionDays
    {
        get => _draftLogRetention;
        set => SetLogDraft(ref _draftLogRetention, value);
    }

    /// <summary>Скрывать IP-адреса (черновик).</summary>
    public bool HideLogAddresses
    {
        get => _draftHideLogAddresses;
        set => SetLogDraft(ref _draftHideLogAddresses, value);
    }

    /// <summary>
    /// Подробный журнал на час: переключатель в настройках действует сразу, без «Применить»
    /// (временный режим, а не настройка). Через час выключается сам (OnLogTimer).
    /// </summary>
    public bool IsDetailedLog
    {
        get => _settings.Logs.IsDetailed(DateTime.UtcNow);
        set
        {
            if (value == IsDetailedLog)
                return;
            ChangeLogs(s => s.DetailedUntilUtc = value ? DateTime.UtcNow + LogSettings.DetailedDuration : null);
        }
    }

    public DateTime? DetailedLogUntilUtc => IsDetailedLog ? _settings.Logs.DetailedUntilUtc : null;

    /// <summary>Метка рядом с названием: «до 12:41».</summary>
    public string DetailedLogText => DetailedLogUntilUtc is { } until ? $"до {until.ToLocalTime():HH:mm}" : string.Empty;

    /// <summary>«Сейчас занято 34 МБ» или «Освобождено 120 МБ».</summary>
    public string? LogUsageText
    {
        get => _logUsageText;
        private set => SetField(ref _logUsageText, value);
    }

    /// <summary>«Скрываю адреса в сохраненных журналах…», пока идет перемаскировка.</summary>
    public string? LogRemaskText
    {
        get => _logRemaskText;
        private set
        {
            if (SetField(ref _logRemaskText, value))
                OnPropertyChanged(nameof(IsRemasking));
        }
    }

    public bool IsRemasking => _logRemaskText is not null;

    private void ChangeLogs(Action<LogSettings> change, bool save = true, long? freedFrom = null)
    {
        var before = _settings.Logs;
        var next = before.Clone();
        change(next);
        next = next.Normalized();
        _settings.Logs = next;

        TechLog.Configure(next);
        _log.Persist = next.Storage == LogStorage.Files;
        if (save)
            SaveSettings();
        RaiseLogProperties();

        if (!before.HideAddresses && next.HideAddresses)
            _ = WatchRemaskAsync();
        _ = RefreshLogUsageAsync(freedFrom);
    }

    private void RaiseLogProperties()
    {
        OnPropertyChanged(nameof(LogsInMemory));
        OnPropertyChanged(nameof(LogsInFiles));
        OnPropertyChanged(nameof(LogRetentionDays));
        OnPropertyChanged(nameof(HideLogAddresses));
        OnPropertyChanged(nameof(IsDetailedLog));
        OnPropertyChanged(nameof(DetailedLogUntilUtc));
        OnPropertyChanged(nameof(DetailedLogText));
    }

    private async Task WatchRemaskAsync()
    {
        LogRemaskText = "Скрываю адреса в сохраненных журналах…";
        await TechLog.WaitRemaskAsync();
        LogRemaskText = null;
    }

    /// <summary>Настройки журналов службе (и ключ меток от нее), затем подпись «Сейчас занято».</summary>
    private async Task RefreshLogUsageAsync(long? freedFrom = null)
    {
        ServiceLogState? state;
        try
        {
            state = await ServiceLogs.ConfigureAsync(_settings.Logs);
        }
        catch (Exception)
        {
            state = null; // служба недоступна: занятое место — только лаунчера
        }
        // ключ меток мог смениться: журнал лаунчера пишет уже с ключом службы
        var bytes = TechLog.UsageBytes() + (state is null ? 0 : state.ServiceBytes + state.CoreBytes);
        _logUsageBytes = bytes;
        LogUsageText = freedFrom is { } before && before > bytes
            ? $"Освобождено {FormatBytes(before - bytes)}, занято {FormatBytes(bytes)}"
            : $"Сейчас занято {FormatBytes(bytes)}";
    }

    internal static string FormatBytes(long bytes)
    {
        var mb = bytes / 1024d / 1024d;
        if (mb < 0.1)
            return "меньше 0,1 МБ";
        return (mb < 10 ? mb.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) : Math.Round(mb).ToString(CultureInfo.InvariantCulture)) + " МБ";
    }

    private async Task DeleteAllLogsAsync()
    {
        if (!_dialogs.Confirm("Удалить все журналы?",
                "Журнал событий, технический журнал и журналы службы будут удалены без возможности восстановления.", "Удалить", destructive: true))
            return;

        var freed = TechLog.Clear();
        _log.Clear();
        string? problem = null;
        try
        {
            freed += await ServiceLogs.ClearAsync();
        }
        catch (Exception ex) when (ex is CoreServiceException or TimeoutException or IOException or OperationCanceledException)
        {
            problem = ServiceLogs.Explain(ex);
        }

        _lastMarkUtc = null;
        await RefreshLogUsageAsync();
        LogUsageText = $"Удалено {FormatBytes(freed)}" + (problem is null ? string.Empty : ". Журналы службы не удалены: " + problem);
    }

    // ---------------------------------------------------------------- отметка, окна

    public string Mark(string? text)
    {
        var cid = TechLog.Mark(text);
        _lastMarkUtc = DateTime.UtcNow;
        _markSearched = true;
        _ = ServiceLogs.MarkAsync(cid, text);
        Toast($"Отметка поставлена {DateTime.Now:HH:mm}", "Когда будете готовы, выгрузите журналы", GlyphCheck, ToastKind.Info,
            "Выгрузить", () => ShowExport(null));
        return cid;
    }

    public void ShowExport(string? preset) => _dialogs.ShowExport(this, preset);

    public void ShowTechLog() => _dialogs.ShowTechLog(this);

    public IReadOnlyDictionary<string, string> TagNames()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in _settings.Connections)
            result[CorePlan.ConnectionTag(c.Id)] = c.Name;
        foreach (var a in _settings.Apps)
            result[CorePlan.AppTag(a.Id)] = a.Name;
        return result;
    }

    /// <summary>Последняя отметка: в этом сеансе или (один раз ищется) за последние сутки в журнале лаунчера.</summary>
    private DateTime? LastMarkUtc()
    {
        if (!_markSearched)
        {
            _markSearched = true;
            var now = DateTime.UtcNow;
            _lastMarkUtc = TechLog.Read(now.AddDays(-1), now, l => l.Code == LogCodes.Marker).LastOrDefault()?.TimeUtc;
        }
        return _lastMarkUtc;
    }

    public ExportChoices GetExportChoices(string? requestedPreset)
    {
        var now = DateTime.UtcNow;
        var mark = LastMarkUtc();
        var periods = ExportPeriods.Build(now, TechLog.SessionStartUtc, TechLog.Previous, mark);
        var hasDns = _settings.Apps.Any(a => _dns.FindJournal(a.Id) is { IsEmpty: false });
        return new ExportChoices(periods, ExportPeriods.DefaultPreset(periods, now, mark, requestedPreset), _settings.Logs.HideAddresses, hasDns,
            ServiceLogs.Unavailable);
    }

    public void OnExported(string path, string? warning)
    {
        TechLog.Info(LogAreas.Session, LogCodes.LogConfig, "Журналы выгружены для анализа" + (warning is null ? string.Empty : ": " + warning));
        Toast("Журналы сохранены", warning ?? Path.GetFileName(path), GlyphCheck, warning is null ? ToastKind.Success : ToastKind.Info,
            "Показать в папке", () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // проводник не открылся: файл все равно сохранен
                }
            });
    }

    public async Task<ExportInput> GatherExportAsync(ExportOptions options, IProgress<string>? progress, CancellationToken cancellation)
    {
        var period = options.Period;

        // Снимки берутся сразу, в UI-потоке: состояние лаунчера меняется только в нем.
        var settings = SettingsSnapshot.ToJson(_settings, s => s);
        var tags = SettingsSnapshot.Tags(_settings);
        var state = DescribeStateForExport();
        var environment = DescribeEnvironmentForExport();
        var events = _log.Snapshot();
        var dns = options.IncludeDns
            ? _settings.Apps.Select(a => _dns.FindJournal(a.Id) is { IsEmpty: false } j ? new ExportDnsJournal(a.Name, j.Snapshot().Entries) : null)
                .Where(j => j is not null).Select(j => j!).ToList()
            : new List<ExportDnsJournal>();
        var logging = _settings.Logs.Clone();

        progress?.Report("Жду, пока скроются адреса в сохраненных журналах…");
        await TechLog.WaitRemaskAsync();

        progress?.Report("Читаю журнал лаунчера…");
        var launcher = await Task.Run(() =>
        {
            TechLog.Store?.Flush();
            return TechLog.Read(period.FromUtc, period.ToUtc);
        }, cancellation);
        var crashes = await Task.Run(() => LogFiles.InPeriod(TechLog.Directory, CrashReport.FilePrefix, CrashReport.FilePattern, period.FromUtc, period.ToUtc)
            .Select(f => ReadFile(f.Path, f.Utc)).Where(f => f is not null).Select(f => f!).ToList(), cancellation);

        IReadOnlyList<LogLine> service = Array.Empty<LogLine>();
        IReadOnlyList<ExportFile> serviceFiles = Array.Empty<ExportFile>();
        string? problem = null;
        string? serviceVersion = null;
        int? protocol = null;
        try
        {
            progress?.Report("Читаю журналы службы и подключений…");
            using (var client = await CoreServiceClient.ConnectAsync(TimeSpan.FromSeconds(3), cancellation))
            {
                var status = await client.StatusAsync(cancellation);
                serviceVersion = status.ServiceVersion;
                protocol = status.Protocol;
            }
            service = await ServiceLogs.ReadAsync(period.FromUtc, period.ToUtc, null,
                new Progress<int>(n => progress?.Report($"Читаю журналы службы и подключений… {n:N0} строк")), cancellation);
            serviceFiles = (await ServiceLogs.FilesAsync(period.FromUtc, period.ToUtc, cancellation))
                .Select(f => new ExportFile(f.Name, f.Utc, f.Content)).ToList();
        }
        catch (Exception ex) when (ex is CoreServiceException or TimeoutException or IOException)
        {
            problem = ServiceLogs.Explain(ex);
        }

        return new ExportInput
        {
            Launcher = launcher,
            Service = service,
            ServiceProblem = problem,
            ServiceFiles = serviceFiles,
            LauncherCrashes = crashes,
            Events = events,
            Dns = dns,
            Settings = settings,
            Environment = environment,
            State = state,
            Tags = tags,
            Logging = logging,
            MaskKeyShared = TechLog.MaskKeyShared,
            LauncherVersion = UpdateService.Format(UpdateService.CurrentVersion),
            ServiceVersion = serviceVersion,
            Protocol = protocol,
            LauncherDropped = TechLog.Store?.Dropped ?? 0,
            ServiceDropped = ServiceLogs.LastState?.Dropped
        };
    }

    private static ExportFile? ReadFile(string path, DateTime utc)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return new ExportFile(Path.GetFileName(path), utc, reader.ReadToEnd());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>state.json: работающие приложения, ядро, упавшие подключения, фильтры, журнал DNS.</summary>
    private JsonObject DescribeStateForExport()
    {
        var running = new JsonArray();
        foreach (var tile in Apps.Where(a => a.IsRunning))
        {
            running.Add(new JsonObject
            {
                ["name"] = tile.Name,
                ["tag"] = CorePlan.AppTag(tile.Entry.Id),
                ["processes"] = tile.RunningCount,
                ["viaCore"] = _network.IsViaCore(tile.Entry.Id),
                ["offline"] = tile.IsOffline,
                ["state"] = _network.StateOf(tile.Entry.Id).ToString()
            });
        }

        JsonObject guard;
        try
        {
            var request = BuildGuardRequest();
            guard = new JsonObject
            {
                ["applied"] = _guard.IsApplied,
                ["rules"] = request.Apps.Count,
                ["viaCore"] = request.Apps.Count(a => a.ViaCore),
                ["dnsInterfaces"] = request.DnsInterfaces?.Count ?? 0
            };
        }
        catch (Exception ex)
        {
            guard = new JsonObject { ["problem"] = ex.Message };
        }

        return new JsonObject
        {
            ["createdUtc"] = LogLine.FormatTime(DateTime.UtcNow),
            ["running"] = running,
            ["network"] = _network.DescribeState(),
            ["guard"] = guard,
            ["vpnConnected"] = _monitor.Current.IsConnected,
            ["dnsJournalActive"] = _dns.IsActive,
            ["killSwitch"] = _settings.KillSwitchEnabled
        };
    }

    /// <summary>environment.json: адаптеры, системный VPN и прокси, IPv6, служба, версии.</summary>
    private JsonObject DescribeEnvironmentForExport()
    {
        var current = _monitor.Current;
        CoreServiceState service;
        try
        {
            service = CoreServiceClient.QueryService();
        }
        catch (Exception)
        {
            service = default;
        }

        return new JsonObject
        {
            ["adapters"] = NetworkSnapshot.ToJson(a => a),
            ["vpnDetected"] = current.IsConnected,
            ["activeVpn"] = new JsonArray(current.ActiveVpnNames.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["vpnExplanation"] = current.Explanation,
            ["ipv6"] = _lastIpv6,
            ["systemProxy"] = _proxy.Current?.Display,
            ["service"] = new JsonObject
            {
                ["installed"] = service.Installed,
                ["running"] = service.Running,
                ["pid"] = service.ProcessId
            },
            ["os"] = Environment.OSVersion.VersionString,
            ["dotnet"] = Environment.Version.ToString(),
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["culture"] = CultureInfo.CurrentCulture.Name
        };
    }
}
