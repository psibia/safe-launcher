using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SafeLauncher.Logging;

/// <summary>
/// Запись падения программы: строка ERROR <c>session/crash</c> в журнал (синхронно, мимо очереди) и файл
/// <c>crash-ВРЕМЯ.txt</c> с полным многострочным текстом. Повторный вход (падение при записи падения) молча игнорируется.
/// </summary>
public static class CrashReport
{
    public const string FilePrefix = "crash";
    public const string FilePattern = "crash-*.txt";

    private static int _crashing;

    /// <summary>Полное описание: тип, сообщение, стек, внутренние исключения.</summary>
    public static string Describe(Exception? ex) => ex?.ToString() ?? "(нет сведений об исключении)";

    /// <summary>Короткое имя для пользователя: тип исключения без пространства имен.</summary>
    public static string ShortType(Exception? ex) => ex?.GetType().Name ?? "Exception";

    /// <summary>
    /// Пишет падение. <paramref name="kind"/> — откуда пришло (AppDomain, Dispatcher, Task). Возвращает путь файла падения или null.
    /// </summary>
    public static string? Write(LogStore? store, LogMasker masker, string directory, string source, string kind, Exception? ex,
        string version, DateTime sessionStartUtc, bool terminating)
    {
        if (terminating && Interlocked.Exchange(ref _crashing, 1) == 1)
            return null;

        string? path = null;
        try
        {
            var now = DateTime.UtcNow;
            var text = masker.Mask(Describe(ex));
            var level = terminating ? LogLevel.Error : LogLevel.Warn;
            var code = terminating ? LogCodes.Crash : LogCodes.CrashTask;
            var head = terminating ? "Падение (" + kind + "): " : "Потерянное исключение задачи: ";
            store?.AppendNow(new LogLine(now, source, level, LogAreas.Session, code, LogLine.None, head + text));

            if (terminating)
            {
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, LogFiles.TimedName(FilePrefix, now, ".txt"));
                var sb = new StringBuilder();
                sb.AppendLine("Источник: " + source);
                sb.AppendLine("Откуда: " + kind);
                sb.AppendLine("Время UTC: " + LogLine.FormatTime(now));
                sb.AppendLine("Версия: " + version);
                sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : string.Empty));
                sb.AppendLine("Сеанс длился: " + (now - sessionStartUtc).ToString(@"d\.hh\:mm\:ss"));
                sb.AppendLine("Тип: " + (ex?.GetType().FullName ?? "?"));
                sb.AppendLine();
                sb.AppendLine(text);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception)
        {
            // падение при записи падения: ничего не поделать
        }
        return path;
    }

    /// <summary>Для тестов: сбросить флаг повторного входа.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _crashing, 0);
}
