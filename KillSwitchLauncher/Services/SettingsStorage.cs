using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using System.Threading;
using KillSwitchLauncher.Models;

namespace KillSwitchLauncher.Services;

/// <summary>Хранит настройки и список приложений в %LOCALAPPDATA%\KillSwitchLauncher\settings.json.</summary>
public sealed class SettingsStorage
{
    /// <summary>
    /// Версия формата settings.json. 1 — лаунчер 2.0: подключения, сетевые настройки приложений.
    /// </summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), // русские названия в файле читаются как есть, а не \u0420…
        Converters = { new TolerantEnumConverter() }
    };

    public SettingsStorage()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitchLauncher", "settings.json"))
    {
    }

    /// <summary>Для тестов: настройки в указанном файле.</summary>
    internal SettingsStorage(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>
    /// Почему файл настроек не прочитан (занят другой программой, нет доступа); null — прочитан или его не было. Тогда
    /// лаунчер работает с чистыми настройками и не сохраняет их, чтобы не затереть настоящие.
    /// </summary>
    public string? LoadProblem { get; private set; }

    public AppSettings Load()
    {
        LoadProblem = null;
        string json;
        try
        {
            if (!File.Exists(FilePath))
                return Migrate(new AppSettings());
            json = ReadWithRetry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadProblem = ex.Message;
            return Migrate(new AppSettings());
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (settings.SettingsVersion < CurrentVersion)
            {
                TryBackup($".v{settings.SettingsVersion}.bak");
                settings.WelcomeDone = true; // лаунчером уже пользовались: мастер первого запуска не нужен
            }
            return Migrate(settings);
        }
        catch (Exception)
        {
            // Повреждённый файл не должен ломать запуск: сохраняем копию и начинаем с чистых настроек.
            TryBackup(".broken");
            return Migrate(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        if (LoadProblem is not null)
            return; // настоящие настройки не прочитаны: чистые не должны их затереть
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Пишем во временный файл и подменяем, чтобы при сбое не остался обрезанный JSON.
        settings.SettingsVersion = CurrentVersion;
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>
    /// Приводит настройки к текущему формату. Лаунчер 1.x (версия 0): у всех приложений «Как в общих»,
    /// в общих — «Системный VPN (адаптеры)» с прежним правилом, запуск только с VPN, как и раньше. Поведение не меняется.
    /// Заодно исправляет то, что могло испортиться при ручной правке файла.
    /// </summary>
    internal static AppSettings Migrate(AppSettings settings)
    {
        settings.Apps ??= new List<AppEntry>();
        settings.Apps.RemoveAll(a => a is null);
        settings.SelectedAdapters ??= new List<string>();
        settings.KnownAdapters ??= new List<KnownAdapter>();
        settings.Connections ??= new List<VpnConnection>();
        settings.Connections.RemoveAll(c => c is null);
        settings.Logs = (settings.Logs ?? new SafeLauncher.Logging.LogSettings()).Normalized();

        if (settings.Apps.Count > 0)
            settings.WelcomeDone = true;

        foreach (var app in settings.Apps)
        {
            app.Network ??= new AppNetwork();
            app.Network.SelectedAdapters ??= new List<string>();
            app.Network.DirectDomains = DomainList.Normalize(app.Network.DirectDomains ?? new List<string>());
        }

        foreach (var connection in settings.Connections)
        {
            connection.SubscriptionServers ??= new List<string>();
            if (connection.SubscriptionUpdateHours <= 0)
                connection.SubscriptionUpdateHours = 24;
        }

        settings.SettingsVersion = Math.Max(settings.SettingsVersion, CurrentVersion);
        return settings;
    }

    /// <summary>Файл бывает ненадолго занят антивирусом или синхронизацией: несколько попыток.</summary>
    private string ReadWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(FilePath);
            }
            catch (IOException) when (attempt < 10 && File.Exists(FilePath))
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>
    /// Значения перечислений по имени. Незнакомое имя (файл от более новой версии лаунчера или правленный вручную) даёт
    /// значение по умолчанию, а не считает повреждённым весь файл.
    /// </summary>
    private sealed class TolerantEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

        private sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named) &&
                    Enum.IsDefined(named))
                    return named;
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number))
                    return (T)Enum.ToObject(typeof(T), number);
                reader.Skip();
                return default;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
                writer.WriteStringValue(value.ToString());
        }
    }

    private void TryBackup(string suffix)
    {
        try
        {
            File.Copy(FilePath, FilePath + suffix, overwrite: true);
        }
        catch
        {
            // ничего страшного
        }
    }
}
