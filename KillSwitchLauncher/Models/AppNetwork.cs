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
    OwnConnection
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

    /// <summary>
    /// «Не запускать без VPN». С подключением лаунчера выключенная защита значит «не ждать проверки сервера»:
    /// трафик приложения всё равно идёт только через ядро.
    /// </summary>
    public ProtectionOverride RequireVpn { get; set; } = ProtectionOverride.Inherit;

    /// <summary>«Закрывать при обрыве» (Kill-Switch). Выключено — при обрыве подключения лаунчера приложение ждёт его возвращения без сети.</summary>
    public ProtectionOverride CloseOnDrop { get; set; } = ProtectionOverride.Inherit;

    /// <summary>
    /// Исключения: домены, к которым приложение ходит напрямую, мимо подключения лаунчера (вместе с поддоменами).
    /// Действуют, только когда трафик ведёт ядро лаунчера.
    /// </summary>
    public List<string> DirectDomains { get; set; } = new();
}
