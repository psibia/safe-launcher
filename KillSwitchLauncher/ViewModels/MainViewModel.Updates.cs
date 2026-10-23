using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using SafeLauncher.Logging;
using SafeLauncher.Updates;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Шаг обновления лаунчера (страница «Обновления»). Автоматическая и ручная проверка проходят одни и те же шаги.</summary>
public enum UpdatePhase
{
    /// <summary>Обновлений нет или ещё не проверялось.</summary>
    Idle,

    Checking,

    /// <summary>Есть новая версия, ещё не скачана: кнопка «Скачать».</summary>
    Available,

    Downloading,

    /// <summary>Файл скачан и сверяется с подписью автора.</summary>
    Verifying,

    /// <summary>Скачано и проверено: кнопка «Обновить».</summary>
    Ready,

    /// <summary>Нажато «Обновить», но работают приложения: «Закрыть приложения и обновить».</summary>
    AppsRunning,

    /// <summary>Приложения закрываются; обновление начнётся, когда закроются все.</summary>
    ClosingApps,

    /// <summary>Обновление передаётся службе (или запускается установщик), лаунчер закроется.</summary>
    Installing
}

/// <summary>Цвет строки состояния обновления.</summary>
public enum UpdateTone
{
    Neutral,
    Accent,
    Success,
    Danger
}

