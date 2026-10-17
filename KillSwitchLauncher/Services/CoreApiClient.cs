using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Управление работающим ядром на 127.0.0.1 (Clash API ядра, порт и ключ на каждый запуск сообщает служба).
/// Через него лаунчер проверяет, отвечает ли сервер подключения, не трогая трафик приложений, и видит соединения через ядро.
/// </summary>
public sealed class CoreApiClient : IDisposable
{
    /// <summary>
    /// Маленькие страницы, которые ядро открывает через сервер: вторая — другой компании, на случай если первая недоступна
    /// из страны сервера. Только https: вместо http ядро открывает свою страницу (она и есть вторая).
    /// </summary>
    public const string ProbeUrl = "https://cp.cloudflare.com/generate_204";

    public const string BackupProbeUrl = "https://www.gstatic.com/generate_204";

    private readonly HttpClient _client;

    public CoreApiClient(int port, string secret, HttpMessageHandler? handler = null)
    {
        _client = handler is null
            ? new HttpClient(new HttpClientHandler { UseProxy = false })
            : new HttpClient(handler, disposeHandler: false);
        _client.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        _client.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Задержка через подключение, мс; null — сервер не ответил (или ядро недоступно).</summary>
    public async Task<int?> DelayAsync(string outboundTag, TimeSpan timeout, CancellationToken cancellation = default) =>
        (await ProbeAsync(outboundTag, timeout, cancellation).ConfigureAwait(false)).Delay;

    /// <summary>
    /// Проверка сервера через подключение: задержка или что именно не так (для журнала). Если сервер не открыл первую
    /// страницу, сразу пробуется вторая: промах, только если не открылась ни одна.
    /// </summary>
    public async Task<CoreProbe> ProbeAsync(string outboundTag, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var (first, server) = await ProbePageAsync(outboundTag, ProbeUrl, timeout, cancellation).ConfigureAwait(false);
        if (first.Delay is not null || !server)
            return first;

        var (second, _) = await ProbePageAsync(outboundTag, BackupProbeUrl, timeout, cancellation).ConfigureAwait(false);
        return second.Delay is not null
            ? second with { Note = "первая страница не открылась: " + first.Failure }
            : CoreProbe.Failed(first.Failure + "; вторая страница: " + second.Failure);
    }

    /// <returns>Итог и Server: не открылась сама страница (тогда стоит попробовать другую), а не отказало ядро или его управление.</returns>
    private async Task<(CoreProbe Probe, bool Server)> ProbePageAsync(string outboundTag, string page, TimeSpan timeout, CancellationToken cancellation)
    {
        var ms = (int)Math.Clamp(timeout.TotalMilliseconds, 500, 30000);
        var url = $"proxies/{Uri.EscapeDataString(outboundTag)}/delay?timeout={ms}&url={Uri.EscapeDataString(page)}";
        try
        {
            using var response = await _client.GetAsync(url, cancellation).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return (CoreProbe.Failed(response.StatusCode switch
                {
                    HttpStatusCode.GatewayTimeout => $"сервер не ответил за {ms / 1000.0:0.#} с",
                    HttpStatusCode.ServiceUnavailable => "ядро не смогло связаться с сервером через подключение",
                    HttpStatusCode.NotFound => "ядро не знает это подключение",
                    HttpStatusCode.Unauthorized => "ядро не приняло ключ управления",
                    _ => $"ядро ответило {(int)response.StatusCode}"
                } + Detail(body)), response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Unauthorized));

            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out var value) && value > 0
                ? (new CoreProbe(value, null), true)
                : (CoreProbe.Failed("ядро вернуло пустую задержку"), true);
        }
        catch (HttpRequestException ex)
        {
            return (CoreProbe.Failed("нет связи с управлением ядра: " + ex.Message), false);
        }
        catch (JsonException)
        {
            return (CoreProbe.Failed("непонятный ответ ядра"), false);
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return (CoreProbe.Failed("управление ядра не ответило"), false);
        }
    }

    /// <summary>Соединения, которые сейчас идут через ядро, или почему их узнать не удалось (не дольше <paramref name="timeout"/>).</summary>
    public async Task<CoreConnectionList> ConnectionsAsync(TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await _client.GetAsync("connections", limit.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return CoreConnectionList.Failed(response.StatusCode == HttpStatusCode.Unauthorized
                    ? "ядро не приняло ключ управления"
                    : $"ядро ответило {(int)response.StatusCode}" + Detail(body));

            using var json = JsonDocument.Parse(body);
            var list = new List<CoreConnection>();
            // Без соединений ядро присылает "connections": null.
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("connections", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("metadata", out var metadata) ||
                        metadata.ValueKind != JsonValueKind.Object)
                        continue;
                    list.Add(new CoreConnection(
                        Text(metadata, "network"),
                        int.TryParse(Text(metadata, "destinationPort"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0,
                        Text(metadata, "processPath")));
                }
            }

            return new CoreConnectionList(list, null);
        }
        catch (HttpRequestException ex)
        {
            return CoreConnectionList.Failed("нет связи с управлением ядра: " + ex.Message);
        }
        catch (JsonException)
        {
            return CoreConnectionList.Failed("непонятный ответ ядра");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return CoreConnectionList.Failed($"управление ядра не ответило за {timeout.TotalSeconds:0.#} с");
        }
    }

    private static string Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    /// <summary>Сообщение ядра из ответа {"message": "..."}, если есть.</summary>
    private static string Detail(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("message", out var message) &&
                   message.ValueKind == JsonValueKind.String && message.GetString() is { Length: > 0 } text
                ? $" («{text}»)"
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Итог проверки сервера через ядро: задержка, мс, или причина неудачи.</summary>
/// <param name="Note">Сервер ответил, но не сразу: например, первая страница не открылась (для журнала).</param>
public sealed record CoreProbe(int? Delay, string? Failure, string? Note = null)
{
    public static CoreProbe Failed(string reason) => new(null, reason);
}

/// <summary>
/// Соединение через ядро: tcp или udp, порт сервера и путь программы (у приложений лаунчера «launcher:метка|путь»,
/// пусто — ядро программу не узнало).
/// </summary>
public sealed record CoreConnection(string Network, int Port, string Process);

/// <summary>Соединения через ядро или причина, по которой их узнать не удалось.</summary>
public sealed record CoreConnectionList(IReadOnlyList<CoreConnection>? Items, string? Failure)
{
    public static CoreConnectionList Failed(string reason) => new(null, reason);
}
