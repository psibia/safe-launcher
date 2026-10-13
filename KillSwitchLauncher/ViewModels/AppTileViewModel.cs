using System.Windows.Input;
using System.Windows.Media;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Состояние приложения для плитки и сводки в шапке.</summary>
public enum AppNetStatus
{
    /// <summary>Не запущено.</summary>
    Idle,

    /// <summary>Работает, и его трафик защищён: через подключение лаунчера или при включённом VPN.</summary>
    Protected,

    /// <summary>Сервер подключения не отвечает: приложение ждёт сеть, мимо подключения трафик не уходит.</summary>
    Waiting,

    /// <summary>Работает без защиты: копия мимо лаунчера или VPN отключён, а Kill-Switch у приложения выключен.</summary>
    Unprotected
}

/// <summary>Плитка приложения на главном экране.</summary>
public sealed class AppTileViewModel : ObservableObject
{
    private int _runningCount;
    private bool _isDnsTracking;
    private bool _isDnsActive;
    private bool _hasDnsJournal;
    private int _outsideCount;
    private CoreAppState _netState;
    private string? _launchStage;
    private AppNetStatus _status;
    private string _statusText = "Не запущено";
    private string? _subtitle;
    private bool _isSubtitleWarning;

    public AppTileViewModel(AppEntry entry, ImageSource? icon, ICommand launch, ICommand remove, ICommand openLocation,
        ICommand? toggleDns = null, ICommand? openDnsJournal = null, ICommand? kill = null, ICommand? restartProtected = null,
        ICommand? openSettings = null)
    {
        Entry = entry;
        Icon = icon;
        LaunchCommand = launch;
        RemoveCommand = remove;
        OpenLocationCommand = openLocation;
        ToggleDnsCommand = toggleDns ?? new RelayCommand(() => { });
        OpenDnsJournalCommand = openDnsJournal ?? new RelayCommand(() => { });
        KillCommand = kill ?? new RelayCommand(() => { });
        RestartProtectedCommand = restartProtected ?? new RelayCommand(() => { });
        OpenSettingsCommand = openSettings ?? new RelayCommand(() => { });
        _isDnsTracking = entry.TrackDns;
    }

    public AppEntry Entry { get; }

    public string Name => Entry.Name;

