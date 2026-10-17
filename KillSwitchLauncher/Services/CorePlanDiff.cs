using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Почему ядро перезапускается: чем новые настройки отличаются от работающих (для технического журнала).
/// По именам подключений и приложений, без ключей; адрес сервера пишется как есть, журнал сам скрывает его по настройке.
/// </summary>
public static class CorePlanDiff
{
    /// <summary>Поля подключения, о которых пишем значение (не секрет и важно для разбора).</summary>
    private static readonly HashSet<string> ValueFields = new(StringComparer.Ordinal)
    {
        "type", "server", "server_port", "network", "flow", "packet_encoding", "version", "mtu"
    };

    public static string Describe(CorePlan? before, CorePlan after)
    {
        if (before is null)
            return "первый запуск ядра";
        if (before.Fingerprint == after.Fingerprint)
            return "настройки не менялись";

        var parts = new List<string>();
        if (before.Request.Ipv6 != after.Request.Ipv6)
            parts.Add($"IPv6: {Yes(before.Request.Ipv6)} → {Yes(after.Request.Ipv6)}");
        if (before.Request.HideDns != after.Request.HideDns)
            parts.Add($"скрывать DNS: {Yes(before.Request.HideDns)} → {Yes(after.Request.HideDns)}");

        var names = ConnectionNames(before, after);
        var oldOut = before.Request.Outbounds.ToDictionary(o => Tag(o), o => o);
        var newOut = after.Request.Outbounds.ToDictionary(o => Tag(o), o => o);
        foreach (var tag in newOut.Keys.Except(oldOut.Keys))
            parts.Add($"добавлено подключение «{Name(names, tag)}» ({Type(newOut[tag])})");
        foreach (var tag in oldOut.Keys.Except(newOut.Keys))
            parts.Add($"убрано подключение «{Name(names, tag)}»");
        foreach (var tag in newOut.Keys.Intersect(oldOut.Keys))
        {
            var changed = ChangedFields(oldOut[tag], newOut[tag]);
            if (changed.Count > 0)
                parts.Add($"изменено подключение «{Name(names, tag)}»: {string.Join(", ", changed)}");
        }

        var oldDns = before.Request.OutboundDns ?? new Dictionary<string, string>();
        var newDns = after.Request.OutboundDns ?? new Dictionary<string, string>();
        foreach (var tag in oldDns.Keys.Union(newDns.Keys).Distinct())
        {
            oldDns.TryGetValue(tag, out var a);
            newDns.TryGetValue(tag, out var b);
            if (a != b)
                parts.Add($"DNS внутри «{Name(names, tag)}»: {a ?? "нет"} → {b ?? "нет"}");
        }

        if (before.Request.DnsOutbound != after.Request.DnsOutbound)
            parts.Add($"редкие DNS-запросы: {Via(names, before.Request.DnsOutbound)} → {Via(names, after.Request.DnsOutbound)}");

        var appNames = AppNames(before, after);
        var oldApps = before.Request.Apps.ToDictionary(a => a.Tag);
        var newApps = after.Request.Apps.ToDictionary(a => a.Tag);

        // Копии всех приложений сменились одинаково (обычно это общая настройка): одной строкой, иначе у каждого приложения.
        var outside = newApps.Keys.Intersect(oldApps.Keys)
            .Select(tag => (Before: Outside(oldApps[tag]), After: Outside(newApps[tag])))
            .Distinct()
            .ToList();
        var together = outside.Count == 1 && outside[0].Before != outside[0].After;
        if (together)
            parts.Add($"копии приложений мимо лаунчера: {outside[0].Before} → {outside[0].After}");

        foreach (var tag in newApps.Keys.Except(oldApps.Keys))
            parts.Add($"добавлено приложение «{Name(appNames, tag)}» через «{Name(names, newApps[tag].Outbound)}»");
        foreach (var tag in oldApps.Keys.Except(newApps.Keys))
            parts.Add($"убрано приложение «{Name(appNames, tag)}»");
        foreach (var tag in newApps.Keys.Intersect(oldApps.Keys))
        {
            var a = oldApps[tag];
            var b = newApps[tag];
            if (a.Outbound != b.Outbound)
                parts.Add($"«{Name(appNames, tag)}» сменило подключение: «{Name(names, a.Outbound)}» → «{Name(names, b.Outbound)}»");
            if (!a.DirectDomains.SequenceEqual(b.DirectDomains, StringComparer.OrdinalIgnoreCase))
                parts.Add($"«{Name(appNames, tag)}»: исключения доменов {a.DirectDomains.Count} → {b.DirectDomains.Count}");
            if (Outside(a) != Outside(b))
            {
                if (!together)
                    parts.Add($"«{Name(appNames, tag)}»: копии мимо лаунчера {Outside(a)} → {Outside(b)}");
            }
            else if (!SamePrograms(a.Programs, b.Programs))
                parts.Add($"«{Name(appNames, tag)}»: программы для копий мимо лаунчера {a.Programs?.Count ?? 0} → {b.Programs?.Count ?? 0}");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "изменился порядок или служебные поля настроек";
    }

    private static List<string> ChangedFields(JsonObject a, JsonObject b)
    {
        var result = new List<string>();
        foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Distinct())
        {
            var x = a[key]?.ToJsonString();
            var y = b[key]?.ToJsonString();
            if (x == y)
                continue;
            if (ValueFields.Contains(key))
                result.Add($"{key} {Short(a[key])} → {Short(b[key])}");
            else if (key is "peers" or "address")
                result.Add(key + " " + Peers(a[key]) + " → " + Peers(b[key]));
            else
                result.Add(key);
        }
        return result;
    }

