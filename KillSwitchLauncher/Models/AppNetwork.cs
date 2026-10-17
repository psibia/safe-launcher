using System;
using System.Collections.Generic;

namespace KillSwitchLauncher.Models;

/// <summary>Условие запуска приложения: какой VPN нужен, чтобы его запустить.</summary>
public enum AppNetworkMode
{
    /// <summary>«Как в общих»: то, что выбрано в общих настройках.</summary>
    Inherit,

    /// <summary>«Свои адаптеры»: свой набор VPN-адаптеров и правило для них; лаунчер только проверяет, что VPN включён.</summary>
    OwnAdapters,

    /// <summary>«Своё подключение»: трафик приложения ведёт ядро лаунчера через выбранное подключение.</summary>
    OwnConnection,

    /// <summary>
    /// «Без сети»: фильтр Windows не пускает программы приложения никуда, кроме связи внутри компьютера (127.0.0.1).
    /// VPN для запуска не нужен.
    /// </summary>
    Offline
}

/// <summary>Общие настройки VPN: как лаунчер понимает, что VPN есть.</summary>
public enum GlobalNetworkMode
{
    /// <summary>«Системный VPN (адаптеры)»: VPN включается другой программой, лаунчер проверяет адаптеры.</summary>
    SystemVpn,

    /// <summary>«Подключение лаунчера»: трафик приложений ведёт ядро лаунчера через выбранное подключение.</summary>
    LauncherConnection
}

/// <summary>Защита приложения поверх общих настроек.</summary>
public enum ProtectionOverride
{
    /// <summary>Как в общих настройках.</summary>
    Inherit,

    On,

    Off
}

/// <summary>Сетевые настройки одного приложения (страница приложения, раздел «Сеть»).</summary>
public sealed class AppNetwork
{
    public AppNetworkMode Mode { get; set; } = AppNetworkMode.Inherit;

    /// <summary>Правило для своих адаптеров (<see cref="AppNetworkMode.OwnAdapters"/>).</summary>
    public VpnRuleMode RuleMode { get; set; } = VpnRuleMode.AnyVpn;

    /// <summary>Свои адаптеры (NetworkInterface.Name) для <see cref="AppNetworkMode.OwnAdapters"/>.</summary>
    public List<string> SelectedAdapters { get; set; } = new();

    /// <summary>Своё подключение (<see cref="AppNetworkMode.OwnConnection"/>): Id из списка подключений.</summary>
    public Guid? ConnectionId { get; set; }

    /// <summary>«Закрывать при обрыве» (Kill-Switch). Выключено — при обрыве подключения лаунчера приложение ждёт его возвращения без сети.</summary>
    public ProtectionOverride CloseOnDrop { get; set; } = ProtectionOverride.Inherit;

    /// <summary>
    /// «Если открыто не из лаунчера»: что делать с программой приложения, открытой не из лаунчера. Действует, только когда
    /// трафик ведёт ядро лаунчера.
    /// </summary>
    public OutsideCopiesOverride OutsideCopies { get; set; } = OutsideCopiesOverride.Inherit;

    /// <summary>
    /// Исключения: домены, к которым приложение ходит напрямую, мимо подключения лаунчера (вместе с поддоменами).
    /// Действуют, только когда трафик ведёт ядро лаунчера.
    /// </summary>
    public List<string> DirectDomains { get; set; } = new();
}
