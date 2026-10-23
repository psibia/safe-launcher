using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KillSwitchLauncher.Models;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Настройки без секретов: для записи «что изменилось» в технический журнал и для settings.json в выгрузке.
/// Ссылки, подписки и файлы WireGuard не попадают сюда никогда: вместо них короткий отпечаток (видно, что поменялось).
/// </summary>
public static class SettingsSnapshot
{
    /// <summary>Плоский снимок «путь → значение» для сравнения.</summary>
    public static Dictionary<string, string> Flatten(AppSettings s)
    {
        var connections = s.Connections.ToDictionary(c => c.Id, c => c.Name);
        string Conn(Guid? id) => id is { } g ? connections.TryGetValue(g, out var n) ? "«" + n + "»" : "нет такого" : "не выбрано";

        var d = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Kill-Switch"] = Yes(s.KillSwitchEnabled),
            ["Копии приложений мимо лаунчера"] = s.OutsideCopies.ToString(),
            ["Защита от утечки DNS"] = Yes(s.HideDns),
            ["Общие исключения доменов"] = s.DirectDomains.Count.ToString(),
            ["Как понимать, что VPN есть"] = s.NetworkMode.ToString(),
            ["Основное подключение"] = Conn(s.ConnectionId),
            ["Правило адаптеров"] = s.RuleMode.ToString(),
            ["Выбранные адаптеры"] = string.Join(", ", s.SelectedAdapters),
            ["Автозапуск"] = Yes(s.StartWithWindows),
            ["Кнопка «Закрыть»"] = s.HideBehavior.ToString()
            // настройки журналов пишет сам журнал при их смене (TechLog.Configure)
        };

        foreach (var app in s.Apps)
        {
            var p = "Приложение «" + app.Name + "» ";
            var n = app.Network;
            d[p + "программа"] = app.ExePath;
            d[p + "сеть"] = n.Mode switch
            {
                AppNetworkMode.OwnAdapters => "свои адаптеры (" + n.RuleMode + ": " + string.Join(", ", n.SelectedAdapters) + ")",
                AppNetworkMode.OwnConnection => "свое подключение " + Conn(n.ConnectionId),
                AppNetworkMode.Offline => "без сети",
                _ => "основное подключение"
            };
            d[p + "закрывать при обрыве"] = n.CloseOnDrop.ToString();
            d[p + "копии мимо лаунчера"] = n.OutsideCopies.ToString();
            d[p + "исключения доменов"] = n.DirectDomains.Count.ToString();
            d[p + "исключения"] = n.DomainsMode == DomainsMode.OwnOnly ? "вместо общих" : "дополняют общие";
            d[p + "журнал DNS"] = Yes(app.TrackDns);
        }

        foreach (var c in s.Connections)
        {
            var p = "Подключение «" + c.Name + "» ";
            d[p + "вид"] = c.Kind.ToString();
            switch (c.Kind)
            {
                case ConnectionKind.Link:
                    d[p + "ссылка"] = Print(c.Link);
                    break;
                case ConnectionKind.Subscription:
                    d[p + "адрес подписки"] = Print(c.SubscriptionUrl);
                    d[p + "серверов"] = c.SubscriptionServers.Count.ToString();
                    d[p + "выбранный сервер"] = c.SelectedServer is null ? "первый" : Print(c.SelectedServer) + " (№" + (IndexOf(c) + 1) + ")";
                    break;
                case ConnectionKind.WireGuard:
                    d[p + "файл WireGuard"] = Print(c.WireGuardConfig);
                    break;
                case ConnectionKind.LocalProxy:
                    d[p + "прокси"] = c.ProxyProtocol + " " + c.ProxyHost + ":" + c.ProxyPort;
                    break;
            }
        }

