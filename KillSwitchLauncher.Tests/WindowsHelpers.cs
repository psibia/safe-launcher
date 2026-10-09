using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.Views;

namespace KillSwitchLauncher.Tests;

/// <summary>Код WPF (элементы, Dispatcher, окна) работает только в STA-потоке: выполняем его в отдельном.</summary>
public static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        error?.Throw();
    }
}

/// <summary>Диалоги без окон: ответы задаёт тест, вызовы считаются.</summary>
public sealed class FakeDialogs : IDialogService
{
    public bool? HideAnswer { get; set; } = true;
    public bool HideRemember { get; set; }
    public bool ConfirmAnswer { get; set; } = true;

    public int AskHideCalls { get; private set; }
    public int ConfirmCalls { get; private set; }
    public int ErrorCalls { get; private set; }
    public ActivityLog? ShownLog { get; private set; }

    public string? PickExecutableOrShortcut() => null;

    public void ShowWarning(string message, string title = "Внимание")
    {
    }

    public void ShowError(string message, string title = "Ошибка") => ErrorCalls++;

    public bool Confirm(string title, string message, string confirmText, bool destructive = false)
    {
        ConfirmCalls++;
        return ConfirmAnswer;
    }

    public bool ShowChangelog(string title, string subtitle, string notes, bool canUpdate) => false;

    public ProgressOutcome RunWithProgress(string title, string subtitle, Func<IProgress<DownloadProgress>, CancellationToken, Task> work) =>
        new(false, true, null);

    public bool? AskHide(out bool remember)
    {
        AskHideCalls++;
        remember = HideRemember;
        return HideAnswer;
    }

    public void ShowActivityLog(ActivityLog log) => ShownLog = log;
}

/// <summary>Тестовое «приложение» KillSwitchLauncher.Tests.exe (см. TestApp) и уборка его копий.</summary>
public static class TestAppProcess
{
    public const string ProcessName = "KillSwitchLauncher.Tests";

    public static string ExePath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, ProcessName + ".exe");
            if (!File.Exists(path))
                throw new FileNotFoundException("Не найдена тестовая программа рядом с тестами.", path);
            return path;
        }
    }

    /// <summary>Сколько копий тестовой программы работает (не считая самих тестов).</summary>
    public static int Count() => Others().Count(p =>
    {
        using (p)
            return !p.HasExited;
    });

    public static Process StartExternal(int seconds = 60) =>
        Process.Start(new ProcessStartInfo(ExePath, $"--sleep {seconds}") { UseShellExecute = false })!;

    /// <summary>Закрыть все копии, оставшиеся после теста.</summary>
    public static void KillAll()
    {
        foreach (var p in Others())
        {
            using (p)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception)
                {
                    // уже завершилась
                }
            }
        }
    }

    private static Process[] Others() =>
        Process.GetProcessesByName(ProcessName).Where(p =>
        {
            if (p.Id != Environment.ProcessId) return true;
            p.Dispose();
            return false;
        }).ToArray();
}
