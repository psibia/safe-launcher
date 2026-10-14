using System;
using System.Collections.Generic;
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

    /// <summary>«Закрывать при обрыве» для приложений, у которых это не задано отдельно (Kill-Switch).</summary>
    public bool KillSwitchEnabled { get; set; } = true;

    /// <summary>«Не запускать без VPN» для приложений, у которых это не задано отдельно.</summary>
    public bool RequireVpn { get; set; } = true;

    /// <summary>Как лаунчер понимает, что VPN есть: адаптеры другой программы или своё подключение.</summary>
    public GlobalNetworkMode NetworkMode { get; set; } = GlobalNetworkMode.SystemVpn;

    /// <summary>Подключение лаунчера в общих настройках (<see cref="GlobalNetworkMode.LauncherConnection"/>).</summary>
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
    /// «Блокировать трафик мимо VPN»: фильтры Windows пускают программы приложения в сеть только через его VPN
    /// (адаптер ядра лаунчера или адаптеры системного VPN), даже если программа сама выберет обычный адаптер.
    /// Действует на программы приложения, пока оно работает; см. VpnGuard.
    /// </summary>
    public bool BlockBypass { get; set; } = true;

    /// <summary>
    /// «Скрывать DNS-запросы от провайдера» — настройка на весь компьютер, пока работает приложение лаунчера:
    /// служба DNS Windows получает подменные адреса от ядра (подключения лаунчера) или ходит только через VPN
    /// (системный VPN). По умолчанию выключено; при включении пользователь видит, на что это влияет.
    /// </summary>
    public bool HideDns { get; set; }

    /// <summary>Запускать лаунчер при входе в Windows (свёрнутым в трей).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Журналы (раздел «Журналы»): где хранить, сколько дней, скрывать ли IP-адреса, подробный журнал.
    /// По умолчанию: в файлах, 7 дней, адреса не скрываются (см. notes/logging).
    /// </summary>
    public LogSettings Logs { get; set; } = new();
}
