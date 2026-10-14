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

        var appNames = AppNames(before, after);
        var oldApps = before.Request.Apps.ToDictionary(a => a.Tag);
        var newApps = after.Request.Apps.ToDictionary(a => a.Tag);
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

    private static string Tag(JsonObject outbound) => outbound["tag"]?.ToString() ?? "?";

    private static string Type(JsonObject outbound) => outbound["type"]?.ToString() ?? "?";

    private static string Yes(bool value) => value ? "да" : "нет";

    private static string Name(IReadOnlyDictionary<string, string> names, string tag) =>
        names.TryGetValue(tag, out var name) ? name : tag;

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
