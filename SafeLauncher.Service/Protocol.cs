using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SafeLauncher.Service;

/// <summary>
/// Обмен лаунчера со службой: именованный канал <see cref="ControlProtocol.PipeName"/>, по строке JSON на сообщение.
/// Лаунчер шлёт <see cref="ControlMessage"/>, служба отвечает <see cref="ControlReply"/> с тем же Id
/// и по своей инициативе присылает события (<see cref="ControlReply.Event"/>, без Id).
/// Лаунчер описывает те же сообщения у себя (KillSwitchLauncher/Services/CoreServiceClient.cs); что они совпадают,
/// проверяют тесты (CoreServiceTests).
/// </summary>
public static class ControlProtocol
{
    public const string ServiceName = "SafeLauncherNet";
    public const string DisplayName = "Безопасный лаунчер — сеть";
    public const string PipeName = "SafeLauncher.Service";

    /// <summary>
    /// Что умеет служба. Растёт, когда лаунчеру нужно новое; лаунчер сверяет его со своим минимумом и просит обновить
    /// службу, если она старше (номер версии службы для этого не годится: он меняется вместе с лаунчером).
    /// 1 — запуск ядра (служба без этого поля); 2 — управление ядром для проверки серверов (ApiPort/ApiSecret);
    /// 3 — перехват DNS и подменные адреса (HideDns, OutboundDns), сброс кэша DNS, блокировка обхода VPN (guard);
    /// 4 — непроверенные программы в ответе (Unguarded), DNS-правило для службы DNS Windows и приложений (DnsPaths);
    /// 5 — журналы службы и ядра (logConfig, logRead, logMark, logClear, logFiles), корреляция команд (Cid).
    /// </summary>
    public const int Revision = 5;

    /// <summary>С какой ревизии есть команды журналов.</summary>
    public const int LogsRevision = 5;

    /// <summary>Самое длинное сообщение: настройки ядра со всеми подключениями и исключениями.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    public const string OpStart = "start";
    public const string OpStop = "stop";
    public const string OpStatus = "status";

    /// <summary>Какие программы пускать в сеть только через VPN (<see cref="ControlMessage.Guard"/>). Заменяет прежний список этого лаунчера.</summary>
    public const string OpGuard = "guard";

    /// <summary>Настройки журналов (<see cref="LogRequest"/>); ответ — ключ меток адресов и занятое место.</summary>
    public const string OpLogConfig = "logConfig";

    /// <summary>Строки журналов службы и ядра за период, порциями (<see cref="LogRequest.Cursor"/>).</summary>
    public const string OpLogRead = "logRead";

    /// <summary>Отметка пользователя «здесь проблема» в журналах службы и ядра.</summary>
    public const string OpLogMark = "logMark";

    /// <summary>Удалить журналы службы и ядра (кнопка «Удалить все журналы»); ключ меток меняется.</summary>
    public const string OpLogClear = "logClear";

    /// <summary>Файлы рядом с журналами за период: очищенные конфиги ядра и падения службы (для выгрузки).</summary>
    public const string OpLogFiles = "logFiles";

    /// <summary>Ядро завершилось само (упало или не смогло работать дальше).</summary>
    public const string EventCoreExited = "coreExited";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Команда лаунчера.</summary>
public sealed class ControlMessage
{
    public int Id { get; set; }

    public string Op { get; set; } = string.Empty;

    /// <summary>Для start: имя канала лаунчера «чей процесс» (SafeLauncher-Owner-…), см. core/README.md.</summary>
    public string? OwnerPipe { get; set; }

    /// <summary>Для start: что ядро должно делать.</summary>
    public CoreRequest? Request { get; set; }

    /// <summary>Для guard: что разрешать в сеть только через VPN.</summary>
    public GuardRequest? Guard { get; set; }

    /// <summary>Корреляция: id операции лаунчера (L + 4 hex), служба ставит его в свои строки журнала по этой команде.</summary>
    public string? Cid { get; set; }

    /// <summary>Для команд журналов (logConfig, logRead, logMark, logFiles).</summary>
    public LogRequest? Logs { get; set; }
}

/// <summary>Запрос к журналам службы. Какие поля нужны — см. описание команды в <see cref="ControlProtocol"/>.</summary>
public sealed class LogRequest
{
    /// <summary>logConfig: files или memory.</summary>
    public string? Storage { get; set; }