        return d;
    }

    /// <summary>Что поменялось: «поле: было → стало» (новые и убранные поля — «добавлено/убрано»).</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var result = new List<string>();
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var old))
                result.Add($"{key}: добавлено ({value})");
            else if (old != value)
                result.Add($"{key}: {old} → {value}");
        }
        foreach (var key in before.Keys.Where(k => !after.ContainsKey(k)))
            result.Add($"{key}: убрано");
        return result;
    }

    /// <summary>settings.json для выгрузки: структура настроек без ссылок, ключей и адресов подписок.</summary>
    public static JsonObject ToJson(AppSettings s, Func<string, string> mask)
    {
        var apps = new JsonArray();
        foreach (var app in s.Apps)
        {
            var plan = NetworkPlan.For(app, s);
            apps.Add(new JsonObject
            {
                ["name"] = app.Name,
                ["tag"] = CorePlan.AppTag(app.Id),
                ["exe"] = mask(app.ExePath),
                ["packaged"] = app.IsPackaged,
                ["network"] = app.Network.Mode.ToString(),
                ["plan"] = plan.Kind.ToString(),
                ["planSource"] = plan.Source.ToString(),
                ["connection"] = plan.Connection is { } c ? CorePlan.ConnectionTag(c.Id) : null,
                ["closeOnDrop"] = plan.CloseOnDrop,
                ["outsideCopies"] = app.Network.OutsideCopies.ToString(),
                ["trackDns"] = app.TrackDns,
                ["directDomains"] = new JsonArray(app.Network.DirectDomains.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["domainsMode"] = app.Network.DomainsMode.ToString(),
                ["planDirectDomains"] = plan.DirectDomains.Count,
                ["adapters"] = new JsonArray(app.Network.SelectedAdapters.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray())
            });
        }

        var connections = new JsonArray();
        foreach (var c in s.Connections)
        {
            var o = new JsonObject
            {
                ["name"] = c.Name,
                ["tag"] = CorePlan.ConnectionTag(c.Id),
                ["kind"] = c.Kind.ToString()
            };
            try
            {
                var server = ConnectionOutbound.Resolve(c);
                var outbound = server.Outbound;
                o["type"] = (string?)outbound["type"];
                o["server"] = mask(server.Server);
                o["port"] = server.Port;
                o["transport"] = outbound["transport"]?["type"]?.ToString();
                o["tls"] = outbound["tls"] is JsonObject tls ? (tls["reality"] is not null ? "reality" : "tls") : "нет";
                o["dnsInside"] = server.Dns is not null;
            }
            catch (Exception ex)
            {
                o["problem"] = mask(ex.Message);
            }
            if (c.Kind == ConnectionKind.Subscription)
            {
                o["servers"] = c.SubscriptionServers.Count;
                o["selectedIndex"] = IndexOf(c);
                o["updatedUtc"] = c.SubscriptionUpdatedAt?.UtcDateTime.ToString("O");
                o["updateHours"] = c.SubscriptionUpdateHours;
            }
            connections.Add(o);
        }

        return new JsonObject
        {
            ["killSwitch"] = s.KillSwitchEnabled,
            ["outsideCopies"] = s.OutsideCopies.ToString(),
            ["hideDns"] = s.HideDns,
            ["directDomains"] = new JsonArray(s.DirectDomains.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["networkMode"] = s.NetworkMode.ToString(),
            ["connection"] = s.ConnectionId is { } id ? CorePlan.ConnectionTag(id) : null,
            ["ruleMode"] = s.RuleMode.ToString(),
            ["selectedAdapters"] = new JsonArray(s.SelectedAdapters.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["startWithWindows"] = s.StartWithWindows,
            ["logs"] = new JsonObject
            {
                ["storage"] = s.Logs.Storage.ToString(),
                ["retentionDays"] = s.Logs.RetentionDays,
                ["hideAddresses"] = s.Logs.HideAddresses,
                ["detailedUntilUtc"] = s.Logs.DetailedUntilUtc?.ToString("O")
            },
            ["apps"] = apps,
            ["connections"] = connections
        };
    }

    /// <summary>Метки для summary.json: тег → имя и вид (приложение или подключение).</summary>
    public static JsonObject Tags(AppSettings s)
    {
        var tags = new JsonObject();
        foreach (var c in s.Connections)
            tags[CorePlan.ConnectionTag(c.Id)] = new JsonObject { ["kind"] = "connection", ["name"] = c.Name, ["type"] = c.Kind.ToString() };
        foreach (var app in s.Apps)
        {
            var plan = NetworkPlan.For(app, s);
            tags[CorePlan.AppTag(app.Id)] = new JsonObject
            {
                ["kind"] = "app",
                ["name"] = app.Name,
                ["connection"] = plan.Connection is { } c ? CorePlan.ConnectionTag(c.Id) : null
            };
        }
        return tags;
    }

    /// <summary>Серверы подключений для маскировщика: адрес или имя → тег подключения.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Servers(AppSettings s)
    {
        foreach (var c in s.Connections)
        {
            var tag = CorePlan.ConnectionTag(c.Id);
            var links = c.Kind switch
            {
                ConnectionKind.Link => c.Link is { } l ? new[] { l } : Array.Empty<string>(),
                ConnectionKind.Subscription => c.SubscriptionServers.ToArray(),
                _ => Array.Empty<string>()
            };
            foreach (var link in links)
            {
                string? host = null;
                try
                {
                    host = ConnectionLinks.Parse(link).Server;
                }
                catch (Exception)
                {
                    // битая ссылка
                }
                if (host is not null)
                    yield return new(host, tag);
            }

            if (c.Kind == ConnectionKind.WireGuard && c.WireGuardConfig is { } conf)
            {
                string? host = null;
                try
                {
                    host = WireGuardConf.Parse(conf, c.Name).Server;
                }
                catch (Exception)
                {
                    // битый файл
                }
                if (host is not null)
                    yield return new(host, tag);
            }
        }
    }

    /// <summary>Строки, которых не должно быть в журналах: адреса подписок.</summary>
    public static IEnumerable<string> Secrets(AppSettings s) =>
        s.Connections.Select(c => c.SubscriptionUrl).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!);

    private static int IndexOf(VpnConnection c)
    {
        if (c.SelectedServer is null)
            return 0;
        var picked = Subscriptions.Pick(c.SubscriptionServers, c.SelectedServer);
        return picked is null ? -1 : c.SubscriptionServers.IndexOf(picked);
    }

    private static string Yes(bool value) => value ? "да" : "нет";

    /// <summary>Отпечаток секрета: видно, что он изменился, но не сам секрет.</summary>
    private static string Print(string? secret) =>
        string.IsNullOrEmpty(secret) ? "нет" : "#" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), 0, 3).ToLowerInvariant();
}
