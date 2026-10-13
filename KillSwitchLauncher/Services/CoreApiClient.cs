using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Управление работающим ядром на 127.0.0.1 (Clash API ядра, порт и ключ на каждый запуск сообщает служба).
/// Через него лаунчер проверяет, отвечает ли сервер подключения, не трогая трафик приложений.
/// </summary>
public sealed class CoreApiClient : IDisposable
{
    /// <summary>Маленькая страница, которую ядро открывает через сервер. Только https: http ядро не принимает.</summary>
    public const string ProbeUrl = "https://cp.cloudflare.com/generate_204";

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

    /// <summary>Проверка сервера через подключение: задержка или что именно не так (для журнала).</summary>
    public async Task<CoreProbe> ProbeAsync(string outboundTag, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var ms = (int)Math.Clamp(timeout.TotalMilliseconds, 500, 30000);
        var url = $"proxies/{Uri.EscapeDataString(outboundTag)}/delay?timeout={ms}&url={Uri.EscapeDataString(ProbeUrl)}";
        try
        {
            using var response = await _client.GetAsync(url, cancellation).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return CoreProbe.Failed(response.StatusCode switch
                {
                    HttpStatusCode.GatewayTimeout => $"сервер не ответил за {ms / 1000.0:0.#} с",
                    HttpStatusCode.ServiceUnavailable => "ядро не смогло связаться с сервером через подключение",
                    HttpStatusCode.NotFound => "ядро не знает это подключение",
                    HttpStatusCode.Unauthorized => "ядро не приняло ключ управления",
                    _ => $"ядро ответило {(int)response.StatusCode}"
                } + Detail(body));

            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out var value) && value > 0
                ? new CoreProbe(value, null)
                : CoreProbe.Failed("ядро вернуло пустую задержку");
        }
        catch (HttpRequestException ex)
        {
            return CoreProbe.Failed("нет связи с управлением ядра: " + ex.Message);
        }
        catch (JsonException)
        {
            return CoreProbe.Failed("непонятный ответ ядра");
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return CoreProbe.Failed("управление ядра не ответило");
        }
    }

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
public sealed record CoreProbe(int? Delay, string? Failure)
{
    public static CoreProbe Failed(string reason) => new(null, reason);
}
