using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KillSwitchLauncher.Services;
using SafeLauncher.Logging;

namespace KillSwitchLauncher.Models;

/// <summary>Всё, что лаунчер сохраняет между запусками.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Версия формата; 0 — файл лаунчера 1.x без сетевых настроек (или поля нет вовсе). При загрузке файл
    /// приводится к <see cref="SettingsStorage.CurrentVersion"/>, см. SettingsStorage.Migrate.
    /// </summary>
    public int SettingsVersion { get; set; }

    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>
    /// «Закрывать при обрыве» для приложений, у которых это не задано отдельно (Kill-Switch). При новой установке выключен
    /// (SettingsStorage.Load). true здесь — для файлов без этого значения и повреждённых: прежний выбор не меняется.
    /// </summary>
    public bool KillSwitchEnabled { get; set; } = true;

    /// <summary>
    /// Общее условие запуска. Системного VPN в интерфейсе больше нет: при загрузке он заменяется подключением лаунчера
    /// (SettingsStorage.Migrate), значение остаётся только для чтения старых файлов.
    /// </summary>
    public GlobalNetworkMode NetworkMode { get; set; } = GlobalNetworkMode.LauncherConnection;

    /// <summary>Основное подключение: через него идут приложения с условием «Основное подключение».</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>Все подключения лаунчера: общее и подключения отдельных приложений.</summary>
    public List<VpnConnection> Connections { get; set; } = new();

    /// <summary>Правило для адаптеров в общих настройках (<see cref="GlobalNetworkMode.SystemVpn"/>).</summary>
    public VpnRuleMode RuleMode { get; set; } = VpnRuleMode.AnyVpn;

    /// <summary>Имена сетевых адаптеров, выбранных в общих правилах (NetworkInterface.Name).</summary>
    public List<string> SelectedAdapters { get; set; } = new();

    /// <summary>Что делает кнопка «Закрыть» в заголовке окна: спросить, свернуть в трей или закрыть.</summary>
    public HideBehavior HideBehavior { get; set; } = HideBehavior.Ask;

    /// <summary>Мастер первого запуска пройден или пропущен (показываем один раз, только новым пользователям).</summary>
    public bool WelcomeDone { get; set; }

    /// <summary>Подсказка «лаунчер работает в трее» уже показывалась (показываем один раз).</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Все адаптеры, которые лаунчер видел. Те, что сейчас отсутствуют, показываются в списке «удалённых».</summary>
    public List<KnownAdapter> KnownAdapters { get; set; } = new();

    /// <summary>
    /// «Приложения, открытые не из лаунчера» (приложения через подключение лаунчера): через VPN, без сети или
    /// напрямую. Ядро узнаёт их по exe-файлам приложения, см. CorePlan.Programs.
    /// </summary>
    public OutsideCopies OutsideCopies { get; set; } = OutsideCopies.Vpn;

    /// <summary>
    /// «Защита от утечки DNS» — настройка на весь компьютер, пока работает приложение лаунчера:
    /// служба DNS Windows получает подменные адреса от ядра (подключения лаунчера) или ходит только через VPN
    /// (системный VPN). По умолчанию выключено; при включении пользователь видит, на что это влияет.
    /// </summary>
    public bool HideDns { get; set; }

    /// <summary>
    /// Общие исключения (раздел «Исключения»): домены, которые приложения через подключение лаунчера открывают напрямую,
    /// вместе с поддоменами. Приложение дополняет их своими или заменяет своими (<see cref="AppNetwork.DomainsMode"/>).
    /// </summary>
    public List<string> DirectDomains { get; set; } = new();

    /// <summary>Запускать лаунчер при входе в Windows (свёрнутым в трей).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Журналы (раздел «Журналы»): где хранить, сколько дней, скрывать ли IP-адреса, подробный журнал.
    /// По умолчанию: в файлах, 7 дней, адреса не скрываются (см. notes/logging).
    /// </summary>
    public LogSettings Logs { get; set; } = new();

    /// <summary>Обновления лаунчера (раздел «Обновления»).</summary>
    public UpdateSettings Updates { get; set; } = new();

    /// <summary>
    /// Загрузка перевела настройки с системного VPN на подключение лаунчера; null — переводить было нечего. Не сохраняется:
    /// главное окно один раз сообщает об этом и сохраняет настройки.
    /// </summary>
    [JsonIgnore]
    public SystemVpnMigration? MovedFromSystemVpn { get; set; }
}

/// <summary>Приложения, которые шли через системный VPN («Как в общих» или «Свои адаптеры») и теперь идут через основное подключение.</summary>
public sealed record SystemVpnMigration(IReadOnlyList<string> Apps);

/// <summary>Откуда скачивать обновления.</summary>
public enum UpdateRoute
{
    /// <summary>Напрямую через интернет (по умолчанию).</summary>
    Direct,

    /// <summary>Через подключение лаунчера (<see cref="UpdateSettings.ConnectionId"/>), если GitHub напрямую недоступен.</summary>
    Connection
}

/// <summary>Обновления лаунчера: автоматическая проверка и загрузка, маршрут, о чём уже сообщили.</summary>
public sealed class UpdateSettings
{
    /// <summary>
    /// Проверять обновления при запуске и каждые 4 часа и сразу скачивать найденное. Ставится обновление только по кнопке
    /// «Обновить». Выключено — только кнопка «Проверить».
    /// </summary>
    public bool Auto { get; set; } = true;

    /// <summary>Откуда скачивать (скрытая настройка в «Дополнительно»).</summary>
    public UpdateRoute Route { get; set; } = UpdateRoute.Direct;

    /// <summary>Подключение лаунчера для маршрута <see cref="UpdateRoute.Connection"/>.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>О какой версии уже сообщили уведомлением: о каждой версии сообщаем один раз.</summary>
    public string? NotifiedVersion { get; set; }

    /// <summary>Для какой версии настройки уже открывались сразу на странице «Обновления»: один раз на версию.</summary>
    public string? OpenedVersion { get; set; }
}
