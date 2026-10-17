using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafeLauncher.Updates;

/// <summary>Шаги установки обновления через службу (<see cref="UpdateState.Stage"/>).</summary>
public static class UpdateStages
{
    /// <summary>Ждём, пока закроется лаунчер, который попросил обновление.</summary>
    public const string Waiting = "waiting";

    /// <summary>Повторная проверка установщика по подписи.</summary>
    public const string Verifying = "verifying";

    /// <summary>Остановка службы и ядра.</summary>
    public const string Stopping = "stopping";

    /// <summary>Работает установщик новой версии.</summary>
    public const string Installing = "installing";

    /// <summary>Проверка новой версии: файлы, служба.</summary>
    public const string Checking = "checking";

    /// <summary>
    /// Новая версия установлена, помощник ждёт, пока новый лаунчер запустится и сообщит об этом службе (команда updateStarted).
    /// Окно ожидания на этом шаге запускает лаунчер. Не запустился — помощник возвращает прежнюю версию.
    /// </summary>
    public const string Starting = "starting";

    /// <summary>Новая версия не встала, возвращается прежняя.</summary>
    public const string RollingBack = "rollback";

    /// <summary>Готово: <see cref="UpdateState.Ok"/> говорит, установлена ли новая версия.</summary>
    public const string Done = "done";
}

/// <summary>
/// Ход установки обновления: файл status.json в папке обновления (<see cref="UpdateState.DirectoryPath"/>). Пишет помощник
/// службы (SafeLauncher.Updater.exe, от имени системы), читают окно ожидания обновления и новая версия лаунчера после запуска.
/// Помощник переписывает файл раз в секунду (<see cref="Updated"/>): если время давно не менялось, помощник не работает.
/// </summary>
public sealed class UpdateState
{
    public const string FileName = "status.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>Папка обновления: C:\Program Files\SafeLauncher\update, рядом с папкой службы и ядра. Писать в неё могут только администраторы.</summary>
    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SafeLauncher", "update");

    public static string FilePath => Path.Combine(DirectoryPath, FileName);

    /// <summary>Номер обновления, которое приняла служба (новый у каждого запроса).</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Устанавливаемая версия (2.4.0).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Версия, с которой обновляемся.</summary>
    public string FromVersion { get; set; } = string.Empty;

    /// <summary>Шаг, см. <see cref="UpdateStages"/>.</summary>
    public string Stage { get; set; } = UpdateStages.Waiting;

    /// <summary>Что сейчас происходит, для человека.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Когда файл записан (UTC).</summary>
    public DateTime Updated { get; set; }

    /// <summary>Когда помощник начал работу (UTC).</summary>
    public DateTime Started { get; set; }

    /// <summary>Установка закончилась (успешно или нет), помощник больше ничего не делает.</summary>
    public bool Done { get; set; }

    /// <summary>Новая версия установлена и работает.</summary>
    public bool Ok { get; set; }

    /// <summary>Новая версия не встала, и прежняя возвращена.</summary>
    public bool RolledBack { get; set; }

    /// <summary>Почему не получилось, одной строкой для человека; подробности в update.log.</summary>
    public string? Error { get; set; }

    /// <summary>На каком шаге не получилось (<see cref="UpdateStages"/>, без отката); null — получилось.</summary>
    public string? FailedStage { get; set; }

    /// <summary>Помощник давно не обновлял файл: он не работает (упал или его закрыли).</summary>
    public bool IsStale(DateTime utcNow, TimeSpan timeout) => !Done && utcNow - Updated > timeout;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static UpdateState? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateState>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Прочитать файл; null — его нет, он пишется прямо сейчас или повреждён (прочитать позже).</summary>
    public static UpdateState? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            // Не мешаем помощнику заменить файл, пока он открыт здесь.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return FromJson(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Записать целиком: сначала во временный файл, затем заменить, чтобы читатель не увидел половину.</summary>
    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
