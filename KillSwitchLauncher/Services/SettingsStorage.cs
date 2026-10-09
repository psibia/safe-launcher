using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Хранит настройки и список приложений в %LOCALAPPDATA%\KillSwitchLauncher\settings.json.</summary>
public sealed class SettingsStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsStorage()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitchLauncher", "settings.json"))
    {
    }

    /// <summary>Для тестов: настройки в указанном файле.</summary>
    internal SettingsStorage(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Повреждённый файл не должен ломать запуск: сохраняем копию и начинаем с чистых настроек.
            TryBackupBrokenFile();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Пишем во временный файл и подменяем, чтобы при сбое не остался обрезанный JSON.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".broken", overwrite: true);
        }
        catch
        {
            // ничего страшного
        }
    }
}
