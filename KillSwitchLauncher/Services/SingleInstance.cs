using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Одна копия лаунчера на сеанс Windows. Две копии мешали бы друг другу (обе следят за VPN и закрывают
/// одни и те же приложения), поэтому второй запуск только показывает окно уже работающей копии и выходит.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string DefaultScope = "KillSwitchLauncher";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>
    /// Стать единственной копией. Если другая копия ещё закрывается (например, после установки обновления),
    /// ждём её не дольше <paramref name="wait"/>. null — уже работает другая копия.
    /// </summary>
    public static SingleInstance? TryAcquire(TimeSpan wait) => TryAcquire(wait, DefaultScope);

    /// <summary>Для тестов: своё имя, чтобы не пересекаться с запущенным лаунчером.</summary>
    internal static SingleInstance? TryAcquire(TimeSpan wait, string scope)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, MutexName(scope));
        }
        catch (UnauthorizedAccessException)
        {
            return null; // мьютекс создан копией с правами администратора: значит, она работает
        }

        try
        {
            if (!mutex.WaitOne(wait))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // Прошлая копия завершилась аварийно, мьютекс теперь наш.
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName(scope));
        return new SingleInstance(mutex, activate);
    }

    /// <summary>Попросить работающую копию показать окно. Вызывается из второй копии перед выходом.</summary>
    public static void ActivateExisting() => ActivateExisting(DefaultScope);

    internal static void ActivateExisting(string scope)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateEventName(scope), out var activate))
                return;

            using (activate)
            {
                // Вывести окно на передний план может только процесс, который сейчас активен (эта копия), поэтому
                // разрешаем это первой копии.
                AllowSetForegroundWindow(AsfwAny);
                activate.Set();
            }
        }
        catch (Exception)
        {
            // нет доступа к событию (копия с правами администратора): просто выходим
        }
    }

    /// <summary>Вызывать <paramref name="onActivate"/> (в фоновом потоке) при каждом повторном запуске лаунчера.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration ??= ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null,
            Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // мьютекс уже отпущен или принадлежит другому потоку: при выходе процесса Windows отпустит его сама
        }

        _mutex.Dispose();
    }

    private static string MutexName(string scope) => $@"Local\{scope}.SingleInstance";

    private static string ActivateEventName(string scope) => $@"Local\{scope}.Activate";

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
