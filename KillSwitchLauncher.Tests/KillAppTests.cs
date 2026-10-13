using System;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>«Завершить все процессы» для одного приложения на настоящих процессах (см. TestApp).</summary>
public class KillAppTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public KillAppTests() => TestAppProcess.KillAll();

    public void Dispose() => TestAppProcess.KillAll();

    private static AppEntry Entry(string arguments) => new()
    {
        Name = "Тестовое приложение",
        ExePath = TestAppProcess.ExePath,
        Arguments = arguments
    };

    /// <summary>Другое приложение: cmd с ping на минуту. Из System32, поэтому лаунчер не ищет его копии по папке.</summary>
    private static AppEntry OtherEntry() => new()
    {
        Name = "Другое приложение",
        ExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
        Arguments = "/c ping -n 60 127.0.0.1 >nul"
    };

    [Fact]
    public void Kill_ClosesAppWithChildren_AndReports()
    {
        using var tracker = new ProcessTracker();
        var entry = Entry("--spawn-child 60");
        tracker.Launch(entry);
        Assert.True(Wait.Until(() => TestAppProcess.Count() == 1, Timeout), "Должен остаться потомок заглушки");

        var report = tracker.Kill(entry.Id);

        Assert.Equal(new[] { "Тестовое приложение" }, report.Killed);
        Assert.Empty(report.Failed);
        Assert.True(Wait.Until(() => TestAppProcess.Count() == 0, Timeout));
        Assert.True(Wait.Until(() => tracker.CountFor(entry.Id) == 0, Timeout));
    }

    [Fact]
    public void Kill_LeavesOtherAppsRunning()
    {
        using var tracker = new ProcessTracker();
        var entry = Entry("--sleep 60");
        var other = OtherEntry();
        tracker.Launch(entry);
        tracker.Launch(other);
        Assert.True(Wait.Until(() => tracker.CountFor(other.Id) >= 1 && tracker.CountFor(entry.Id) == 1, Timeout));

        tracker.Kill(entry.Id);

        Assert.True(Wait.Until(() => TestAppProcess.Count() == 0, Timeout));
        Thread.Sleep(1500);
        Assert.True(tracker.CountFor(other.Id) >= 1, "Другое приложение не должно закрываться");

        tracker.KillAll(blockRelaunch: false);
    }

    [Fact]
    public void AfterKill_AppCanBeStartedAgain()
    {
        // В отличие от Kill-Switch, повторный запуск не блокируется.
        using var tracker = new ProcessTracker();
        var entry = Entry("--sleep 60");
        var blocked = 0;
        tracker.RelaunchBlocked += (_, _) => Interlocked.Increment(ref blocked);

        tracker.Launch(entry);
        Assert.True(Wait.Until(() => tracker.CountFor(entry.Id) == 1, Timeout));
        tracker.Kill(entry.Id);
        Assert.True(Wait.Until(() => TestAppProcess.Count() == 0, Timeout));

        tracker.Launch(entry);
        Thread.Sleep(2500); // наблюдение успело бы закрыть заблокированный запуск
        Assert.Equal(1, TestAppProcess.Count());
        Assert.Equal(0, Volatile.Read(ref blocked));

        tracker.KillAll(blockRelaunch: false);
    }

    [Fact]
    public void Kill_NotRunning_DoesNothing()
    {
        using var tracker = new ProcessTracker();

        var report = tracker.Kill(Guid.NewGuid());

        Assert.Empty(report.Killed);
        Assert.Empty(report.Failed);
    }
}

/// <summary>Пункт меню «Завершить все процессы» в главном окне.</summary>
public class KillAppViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _dir.Dispose();

    private void WithTile(Action<MainViewModel, AppTileViewModel> test)
    {
        var storage = new SettingsStorage(_dir.File("settings.json"));
        var settings = storage.Load();
        settings.Apps.Add(new AppEntry { Name = "Discord", ExePath = @"C:\Apps\Discord.exe" });
        storage.Save(settings);

        Sta.Run(() =>
        {
            var vm = new MainViewModel(storage, _dialogs, Dispatcher.CurrentDispatcher, new ActivityLog(_dir.File("events.log")),
                new UpdateService(_dir.File("update-pending.json"), _dir.File("download")), guardBackend: QuietGuard.Create, systemProxy: () => null);
            try
            {
                test(vm, Assert.Single(vm.Apps));
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void AsksFirst_AndCancelDoesNothing()
    {
        _dialogs.ConfirmAnswer = false;

        WithTile((vm, tile) =>
        {
            var toasts = 0;
            vm.ToastRequested += (_, _) => toasts++;

            tile.KillCommand.Execute(null);

            Assert.Equal(1, _dialogs.ConfirmCalls);
            Assert.Equal(0, toasts);
        });
    }

    [Fact]
    public void NothingRunning_SaysSo()
    {
        WithTile((vm, tile) =>
        {
            ToastInfo? toast = null;
            vm.ToastRequested += (_, t) => toast = t;

            Assert.False(tile.IsRunning); // пункт меню неактивен

            tile.KillCommand.Execute(null);

            Assert.Equal("Нечего завершать", toast?.Title);
            Assert.Equal(0, _dialogs.ErrorCalls);
        });
    }
}
