using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KillSwitchLauncher.Views;
using Microsoft.Win32;

namespace KillSwitchLauncher.Services;

/// <summary>Диалоги, вынесенные из ViewModel, чтобы её можно было тестировать без окон.</summary>
public interface IDialogService
{
    string? PickExecutableOrShortcut();
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
    void ShowActivityLog(ActivityLog log);
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

    public void ShowActivityLog(ActivityLog log) => WithDimmedOwner(owner => ActivityLogWindow.Show(owner, log));

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
