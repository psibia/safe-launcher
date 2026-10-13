using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using KillSwitchLauncher.Views;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Чей процесс сделал DNS-запрос: приложение лаунчера, его потомок или посторонний.</summary>
public class DnsOwnerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public DnsOwnerTests() => TestAppProcess.KillAll();

    public void Dispose() => TestAppProcess.KillAll();

    private static AppEntry Entry(string arguments) => new()
    {
        Name = "Тестовое приложение",
        ExePath = TestAppProcess.ExePath,
        Arguments = arguments
    };

    [Fact]
    public void LaunchedApp_IsFound()
    {
        using var tracker = new ProcessTracker();
        var entry = Entry("--sleep 60");

        var result = tracker.Launch(entry);
        Assert.True(result.Contained);

        Assert.True(tracker.TryGetOwner(result.ProcessId, out var appId, out var name));
        Assert.Equal(entry.Id, appId);
        Assert.Equal(TestAppProcess.ProcessName + ".exe", name, ignoreCase: true);
    }

    [Fact]
    public void ChildOfLauncherStub_IsFound()
    {
        // Как Discord: Update.exe запускает Discord.exe и выходит. Запросы потомка — тоже запросы приложения.
        using var tracker = new ProcessTracker();
        var entry = Entry("--spawn-child 60");

        var result = tracker.Launch(entry);
        Assert.True(Wait.Until(() => TestAppProcess.Count() == 1 && !IsRunning(result.ProcessId), Timeout));

        var child = ChildPid(result.ProcessId);
        Assert.True(tracker.TryGetOwner(child, out var appId, out _));
        Assert.Equal(entry.Id, appId);
    }

    [Fact]
    public void ExitedProcess_IsStillFound_ForAMoment()
    {
        // Событие DNS может прийти чуть позже, чем процесс завершился.
        using var tracker = new ProcessTracker();
        var entry = Entry("--spawn-child 60");

        var result = tracker.Launch(entry);
        Assert.True(Wait.Until(() => !IsRunning(result.ProcessId), Timeout));

        Assert.True(tracker.TryGetOwner(result.ProcessId, out var appId, out _));
        Assert.Equal(entry.Id, appId);
    }

    [Fact]
    public void OtherProcesses_AreNotFound()
    {
        using var tracker = new ProcessTracker();
        tracker.Launch(Entry("--sleep 60"));

        Assert.False(tracker.TryGetOwner(Environment.ProcessId, out _, out _));
        Assert.False(tracker.TryGetOwner(4, out _, out _)); // System
        Assert.False(tracker.TryGetOwner(int.MaxValue - 1, out _, out _));
    }

    private static int ChildPid(int parentPid)
    {
        foreach (var p in Process.GetProcessesByName(TestAppProcess.ProcessName))
        {
            using (p)
            {
                if (p.Id != Environment.ProcessId && p.Id != parentPid && !p.HasExited)
                    return p.Id;
            }
        }

        throw new InvalidOperationException("Потомок не найден");
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

/// <summary>Журнал DNS в главном окне: пункт меню, значок, открытие журнала. Источник событий — подставной.</summary>
public class DnsViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly List<FakeSource> _sources = new();
    private Exception? _startError;

    public void Dispose() => _dir.Dispose();

    private SettingsStorage Storage => new(_dir.File("settings.json"));

    private sealed class FakeSource : IDnsEventSource
    {
        public bool Disposed { get; private set; }
        public event EventHandler? Stopped;
        public void Stop() => Stopped?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Disposed = true;
    }

    private Task<IDnsEventSource> StartSource(Action<DnsQueryEvent> onEvent)
    {
        if (_startError is not null)
            return Task.FromException<IDnsEventSource>(_startError);
        var source = new FakeSource();
        _sources.Add(source);
        return Task.FromResult<IDnsEventSource>(source);
    }

    private AppEntry AddApp(bool trackDns = false)
    {
        var entry = new AppEntry { Name = "Discord", ExePath = @"C:\Apps\Discord.exe", TrackDns = trackDns };
        var settings = Storage.Load();
        settings.Apps.Add(entry);
        Storage.Save(settings);
        return entry;
    }

    private void WithViewModel(Action<MainViewModel> test)
    {
        Sta.Run(() =>
        {
            var vm = new MainViewModel(Storage, _dialogs, Dispatcher.CurrentDispatcher, new ActivityLog(_dir.File("events.log")),
                new UpdateService(_dir.File("update-pending.json"), _dir.File("download")), StartSource, guardBackend: QuietGuard.Create, systemProxy: () => null);
            try
            {
                test(vm);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void Off_ByDefault()
    {
        AddApp();

        WithViewModel(vm =>
        {
            var tile = vm.Apps.Single();
            Assert.False(tile.IsDnsTracking);
            Assert.False(tile.CanOpenDnsJournal);
            Assert.Equal("Включить журнал DNS", tile.DnsToggleText);
            Assert.Empty(_sources);
        });
    }

    [Fact]
    public void Toggle_TurnsOn_Saves_AndStartsRecording()
    {
        var entry = AddApp();

        WithViewModel(vm =>
        {
            var tile = vm.Apps.Single();
            tile.ToggleDnsCommand.Execute(null);

            Assert.True(tile.IsDnsTracking); // значок «DNS» на плитке
            Assert.True(tile.IsDnsActive);
            Assert.True(tile.CanOpenDnsJournal);
            Assert.Equal("Выключить журнал DNS", tile.DnsToggleText);
            Assert.Single(_sources);
        });

        Assert.True(Storage.Load().Apps.Single(a => a.Id == entry.Id).TrackDns);
        Assert.True(_sources.Single().Disposed); // лаунчер закрылся — запись остановлена
    }

    [Fact]
    public void Toggle_TurnsOff_StopsRecording_JournalStaysOpenable()
    {
        var entry = AddApp();

        WithViewModel(vm =>
        {
            var tile = vm.Apps.Single();
            tile.ToggleDnsCommand.Execute(null);
            tile.ToggleDnsCommand.Execute(null);

            Assert.False(tile.IsDnsTracking);
            Assert.False(tile.IsDnsActive);
            Assert.True(_sources.Single().Disposed);
        });

        Assert.False(Storage.Load().Apps.Single(a => a.Id == entry.Id).TrackDns);
    }

    [Fact]
    public void SavedFlag_DoesNotStartRecordingAtLauncherStart()
    {
        // Запись начнётся при запуске приложения из лаунчера, а не при запуске самого лаунчера (без лишнего окна UAC).
        AddApp(trackDns: true);

        WithViewModel(vm =>
        {
            var tile = vm.Apps.Single();
            Assert.True(tile.IsDnsTracking);
            Assert.False(tile.IsDnsActive);
            Assert.True(tile.CanOpenDnsJournal);
            Assert.Empty(_sources);
        });
    }

    [Fact]
    public void UacRefused_TurnsToggleBack_AndTells()
    {
        var entry = AddApp();
        _startError = new Win32Exception(1223);

        WithViewModel(vm =>
        {
            var toasts = new List<ToastInfo>();
            vm.ToastRequested += (_, t) => toasts.Add(t);
            var tile = vm.Apps.Single();

            tile.ToggleDnsCommand.Execute(null);

            Assert.False(tile.IsDnsTracking);
            Assert.Contains(toasts, t => t.Title == "Журнал DNS не запущен");
            Assert.Equal(0, _dialogs.ErrorCalls);
        });

        Assert.False(Storage.Load().Apps.Single(a => a.Id == entry.Id).TrackDns);
    }

    [Fact]
    public void OtherStartError_IsShown()
    {
        AddApp();
        _startError = new InvalidOperationException("сломалось");

        WithViewModel(vm =>
        {
            var tile = vm.Apps.Single();
            tile.ToggleDnsCommand.Execute(null);

            Assert.False(tile.IsDnsTracking);
            Assert.Equal(1, _dialogs.ErrorCalls);
        });
    }

    [Fact]
    public void OpenJournal_ShowsJournalOfThatApp()
    {
        AddApp(trackDns: true);

        WithViewModel(vm =>
        {
            vm.Apps.Single().OpenDnsJournalCommand.Execute(null);

            Assert.NotNull(_dialogs.ShownDnsJournal);
            Assert.Equal("Discord", _dialogs.ShownDnsJournal!.AppName);
            Assert.True(_dialogs.ShownDnsJournal.IsEmpty);
        });
    }
}

public class DnsJournalWindowTextTests
{
    [Theory]
    [InlineData(0, "0 запросов")]
    [InlineData(1, "1 запрос")]
    [InlineData(2, "2 запроса")]
    [InlineData(5, "5 запросов")]
    [InlineData(11, "11 запросов")]
    [InlineData(21, "21 запрос")]
    [InlineData(112, "112 запросов")]
    [InlineData(1004, "1004 запроса")]
    public void Plural_Russian(long count, string expected) =>
        Assert.Equal(expected, DnsJournalWindow.Plural(count, "запрос", "запроса", "запросов"));
}
