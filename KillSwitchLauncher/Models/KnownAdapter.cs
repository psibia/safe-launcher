namespace KillSwitchLauncher.Models;

/// <summary>
/// Адаптер, который лаунчер хоть раз видел в системе. Запоминается, потому что некоторые VPN
/// (например, WireGuard) создают адаптер при подключении и удаляют при отключении.
/// </summary>
public sealed class KnownAdapter
{
    /// <summary>Системное имя (NetworkInterface.Name), по нему сохраняется выбор в правилах.</summary>
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Понятное название, например «WireGuard».</summary>
    public string Product { get; set; } = string.Empty;

    public bool IsVpnLike { get; set; }
}
