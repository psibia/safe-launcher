using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
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

/// <summary>Фильтры Windows для тестов окна: служба не нужна, набор просто запоминается.</summary>
public sealed class QuietGuard : IGuardBackend
{
    public static readonly Func<IGuardBackend> Create = () => new QuietGuard();

    public bool IsConnected => true;

    public event Action? Lost
    {
        add { }
        remove { }
    }

    public Task<GuardReply> ApplyAsync(GuardRequest request, CancellationToken cancellation) => Task.FromResult(GuardReply.Empty);

    public void Dispose()
    {
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

    public List<string> Warnings { get; } = new();

    public void ShowInfo(string message, string title)
    {
    }

    public void ShowWarning(string message, string title = "Внимание") => Warnings.Add(message);

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

    public void ShowActivityLog(ActivityLog log, ILogActions logs) => ShownLog = log;

    public List<string> LogWarnings { get; } = new();
    public bool ExportAnswer { get; set; }
    public List<string?> ShownExports { get; } = new();
    public int TechLogShown { get; private set; }

    public bool ShowWarningWithLogs(string message, string title)
    {
        LogWarnings.Add(message);
        return ExportAnswer;
    }

    public void ShowExport(ILogActions logs, string? preset) => ShownExports.Add(preset);

    public void ShowTechLog(ILogActions logs) => TechLogShown++;

    public DnsJournal? ShownDnsJournal { get; private set; }

    public void ShowDnsJournal(AppTileViewModel tile, DnsJournal journal) => ShownDnsJournal = journal;

    public void CloseDnsJournal(Guid appId)
    {
    }

    /// <summary>Ответ на «Сохранить изменения?»: true — сохранить, false — не сохранять, null — остаться.</summary>
    public bool? SaveAnswer { get; set; } = false;
    public int AskSaveCalls { get; private set; }

    public bool? AskSave(string title, string message, string saveText)
    {
        AskSaveCalls++;
        return SaveAnswer;
    }

    public string? WireGuardFile { get; set; }
    public string? PickWireGuardFile() => WireGuardFile;

    public string? ClipboardText { get; set; }
    public string? ReadClipboardText() => ClipboardText;
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
