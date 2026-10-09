using System.Windows.Input;
using System.Windows.Media;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;

namespace KillSwitchLauncher.ViewModels;

/// <summary>Плитка приложения на главном экране.</summary>
public sealed class AppTileViewModel : ObservableObject
{
    private int _runningCount;

    public AppTileViewModel(AppEntry entry, ImageSource? icon, ICommand launch, ICommand remove, ICommand openLocation)
    {
        Entry = entry;
        Icon = icon;
        LaunchCommand = launch;
        RemoveCommand = remove;
        OpenLocationCommand = openLocation;
    }

    public AppEntry Entry { get; }

    public string Name => Entry.Name;

    public string ToolTip =>
        Entry.IsPackaged ? $"Microsoft Store: {Entry.ShortcutPath ?? Entry.AppUserModelId}"
        : string.IsNullOrWhiteSpace(Entry.Arguments) ? Entry.ExePath
        : $"{Entry.ExePath} {Entry.Arguments}";

    /// <summary>Иконка из .exe; null — в разметке показывается иконка по умолчанию.</summary>
    public ImageSource? Icon { get; }

    public ICommand LaunchCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand OpenLocationCommand { get; }

    /// <summary>Сколько процессов этой программы сейчас контролирует лаунчер.</summary>
    public int RunningCount
    {
        get => _runningCount;
        set
        {
            if (!SetField(ref _runningCount, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(RunningToolTip));
        }
    }

    public bool IsRunning => RunningCount > 0;

    public string RunningToolTip => $"Запущено из лаунчера. Под контролем Kill-Switch: {ProcessTracker.ProcessesText(RunningCount)}";
}