    /// <summary>Название поменяли на странице приложения.</summary>
    public void RefreshName()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ToolTip));
    }

    public string ToolTip =>
        Entry.IsPackaged ? $"Microsoft Store: {Entry.ShortcutPath ?? Entry.AppUserModelId}"
        : string.IsNullOrWhiteSpace(Entry.Arguments) ? Entry.ExePath
        : $"{Entry.ExePath} {Entry.Arguments}";

    /// <summary>Иконка из .exe; null — в разметке показывается иконка по умолчанию.</summary>
    public ImageSource? Icon { get; }

    public ICommand LaunchCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenLocationCommand { get; }
    public ICommand ToggleDnsCommand { get; }
    public ICommand OpenDnsJournalCommand { get; }

    /// <summary>«Завершить все процессы»: закрыть приложение целиком, не дожидаясь обрыва VPN.</summary>
    public ICommand KillCommand { get; }

    /// <summary>«Настройки приложения»: страница с условием запуска, защитой и исключениями.</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>Кольцо вокруг иконки: зелёное — защищено, жёлтое — ждёт сеть, серое с меткой — без защиты.</summary>
    public AppNetStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    /// <summary>Состояние словами для сводки в шапке: «Через «Германия»», «Ждёт сеть…», «Не запущено».</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    /// <summary>
    /// Мелкая подпись под названием: подключение приложения, если оно отличается от общего («Германия», «Свои адаптеры»).
    /// Во время запуска — этап запуска.
    /// </summary>
    public string? Subtitle
    {
        get => LaunchStage ?? _subtitle;
        set
        {
            if (_subtitle == value) return;
            _subtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public bool HasSubtitle => Subtitle is not null;

    /// <summary>Подпись — предупреждение (например, подключение не выбрано).</summary>
    public bool IsSubtitleWarning
    {
        get => _isSubtitleWarning;
        set => SetField(ref _isSubtitleWarning, value);
    }

    /// <summary>«Перезапустить под защитой»: закрыть копию, запущенную мимо лаунчера, и запустить из лаунчера.</summary>
    public ICommand RestartProtectedCommand { get; }

    /// <summary>Сколько копий приложения работает мимо лаунчера (их соединения идут напрямую).</summary>
    public int OutsideCount
    {
        get => _outsideCount;
        set
        {
            if (!SetField(ref _outsideCount, value)) return;
            OnPropertyChanged(nameof(IsUnprotected));
            OnPropertyChanged(nameof(RunningToolTip));
        }
    }

    /// <summary>Работает без защиты: есть копия мимо лаунчера.</summary>
    public bool IsUnprotected => OutsideCount > 0;

    /// <summary>Через подключение лаунчера: идёт через него или ждёт, пока сервер снова ответит.</summary>
    public CoreAppState NetState
    {
        get => _netState;
        set
        {
            if (SetField(ref _netState, value))
                OnPropertyChanged(nameof(RunningToolTip));
        }
    }

    /// <summary>Что сейчас происходит с запуском («Подключаю…», «Запускаю…»); null — ничего.</summary>
    public string? LaunchStage
    {
        get => _launchStage;
        set
        {
            if (!SetField(ref _launchStage, value)) return;
            OnPropertyChanged(nameof(IsLaunching));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public bool IsLaunching => LaunchStage is not null;

    /// <summary>Сколько процессов этой программы сейчас контролирует лаунчер.</summary>
    public int RunningCount
    {
        get => _runningCount;
        set
        {
            if (!SetField(ref _runningCount, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(RunningToolTip));
        }
    }

    public bool IsRunning => RunningCount > 0;

    public string RunningToolTip =>
        IsUnprotected ? "Работает без защиты: запущено мимо лаунчера, соединения идут напрямую"
        : NetState == CoreAppState.Waiting ? $"Сервер подключения не отвечает: ждёт сеть. Под контролем: {ProcessTracker.ProcessesText(RunningCount)}"
        : NetState == CoreAppState.Protected ? $"Через подключение лаунчера. Под контролем: {ProcessTracker.ProcessesText(RunningCount)}"
        : $"Запущено из лаунчера. Под контролем Kill-Switch: {ProcessTracker.ProcessesText(RunningCount)}";

    // ---------------------------------------------------------------- журнал DNS

    /// <summary>Журнал DNS включён (значок на плитке).</summary>
    public bool IsDnsTracking
    {
        get => _isDnsTracking;
        set
        {
            if (!SetField(ref _isDnsTracking, value)) return;
            OnPropertyChanged(nameof(DnsToggleText));
            OnPropertyChanged(nameof(DnsToolTip));
            OnPropertyChanged(nameof(CanOpenDnsJournal));
        }
    }

    /// <summary>Запросы сейчас действительно записываются (источник событий работает).</summary>
    public bool IsDnsActive
    {
        get => _isDnsActive;
        set
        {
            if (SetField(ref _isDnsActive, value))
                OnPropertyChanged(nameof(DnsToolTip));
        }
    }

    /// <summary>В журнале уже есть записи: его можно открыть, даже если он выключен.</summary>
    public bool HasDnsJournal
    {
        get => _hasDnsJournal;
        set
        {
            if (SetField(ref _hasDnsJournal, value))
                OnPropertyChanged(nameof(CanOpenDnsJournal));
        }
    }

    public bool CanOpenDnsJournal => IsDnsTracking || HasDnsJournal;

    public string DnsToggleText => IsDnsTracking ? "Выключить журнал DNS" : "Включить журнал DNS";

    public string DnsToolTip => IsDnsActive
        ? "Журнал DNS включён: запросы записываются"
        : "Журнал DNS включён: запись начнётся при запуске приложения из лаунчера";
}
