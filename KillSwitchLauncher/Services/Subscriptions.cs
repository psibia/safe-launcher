using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>Что пришло по подписке: ссылки на серверы, которые удалось разобрать, и сколько строк пропущено.</summary>
public sealed record SubscriptionContent(IReadOnlyList<string> Links, int Skipped);

/// <summary>
/// Подписка: адрес, по которому провайдер отдаёт список серверов. Поддерживается самый распространённый формат —
/// ссылки vless://, vmess:// и т. д. по одной в строке, как есть или в base64 (так подписки отдают v2rayN и Hiddify).
/// </summary>
public static class Subscriptions
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Похож ли текст на адрес подписки.</summary>
    public static bool LooksLikeUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Скачивает подписку и разбирает её. Ошибки — <see cref="ConnectionParseException"/> с понятным текстом.</summary>
    public static async Task<SubscriptionContent> FetchAsync(string url, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        if (!LooksLikeUrl(url))
            throw new ConnectionParseException("Адрес подписки должен начинаться с https://.");

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SafeLauncher/2.1");
        string body;
        try
        {
            using var response = await client.GetAsync(url.Trim(), HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ConnectionParseException($"Сервер подписки ответил ошибкой {(int)response.StatusCode}. Проверьте адрес или срок подписки.");
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new ConnectionParseException("Подписка слишком большая.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    throw new ConnectionParseException("Подписка слишком большая.");
                buffer.Write(chunk, 0, read);
            }

            body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectionParseException("Не удалось скачать подписку: " + ex.Message);
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ConnectionParseException("Сервер подписки не ответил за 30 секунд.");
        }

        return Parse(body);
    }

    /// <summary>Разбирает текст подписки.</summary>
    public static SubscriptionContent Parse(string body)
    {
        var text = body.Trim().TrimStart('﻿');
        if (!text.Contains("://", StringComparison.Ordinal) && ConnectionLinks.TryBase64(text) is { } decoded)
            text = decoded;

        var links = new List<string>();
        var skipped = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (!line.Contains("://", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            try
            {
                ConnectionLinks.Parse(line);
                links.Add(line);
            }
            catch (ConnectionParseException)
            {
                skipped++;
            }
        }

        if (links.Count > 0)
            return new SubscriptionContent(links, skipped);

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('<'))
            throw new ConnectionParseException("По этому адресу открывается веб-страница, а не подписка. Скопируйте в личном кабинете ссылку именно на подписку.");
        if (trimmed.StartsWith('{'))
            throw new ConnectionParseException("Подписка в формате sing-box пока не поддерживается. Возьмите в личном кабинете ссылку для v2rayN.");
        if (trimmed.Contains("proxies:", StringComparison.Ordinal))
            throw new ConnectionParseException("Подписка в формате Clash пока не поддерживается. Возьмите в личном кабинете ссылку для v2rayN.");
        throw new ConnectionParseException(skipped > 0
            ? "В подписке нет серверов, которые поддерживает ядро лаунчера."
            : "В подписке не нашлось серверов.");
    }

    /// <summary>Сервер без названия после «#»: по нему запоминается выбор, даже если провайдер переименовал сервер.</summary>
    public static string ServerKey(string link)
    {
        var hash = link.IndexOf('#');
        return (hash >= 0 ? link[..hash] : link).Trim();
    }

    /// <summary>Выбранный сервер подписки: сохранённый, если он ещё есть в списке, иначе первый.</summary>
    public static string? Pick(IReadOnlyList<string> servers, string? selected)
    {
        if (servers.Count == 0)
            return null;
        if (selected is not null)
        {
            var match = servers.FirstOrDefault(s => ServerKey(s) == ServerKey(selected));
            if (match is not null)
                return match;
        }

        return servers[0];
    }
}