    /// <summary>logConfig: срок хранения, дней.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>logConfig: скрывать IP-адреса и серверы подключений при записи.</summary>
    public bool? HideAddresses { get; set; }

    /// <summary>logConfig: подробный журнал до этого времени (UTC); null — выключен.</summary>
    public DateTime? DetailedUntilUtc { get; set; }

    /// <summary>logRead, logFiles: период (UTC).</summary>
    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    /// <summary>logRead: service и/или core; пусто — оба.</summary>
    public List<string>? Sources { get; set; }

    /// <summary>logRead: откуда продолжить (из прошлого ответа); null — с начала периода.</summary>
    public string? Cursor { get; set; }

    /// <summary>logRead: примерный размер порции, байт (служба ограничивает сама).</summary>
    public int? MaxBytes { get; set; }

    /// <summary>logMark: текст отметки (необязательно).</summary>
    public string? Text { get; set; }
}

/// <summary>Ответ команд журналов.</summary>
public sealed class LogReply
{
    /// <summary>logConfig: ключ меток адресов (base64), чтобы метки лаунчера совпадали с метками службы.</summary>
    public string? MaskKey { get; set; }

    /// <summary>Занято файлами журналов службы и ядра (с конфигами и падениями), байт.</summary>
    public long? ServiceBytes { get; set; }

    public long? CoreBytes { get; set; }

    /// <summary>Сколько строк служба и ядро потеряли (переполнение, ошибки записи).</summary>
    public long? Dropped { get; set; }

    /// <summary>Пишется ли сейчас подробный журнал ядра.</summary>
    public bool? Detailed { get; set; }

    /// <summary>Где хранятся журналы службы (files или memory).</summary>
    public string? Storage { get; set; }

    /// <summary>logRead: строки в формате журнала.</summary>
    public List<string>? Lines { get; set; }

    /// <summary>logRead: продолжение; null — дочитано.</summary>
    public string? Cursor { get; set; }

    /// <summary>logFiles: файлы за период.</summary>
    public List<LogFileItem>? Files { get; set; }

    /// <summary>logClear: освобождено байт.</summary>
    public long? Freed { get; set; }
}

/// <summary>Файл рядом с журналами (очищенный конфиг ядра, описание падения).</summary>
public sealed class LogFileItem
{
    public string Name { get; set; } = string.Empty;

    public DateTime Utc { get; set; }

    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Блокировка обхода VPN фильтрами Windows (WFP). Фильтры живут, пока подключён лаунчер, который их задал,
/// и исчезают вместе с ним или со службой. Каждый guard заменяет прежний набор этого лаунчера; пустой — снимает его.
/// </summary>
public sealed class GuardRequest
{
    /// <summary>Приложения, которым сеть разрешена только через указанные адаптеры.</summary>
    public List<GuardRule> Apps { get; set; } = new();

    /// <summary>
    /// Если задано и не пусто: DNS-запросы (порт 53) службы DNS Windows, которая ищет имена за все программы, — только
    /// через эти адаптеры (IfIndex) и адаптер ядра. Это настройка на весь компьютер («Скрывать DNS-запросы» при системном
    /// VPN); null — не трогать DNS.
    /// </summary>
    public List<int>? DnsInterfaces { get; set; }

    /// <summary>
    /// Программы приложений, у которых вместе с <see cref="DnsInterfaces"/> закрыт и их собственный DNS (порт 53) мимо этих
    /// адаптеров: «Скрывать DNS-запросы» без «Блокировать трафик мимо VPN», когда другого фильтра у них нет.
    /// </summary>
    public List<string>? DnsPaths { get; set; }
}

/// <summary>
/// Программы (пути к exe), которым служба разрешает сеть только через указанные адаптеры (и внутри компьютера, 127.0.0.1).
/// Остальное фильтр Windows для них блокирует: так приложение не обойдёт VPN, даже если само выберет адаптер
/// (как WebRTC в браузерах). Фильтр действует на exe целиком: и на копии программы, запущенные не через лаунчер.
/// </summary>
public sealed class GuardRule
{
    /// <summary>Полные пути к exe-файлам приложения.</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>Сеть — через адаптер ядра лаунчера (служба сама знает его номер). Пока ядра нет — сети нет.</summary>
    public bool ViaCore { get; set; }

