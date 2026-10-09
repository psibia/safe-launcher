using System.Collections.Generic;

namespace KillSwitchLauncher.Models;

/// <summary>Всё, что лаунчер сохраняет между запусками.</summary>
public sealed class AppSettings
{
    public List<AppEntry> Apps { get; set; } = new();

    /// <summary>Принудительно закрывать запущенные приложения при обрыве VPN.</summary>
    public bool KillSwitchEnabled { get; set; } = true;

    public VpnRuleMode RuleMode { get; set; } = VpnRuleMode.AnyVpn;

    /// <summary>Имена сетевых адаптеров, выбранных в правилах (NetworkInterface.Name).</summary>
    public List<string> SelectedAdapters { get; set; } = new();

    /// <summary>Что делает кнопка «Закрыть» в заголовке окна: спросить, свернуть в трей или закрыть.</summary>
    public HideBehavior HideBehavior { get; set; } = HideBehavior.Ask;

    /// <summary>Подсказка «лаунчер работает в трее» уже показывалась (показываем один раз).</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Все адаптеры, которые лаунчер видел. Те, что сейчас отсутствуют, показываются в списке «удалённых».</summary>
    public List<KnownAdapter> KnownAdapters { get; set; } = new();
}
