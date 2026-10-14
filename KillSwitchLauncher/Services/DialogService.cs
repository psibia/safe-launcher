using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillSwitchLauncher.ViewModels;
using KillSwitchLauncher.Views;
using Microsoft.Win32;

namespace KillSwitchLauncher.Services;

/// <summary>Диалоги, вынесенные из ViewModel, чтобы её можно было тестировать без окон.</summary>
public interface IDialogService
{
    string? PickExecutableOrShortcut();
    void ShowInfo(string message, string title);
    void ShowWarning(string message, string title = "Внимание");
    void ShowError(string message, string title = "Ошибка");
    bool Confirm(string title, string message, string confirmText, bool destructive = false);

    /// <summary>Окно «Что нового». true — пользователь нажал «Обновить» (кнопка есть только при canUpdate).</summary>
    bool ShowChangelog(string title, string subtitle, string notes, bool canUpdate);

    /// <summary>Модальный лоадер: выполняет работу с прогрессом и кнопкой «Отмена».</summary>
    ProgressOutcome RunWithProgress(string title, string subtitle, Func<IProgress<DownloadProgress>, CancellationToken, Task> work);

    /// <summary>«Свернуть в трей или закрыть?»: true — в трей, false — закрыть, null — отмена.</summary>
    bool? AskHide(out bool remember);

    /// <summary>Окно журнала событий.</summary>
    void ShowActivityLog(ActivityLog log, ILogActions logs);

    /// <summary>Предупреждение об ошибке подключения с кнопкой «Выгрузить журналы»: true — нажата она.</summary>
    bool ShowWarningWithLogs(string message, string title);

    /// <summary>Окно «Выгрузить журналы для анализа». <paramref name="preset"/> — период, выбранный заранее (null — по правилам).</summary>
    void ShowExport(ILogActions logs, string? preset);

    /// <summary>Окно технического журнала. Не модальное; если уже открыто — выводится на передний план.</summary>
    void ShowTechLog(ILogActions logs);

    /// <summary>Окно журнала DNS приложения. Не модальное; если уже открыто — выводится на передний план.</summary>
    void ShowDnsJournal(AppTileViewModel tile, DnsJournal journal);

    /// <summary>Закрыть окно журнала DNS приложения, если оно открыто.</summary>
    void CloseDnsJournal(Guid appId);

    /// <summary>Несохранённые изменения: true — сохранить, false — не сохранять, null — остаться на странице.</summary>
    bool? AskSave(string title, string message, string saveText);

    /// <summary>Файл .conf WireGuard; null — не выбран.</summary>
    string? PickWireGuardFile();

    /// <summary>Текст из буфера обмена; null — там не текст или буфер занят другой программой.</summary>
    string? ReadClipboardText();
}

public sealed class DialogService : IDialogService
{
    public string? PickExecutableOrShortcut()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите программу или ярлык",
            Filter = "Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk|Программы (*.exe)|*.exe|Ярлыки (*.lnk)|*.lnk",
            CheckFileExists = true,
            Multiselect = false,
            // ВАЖНО: иначе диалог сам «разыменует» ярлык и вернёт путь к цели,
            // а нам нужен именно .lnk, чтобы забрать из него аргументы и рабочую папку.
            DereferenceLinks = false
        };

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public void ShowInfo(string message, string title) =>
        WithDimmedOwner(owner => AlertWindow.ShowMessage(owner, AlertKind.Info, title, message));

    public void ShowWarning(string message, string title = "Внимание") =>
        WithDimmedOwner(owner => AlertWindow.ShowMessage(owner, AlertKind.Warning, title, message));

    public void ShowError(string message, string title = "Ошибка") =>
        WithDimmedOwner(owner => AlertWindow.ShowMessage(owner, AlertKind.Error, title, message));

    public bool Confirm(string title, string message, string confirmText, bool destructive = false)
    {
        var result = false;
        WithDimmedOwner(owner => result = AlertWindow.Confirm(owner, title, message, confirmText, destructive));
        return result;
    }

    public bool ShowChangelog(string title, string subtitle, string notes, bool canUpdate)
    {
        var result = false;
        WithDimmedOwner(owner => result = ChangelogWindow.Show(owner, title, subtitle, notes, canUpdate));
        return result;
    }

    public ProgressOutcome RunWithProgress(string title, string subtitle, Func<IProgress<DownloadProgress>, CancellationToken, Task> work)
    {
        ProgressOutcome? outcome = null;
        WithDimmedOwner(owner => outcome = ProgressWindow.Run(owner, title, subtitle, work));
        return outcome!;
    }

    public bool? AskHide(out bool remember)
    {
        bool? result = null;
        var rememberChoice = false;
        WithDimmedOwner(owner => result = AlertWindow.AskHide(owner, out rememberChoice));
        remember = rememberChoice;
        return result;
    }

    public void ShowActivityLog(ActivityLog log, ILogActions logs) => WithDimmedOwner(owner => ActivityLogWindow.Show(owner, log, logs));

    public bool ShowWarningWithLogs(string message, string title)
    {
        var result = false;
        WithDimmedOwner(owner => result = AlertWindow.ShowWithLogs(owner, AlertKind.Warning, title, message));
        return result;
    }

    public void ShowExport(ILogActions logs, string? preset) => WithDimmedOwner(owner => ExportWindow.Show(owner, logs, preset));

    public void ShowTechLog(ILogActions logs) => TechLogWindow.ShowFor(Application.Current?.MainWindow, logs);

    public void ShowDnsJournal(AppTileViewModel tile, DnsJournal journal) =>
        DnsJournalWindow.ShowFor(Application.Current?.MainWindow, tile, journal);

    public void CloseDnsJournal(Guid appId) => DnsJournalWindow.CloseFor(appId);

    public bool? AskSave(string title, string message, string saveText)
    {
        bool? result = null;
        WithDimmedOwner(owner => result = AlertWindow.AskSave(owner, title, message, saveText));
        return result;
    }

    public string? PickWireGuardFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл WireGuard",
            Filter = "Файлы WireGuard (*.conf)|*.conf|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? ReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // буфер обмена открыт другой программой
        }
    }

    /// <summary>
    /// Пока открыт диалог, главное окно затемняется. Если окно свёрнуто или убрано в трей, сначала оно
    /// показывается: диалог без видимого окна легко не заметить, а он ждёт ответа.
    /// </summary>
    private static void WithDimmedOwner(System.Action<Window?> show)
    {
        var owner = Application.Current?.MainWindow;
        var main = owner as MainWindow;
        main?.ShowFromTray();
        main?.SetDimmed(true);
        try
        {
            show(owner);
        }
        finally
        {
            main?.SetDimmed(false);
        }
    }
}
