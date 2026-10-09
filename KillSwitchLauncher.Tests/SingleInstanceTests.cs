using System;
using System.Threading;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Одна копия лаунчера. Мьютекс принадлежит потоку, поэтому «копии» здесь — разные потоки, а имя у каждого теста
/// своё: запущенный рядом настоящий лаунчер тестам не мешает.
/// </summary>
public class SingleInstanceTests
{
    private static string NewScope() => "KslTest." + Guid.NewGuid().ToString("N");

    /// <summary>Держит «первую копию» в отдельном потоке, пока тест не отпустит.</summary>
    private sealed class FirstCopy : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public FirstCopy(string scope, Action<SingleInstance>? onAcquired = null)
        {
            _thread = new Thread(() =>
            {
                var instance = SingleInstance.TryAcquire(TimeSpan.Zero, scope);
                Instance = instance;
                if (instance is not null)
                    onAcquired?.Invoke(instance);
                _acquired.Set();
                _release.Wait();
                instance?.Dispose();
            }) { IsBackground = true };
            _thread.Start();
            Assert.True(_acquired.Wait(TimeSpan.FromSeconds(5)));
        }

        public SingleInstance? Instance { get; private set; }

        public void Release()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Release();
    }

    [Fact]
    public void FirstCopy_Acquires()
    {
        using var first = new FirstCopy(NewScope());
        Assert.NotNull(first.Instance);
    }

    [Fact]
    public void SecondCopy_IsRefused_WhileFirstRuns()
    {
        var scope = NewScope();
        using var first = new FirstCopy(scope);

        Assert.Null(SingleInstance.TryAcquire(TimeSpan.Zero, scope));
    }

    [Fact]
    public void AfterFirstCopyExits_NextOneAcquires()
    {
        var scope = NewScope();
        var first = new FirstCopy(scope);
        first.Release();

        using var second = SingleInstance.TryAcquire(TimeSpan.Zero, scope);
        Assert.NotNull(second);
    }

    [Fact]
    public void WaitingCopy_AcquiresWhenFirstExitsInTime()
    {
        // Так новая версия после установки обновления дожидается, пока закроется старая.
        var scope = NewScope();
        var first = new FirstCopy(scope);
        var releaser = new Thread(() =>
        {
            Thread.Sleep(300);
            first.Release();
        });
        releaser.Start();

        using var second = SingleInstance.TryAcquire(TimeSpan.FromSeconds(10), scope);

        Assert.NotNull(second);
        releaser.Join();
    }

    [Fact]
    public void CrashedFirstCopy_DoesNotBlockTheNext()
    {
        var scope = NewScope();

        // Поток захватил мьютекс и завершился, не отпустив его: так выглядит аварийное завершение.
        var crashed = new Thread(() => SingleInstance.TryAcquire(TimeSpan.Zero, scope));
        crashed.Start();
        crashed.Join();

        using var next = SingleInstance.TryAcquire(TimeSpan.FromSeconds(2), scope);
        Assert.NotNull(next);
    }

    [Fact]
    public void SecondLaunch_AsksFirstCopyToShowItsWindow()
    {
        var scope = NewScope();
        using var activated = new ManualResetEventSlim();
        using var first = new FirstCopy(scope, instance => instance.ListenForActivation(() => activated.Set()));

        SingleInstance.ActivateExisting(scope);

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void EveryRepeatedLaunch_IsDelivered()
    {
        var scope = NewScope();
        var count = 0;
        using var first = new FirstCopy(scope, instance => instance.ListenForActivation(() => Interlocked.Increment(ref count)));

        for (var i = 0; i < 3; i++)
        {
            SingleInstance.ActivateExisting(scope);
            Assert.True(Wait.Until(() => Volatile.Read(ref count) == i + 1, TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void ActivateExisting_WithoutRunningCopy_DoesNothing() =>
        SingleInstance.ActivateExisting(NewScope());
}
