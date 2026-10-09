using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Kill-Switch на настоящих процессах. «Приложение» — KillSwitchLauncher.Tests.exe из папки тестов (см. TestApp):
/// оно просто работает минуту, а с --spawn-child запускает свою копию и сразу выходит, как ярлык Discord.
/// После каждого теста все копии закрываются.
/// </summary>
public class ProcessTrackerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public ProcessTrackerTests() => TestAppProcess.KillAll();

    public void Dispose() => TestAppProcess.KillAll();

    private static AppEntry Entry(string arguments = "--sleep 60") => new()
    {
        Name = "Тестовое приложение",
        ExePath = TestAppProcess.ExePath,
        Arguments = arguments
    };

    private static bool AllClosed() => Wait.Until(() => TestAppProcess.Count() == 0, Timeout);

    [Theory]
    [InlineData(0, "0 процессов")]
    [InlineData(1, "1 процесс")]
    [InlineData(3, "3 процесса")]
    [InlineData(5, "5 процессов")]
    [InlineData(11, "11 процессов")]
    [InlineData(14, "14 процессов")]
    [InlineData(21, "21 процесс")]
    [InlineData(22, "22 процесса")]
    [InlineData(111, "111 процессов")]
    public void ProcessesText_RussianPlurals(int count, string expected) =>
        Assert.Equal(expected, ProcessTracker.ProcessesText(count));

    [Fact]
    public void Launch_PutsAppUnderControl_AndKillAllClosesIt()
    {
        using var tracker = new ProcessTracker();
        var entry = Entry();
        var changed = 0;
        tracker.Changed += (_, _) => Interlocked.Increment(ref changed);

        var result = tracker.Launch(entry);

        Assert.True(result.ProcessId > 0);
        Assert.True(result.Contained, "Процесс должен попасть в задание Windows");
        Assert.False(result.Elevated);
        Assert.True(Volatile.Read(ref changed) > 0);
        Assert.True(Wait.Until(() => tracker.Count == 1, Timeout));
        Assert.Equal(1, tracker.CountFor(entry.Id));
        Assert.Equal(0, tracker.CountFor(Guid.NewGuid()));

        var report = tracker.KillAll(blockRelaunch: false);

        Assert.Equal(new[] { "Тестовое приложение" }, report.Killed);
        Assert.Empty(report.Failed);
        Assert.True(AllClosed());
        Assert.True(Wait.Until(() => tracker.Count == 0, Timeout));
    }

    [Fact]
    public void ChildOfExitedLauncherStub_IsStillControlled()
    {
        // Как Discord: Update.exe запускает Discord.exe и выходит. Потомок остаётся в задании.
        using var tracker = new ProcessTracker();

        var result = tracker.Launch(Entry("--spawn-child 60"));
        Assert.True(result.Contained);

        Assert.True(Wait.Until(() => !IsRunning(result.ProcessId) && TestAppProcess.Count() == 1, Timeout),
            "Заглушка должна выйти, а её потомок — работать");
        Assert.True(Wait.Until(() => tracker.Count == 1, Timeout));

        var report = tracker.KillAll(blockRelaunch: false);

        Assert.Single(report.Killed);
        Assert.True(AllClosed(), "Потомок заглушки должен быть закрыт вместе с приложением");
    }

    [Fact]
    public void AlreadyRunningCopy_IsAdoptedOnLaunch()
    {
        // Например, Discord уже запущен из автозагрузки, а пользователь открыл его через лаунчер.
        using var external = TestAppProcess.StartExternal();
        using var tracker = new ProcessTracker();

        var result = tracker.Launch(Entry());

        Assert.Equal(1, result.Adopted);
        Assert.True(Wait.Until(() => tracker.Count == 2, Timeout));

        var report = tracker.KillAll(blockRelaunch: false);

        Assert.Equal(new[] { "Тестовое приложение (2 процесса)" }, report.Killed);
        Assert.True(external.WaitForExit(5000), "Запущенная до лаунчера копия тоже должна закрыться");
        Assert.True(AllClosed());
    }

    [Fact]
    public void CopyStartedLater_IsPickedUpByWatcher()
    {
        using var tracker = new ProcessTracker();
        tracker.Launch(Entry());
        Assert.True(Wait.Until(() => tracker.Count == 1, Timeout));

        using var external = TestAppProcess.StartExternal();

        Assert.True(Wait.Until(() => tracker.Count == 2, Timeout), "Копию, запущенную в обход лаунчера, нужно взять под контроль");
        tracker.KillAll(blockRelaunch: false);
        Assert.True(external.WaitForExit(5000));
    }

    [Fact]
    public void AfterKillSwitch_RelaunchIsBlocked_AndReported()
    {
        using var tracker = new ProcessTracker();
        var entry = Entry();
        using var blocked = new ManualResetEventSlim();
        string? blockedName = null;
        tracker.RelaunchBlocked += (_, name) =>
        {
            blockedName = name;
            blocked.Set();
        };

        tracker.Launch(entry);
        Assert.True(Wait.Until(() => tracker.Count == 1, Timeout));
        tracker.KillAll(blockRelaunch: true);
        Assert.True(AllClosed());

        // Уведомление о заблокированном запуске — не чаще раза в 10 секунд (считая от срабатывания Kill-Switch).
        Thread.Sleep(TimeSpan.FromSeconds(10.5));
        using var relaunched = TestAppProcess.StartExternal();

        Assert.True(relaunched.WaitForExit(5000), "Без VPN приложение должно закрываться сразу после запуска");
        Assert.True(blocked.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("Тестовое приложение", blockedName);
    }

    [Fact]
    public void StopBlocking_AllowsRelaunch()
    {
        // VPN вернулся: приложение снова можно запускать.
        using var tracker = new ProcessTracker();
        tracker.Launch(Entry());
        Assert.True(Wait.Until(() => tracker.Count == 1, Timeout));
        tracker.KillAll(blockRelaunch: true);
        Assert.True(AllClosed());

        tracker.StopBlocking();
        using var relaunched = TestAppProcess.StartExternal();

        Assert.False(relaunched.WaitForExit(3000), "После возврата VPN запуск не должен блокироваться");
    }

    [Fact]
    public void LauncherExit_WithKillSwitchOn_ClosesApps()
    {
        // Флаг задания «закрыть процессы при закрытии задания»: сработает и при аварийном завершении лаунчера.
        var tracker = new ProcessTracker();
        tracker.SetKillOnClose(true);
        tracker.Launch(Entry());
        Assert.True(Wait.Until(() => TestAppProcess.Count() == 1, Timeout));

        tracker.Dispose();

        Assert.True(AllClosed());
    }

    [Fact]
    public void LauncherExit_WithKillSwitchOff_LeavesAppsRunning()
    {
        var tracker = new ProcessTracker();
        tracker.SetKillOnClose(false);
        tracker.Launch(Entry());
        Assert.True(Wait.Until(() => TestAppProcess.Count() == 1, Timeout));

        tracker.Dispose();
        Thread.Sleep(1000);

        Assert.Equal(1, TestAppProcess.Count());
    }

    [Fact]
    public void KillSwitchTurnedOnAfterLaunch_AppliesToRunningApps()
    {
        var tracker = new ProcessTracker();
        tracker.Launch(Entry());
        Assert.True(Wait.Until(() => TestAppProcess.Count() == 1, Timeout));

        tracker.SetKillOnClose(true);
        tracker.Dispose();

        Assert.True(AllClosed());
    }

    [Fact]
    public void Launch_MissingProgram_Throws()
    {
        using var tracker = new ProcessTracker();
        var entry = new AppEntry { Name = "Нет такой", ExePath = @"C:\нет\такой\программы.exe" };

        Assert.Throws<Win32Exception>(() => tracker.Launch(entry));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Launch_AfterDispose_Throws()
    {
        var tracker = new ProcessTracker();
        tracker.Dispose();

        Assert.Throws<ObjectDisposedException>(() => tracker.Launch(Entry()));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