    /// <summary>Номера адаптеров (IfIndex) системного VPN, через которые сеть разрешена. Пусто — сети нет.</summary>
    public List<int> Interfaces { get; set; } = new();

    /// <summary>
    /// Порты на 127.0.0.1, закрытые для приложения: там системный прокси другой VPN-программы, через который трафик
    /// ушёл бы мимо адаптера. Остальная сеть внутри компьютера открыта.
    /// </summary>
    public List<int>? LoopbackPorts { get; set; }
}

/// <summary>Ответ службы на команду или событие.</summary>
public sealed class ControlReply
{
    public int? Id { get; set; }

    public string? Event { get; set; }

    public bool Ok { get; set; }

    /// <summary>Причина отказа или сбоя, по-русски, для показа пользователю.</summary>
    public string? Error { get; set; }

    public int? CorePid { get; set; }

    public bool? Running { get; set; }

    public string? Version { get; set; }

    /// <summary>Для status и start: <see cref="ControlProtocol.Revision"/> службы.</summary>
    public int? Protocol { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>Для stop: ядро закрылось само по Ctrl+C (false — пришлось завершить принудительно).</summary>
    public bool? Graceful { get; set; }

    /// <summary>Для start: порт управления ядром на 127.0.0.1 (проверка серверов, список соединений).</summary>
    public int? ApiPort { get; set; }

    /// <summary>Для start: ключ к управлению ядром, свой на каждый запуск.</summary>
    public string? ApiSecret { get; set; }

    /// <summary>Последние строки журнала ядра (при сбое и в status).</summary>
    public List<string>? Log { get; set; }

    /// <summary>Для guard: что не удалось включить (фильтры частично не добавлены), иначе null.</summary>
    public string? Warning { get; set; }

    /// <summary>Для команд журналов.</summary>
    public LogReply? Logs { get; set; }

    /// <summary>
    /// Для guard: пути из запроса, которые служба не смогла проверить (сетевой диск, нет доступа) и поэтому не ограничила.
    /// Лаунчер не запускает приложение, если среди них его программа.
    /// </summary>
    public List<string>? Unguarded { get; set; }
}

/// <summary>
/// Что ядро должно делать: подключения и какие приложения через какое из них идут. Остальной трафик
/// компьютера всегда идёт напрямую — это служба задаёт сама, и лаунчер изменить этого не может.
/// </summary>
public sealed class CoreRequest
{
    /// <summary>Подключения в формате outbound/endpoint sing-box; у каждого свой tag.</summary>
    public List<JsonObject> Outbounds { get; set; } = new();

    public List<CoreAppRoute> Apps { get; set; } = new();

    /// <summary>Давать адаптеру ядра адрес IPv6 (только если в сети есть IPv6; без него ядро закрывает IPv6 на время работы).</summary>
    public bool Ipv6 { get; set; }

    /// <summary>
    /// Скрывать DNS-запросы от провайдера: на запросы службы DNS Windows ядро отвечает подменными адресами, а настоящий
    /// адрес ищется уже при подключении — для приложений лаунчера сервером VPN. Затрагивает весь компьютер, пока работает ядро.
    /// </summary>
    public bool HideDns { get; set; }

    /// <summary>DNS-сервер внутри подключения (tag → IP), например из файла WireGuard. Без него — 1.1.1.1 через подключение.</summary>
    public Dictionary<string, string>? OutboundDns { get; set; }
}

/// <summary>Приложение лаунчера: его процессы (метка от лаунчера) идут через подключение Outbound.</summary>
public sealed class CoreAppRoute
{
    /// <summary>Метка, которой лаунчер отвечает ядру на вопрос «чей процесс».</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Tag подключения из <see cref="CoreRequest.Outbounds"/>.</summary>
    public string Outbound { get; set; } = string.Empty;

    /// <summary>Домены (с поддоменами), к которым приложение ходит напрямую.</summary>
    public List<string> DirectDomains { get; set; } = new();
}
