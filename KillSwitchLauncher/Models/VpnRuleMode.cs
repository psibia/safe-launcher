namespace KillSwitchLauncher.Models;

/// <summary>Правило, по которому лаунчер считает, что VPN подключён.</summary>
public enum VpnRuleMode
{
    /// <summary>Достаточно любого активного VPN-адаптера (автоопределение).</summary>
    AnyVpn,

    /// <summary>Должен быть активен хотя бы один из выбранных адаптеров.</summary>
    AnyOfSelected,

    /// <summary>Должны быть активны все выбранные адаптеры одновременно.</summary>
    AllOfSelected
}
