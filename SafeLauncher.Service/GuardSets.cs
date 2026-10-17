using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SafeLauncher.Service;

/// <summary>
/// Наборы фильтров <see cref="NetworkGuard"/>: у подключённых лаунчеров и оставшиеся после отключения лаунчера.
///
/// Лаунчер отключился (закрылся или упал): правила его приложений снимаются сразу. Эти приложения Windows закрывает
/// вместе с лаунчером (их задания так помечены), а правило осталось бы на копиях тех же программ, открытых не через
/// лаунчер. Остаётся только скрытие DNS (<see cref="GuardRequest.DnsInterfaces"/>, <see cref="GuardRequest.DnsPaths"/>):
/// приложения под системным VPN со «Скрывать DNS-запросы», но без блокировки трафика и Kill-Switch продолжают работать
/// и без лаунчера. Оно действует, пока работает хоть одна их программа и есть адаптер VPN со своим DNS, или пока лаунчер
/// того же пользователя не пришлёт новый набор.
///
/// Блокировок нет: их держит <see cref="NetworkGuard"/>.
/// </summary>
internal sealed class GuardSets
{
    private Dictionary<object, Entry> _owners = new();
    private List<Entry> _orphans = new();

    /// <summary>Набор: пользователь Windows лаунчера (SID) и что в нём.</summary>
    internal sealed record Entry(string? User, GuardRequest Request);

    /// <summary>Все наборы, которые сейчас должны действовать.</summary>
    public IEnumerable<GuardRequest> All => _owners.Values.Concat(_orphans).Select(e => e.Request);

    public int Owners => _owners.Count;

    /// <summary>Сколько наборов осталось от отключившихся лаунчеров.</summary>
    public int Orphans => _orphans.Count;

    public bool IsEmpty => _owners.Count == 0 && _orphans.Count == 0;

    /// <summary>Снимок для отката, если фильтры Windows не приняли изменение.</summary>
    public (Dictionary<object, Entry>, List<Entry>) Snapshot() => (new(_owners), new(_orphans));

    public void Restore((Dictionary<object, Entry> Owners, List<Entry> Orphans) snapshot)
    {
        _owners = snapshot.Owners;
        _orphans = snapshot.Orphans;
    }

    /// <summary>Новый набор лаунчера (null — пустой). Наборы, оставшиеся от прежних лаунчеров того же пользователя, он заменяет.</summary>
    public void Set(object owner, string? user, GuardRequest? request)
    {
        _orphans.RemoveAll(o => o.User == user);
        if (request is null)
            _owners.Remove(owner);
        else
            _owners[owner] = new Entry(user, request);
    }

    /// <summary>Лаунчер отключился: от его набора остаётся только скрытие DNS. true — наборы изменились.</summary>
    public bool Release(object owner)
    {
        if (!_owners.Remove(owner, out var entry))
            return false;
        var request = entry.Request;
        var kept = Reduce(new List<GuardRule>(), request.DnsInterfaces, request.DnsPaths);
        if (kept is not null)
            _orphans.Add(entry with { Request = kept });
        return true;
    }

    /// <summary>
    /// Убрать из оставшихся наборов правила, ни одна программа которых не работает, и адаптеры DNS, которых больше нет
    /// (VPN отключился). Без адаптера VPN DNS не закрывается: иначе имена пропали бы на всём компьютере, и лаунчера, который
    /// снял бы это ограничение, уже нет. Набор без правил снимается целиком. true — что-то убрано.
    /// </summary>
    /// <param name="dnsAdapter">Работает ли адаптер (IfIndex) со своим DNS-сервером.</param>
    public bool Prune(Func<string, bool> running, Func<int, bool> dnsAdapter)
    {
        var changed = false;
        for (var i = _orphans.Count - 1; i >= 0; i--)
        {
            var request = _orphans[i].Request;
            var apps = request.Apps.Where(r => r.Paths.Any(running)).ToList();
            var dnsInterfaces = request.DnsInterfaces?.Where(dnsAdapter).ToList();
            var dnsPaths = dnsInterfaces is { Count: > 0 } ? request.DnsPaths?.Where(running).ToList() : null;
            if (apps.Count == request.Apps.Count && (dnsInterfaces?.Count ?? 0) == (request.DnsInterfaces?.Count ?? 0) &&
                (dnsPaths?.Count ?? 0) == (request.DnsPaths?.Count ?? 0))
                continue;
            changed = true;
            if (Reduce(apps, dnsInterfaces, dnsPaths) is { } kept)
                _orphans[i] = _orphans[i] with { Request = kept };
            else
                _orphans.RemoveAt(i);
        }

        return changed;
    }

    public void Clear()
    {
        _owners.Clear();
        _orphans.Clear();
    }

    /// <summary>Набор с этими правилами и скрытием DNS; null — в нём не осталось ни правил, ни программ DNS.</summary>
    private static GuardRequest? Reduce(List<GuardRule> apps, List<int>? dnsInterfaces, List<string>? dnsPaths)
    {
        var dns = dnsInterfaces is { Count: > 0 };
        return apps.Count == 0 && !(dns && dnsPaths is { Count: > 0 })
            ? null
            : new GuardRequest
            {
                Apps = apps,
                DnsInterfaces = dns ? dnsInterfaces : null,
                DnsPaths = dns && dnsPaths is { Count: > 0 } ? dnsPaths : null
            };
    }

    // ---- файл состояния: на случай падения службы ----

    /// <summary>Все наборы для файла состояния.</summary>
    public string Serialize() => JsonSerializer.Serialize(new State
    {
        Sets = _owners.Values.Select(e => new StateSet { User = e.User, Guard = e.Request })
            .Concat(_orphans.Select(e => new StateSet { User = e.User, Orphan = true, Guard = e.Request }))
            .ToList()
    }, ControlProtocol.JsonOptions);

    /// <summary>
    /// Наборы из файла состояния упавшей службы: все становятся оставшимися, целиком. Лаунчеры, которые их прислали,
    /// скорее всего живы: они переподключатся и пришлют свои наборы, а до тех пор их приложения остаются под фильтрами.
    /// Непонятный файл — пусто.
    /// </summary>
    public static List<Entry> Deserialize(string json)
    {
        var result = new List<Entry>();
        try
        {
            var state = JsonSerializer.Deserialize<State>(json, ControlProtocol.JsonOptions);
            foreach (var set in state?.Sets ?? new List<StateSet>())
            {
                if (set?.Guard is not { } guard)
                    continue;
                var apps = (guard.Apps ?? new List<GuardRule>())
                    .Where(r => r?.Paths is { Count: > 0 })
                    .Select(r => new GuardRule { Paths = r.Paths, ViaCore = r.ViaCore, Interfaces = r.Interfaces ?? new List<int>(), LoopbackPorts = r.LoopbackPorts })
                    .ToList();
                if (Reduce(apps, guard.DnsInterfaces, guard.DnsPaths) is { } kept)
                    result.Add(new Entry(set.User, kept));
            }
        }
        catch (JsonException)
        {
            // файл испорчен: восстанавливать нечего
        }

        return result;
    }

    public void AddOrphans(IEnumerable<Entry> entries) => _orphans.AddRange(entries);

    private sealed class State
    {
        public List<StateSet> Sets { get; set; } = new();
    }

    private sealed class StateSet
    {
        public string? User { get; set; }

        public bool Orphan { get; set; }

        public GuardRequest? Guard { get; set; }
    }
}