/// <summary>
/// Обновления лаунчера (docs/DETAILS.md, «Обновления»): проверка при запуске и каждые 4 часа (после неудачи — через час),
/// автоматическая загрузка с проверкой подписи, страница «Обновления», закрытие приложений и передача обновления службе.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan UpdateRetryInterval = TimeSpan.FromHours(1);

    /// <summary>Сколько ждать, пока приложения закроются сами, прежде чем предложить закрыть принудительно.</summary>
    private static readonly TimeSpan AppsCloseGrace = TimeSpan.FromSeconds(15);

    private UpdatePhase _updatePhase = UpdatePhase.Idle;
    private UpdateCheckResult? _updateCheck;
    private DownloadedUpdate? _downloaded;
    private DownloadProgress _downloadProgress;
    private readonly Stopwatch _downloadWatch = new();
    private CancellationTokenSource? _downloadCancel;

    /// <summary>Текущая попытка загрузки: отменяется при смене маршрута, загрузка продолжается по новому.</summary>
    private CancellationTokenSource? _downloadAttempt;

    /// <summary>Загрузка начата сама (без «Обновить»): останавливается, если выключить автоматическую загрузку.</summary>
    private bool _downloadAutomatic;

    /// <summary>Пауза перед повтором загрузки: когда повтор и почему прервалось.</summary>
    private DateTime? _retryAtUtc;
    private string? _retryReason;

    /// <summary>Файл этой версии дважды не прошёл проверку или не совпал с подписью: сама она больше не скачивается.</summary>
    private Version? _rejectedVersion;
    private DateTime? _lastCheckUtc;
    private DateTime _nextCheckUtc = DateTime.MaxValue;
    private string? _updateError;
    private string _installStep = string.Empty;

    /// <summary>Прошлая установка этой версии не удалась (стало известно после перезапуска): показывается, пока не начата новая.</summary>
    private (Version Version, string Text)? _installFailure;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _appsTimer;
    private DateTime _closeRequestedUtc;
    private bool _closeTimedOut;
    private IReadOnlyList<RunningApp> _blockingApps = Array.Empty<RunningApp>();

    /// <summary>Обновление передаётся службе: ядро не запускается, приложения не запускаются.</summary>
    private bool _updateHandoff;

    /// <summary>Лаунчер закрывается ради установки обновления: всё уже подтверждено.</summary>
    private bool _closingForUpdate;

    /// <summary>Нажато «Обновить», пока версия ещё не скачана: установка начнётся сразу после загрузки.</summary>
    private bool _installAfterDownload;

    public ICommand CheckUpdatesCommand { get; private set; } = null!;
    public ICommand CancelUpdateDownloadCommand { get; private set; } = null!;
    public ICommand InstallUpdateCommand { get; private set; } = null!;
    public ICommand CloseAppsAndUpdateCommand { get; private set; } = null!;
    public ICommand ForceCloseAppsCommand { get; private set; } = null!;
    public ICommand CancelUpdateInstallCommand { get; private set; } = null!;
    public ICommand ShowChangelogCommand { get; private set; } = null!;

    private void CreateUpdateCommands()
    {
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(manual: true));
        CancelUpdateDownloadCommand = new RelayCommand(() => _downloadCancel?.Cancel());
        InstallUpdateCommand = new RelayCommand(InstallUpdate);
        CloseAppsAndUpdateCommand = new RelayCommand(CloseAppsForUpdate);
        ForceCloseAppsCommand = new RelayCommand(ForceCloseAppsForUpdate);
        CancelUpdateInstallCommand = new RelayCommand(CancelUpdateInstall);
        ShowChangelogCommand = new RelayCommand(ShowChangelog);

        // Раз в минуту: не пора ли проверить. Так проверка не теряется после сна и гибернации.
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += (_, _) => OnUpdateTimer();
        _appsTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _appsTimer.Tick += (_, _) => OnAppsTimer();
    }

    // ---------------------------------------------------------------- свойства для страницы «Обновления»

    public string AppVersionText => $"v{UpdateService.Format(UpdateService.CurrentVersion)}";

    /// <summary>«Безопасный лаунчер 2.3.3».</summary>
    public string InstalledVersionText => $"Безопасный лаунчер {UpdateService.Format(UpdateService.CurrentVersion)}";

    public UpdatePhase UpdatePhase => _updatePhase;

    /// <summary>Главная строка состояния: что происходит сейчас.</summary>
    public string UpdateStatusText => DescribeUpdate().Status;

    /// <summary>Вторая строка: подробности (скорость, что делать дальше, когда следующая проверка).</summary>
    public string UpdateDetailText => DescribeUpdate().Detail;

    public UpdateTone UpdateTone => DescribeUpdate().Tone;

    /// <summary>Есть новая версия (не во время проверки и загрузки).</summary>
    public bool IsUpdateAvailable => _updatePhase is UpdatePhase.Available or UpdatePhase.Ready or UpdatePhase.AppsRunning or UpdatePhase.ClosingApps;

    /// <summary>Значок на кнопке «Настройки» и у раздела «Обновления»: найдена новая версия, скачана она или нет.</summary>
    public bool IsUpdateFound => _updateCheck?.IsUpdateAvailable == true && _updatePhase != UpdatePhase.Idle;

    public bool IsUpdateNotAvailable => !IsUpdateAvailable;

    /// <summary>«Проверить» доступна, когда ничего не выполняется.</summary>
    public bool CanUseUpdates => _updatePhase is UpdatePhase.Idle or UpdatePhase.Available or UpdatePhase.Ready;

    public bool IsUpdateBusy => _updatePhase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying
        or UpdatePhase.ClosingApps or UpdatePhase.Installing;

    public bool IsUpdateProgressVisible => IsUpdateBusy;

    /// <summary>Сколько скачано, 0–100; при неизвестном размере и на остальных шагах полоса бежит без процентов.</summary>
    public double UpdateProgressValue =>
        _updatePhase == UpdatePhase.Downloading && _downloadProgress.Total is > 0 and var total ? 100.0 * _downloadProgress.Received / total : 0;

    /// <summary>Полоса без процентов. Только пока она видна: скрытая анимация впустую нагружала бы процессор.</summary>
    public bool IsUpdateProgressIndeterminate =>
        IsUpdateBusy && (_updatePhase != UpdatePhase.Downloading || _downloadProgress.Total is not > 0);

    public bool IsCancelDownloadVisible => _updatePhase == UpdatePhase.Downloading;

    /// <summary>
    /// «Обновить»: скачанная версия ставится сразу; ещё не скачанная сначала скачивается. Во время загрузки кнопка тоже есть:
    /// нажатая, она ставит обновление сразу после загрузки.
    /// </summary>
    public bool IsInstallUpdateVisible => _updatePhase is UpdatePhase.Available or UpdatePhase.Ready ||
                                          _updatePhase is UpdatePhase.Downloading or UpdatePhase.Verifying && !_installAfterDownload;

    /// <summary>«Что нового» есть, когда известен релиз: новый или установленный.</summary>
    public bool CanShowChangelog => _updateCheck is null || _updateCheck.Latest is not null || _updateCheck.CurrentRelease is not null;

    /// <summary>Перед установкой работают приложения: блок со списком и кнопками.</summary>
    public bool IsUpdateAppsPanelVisible => _updatePhase is UpdatePhase.AppsRunning or UpdatePhase.ClosingApps;

    public bool IsCloseAppsVisible => _updatePhase == UpdatePhase.AppsRunning;

    public bool IsForceCloseAppsVisible => _updatePhase == UpdatePhase.ClosingApps && _closeTimedOut;

    /// <summary>Работающие приложения, по одному в строке.</summary>
    public string UpdateAppsText => string.Join("\n", _blockingApps.Select(a => a.Outside ? a.Name + " (запущено не из лаунчера)" : a.Name));

    public string UpdateAppsHint => _updatePhase switch
    {
        UpdatePhase.AppsRunning =>
            "Обновление ставится, только когда приложения закрыты: так ему ничто не помешает. «Закрыть приложения и обновить» " +
            "закроет их, как крестиком. Можно закрыть их и самим: обновление начнётся сразу.",
        UpdatePhase.ClosingApps when _closeTimedOut =>
            "Эти приложения не закрылись. Возможно, они спрашивают, сохранить ли данные: ответьте им или закройте их сами. " +
            "Обновление начнётся, как только они закроются. Закрыть принудительно можно, но несохранённое пропадёт.",
        UpdatePhase.ClosingApps => "Приложения закрываются. Если какое-то спросит о несохранённых данных, ответьте ему.",
        _ => string.Empty
    };

    /// <summary>Когда была последняя удачная проверка.</summary>
    public string UpdateLastCheckText => _lastCheckUtc is { } last ? "Последняя проверка: " + When(last) : "Проверок ещё не было";

    // ---------------------------------------------------------------- настройки раздела «Обновления»

    // Черновик раздела «Обновления»: как остальные настройки, вступает в силу по «Применить» (ApplySettings).
    private bool _draftAutoUpdate = true;
    private UpdateRoute _draftUpdateRoute;
    private Guid? _draftUpdateConnectionId;

    /// <summary>В разделе «Обновления» есть неприменённые изменения.</summary>
    private bool HasUpdateDraftChanges =>
        _settings.Updates is { } updates &&
        (_draftAutoUpdate != updates.Auto ||
         _draftUpdateRoute != updates.Route ||
         _draftUpdateRoute == UpdateRoute.Connection && _draftUpdateConnectionId != updates.ConnectionId);

    /// <summary>Выбрана загрузка через подключение лаунчера, а само подключение — нет (черновик).</summary>
    private bool NeedsUpdateConnectionSelection =>
        _draftUpdateRoute == UpdateRoute.Connection && _settings.Connections.All(c => c.Id != _draftUpdateConnectionId);

    private void ResetUpdateDraft()
    {
        var updates = _settings.Updates ??= new UpdateSettings();
        _draftAutoUpdate = updates.Auto;
        _draftUpdateRoute = updates.Route;
        _draftUpdateConnectionId = updates.ConnectionId;
        OnPropertyChanged(nameof(AutoUpdate));
        OnPropertyChanged(nameof(UpdateViaConnection));
        OnPropertyChanged(nameof(UpdateDirect));
        OnPropertyChanged(nameof(UpdateConnectionId));
    }

    /// <summary>Применить черновик обновлений (сохраняет настройки вызывающий).</summary>
    private void ApplyUpdateDraft()
    {
        if (!HasUpdateDraftChanges)
            return;
        var updates = _settings.Updates;
        var autoChanged = _draftAutoUpdate != updates.Auto;
        var routeChanged = _draftUpdateRoute != updates.Route || _draftUpdateConnectionId != updates.ConnectionId;
        updates.Auto = _draftAutoUpdate;
        updates.Route = _draftUpdateRoute;
        if (_draftUpdateRoute == UpdateRoute.Connection)
            updates.ConnectionId = _draftUpdateConnectionId;

        if (autoChanged)
            TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck, updates.Auto
                ? "Автоматическая проверка и загрузка обновлений включена"
                : "Автоматическая проверка и загрузка обновлений выключена");
        if (routeChanged)
            TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck, "Обновления скачиваются " + DescribeUpdateRoute());
        RaiseUpdateProperties();
        if (routeChanged)
            RestartDownloadAttempt(); // идущая загрузка продолжится по новому маршруту
        if (autoChanged && !updates.Auto && _downloadAutomatic && !_installAfterDownload)
            _downloadCancel?.Cancel(); // автоматическая загрузка выключена: начатую саму останавливаем, недокачанное остаётся
        if (autoChanged && updates.Auto)
            _ = CheckForUpdatesAsync(manual: false);
    }

    private void RestartDownloadAttempt()
    {
        try
        {
            _downloadAttempt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // попытка уже закончилась
        }
    }

    private void SetUpdateDraft<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
        OnDraftChanged();
    }

    /// <summary>Автоматически проверять и скачивать обновления (черновик).</summary>
    public bool AutoUpdate
    {
        get => _draftAutoUpdate;
        set => SetUpdateDraft(ref _draftAutoUpdate, value);
    }

    /// <summary>Скачивать обновления через подключение лаунчера (черновик, скрытая настройка в «Дополнительно»).</summary>
    public bool UpdateViaConnection
    {
        get => _draftUpdateRoute == UpdateRoute.Connection;
        set
        {
            if (value && _draftUpdateConnectionId is null)
            {
                _draftUpdateConnectionId = _settings.ConnectionId ?? _settings.Connections.FirstOrDefault()?.Id;
                OnPropertyChanged(nameof(UpdateConnectionId));
            }

            SetUpdateDraft(ref _draftUpdateRoute, value ? UpdateRoute.Connection : UpdateRoute.Direct);
            OnPropertyChanged(nameof(UpdateDirect));
        }
    }

    public bool UpdateDirect
    {
        get => !UpdateViaConnection;
        set => UpdateViaConnection = !value;
    }

    /// <summary>Подключение для загрузки обновлений (черновик).</summary>
    public Guid? UpdateConnectionId
    {
        get => _draftUpdateConnectionId;
        set
        {
            if (value is not null)
                SetUpdateDraft(ref _draftUpdateConnectionId, value);
        }
    }

    private (string Status, string Detail, UpdateTone Tone) DescribeUpdate()
    {
        var latest = _updateCheck?.Latest;
        var version = latest is null ? string.Empty : UpdateService.Format(latest.Version);
        var auto = _settings.Updates.Auto;
        string Retry() => auto && _nextCheckUtc != DateTime.MaxValue ? $"Следующая попытка {When(_nextCheckUtc)}." : "Нажмите «Проверить», чтобы попробовать снова.";

        switch (_updatePhase)
        {
            case UpdatePhase.Checking:
                return ("Проверка обновлений…", "Запрос к GitHub " + DescribeUpdateRoute(), UpdateTone.Accent);

            case UpdatePhase.Available:
                if (_updateError is not null)
                    return (_updateError, auto ? Retry() + " Или нажмите «Обновить»." : "Нажмите «Обновить», чтобы попробовать снова.", UpdateTone.Danger);
                return ($"Доступна версия {version}",
                    (latest!.SetupSize > 0 ? $"Размер {Megabytes(latest.SetupSize)}. " : string.Empty) + "Нажмите «Обновить»: новая версия скачается и установится.",
                    UpdateTone.Accent);

            case UpdatePhase.Downloading when _retryAtUtc is { } retryAt:
            {
                var left = Math.Max(1, (int)Math.Ceiling((retryAt - DateTime.UtcNow).TotalSeconds));
                var saved = _downloadProgress.Received > 0 ? $"Скачанные {Megabytes(_downloadProgress.Received)} сохранены, загрузка продолжится с того же места. " : string.Empty;
                return ($"Загрузка версии {version} прервалась: {_retryReason}",
                    $"Повтор через {left} с. {saved}Остановить можно кнопкой «Отменить загрузку».", UpdateTone.Danger);
            }

            case UpdatePhase.Downloading:
            {
                var received = _downloadProgress.Received;
                var status = _downloadProgress.Total is > 0 and var total
                    ? $"Загрузка версии {version}: {Megabytes(received)} из {Megabytes(total)}"
                    : $"Загрузка версии {version}: {Megabytes(received)}";
                var seconds = _downloadWatch.Elapsed.TotalSeconds;
                var speed = seconds > 1 ? Math.Max(0, received - _downloadProgress.Resumed) / seconds : 0;
                var detail = speed > 0 ? $"{Megabytes((long)speed)}/с" : "Подключение к GitHub";
                if (_downloadProgress.Resumed > 0)
                    detail = $"Продолжаем с {Megabytes(_downloadProgress.Resumed)}, скачанных раньше. " + detail;
                if (speed > 0 && _downloadProgress.Total is > 0 and var size && size > received)
                    detail += ", осталось " + Duration(TimeSpan.FromSeconds((size - received) / speed));
                detail += " " + DescribeUpdateRoute();
                return (status, _installAfterDownload ? detail + ". После загрузки начнётся установка" : detail, UpdateTone.Accent);
            }

            case UpdatePhase.Verifying:
                return ($"Подготовка версии {version} к установке…",
                    _installAfterDownload ? "Проверяем, что файл не повреждён. Потом начнётся установка" : "Проверяем, что файл не повреждён", UpdateTone.Accent);

            case UpdatePhase.Ready:
                if (_updateError is not null)
                    return (_updateError, "Нажмите «Обновить», чтобы попробовать снова.", UpdateTone.Danger);
                if (_installFailure is { } failure && failure.Version == _downloaded?.Version)
                    return ($"Версия {version} не установилась", failure.Text + " Нажмите «Обновить», чтобы попробовать ещё раз.", UpdateTone.Danger);
                return ($"Версия {version} готова к установке",
                    _downloaded?.IsSigned == true && UpdateService.CanInstallViaService
                        ? "Нажмите «Обновить»: лаунчер закроется и через несколько секунд откроется новой версией."
                        : "Нажмите «Обновить»: Windows спросит разрешение на установку, потом лаунчер откроется новой версией.",
                    UpdateTone.Accent);

            case UpdatePhase.AppsRunning:
                return ("Чтобы обновиться, закройте приложения", $"Версия {version} готова к установке.", UpdateTone.Danger);

            case UpdatePhase.ClosingApps:
                return (_closeTimedOut ? "Не все приложения закрылись" : "Закрываем приложения…",
                    $"Версия {version} установится, когда они закроются.", _closeTimedOut ? UpdateTone.Danger : UpdateTone.Accent);

            case UpdatePhase.Installing:
                return (_installStep, $"Устанавливается версия {version}. Не закрывайте лаунчер: он закроется сам.", UpdateTone.Accent);
        }

        if (_updateError is not null)
            return (_updateError, Retry(), UpdateTone.Danger);
        if (_lastCheckUtc is null)
            return ("Ещё не проверялось", auto ? "Проверка запустится автоматически." : "Нажмите «Проверить».", UpdateTone.Neutral);
        return ("Установлена последняя версия", "Проверено " + When(_lastCheckUtc.Value) + ".", UpdateTone.Success);
    }

    private string DescribeUpdateRoute()
    {
        if (_settings.Updates.Route != UpdateRoute.Connection)
            return "напрямую";
        var connection = _settings.Connections.FirstOrDefault(c => c.Id == _settings.Updates.ConnectionId);
        return connection is null ? "через подключение лаунчера (не выбрано)" : $"через подключение «{connection.Name}»";
    }

    private void RaiseUpdateProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(UpdatePhase), nameof(UpdateStatusText), nameof(UpdateDetailText), nameof(UpdateTone), nameof(IsUpdateAvailable), nameof(IsUpdateFound),
                     nameof(IsUpdateNotAvailable), nameof(CanUseUpdates), nameof(IsUpdateBusy), nameof(IsUpdateProgressVisible),
                     nameof(UpdateProgressValue), nameof(IsUpdateProgressIndeterminate),
                     nameof(IsCancelDownloadVisible), nameof(IsInstallUpdateVisible), nameof(CanShowChangelog), nameof(IsUpdateAppsPanelVisible),
                     nameof(IsCloseAppsVisible), nameof(IsForceCloseAppsVisible), nameof(UpdateAppsText), nameof(UpdateAppsHint),
                     nameof(UpdateLastCheckText)
                 })
            OnPropertyChanged(name);
    }

    private void SetUpdatePhase(UpdatePhase phase)
    {
        _updatePhase = phase;

        // «Обновить» нажата до загрузки: версия готова — ставим; загрузка не удалась или отменена — просьба снимается.
        var install = false;
        if (_installAfterDownload && phase is not (UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Verifying))
        {
            install = phase == UpdatePhase.Ready && _updateError is null;
            _installAfterDownload = false;
        }

        RaiseUpdateProperties();
        if (install)
            _dispatcher.BeginInvoke(StartUpdateInstall);
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024)).ToString(bytes < 10 * 1024 * 1024 ? "0.0" : "0", CultureInfo.GetCultureInfo("ru-RU")) + " МБ";

    private static string Duration(TimeSpan time) =>
        time.TotalSeconds < 60 ? $"{Math.Max(1, (int)time.TotalSeconds)} с" : $"{(int)Math.Ceiling(time.TotalMinutes)} мин";

    /// <summary>«сегодня в 14:05», «вчера в 09:10», «12 октября в 18:30».</summary>
    private static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Today;
        var day = local.Date == today ? "сегодня"
            : local.Date == today.AddDays(-1) ? "вчера"
            : local.Date == today.AddDays(1) ? "завтра"
            : local.ToString("d MMMM", CultureInfo.GetCultureInfo("ru-RU"));
        return $"{day} в {local:HH:mm}";
    }

    private static string ShortReason(Exception ex) => ex switch
    {
        DownloadStalledException => "GitHub перестал присылать данные",
        UpdateRouteException => ex.Message,
        HttpRequestException { StatusCode: { } code } => $"GitHub ответил ошибкой {(int)code}",
        HttpRequestException => "нет связи с GitHub",
        TaskCanceledException => "GitHub не ответил вовремя",
        IOException io when UpdateService.IsDiskFull(io) => "не хватает места на диске",
        _ => ex.Message.Length <= 160 ? ex.Message.TrimEnd('.') : ex.Message[..157] + "…"
    };

    // ---------------------------------------------------------------- запуск и таймер

    private async void OnStartupIdle()
    {
        OnStartupLogs();

        // Эта версия только что установлена через службу: сообщить помощнику, что она запустилась, иначе он вернёт прежнюю.
        // Сообщить не вышло — итог обновления не забираем: его покажет прежняя версия, которую вернёт помощник.
        var confirmed = await ConfirmUpdateStartedAsync();

        // Итог обновления, запущенного в прошлый раз (лаунчер перезапустился после установки).
        var outcome = confirmed ? _updates.TakePendingOutcome() : null;
        if (outcome is not null)
            ReportUpdateOutcome(outcome);
        ReportSystemVpnRemoved();

        var repeated = RepeatedApps();
        if (repeated.Count > 0 && outcome is null)
            Toast("Одна программа добавлена несколько раз", string.Join("; ", repeated) + ". Лишние удалите через меню плитки", GlyphInfo, ToastKind.Info);

        // Подписки обновляются при каждом запуске (если пора) и потом раз в час проверяется, не пора ли снова.
        _subscriptionTimer.Start();
        _ = UpdateSubscriptionsAsync();

        _updateTimer?.Start();
        // Не перекрываем уведомление об итоге обновления.
        await Task.Delay(outcome is null ? 1500 : 4500);
        if (outcome is not null)
            _ = RemoveWaiterCopyLaterAsync();
        if (_settings.Updates.Auto)
            await CheckForUpdatesAsync(manual: false);
    }

    /// <summary>
    /// Помощник службы ждёт, что эта версия сообщит о запуске (<see cref="UpdateService.PendingStartConfirmation"/>): сообщаем.
    /// true — сообщили или сообщать нечего; false — не вышло (помощник вернёт прежнюю версию).
    /// </summary>
    private async Task<bool> ConfirmUpdateStartedAsync()
    {
        var jobId = await Task.Run(_updates.PendingStartConfirmation);
        if (jobId is null)
            return true;

        var version = UpdateService.Format(UpdateService.CurrentVersion);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = await CoreServiceClient.ConnectAsync(TimeSpan.FromSeconds(10));
                await client.ConfirmUpdateStartedAsync(jobId);
                TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, $"Версия {version} запустилась после обновления, служба получила подтверждение");
                return true;
            }
            catch (Exception ex) when (attempt < 3)
            {
                TechLog.Warn(LogAreas.Update, LogCodes.UpdateInstall, $"Не удалось подтвердить службе запуск версии {version} (попытка {attempt}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                TechLog.Error(LogAreas.Update, LogCodes.UpdateFail,
                    $"Не удалось подтвердить службе запуск версии {version}: {ex.Message}. Помощник обновления вернёт прежнюю версию");
                return false;
            }
        }
    }

    /// <summary>Окно ожидания обновления закрывается, когда открылся лаунчер: его копию из %TEMP% можно удалить чуть позже.</summary>
    private async Task RemoveWaiterCopyLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (_updatePhase != UpdatePhase.Installing)
            await Task.Run(UpdateService.RemoveWaiterCopy);
    }

    private void ReportUpdateOutcome(UpdateOutcome outcome)
    {
        var target = UpdateService.Format(outcome.Target);
        var current = UpdateService.Format(UpdateService.CurrentVersion);
        if (outcome.Succeeded)
        {
            StatusMessage = $"Обновлено до версии {target}";
            _log.Add(LogKind.Update, $"Обновление до версии {target} установлено");
            TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, $"Обновление до версии {target} установлено");
            Toast($"Обновлено до версии {target}", "Безопасный лаунчер работает в новой версии", GlyphCheck, ToastKind.Success, "Что нового", ShowChangelog);
            return;
        }

        var line = outcome.RolledBack
            ? $"Обновление до версии {target} не установилось, лаунчер остался на версии {current}"
            : $"Не удалось обновить до версии {target}, осталась версия {current}";
        _installFailure = (outcome.Target, outcome.RolledBack
            ? $"Прошлая попытка не удалась: новая версия не встала, и служба вернула версию {current}. Подробности в журнале."
            : "Прошлая попытка не удалась: " + (outcome.Error is { Length: > 0 } error ? char.ToLower(error[0]) + error[1..] : "подробности в журнале."));
        StatusMessage = line;
        _log.Add(LogKind.Failed, line + (outcome.Error is null ? string.Empty : ": " + outcome.Error));
        var tail = _updates.UpdateLogTail(40);
        TechLog.Error(LogAreas.Update, LogCodes.UpdateFail, line + (outcome.Error is null ? string.Empty : ". Причина: " + outcome.Error) +
            (tail.Count > 0 ? "\nЖурнал установки (update.log):\n" + string.Join("\n", tail) : string.Empty));
        Toast(outcome.RolledBack ? "Обновление не установилось" : "Не удалось обновить",
            $"Работает версия {current}. Подробности в журнале", GlyphInfo, ToastKind.Danger, "Обновления", () => OpenSettings(SettingsSection.Updates));
    }

    private void OnUpdateTimer()
    {
        if (_settings.Updates.Auto && DateTime.UtcNow >= _nextCheckUtc && _updatePhase is UpdatePhase.Idle or UpdatePhase.Available or UpdatePhase.Ready)
            _ = CheckForUpdatesAsync(manual: false);
    }

    /// <summary>Настройки открыты без раздела: найденное обновление показывается сразу, один раз на версию.</summary>
    private bool TakeUpdatePageJump() => MarkUpdateSeen();

    /// <summary>Раздел «Обновления» с найденным обновлением открыт: больше сам он для этой версии не открывается. true — открыт впервые.</summary>
    private bool MarkUpdateSeen()
    {
        if (!IsUpdateFound || _updateCheck?.Latest is not { } latest)
            return false;
        var version = UpdateService.Format(latest.Version);
        if (_settings.Updates.OpenedVersion == version)
            return false;
        _settings.Updates.OpenedVersion = version;
        SaveSettings();
        return true;
    }

    // ---------------------------------------------------------------- проверка и загрузка

    /// <summary>
    /// Проверить обновления. Ручная проверка только сообщает о новой версии (кнопка «Обновить» скачает и установит её); автоматическая сразу её
    /// скачивает и один раз сообщает о ней уведомлением.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updatePhase is not (UpdatePhase.Idle or UpdatePhase.Available or UpdatePhase.Ready))
            return;

        var previous = _updatePhase;
        _updateError = null;
        SetUpdatePhase(UpdatePhase.Checking);
        try
        {
            UpdateCheckResult result;
            using (var route = await OpenUpdateRouteAsync(CancellationToken.None))
            using (var handler = route?.CreateHandler())
                result = await _updates.CheckAsync(CancellationToken.None, handler);

            _updateCheck = result;
            _lastCheckUtc = DateTime.UtcNow;
            _nextCheckUtc = _lastCheckUtc.Value + UpdateCheckInterval;
            TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck,
                $"Проверка обновлений{(manual ? " (вручную)" : string.Empty)} {DescribeUpdateRoute()}: установлена {UpdateService.Format(result.Current)}, " +
                $"последняя {(result.Latest is { } found ? UpdateService.Format(found.Version) + (found.SignatureUrl is null ? ", без подписи" : ", с подписью") : "не найдена")}");

            if (!result.IsUpdateAvailable)
            {
                _downloaded = null;
                SetUpdatePhase(UpdatePhase.Idle);
                return;
            }

            var latest = result.Latest!;
            if (_downloaded?.Version == latest.Version)
            {
                SetUpdatePhase(UpdatePhase.Ready);
                return;
            }

            // Скачано в прошлый раз (до перезапуска лаунчера): заново не качаем, но проверяем.
            SetUpdatePhase(UpdatePhase.Verifying);
            var ready = await Task.Run(() => _updates.FindDownloaded(latest));
            if (ready is not null)
            {
                _downloaded = ready;
                TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck, $"Версия {UpdateService.Format(latest.Version)} уже скачана и проверена");
                SetUpdatePhase(UpdatePhase.Ready);
                return;
            }

            _downloaded = null;
            SetUpdatePhase(UpdatePhase.Available);
            if (!manual && _settings.Updates.Auto)
            {
                NotifyUpdateFound(latest);
                await DownloadUpdateAsync(automatic: true);
            }
        }
        catch (Exception ex)
        {
            _nextCheckUtc = DateTime.UtcNow + UpdateRetryInterval;
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail, $"Не удалось проверить обновления {DescribeUpdateRoute()}: {ex.Message}");
            _updateError = "Не удалось проверить обновления: " + ShortReason(ex);
            SetUpdatePhase(previous);
        }
    }

    /// <summary>О новой версии сообщаем один раз: в окне или уведомлением Windows, если окно скрыто.</summary>
    private void NotifyUpdateFound(ReleaseInfo release)
    {
        var version = UpdateService.Format(release.Version);
        if (_settings.Updates.NotifiedVersion == version)
            return;
        _settings.Updates.NotifiedVersion = version;
        SaveSettings();
        _log.Add(LogKind.Update, $"Доступна версия {version}, загружается");
        Toast($"Доступна версия {version}", "Скачивается сама. Установить её можно в настройках, кнопкой «Обновить»",
            GlyphInfo, ToastKind.Info, "Подробнее", () => OpenSettings(SettingsSection.Updates));
    }

    /// <summary>
    /// Скачать новую версию. Временные сбои (нет связи, зависание, сбой GitHub, подключение не запустилось) повторяются с паузой
    /// 5 с, 15 с, 30 с, дальше раз в минуту, пока загрузка не завершится или её не отменят; каждая попытка заново открывает маршрут
    /// и продолжает с того же места. Ответ 404 — релиз удалён: сверяемся со списком релизов. Окончательные ошибки не повторяются.
    /// </summary>
    private async Task DownloadUpdateAsync(bool automatic)
    {
        if (_updateCheck?.Latest is not { } release || !_updateCheck.IsUpdateAvailable ||
            _updatePhase is not (UpdatePhase.Available or UpdatePhase.Idle or UpdatePhase.Ready))
            return;
        if (automatic && _rejectedVersion == release.Version)
            return; // файл этой версии не прошёл проверку: сама больше не качаем, «Обновить» попробует снова

        _updateError = null;
        _downloadProgress = new DownloadProgress(0, release.SetupSize > 0 ? release.SetupSize : null);
        var total = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource();
        _downloadCancel = cancel;
        _downloadAutomatic = automatic;
        SetUpdatePhase(UpdatePhase.Downloading);
        var progress = new Progress<DownloadProgress>(p =>
        {
            _downloadProgress = p;
            if (_updatePhase == UpdatePhase.Downloading && _retryAtUtc is null)
                RaiseUpdateProperties();
        });

        var failures = 0;
        var damaged = false;
        try
        {
            while (true)
            {
                var version = UpdateService.Format(release.Version);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
                _downloadAttempt = attempt;
                _downloadWatch.Restart();
                TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck,
                    $"Загрузка версии {version}{(automatic ? " (автоматически)" : string.Empty)}{(failures > 0 ? $", попытка {failures + 1}" : string.Empty)} " +
                    $"{DescribeUpdateRoute()}: {release.SetupName}, {release.SetupSize} байт" + (release.SignatureUrl is null ? ", подписи нет" : ", с подписью"));
                try
                {
                    using (var route = await OpenUpdateRouteAsync(attempt.Token))
                    using (var handler = route?.CreateHandler())
                    {
                        _downloaded = await _updates.DownloadAsync(release, progress, attempt.Token, handler,
                            verifying: () => _dispatcher.BeginInvoke(() => SetUpdatePhase(UpdatePhase.Verifying)));
                    }

                    TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck,
                        $"Версия {version} скачана за {total.Elapsed.TotalSeconds:0} с и проверена" + (_downloaded.IsSigned ? " по подписи" : " по контрольной сумме GitHub (подписи нет)"));
                    _log.Add(LogKind.Update, $"Версия {version} скачана и готова к установке");
                    SetUpdatePhase(UpdatePhase.Ready);
                    return;
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancel.IsCancellationRequested)
                {
                    TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck, $"Способ загрузки изменён: загрузка версии {version} продолжается {DescribeUpdateRoute()}");
                    SetUpdatePhase(UpdatePhase.Downloading); // если сменили во время проверки файла
                    continue;
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    var kind = UpdateService.ClassifyDownloadFailure(ex);
                    if (kind == DownloadFailureKind.Damaged && !damaged)
                    {
                        damaged = true; // испорченный файл удалён: один раз качаем заново, с нуля
                        kind = DownloadFailureKind.Transient;
                    }
                    else if (kind == DownloadFailureKind.Damaged || ex is UpdateSignatureException)
                    {
                        _rejectedVersion = release.Version;
                        throw;
                    }

                    if (kind == DownloadFailureKind.Fatal)
                        throw;

                    if (kind == DownloadFailureKind.ReleaseGone)
                    {
                        TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail, $"Файла версии {version} нет на GitHub ({ex.Message}): сверяемся со списком релизов");
                        var result = await TryCheckReleasesAsync(attempt.Token);
                        if (result is not null)
                        {
                            if (result.Latest is not { } latest || !result.IsUpdateAvailable)
                            {
                                // Релиз удалён, а других новых версий нет.
                                _updates.DiscardDownloads();
                                _downloaded = null;
                                TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail, $"Версии {version} больше нет на GitHub: загрузка остановлена, скачанное удалено");
                                _updateError = $"Версии {version} больше нет на GitHub, загрузка остановлена";
                                SetUpdatePhase(UpdatePhase.Idle);
                                return;
                            }

                            if (latest.Version == release.Version && latest.SetupUrl == release.SetupUrl)
                                throw new InvalidOperationException($"Установщика версии {version} нет в релизе на GitHub");

                            // Другая версия (релиз удалён или вышел новый) или файл переложен: качаем то, что опубликовано сейчас.
                            TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck,
                                $"На GitHub сейчас версия {UpdateService.Format(latest.Version)}: качаем её вместо {version}");
                            release = latest;
                            failures = 0;
                            damaged = false;
                            _downloadProgress = new DownloadProgress(0, release.SetupSize > 0 ? release.SetupSize : null);
                            RaiseUpdateProperties();
                            continue;
                        }
                    }

                    failures++;
                    var delay = UpdateService.RetryDelay(failures);
                    TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail,
                        $"Загрузка версии {version} {DescribeUpdateRoute()} прервалась: {ex.Message}. Повтор через {delay.TotalSeconds:0} с, с того же места");
                    await PauseBeforeRetryAsync(delay, ShortReason(ex), attempt.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TechLog.Info(LogAreas.Update, LogCodes.UpdateCheck, $"Загрузка версии {UpdateService.Format(release.Version)} отменена, недокачанное сохранено");
            StatusMessage = "Загрузка обновления остановлена. «Обновить» продолжит её с того же места";
            SetUpdatePhase(UpdatePhase.Available);
        }
        catch (Exception ex)
        {
            var version = UpdateService.Format(release.Version);
            _nextCheckUtc = DateTime.UtcNow + UpdateRetryInterval;
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail, $"Не удалось скачать версию {version} {DescribeUpdateRoute()}: {ex.Message}");
            _updateError = _rejectedVersion == release.Version
                ? $"Файл версии {version} не прошёл проверку, поэтому сам больше не скачивается"
                : "Не удалось скачать обновление: " + ShortReason(ex);
            SetUpdatePhase(UpdatePhase.Available);
        }
        finally
        {
            _downloadCancel = null;
            _downloadAttempt = null;
            _downloadAutomatic = false;
            _retryAtUtc = null;
            _retryReason = null;
            _downloadWatch.Stop();
        }
    }

    /// <summary>Пауза перед повтором с отсчётом на странице. Смена маршрута прерывает её: повтор сразу.</summary>
    private async Task PauseBeforeRetryAsync(TimeSpan delay, string reason, CancellationToken attempt)
    {
        _retryAtUtc = DateTime.UtcNow + delay;
        _retryReason = reason;
        SetUpdatePhase(UpdatePhase.Downloading);
        RaiseUpdateProperties();
        try
        {
            while (DateTime.UtcNow < _retryAtUtc)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), attempt);
                RaiseUpdateProperties();
            }
        }
        catch (OperationCanceledException) when (!(_downloadCancel?.IsCancellationRequested ?? true))
        {
            // маршрут сменился: пробуем сразу
        }
        finally
        {
            _retryAtUtc = null;
            _retryReason = null;
            RaiseUpdateProperties();
        }

        _downloadCancel?.Token.ThrowIfCancellationRequested();
    }

    /// <summary>Список релизов сейчас; null — проверить не удалось (тогда это обычный сбой связи).</summary>
    private async Task<UpdateCheckResult?> TryCheckReleasesAsync(CancellationToken cancellation)
    {
        try
        {
            UpdateCheckResult result;
            using (var route = await OpenUpdateRouteAsync(cancellation))
            using (var handler = route?.CreateHandler())
                result = await _updates.CheckAsync(cancellation, handler);
            _updateCheck = result;
            _lastCheckUtc = DateTime.UtcNow;
            _nextCheckUtc = _lastCheckUtc.Value + UpdateCheckInterval;
            return result;
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail, $"Не удалось проверить список релизов {DescribeUpdateRoute()}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Маршрут загрузки: null — напрямую, иначе прокси через выбранное подключение лаунчера.</summary>
    private async Task<ConnectionProxy?> OpenUpdateRouteAsync(CancellationToken cancellation)
    {
        if (_settings.Updates.Route != UpdateRoute.Connection)
            return null;
        var connection = _settings.Connections.FirstOrDefault(c => c.Id == _settings.Updates.ConnectionId)
                         ?? throw new InvalidOperationException("не выбрано подключение для загрузки обновлений (раздел «Обновления», «Дополнительно»)");
        var server = ConnectionOutbound.Resolve(connection).Outbound;
        try
        {
            return await new ConnectionTester().OpenProxyAsync(server, _network.CheckRouteFor(server), cancellation);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or InvalidOperationException))
        {
            throw new UpdateRouteException($"подключение «{connection.Name}» не запустилось", ex);
        }
    }

    /// <summary>«Что нового»: описание нового релиза с GitHub, а если обновления нет — установленной версии.</summary>
    private async void ShowChangelog()
    {
        if (_updateCheck is null && CanUseUpdates)
            await CheckForUpdatesAsync(manual: true);

        if (_updateCheck is null)
        {
            _dialogs.ShowWarning("Не удалось загрузить описание изменений. Проверьте подключение к интернету и попробуйте ещё раз.", "Что нового");
            return;
        }

        var hasUpdate = _updateCheck.IsUpdateAvailable;
        var release = hasUpdate ? _updateCheck.Latest : _updateCheck.CurrentRelease;
        var version = UpdateService.Format(release?.Version ?? UpdateService.CurrentVersion);
        if (release is null)
        {
            _dialogs.ShowWarning($"Описание изменений для версии {version} пока не опубликовано.", "Что нового");
            return;
        }

        var date = release.PublishedAt is { } published ? published.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")) : null;
        var subtitle = (hasUpdate ? "Новая версия" : "Установленная версия") + (date is null ? string.Empty : $" · {date}");
        if (_dialogs.ShowChangelog($"Что нового в версии {version}", subtitle, release.Notes, hasUpdate && IsInstallUpdateVisible))
            InstallUpdate();
    }

    // ---------------------------------------------------------------- установка

    /// <summary>«Обновить»: скачанная версия ставится сразу, иначе сначала скачивается (или установка ждёт идущую загрузку).</summary>
    private void InstallUpdate()
    {
        switch (_updatePhase)
        {
            case UpdatePhase.Ready:
                _ = ConfirmAndInstallAsync();
                break;
            case UpdatePhase.Available:
                _installAfterDownload = true;
                TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, "Нажато «Обновить»: версия скачается и установится");
                _ = DownloadUpdateAsync(automatic: false);
                break;
            case UpdatePhase.Downloading or UpdatePhase.Verifying:
                _installAfterDownload = true;
                TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, "Нажато «Обновить» во время загрузки: установка начнётся после неё");
                RaiseUpdateProperties();
                break;
        }
    }

    /// <summary>Установка скачанной версии: если работают приложения, сначала их нужно закрыть; иначе сразу передать обновление.</summary>
    /// <summary>
    /// «Обновить» для скачанной версии: перед установкой сверяемся со списком релизов. Релиз удалён — не ставим и удаляем скачанное;
    /// вышла версия новее — качаем и ставим её. Если GitHub недоступен, ставим скачанное: оно уже проверено.
    /// </summary>
    private async Task ConfirmAndInstallAsync()
    {
        if (_downloaded is not { } downloaded)
            return;

        var version = UpdateService.Format(downloaded.Version);
        _updateError = null;
        SetUpdatePhase(UpdatePhase.Checking);
        UpdateCheckResult? result = null;
        try
        {
            using (var route = await OpenUpdateRouteAsync(CancellationToken.None))
            using (var handler = route?.CreateHandler())
                result = await _updates.CheckAsync(CancellationToken.None, handler);
            _updateCheck = result;
            _lastCheckUtc = DateTime.UtcNow;
            _nextCheckUtc = _lastCheckUtc.Value + UpdateCheckInterval;
        }
        catch (Exception ex)
        {
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateFail,
                $"Перед установкой не удалось сверить версию {version} со списком релизов {DescribeUpdateRoute()}: {ex.Message}. Ставим скачанную, она проверена");
        }

        if (result is null || result.IsUpdateAvailable && result.Latest!.Version == downloaded.Version)
        {
            SetUpdatePhase(UpdatePhase.Ready);
            StartUpdateInstall();
            return;
        }

        if (result.IsUpdateAvailable && result.Latest!.Version > downloaded.Version)
        {
            TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall,
                $"Перед установкой версии {version} на GitHub найдена версия {UpdateService.Format(result.Latest.Version)}: скачиваем и ставим её");
            _downloaded = null;
            SetUpdatePhase(UpdatePhase.Available);
            InstallUpdate();
            return;
        }

        // Скачанной версии в списке релизов больше нет: автор удалил релиз.
        _updates.DiscardDownloads();
        _downloaded = null;
        TechLog.Warn(LogAreas.Update, LogCodes.UpdateInstall, $"Версии {version} больше нет на GitHub: установка отменена, скачанное удалено");
        _log.Add(LogKind.Update, $"Версии {version} больше нет на GitHub, установка отменена");
        _updateError = $"Версии {version} больше нет на GitHub, установка отменена";
        SetUpdatePhase(result.IsUpdateAvailable ? UpdatePhase.Available : UpdatePhase.Idle);
    }

    private void StartUpdateInstall()
    {
        if (_updatePhase != UpdatePhase.Ready || _downloaded is null)
            return;

        _updateError = null;
        var apps = _tracker.RunningForUpdate();
        if (apps.Count == 0)
        {
            _ = HandOffUpdateAsync();
            return;
        }

        _blockingApps = apps;
        TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, "Обновление ждёт закрытия приложений: " + DescribeApps(apps));
        SetUpdatePhase(UpdatePhase.AppsRunning);
        _appsTimer?.Start();
    }

    /// <summary>«Закрыть приложения и обновить»: попросить окна закрыться, как крестиком, и ждать.</summary>
    private void CloseAppsForUpdate()
    {
        if (_updatePhase != UpdatePhase.AppsRunning)
            return;
        var apps = _tracker.RunningForUpdate();
        if (apps.Count == 0)
        {
            OnAppsTimer();
            return;
        }

        var windows = ProcessTracker.RequestClose(apps.SelectMany(a => a.ProcessIds).ToList());
        TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, $"Закрываем приложения перед обновлением ({windows} окон): {DescribeApps(apps)}");
        _blockingApps = apps;
        _closeRequestedUtc = DateTime.UtcNow;
        _closeTimedOut = false;
        SetUpdatePhase(UpdatePhase.ClosingApps);
        _appsTimer?.Start();
    }

    /// <summary>«Закрыть принудительно»: приложения не закрылись сами, пользователь согласен потерять несохранённое.</summary>
    private void ForceCloseAppsForUpdate()
    {
        if (_updatePhase != UpdatePhase.ClosingApps)
            return;
        var report = _tracker.KillForUpdate(_blockingApps.Select(a => a.AppId).ToList());
        if (report.Killed.Count > 0)
            _log.Add(LogKind.Closed, "Перед обновлением принудительно закрыты: " + string.Join(", ", report.Killed));
        if (report.Failed.Count > 0)
        {
            _log.Add(LogKind.Failed, "Перед обновлением не удалось закрыть: " + string.Join(", ", report.Failed));
            _dialogs.ShowWarning("Эти приложения не удалось закрыть (обычно так бывает с программами, запущенными с правами администратора):\n\n" +
                                 string.Join("\n", report.Failed) + "\n\nЗакройте их сами: обновление начнётся, как только они закроются.", "Обновление");
        }

        OnAppsTimer();
    }

    private void CancelUpdateInstall()
    {
        if (_updatePhase is not (UpdatePhase.AppsRunning or UpdatePhase.ClosingApps))
            return;
        _appsTimer?.Stop();
        TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, "Установка обновления отменена: приложения остались работать");
        SetUpdatePhase(UpdatePhase.Ready);
    }

    /// <summary>Раз в секунду, пока ждём приложения: закрылись все — обновление начинается само.</summary>
    private void OnAppsTimer()
    {
        if (_updatePhase is not (UpdatePhase.AppsRunning or UpdatePhase.ClosingApps))
        {
            _appsTimer?.Stop();
            return;
        }

        var apps = _tracker.RunningForUpdate();
        if (apps.Count == 0)
        {
            _appsTimer?.Stop();
            TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, "Приложения закрыты, обновление начинается");
            _ = HandOffUpdateAsync();
            return;
        }

        _blockingApps = apps;
        if (_updatePhase == UpdatePhase.ClosingApps && !_closeTimedOut && DateTime.UtcNow - _closeRequestedUtc > AppsCloseGrace)
        {
            _closeTimedOut = true;
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateInstall, $"За {AppsCloseGrace.TotalSeconds:0} с не закрылись: {DescribeApps(apps)}");
        }

        RaiseUpdateProperties();
    }

    private static string DescribeApps(IReadOnlyList<RunningApp> apps) =>
        string.Join(", ", apps.Select(a => $"«{a.Name}» ({(a.Outside ? "мимо лаунчера, " : string.Empty)}PID {string.Join(",", a.ProcessIds)})"));

    private void InstallStep(string text)
    {
        _installStep = text;
        TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, "Обновление: " + text);
        RaiseUpdateProperties();
    }

    /// <summary>
    /// Приложения закрыты: передать обновление. Через службу — остановить ядро, подготовить окно ожидания, отдать службе
    /// (она проверит подпись и сохранит текущую версию для отката), запустить окно ожидания и закрыться. Если служба
    /// не умеет (старая) или релиз без подписи — по-старому, установщиком с запросом прав.
    /// </summary>
    private async Task HandOffUpdateAsync()
    {
        if (_downloaded is not { } update || _updateCheck?.Latest is not { } release || _updatePhase == UpdatePhase.Installing)
            return;

        using var operation = TechLog.Begin(out _);
        var version = UpdateService.Format(update.Version);
        _updateHandoff = true;
        _installFailure = null;
        _installStep = "Готовим установку";
        SetUpdatePhase(UpdatePhase.Installing);
        CoreServiceClient? client = null;
        var held = false;
        try
        {
            if (update.IsSigned && UpdateService.CanInstallViaService)
            {
                InstallStep("Проверяем, может ли служба установить обновление");
                client = await ConnectForUpdateAsync();
            }

            if (client is null)
            {
                InstallWithSetup(update, release);
                return;
            }

            InstallStep("Останавливаем подключение лаунчера");
            held = true;
            await _network.HoldAsync();

            InstallStep("Готовим окно установки");
            var waiter = await Task.Run(UpdateService.PrepareWaiter);

            InstallStep("Служба проверяет подпись и сохраняет текущую версию для отката");
            var accepted = await client.UpdateAsync(update.SetupPath, update.SignaturePath!);

            InstallStep("Лаунчер закрывается, установка продолжится в отдельном окне");
            _updates.StartWaiter(waiter, update.Version);
            _log.Add(LogKind.Update, $"Устанавливается версия {accepted}, лаунчер перезапустится");
            _closingForUpdate = true;
            ShutdownRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            TechLog.Error(LogAreas.Update, LogCodes.UpdateFail, $"Обновление до версии {version} не начато: {ex.Message}");
            _updateError = "Обновление не начато: " + ShortReason(ex) + ". Ничего не изменилось";
            _updateHandoff = false;
            if (held)
            {
                _network.Resume();
                KeepCoreRunning("обновление не начато", null);
            }

            SetUpdatePhase(UpdatePhase.Ready);
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>Связь со службой, которая умеет ставить обновления (ревизия 9 и новее); null — нет такой: ставим установщиком.</summary>
    private static async Task<CoreServiceClient?> ConnectForUpdateAsync()
    {
        CoreServiceClient? client = null;
        try
        {
            client = await CoreServiceClient.ConnectAsync(TimeSpan.FromSeconds(10));
            var status = await client.StatusAsync();
            if (status.Protocol >= CoreServiceClient.UpdateProtocol)
                return client;
            TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, $"Служба версии {status.ServiceVersion ?? "?"} (ревизия {status.Protocol}) не ставит обновления сама: установщик с запросом прав");
        }
        catch (CoreServiceException ex)
        {
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateInstall, "Служба недоступна, обновление ставится установщиком с запросом прав: " + ex.Message);
        }

        client?.Dispose();
        return null;
    }

    /// <summary>По-старому: установщик с запросом прав Windows (релиз без подписи или старая служба). Он сам запустит новую версию.</summary>
    private void InstallWithSetup(DownloadedUpdate update, ReleaseInfo release)
    {
        var version = UpdateService.Format(update.Version);
        InstallStep("Запускаем установщик: Windows спросит разрешение");
        try
        {
            _updates.StartSetup(update.SetupPath, release);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            TechLog.Info(LogAreas.Update, LogCodes.UpdateInstall, "Установка обновления отменена в запросе прав Windows");
            _updateError = "Установка отменена: Windows не получила разрешения";
            _updateHandoff = false;
            SetUpdatePhase(UpdatePhase.Ready);
            return;
        }

        _log.Add(LogKind.Update, $"Запущена установка версии {version}, лаунчер перезапустится");
        _closingForUpdate = true;
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Лаунчер закрывается ради обновления: запомнить окно, чтобы новая версия открылась на том же месте и той же странице.</summary>
    public void SaveWindowForUpdate(double left, double top, double width, double height, bool maximized)
    {
        try
        {
            _updates.SaveWindowResume(new WindowResume
            {
                Left = left, Top = top, Width = width, Height = height, Maximized = maximized,
                SettingsSection = _page == MainPage.Settings ? Section.ToString() : null
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TechLog.Warn(LogAreas.Update, LogCodes.UpdateInstall, "Не удалось запомнить окно перед обновлением: " + ex.Message);
        }
    }

    /// <summary>
    /// Запуск сразу после обновления: окно, каким оно было перед ним (место и размер применяет окно), и та же страница.
    /// null — запуск не после обновления.
    /// </summary>
    public WindowResume? TakeWindowAfterUpdate()
    {
        var resume = _updates.TakeWindowResume();
        if (resume?.SettingsSection is { } name && _page == MainPage.Apps && Enum.TryParse<SettingsSection>(name, out var section))
            OpenSettings(section);
        return resume;
    }

    private void DisposeUpdates()
    {
        _updateTimer?.Stop();
        _appsTimer?.Stop();
        _downloadCancel?.Cancel();
    }
}