    private static string Peers(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return "[" + string.Join(", ", array.Select(p => p is JsonObject o
                ? (o["address"]?.ToString() ?? "?") + ":" + (o["port"]?.ToString() ?? "?")
                : p?.ToString() ?? "?")) + "]";
        }
        return Short(node);
    }

    private static string Short(JsonNode? node) => node is null ? "нет" : node is JsonValue v ? v.ToString() : "…";

    /// <summary>Как идут копии приложения, запущенные мимо лаунчера (<see cref="CoreAppRoute.Programs"/>).</summary>
    private static string Outside(CoreAppRoute route) =>
        route.Programs is not { Count: > 0 } ? "напрямую" : route.BlockOutside ? "без сети" : "через VPN";

    internal static bool SamePrograms(List<CoreProgram>? a, List<CoreProgram>? b) =>
        (a ?? new List<CoreProgram>()).Select(Key).SequenceEqual((b ?? new List<CoreProgram>()).Select(Key), StringComparer.OrdinalIgnoreCase);

    private static string Key(CoreProgram program) => program.Folder + "\\" + program.Name + (program.Subfolders ? "\\…" : string.Empty);

    private static string Tag(JsonObject outbound) => outbound["tag"]?.ToString() ?? "?";

    private static string Type(JsonObject outbound) => outbound["type"]?.ToString() ?? "?";

    private static string Yes(bool value) => value ? "да" : "нет";

    private static string Name(IReadOnlyDictionary<string, string> names, string tag) =>
        names.TryGetValue(tag, out var name) ? name : tag;

    private static string Via(IReadOnlyDictionary<string, string> names, string? tag) =>
        tag is null ? "DNS компьютера" : $"через «{Name(names, tag)}»";

    private static Dictionary<string, string> ConnectionNames(CorePlan a, CorePlan b)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var plan in new[] { a, b })
        {
            foreach (var app in plan.Apps.Values)
            {
                if (plan.ConnectionTags.TryGetValue(app.ConnectionId, out var tag))
                    result[tag] = app.ConnectionName;
            }
            if (plan.Request.DnsOutbound is { } dnsTag && plan.DnsConnectionName is { } dnsName)
                result.TryAdd(dnsTag, dnsName);
        }
        return result;
    }

    private static Dictionary<string, string> AppNames(CorePlan a, CorePlan b)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var app in a.Apps.Values.Concat(b.Apps.Values))
            result[app.Tag] = app.Name;
        return result;
    }
}
