using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Логика главного окна без самого окна: действие при закрытии, настройки, Kill-Switch, журнал, версия.
/// Настройки, журнал и отметка об обновлении — во временной папке, настоящие данные лаунчера не трогаются.
/// Проверка обновлений при запуске в тестах не выполняется (она ждёт, пока окно покажется).
/// </summary>
public class MainViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _dir.Dispose();

    private SettingsStorage Storage => new(_dir.File("settings.json"));

    private ActivityLog Log => new(_dir.File("events.log"));

    private void Preset(Action<AppSettings> change)
    {
        var settings = Storage.Load();
        change(settings);
        Storage.Save(settings);
    }

    /// <summary>Создаёт ViewModel в STA-потоке (как в приложении), выполняет тест и закрывает её.</summary>
    private void WithViewModel(Action<MainViewModel, ActivityLog> test)
    {
        Sta.Run(() =>
        {
            var log = Log;
            var vm = new MainViewModel(Storage, _dialogs, Dispatcher.CurrentDispatcher, log,
                new UpdateService(_dir.File("update-pending.json"), _dir.File("download")), guardBackend: QuietGuard.Create, systemProxy: () => null);
            try
            {
                test(vm, log);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    // ---------------------------------------------------------------- версия и обновления

    [Fact]
    public void Version_IsShortWithV()
    {
        WithViewModel((vm, _) =>
        {
            Assert.Equal("v" + UpdateService.Format(UpdateService.CurrentVersion), vm.AppVersionText);
            Assert.Matches(@"^v\d+\.\d+\.\d+$", vm.AppVersionText);
        });
    }

    [Fact]
    public void UpdateStatus_BeforeFirstCheck()
    {
        WithViewModel((vm, _) =>
        {
            Assert.Equal("Ещё не проверялось", vm.UpdateStatusText);
            Assert.False(vm.IsUpdateAvailable); // значок «1» на «Настройках» не показан
            Assert.True(vm.IsUpdateNotAvailable);
            Assert.True(vm.CanUseUpdates);
        });
    }

    // ---------------------------------------------------------------- действие при закрытии

    [Fact]
    public void Close_Ask_WithoutRemember_AsksEveryTime()
    {
        _dialogs.HideAnswer = true;
        _dialogs.HideRemember = false;

        WithViewModel((vm, _) =>
        {
            Assert.True(vm.DecideHide());
            Assert.True(vm.DecideHide());
            Assert.Equal(2, _dialogs.AskHideCalls);
            Assert.Equal(HideBehavior.Ask, vm.HideBehavior);
        });

        Assert.Equal(HideBehavior.Ask, Storage.Load().HideBehavior);
    }

    [Fact]
    public void Close_RememberTray_SavesAndStopsAsking()
    {
        _dialogs.HideAnswer = true;
        _dialogs.HideRemember = true;

        WithViewModel((vm, _) =>
        {
            Assert.True(vm.DecideHide());
            Assert.True(vm.DecideHide());
            Assert.Equal(1, _dialogs.AskHideCalls);

            // В настройках сразу видно запомненный выбор, и это не «несохранённое изменение».
            Assert.Equal(HideBehavior.Tray, vm.HideBehavior);
            Assert.False(vm.HasPendingChanges);
        });

        Assert.Equal(HideBehavior.Tray, Storage.Load().HideBehavior);
    }

    [Fact]
    public void Close_RememberExit_SavesAndStopsAsking()
    {
        _dialogs.HideAnswer = false;
        _dialogs.HideRemember = true;

        WithViewModel((vm, _) =>
        {
            Assert.False(vm.DecideHide());
            Assert.False(vm.DecideHide());
            Assert.Equal(1, _dialogs.AskHideCalls);
            Assert.Equal(HideBehavior.Exit, vm.HideBehavior);
        });

        Assert.Equal(HideBehavior.Exit, Storage.Load().HideBehavior);
    }

    [Fact]
    public void Close_Cancelled_ChangesNothing_EvenWithRemember()
    {
        _dialogs.HideAnswer = null;
        _dialogs.HideRemember = true;

        WithViewModel((vm, _) =>
        {
            Assert.Null(vm.DecideHide());
            Assert.Equal(HideBehavior.Ask, vm.HideBehavior);
        });

        Assert.Equal(HideBehavior.Ask, Storage.Load().HideBehavior);
    }

    [Theory]
    [InlineData(HideBehavior.Tray, true)]
    [InlineData(HideBehavior.Exit, false)]
    public void Close_ChosenInSettings_DoesNotAsk(HideBehavior behavior, bool toTray)
    {
        Preset(s => s.HideBehavior = behavior);

        WithViewModel((vm, _) =>
        {
            Assert.Equal(toTray, vm.DecideHide());
            Assert.Equal(0, _dialogs.AskHideCalls);
        });
    }

    [Fact]
    public void Close_SettingIsADraft_UntilApplied()
    {
        WithViewModel((vm, _) =>
        {
            vm.HideBehavior = HideBehavior.Exit;
            Assert.True(vm.HasPendingChanges);
            Assert.Equal(HideBehavior.Ask, Storage.Load().HideBehavior);

            vm.CancelSettingsCommand.Execute(null);
            Assert.Equal(HideBehavior.Ask, vm.HideBehavior);
            Assert.False(vm.HasPendingChanges);

            vm.HideBehavior = HideBehavior.Tray;
            vm.ApplySettingsCommand.Execute(null);
            Assert.False(vm.HasPendingChanges);
            Assert.Equal(HideBehavior.Tray, Storage.Load().HideBehavior);
            Assert.True(vm.DecideHide());
            Assert.Equal(0, _dialogs.AskHideCalls);
        });
    }

    [Fact]
    public void UpdateSettings_AreADraft_UntilApplied()
    {
        WithViewModel((vm, _) =>
        {
            vm.AutoUpdate = false;
            vm.UpdateViaConnection = true;
            Assert.True(vm.HasPendingChanges);
            Assert.True(Storage.Load().Updates.Auto);
            Assert.Equal(UpdateRoute.Direct, Storage.Load().Updates.Route);

            vm.CancelSettingsCommand.Execute(null);
            Assert.True(vm.AutoUpdate);
            Assert.True(vm.UpdateDirect);
            Assert.False(vm.HasPendingChanges);

            vm.AutoUpdate = false;
            vm.ApplySettingsCommand.Execute(null);
            Assert.False(vm.HasPendingChanges);
            Assert.False(Storage.Load().Updates.Auto);
        });
    }

    [Fact]
    public void UpdateViaConnection_WithoutConnections_IsNotApplied()
    {
        WithViewModel((vm, _) =>
        {
            vm.UpdateViaConnection = true;
            vm.ApplySettingsCommand.Execute(null);

            Assert.True(vm.HasPendingChanges);
            Assert.Contains("сначала добавьте подключение", Assert.Single(_dialogs.Warnings));
            Assert.Equal(UpdateRoute.Direct, Storage.Load().Updates.Route);
        });
    }

    [Fact]
    public void SettingsClosedWithoutApply_DiscardsDraft()
    {
        WithViewModel((vm, _) =>
        {
            vm.OpenSettingsCommand.Execute(null);
            Assert.True(vm.IsSettingsOpen);
            vm.HideBehavior = HideBehavior.Exit;

            vm.IsSettingsOpen = false; // на вопрос «Применить изменения?» — «Не сохранять»

            Assert.Equal(HideBehavior.Ask, vm.HideBehavior);
            Assert.False(vm.HasPendingChanges);
        });

        Assert.Equal(HideBehavior.Ask, Storage.Load().HideBehavior);
    }

    [Fact]
    public void TrayHint_IsShownOnlyOnce_EvenAfterRestart()
    {
        WithViewModel((vm, _) =>
        {
            Assert.True(vm.TakeTrayHint());
            Assert.False(vm.TakeTrayHint());
        });

        Assert.True(Storage.Load().TrayHintShown);
        WithViewModel((vm, _) => Assert.False(vm.TakeTrayHint()));
    }

    // ---------------------------------------------------------------- закрытие лаунчера

    [Fact]
    public void Exit_WithNothingRunning_NeedsNoConfirmation()
    {
        WithViewModel((vm, _) =>
        {
            Assert.False(vm.NeedsCloseConfirmation);
            Assert.True(vm.ConfirmClose());
            Assert.Equal(0, _dialogs.ConfirmCalls);
        });
    }

    // ---------------------------------------------------------------- Kill-Switch

    [Fact]
    public void KillSwitchOff_RequiresConfirmation()
    {
        _dialogs.ConfirmAnswer = false;
        Preset(s => s.KillSwitchEnabled = true);

        WithViewModel((vm, _) =>
        {
            vm.KillSwitchEnabled = false;

            Assert.Equal(1, _dialogs.ConfirmCalls);
            Assert.True(vm.KillSwitchEnabled);
            Assert.False(vm.HasPendingChanges);
        });
    }

    [Fact]
    public void KillSwitchOffAndOn_IsSavedAndLogged()
    {
        _dialogs.ConfirmAnswer = true;
        Preset(s => s.KillSwitchEnabled = true);

        WithViewModel((vm, log) =>
        {
            vm.KillSwitchEnabled = false;
            Assert.True(vm.HasPendingChanges);

            vm.ApplySettingsCommand.Execute(null);
            Assert.False(Storage.Load().KillSwitchEnabled);
            Assert.Equal("Kill-Switch выключен", log.Snapshot()[^1].Text);

            // Включение подтверждать не нужно.
            vm.KillSwitchEnabled = true;
            vm.ApplySettingsCommand.Execute(null);
            Assert.Equal(1, _dialogs.ConfirmCalls);
            Assert.True(Storage.Load().KillSwitchEnabled);
            Assert.Equal("Kill-Switch включён", log.Snapshot()[^1].Text);
        });
    }

    [Fact]
    public void OutsideCopies_ViaLauncher_IsSavedAndLogged()
    {
        WithViewModel((vm, log) =>
        {
            vm.OutsideCopies = OutsideCopies.Launcher;
            Assert.True(vm.HasPendingChanges);

            vm.ApplySettingsCommand.Execute(null);

            Assert.Equal(OutsideCopies.Launcher, Storage.Load().OutsideCopies);
            Assert.Contains(log.Snapshot(), e => e.Text == "Приложения, открытые не из лаунчера: через лаунчер");
        });
    }

    [Fact]
    public void CommonDomains_AreNormalizedSavedAndLogged()
    {
        WithViewModel((vm, log) =>
        {
            vm.CommonDomainsText = "https://www.Kaspi.kz/shop\nkaspi.kz\n*.gosuslugi.ru";
            Assert.True(vm.HasPendingChanges);

            vm.ApplySettingsCommand.Execute(null);

            Assert.False(vm.HasPendingChanges);
            Assert.Equal(new[] { "www.kaspi.kz", "kaspi.kz", "gosuslugi.ru" }, Storage.Load().DirectDomains);
            Assert.Equal(string.Join(Environment.NewLine, "www.kaspi.kz", "kaspi.kz", "gosuslugi.ru"), vm.CommonDomainsText);
            Assert.Contains(log.Snapshot(), e => e.Text == "Общие исключения: 3 (напрямую, мимо подключения лаунчера)");

            vm.CommonDomainsText = "";
            vm.ApplySettingsCommand.Execute(null);
            Assert.Empty(Storage.Load().DirectDomains);
            Assert.Contains(log.Snapshot(), e => e.Text == "Общие исключения убраны");
        });
    }

    [Fact]
    public void CommonDomains_WrongDomain_IsNotApplied()
    {
        WithViewModel((vm, _) =>
        {
            vm.CommonDomainsText = "kaspi.kz\nне_домен";
            vm.ApplySettingsCommand.Execute(null);

            Assert.True(vm.HasPendingChanges);
            Assert.Equal(SettingsSection.Exclusions, vm.Section);
            Assert.Contains("не похоже на домен", Assert.Single(_dialogs.Warnings));
            Assert.Empty(Storage.Load().DirectDomains);
        });
    }

    [Fact]
    public void ViaLauncher_FiltersActBeforeTheAppIsLaunched()
    {
        // Копия, открытая не из лаунчера, ещё до того, как он возьмёт её под контроль, выходит в сеть только через VPN приложения.
        Preset(s =>
        {
            s.Apps.Add(new AppEntry { Name = "Тестовое приложение", ExePath = TestAppProcess.ExePath });
            s.OutsideCopies = OutsideCopies.Launcher;
        });
        var guard = new RecordingGuard();
        Sta.Run(() =>
        {
            var vm = new MainViewModel(Storage, _dialogs, Dispatcher.CurrentDispatcher, Log,
                new UpdateService(_dir.File("update-pending.json"), _dir.File("download")), guardBackend: () => guard, systemProxy: () => null);
            try
            {
                Assert.True(Wait.Until(() => guard.Last?.Apps.Any(r => r.Paths.Any(p =>
                    p.EndsWith(TestAppProcess.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase))) == true, TimeSpan.FromSeconds(5)),
                    "Фильтр для программы приложения должен действовать, хотя из лаунчера ничего не запущено");
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    /// <summary>Фильтры без службы: запоминает последний отправленный набор.</summary>
    private sealed class RecordingGuard : IGuardBackend
    {
        private GuardRequest? _last;

        public GuardRequest? Last => Volatile.Read(ref _last);

        public bool IsConnected => true;

        public event Action? Lost
        {
            add { }
            remove { }
        }

        public Task<GuardReply> ApplyAsync(GuardRequest request, CancellationToken cancellation)
        {
            Volatile.Write(ref _last, request);
            return Task.FromResult(GuardReply.Empty);
        }

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- журнал

    [Fact]
    public void Log_RecordsLauncherStartAndExit()
    {
        WithViewModel((_, log) =>
        {
            var start = log.Snapshot().Single();
            Assert.Equal(LogKind.Info, start.Kind);
            Assert.Equal($"Лаунчер {UpdateService.Format(UpdateService.CurrentVersion)} запущен", start.Text);
        });

        Assert.Equal("Лаунчер закрыт", Log.Snapshot()[^1].Text);
    }

    [Fact]
    public void NewInstall_KillSwitchOff_MainScreenWarnsAboutDnsLeak()
    {
        WithViewModel((vm, _) =>
        {
            Assert.False(vm.KillSwitchEnabled);
            Assert.True(vm.IsDnsLeakPossible);
        });
    }

    [Fact]
    public void DnsLeakWarning_GoesAwayWhenProtectionApplied()
    {
        _dialogs.ConfirmAnswer = true;

        WithViewModel((vm, _) =>
        {
            vm.HideDns = true;
            Assert.True(vm.IsDnsLeakPossible); // до «Применить» защита ещё не действует

            vm.ApplySettingsCommand.Execute(null);
            Assert.False(vm.IsDnsLeakPossible);
            Assert.True(Storage.Load().HideDns);
        });
    }

    [Fact]
    public void ShowLogCommand_OpensTheLauncherLog()
    {
        WithViewModel((vm, log) =>
        {
            vm.ShowLogCommand.Execute(null);
            Assert.Same(log, _dialogs.ShownLog);
        });
    }
}
