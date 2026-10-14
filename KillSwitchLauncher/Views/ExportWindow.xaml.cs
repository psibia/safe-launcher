using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using Microsoft.Win32;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Views;

/// <summary>
/// «Выгрузить журналы для анализа» (UI-4): период, «что случилось», галочки скрытия, оценка размера, сохранение zip.
/// Выгрузка не падает целиком: без службы архив собирается из журнала лаунчера и снимков.
/// </summary>
public partial class ExportWindow : Window
{
    private readonly ILogActions _logs;
    private readonly ExportChoices _choices;
    private readonly Dictionary<RadioButton, ExportPeriod> _radios = new();
    private readonly DispatcherTimer _estimateDelay;
    private CancellationTokenSource? _estimate;
    private CancellationTokenSource? _saving;

    private ExportWindow(ILogActions logs, string? preset)
    {
        InitializeComponent();
        _logs = logs;
        _choices = logs.GetExportChoices(preset);
        _estimateDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            _estimateDelay!.Stop();
            _ = EstimateAsync();
        }, Dispatcher);
        _estimateDelay.Stop();


        foreach (var period in _choices.Periods)
        {
            var radio = new RadioButton { Style = (Style)FindResource("PeriodRadio"), Content = period.Title, GroupName = "Period" };
            radio.Checked += (_, _) => OnPeriodChanged();
            _radios[radio] = period;
            PeriodsPanel.Children.Add(radio);
            if (period.Preset == _choices.DefaultPreset)
                radio.IsChecked = true;
        }

        CustomRadio.Checked += (_, _) => OnPeriodChanged();
        // Список дней — пока хранятся журналы: срок хранения или, если журналы только в памяти, с начала сеанса.
        var stored = TechLog.Settings;
        var firstDay = stored.Storage == LogStorage.Memory
            ? TechLog.SessionStartUtc.ToLocalTime().Date
            : DateTime.Today.AddDays(-stored.RetentionDays);
        foreach (var field in new[] { FromField, ToField })
        {
            field.FirstDay = firstDay;
            field.ValueChanged += (_, _) => OnCustomChanged();
            field.Opened += (_, _) => CustomRadio.IsChecked = true;
            field.GotKeyboardFocus += (_, _) => CustomRadio.IsChecked = true;
        }
        FromField.Value = DateTimeField.RoundDown(DateTime.Now.AddHours(-1));
        ToField.Value = null;

        if (_choices.AddressesHidden)
        {
            HideAddressesBox.IsChecked = true;
            HideAddressesBox.IsEnabled = false;
            HideAddressesNote.Visibility = Visibility.Visible;
        }
        DnsBox.Visibility = _choices.HasDns ? Visibility.Visible : Visibility.Collapsed;

        SaveButton.Click += (_, _) => _ = SaveAsync();
        CancelButton.Click += (_, _) => OnCancel();
        Closing += (_, e) =>
        {
            // Во время сборки «Закрыть» = отменить сборку, окно закроется само.
            if (_saving is not null)
            {
                e.Cancel = true;
                _saving.Cancel();
            }
        };
        Closed += (_, _) => _estimate?.Cancel();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox)
                DragMove();
        };

        OnPeriodChanged();
    }

    public static void Show(Window? owner, ILogActions logs, string? preset)
    {
        var window = new ExportWindow(logs, preset);
        if (owner is { IsLoaded: true, IsVisible: true })
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    /// <summary>Папка «Загрузки» пользователя (если ее нет — «Документы»).</summary>
    public static string DownloadsFolder()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void OnPeriodChanged()
    {
        CustomPanel.Opacity = CustomRadio.IsChecked == true ? 1 : 0.6;
        _estimateDelay.Stop();
        _estimateDelay.Start();
    }

    private void OnCustomChanged()
    {
        if (CustomRadio.IsChecked == true)
            OnPeriodChanged();
    }

    /// <summary>Выбранный период; для «своего» — из полей (null и текст ошибки, если введено неверно).</summary>
    private ExportPeriod? SelectedPeriod(out string? error, bool fresh)
    {
        error = null;
        if (CustomRadio.IsChecked == true)
        {
            var from = (FromField.Value ?? DateTime.Now).ToUniversalTime();
            var to = ToField.Value is { } end ? end.ToUniversalTime() : DateTime.UtcNow;
            if (to <= from)
            {
                error = "Конец периода должен быть позже начала";
                return null;
            }
            if (to - from < ExportPeriods.MinDuration)
            {
                error = $"Промежуток должен быть не короче {ExportPeriods.MinDuration.TotalMinutes:0} минут";
                return null;
            }
            return new ExportPeriod(ExportPeriods.Custom, "Свой промежуток", from, to);
        }

        var selected = _radios.FirstOrDefault(p => p.Key.IsChecked == true).Value;
        if (selected is null)
            return null;
        if (!fresh)
            return selected;
        // Периоды «до сейчас» пересчитываются в момент сохранения.
        return _logs.GetExportChoices(selected.Preset).Periods.FirstOrDefault(p => p.Preset == selected.Preset) ?? selected;
    }

    private async Task EstimateAsync()
    {
        _estimate?.Cancel();
        var cancel = _estimate = new CancellationTokenSource();
        var period = SelectedPeriod(out var error, fresh: false);
        WarningText.Visibility = Visibility.Collapsed;
        if (period is null)
        {
            EstimateText.Text = error ?? string.Empty;
            return;
        }

        EstimateText.Text = "Считаю размер…";
        try
        {
            var input = await _logs.GatherExportAsync(new ExportOptions { Period = period }, null, cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            var lines = input.Launcher.Concat(input.Service).ToList();
            var bytes = LogExport.EstimateBytes(lines);
            EstimateText.Text = lines.Count == 0
                ? "За этот период записей нет. Архив все равно можно сохранить: в нем будут настройки и состояние"
                : $"Примерно {MainViewModel.FormatBytes(LogExport.Compressed(bytes))} в архиве ({lines.Count:N0} строк)";

            var warnings = new List<string>();
            if (input.ServiceProblem is { } problem)
                warnings.Add("Журналов службы и подключений не будет: " + problem);
            var large = bytes > LogExport.LargeBytes;
            CoreErrorsBox.Visibility = large || CoreErrorsBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (large)
                warnings.Add("Записей очень много: выберите период короче или отметьте «только ошибки» для подключений");
            if (_logs.IsRemasking)
                warnings.Add("Сейчас скрываются адреса в сохраненных журналах: сохранение дождется окончания");
            ShowWarning(warnings);
        }
        catch (OperationCanceledException)
        {
            // выбрали другой период
        }
        catch (Exception ex)
        {
            EstimateText.Text = "Размер не посчитать: " + ex.Message;
        }
    }

    private void ShowWarning(IReadOnlyList<string> warnings)
    {
        WarningText.Text = string.Join("\n", warnings);
        WarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SaveAsync()
    {
        if (_saving is not null)
            return;
        var period = SelectedPeriod(out var error, fresh: true);
        if (period is null)
        {
            ShowWarning(new[] { error ?? "Выберите период" });
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить журналы",
            Filter = "Архив zip (*.zip)|*.zip",
            FileName = ExportPeriods.FileName(period.FromUtc, period.ToUtc),
            InitialDirectory = DownloadsFolder()
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var options = new ExportOptions
        {
            Period = period,
            HideAddresses = HideAddressesBox.IsChecked == true,
            HideSites = HideSitesBox.IsChecked == true,
            IncludeDns = DnsBox.IsVisible && DnsBox.IsChecked == true,
            CoreErrorsOnly = CoreErrorsBox.IsVisible && CoreErrorsBox.IsChecked == true,
            Note = NoteBox.Text
        };

        _estimate?.Cancel();
        _saving = new CancellationTokenSource();
        SetBusy(true);
        var path = dialog.FileName;
        var temp = path + ".part";
        try
        {
            var progress = new Progress<string>(text => SaveButton.Content = text.Length > 34 ? "Собираю…" : text);
            ((IProgress<string>)progress).Report("Собираю…");
            if (_logs.IsRemasking)
                ((IProgress<string>)progress).Report("Жду скрытия адресов…");
            var input = await _logs.GatherExportAsync(options, new Progress<string>(text => EstimateText.Text = text), _saving.Token);
            ((IProgress<string>)progress).Report("Сохраняю…");
            var masker = TechLog.Masker;
            var created = DateTime.UtcNow;
            await Task.Run(() =>
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    LogExport.Write(stream, options, input, masker, created);
                File.Move(temp, path, overwrite: true);
            }, _saving.Token);

            var warning = input.ServiceProblem is { } problem ? "Сохранено без журналов службы: " + problem : null;
            _saving = null;
            Close();
            _logs.OnExported(path, warning);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            _saving = null;
            SetBusy(false);
            EstimateText.Text = "Сохранение отменено";
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            _saving = null;
            SetBusy(false);
            ShowWarning(new[] { "Не удалось сохранить архив: " + ex.Message });
        }
    }

    private void OnCancel()
    {
        if (_saving is not null)
            _saving.Cancel();
        else
            Close();
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy;
        if (!busy)
            SaveButton.Content = "Сохранить";
        PeriodsPanel.IsEnabled = CustomRadio.IsEnabled = CustomPanel.IsEnabled = NoteBox.IsEnabled = !busy;
        HideAddressesBox.IsEnabled = !busy && !_choices.AddressesHidden;
        HideSitesBox.IsEnabled = DnsBox.IsEnabled = CoreErrorsBox.IsEnabled = !busy;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // не страшно
        }
    }
}
