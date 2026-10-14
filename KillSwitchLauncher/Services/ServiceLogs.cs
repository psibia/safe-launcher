using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>Состояние журналов службы и ядра: занятое место, потери, пишется ли подробный журнал.</summary>
public sealed record ServiceLogState(long ServiceBytes, long CoreBytes, long Dropped, bool Detailed);

/// <summary>
/// Журналы службы и ядра через канал службы (команды журналов, Revision 5). Каждая операция — отдельное короткое подключение:
/// основное подключение лаунчера держит ядро, и долгое чтение журнала не должно задерживать его команды.
/// Служба не установлена или старая — <see cref="CoreServiceException"/> с понятным текстом.
/// </summary>
public static class ServiceLogs
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public const string OldServiceMessage =
        "Служба «Безопасный лаунчер — сеть» старой версии и не ведет журналов. Переустановите лаунчер, чтобы обновить ее.";

    /// <summary>Последнее известное состояние (null — служба недоступна или старая).</summary>
    public static ServiceLogState? LastState { get; private set; }

    /// <summary>Почему журналы службы недоступны (для подписей в окнах), иначе null.</summary>
    public static string? Unavailable { get; private set; }

    /// <summary>Передать службе настройки журналов и получить ключ меток адресов. Ошибки не бросает: служба может быть не установлена.</summary>
    public static async Task<ServiceLogState?> ConfigureAsync(LogSettings settings, CancellationToken cancellation = default)
    {
        try
        {
            var reply = await RunAsync(ControlOps.LogConfig, new ServiceLogRequest
            {
                Storage = settings.Storage == LogStorage.Memory ? "memory" : "files",
                RetentionDays = settings.RetentionDays,
                HideAddresses = settings.HideAddresses,
                DetailedUntilUtc = settings.DetailedUntilUtc
            }, cancellation).ConfigureAwait(false);

            if (reply.MaskKey is { } key)
            {
                try
                {
                    TechLog.UseSharedKey(Convert.FromBase64String(key));
                }
                catch (FormatException)
                {
                    // ключ испорчен: остается свой
                }
            }

            LastState = new ServiceLogState(reply.ServiceBytes ?? 0, reply.CoreBytes ?? 0, reply.Dropped ?? 0, reply.Detailed == true);
            Unavailable = null;
            return LastState;
        }
        catch (CoreServiceException ex)
        {
            Unavailable = Explain(ex);
            LastState = null;
            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            Unavailable = "Служба «Безопасный лаунчер — сеть» не ответила.";
            return null;
        }
    }

    /// <summary>Строки службы и ядра за период (порциями), по времени.</summary>
    /// <param name="sources">service и/или core; null — оба.</param>
    public static async Task<IReadOnlyList<LogLine>> ReadAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string>? sources = null,
        IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        using var client = await ConnectAsync(cancellation).ConfigureAwait(false);
        var result = new List<LogLine>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var reply = await client.LogsAsync(ControlOps.LogRead, new ServiceLogRequest
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Sources = sources?.ToList(),
                Cursor = cursor,
                MaxBytes = 512 * 1024
            }, cancellation).ConfigureAwait(false);

            foreach (var raw in reply.Lines ?? new List<string>())
            {
                if (LogLine.TryParse(raw, out var line))
                    result.Add(line);
            }

            progress?.Report(result.Count);
            cursor = reply.Cursor;
            // защита от зацикливания при ошибке курсора: 2000 порций по 512 КБ — больше любого лимита журналов
            if (++pages > 2000)
                break;
        }
        while (cursor is not null);

        LogStore.Sort(result);
        return result;
    }

    /// <summary>Отметка пользователя в журналах службы и ядра.</summary>
    public static async Task MarkAsync(string cid, string? text, CancellationToken cancellation = default)
    {
        try
        {
            await RunAsync(ControlOps.LogMark, new ServiceLogRequest { Text = text }, cancellation, cid).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CoreServiceException or OperationCanceledException or TimeoutException)
        {
            // без службы отметка есть в журнале лаунчера
        }
    }

    /// <summary>Удалить журналы службы и ядра. Возвращает освобожденные байты; служба недоступна — исключение.</summary>
    public static async Task<long> ClearAsync(CancellationToken cancellation = default)
    {
        var reply = await RunAsync(ControlOps.LogClear, null, cancellation).ConfigureAwait(false);
        // Ключ меток сменился: забираем новый.
        await ConfigureAsync(TechLog.Settings, cancellation).ConfigureAwait(false);
        return reply.Freed ?? 0;
    }

    /// <summary>Очищенные конфиги ядра и описания падений службы за период.</summary>
    public static async Task<IReadOnlyList<ServiceLogFile>> FilesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellation = default)
    {
        var reply = await RunAsync(ControlOps.LogFiles, new ServiceLogRequest { FromUtc = fromUtc, ToUtc = toUtc }, cancellation).ConfigureAwait(false);
        return reply.Files ?? new List<ServiceLogFile>();
    }

    private static async Task<ServiceLogReply> RunAsync(string op, ServiceLogRequest? request, CancellationToken cancellation, string? cid = null)
    {
        using var client = await ConnectAsync(cancellation).ConfigureAwait(false);
        return await client.LogsAsync(op, request, cancellation, cid).ConfigureAwait(false);
    }

    private static async Task<CoreServiceClient> ConnectAsync(CancellationToken cancellation)
    {
        try
        {
            return await CoreServiceClient.ConnectAsync(ConnectTimeout, cancellation).ConfigureAwait(false);
        }
        catch (CoreServiceException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CoreServiceException("Не удалось связаться со службой «Безопасный лаунчер — сеть»: " + ex.Message);
        }
    }

    /// <summary>Понятный текст ошибки службы: «неизвестная команда» значит старая служба.</summary>
    public static string Explain(Exception ex) =>
        ex.Message.Contains("Неизвестная команда", StringComparison.Ordinal) ? OldServiceMessage : ex.Message;

    /// <summary>Имена команд журналов (как в SafeLauncher.Service/Protocol.cs).</summary>
    public static class ControlOps
    {
        public const string LogConfig = "logConfig";
        public const string LogRead = "logRead";
        public const string LogMark = "logMark";
        public const string LogClear = "logClear";
        public const string LogFiles = "logFiles";
    }
}
