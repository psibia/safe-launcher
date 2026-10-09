using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Когда VPN считается подключённым: правила «Любой VPN», «Один из выбранных», «Все выбранные» и распознавание адаптеров.</summary>
public class VpnRulesTests
{
    private static VpnAdapterInfo Adapter(string name, bool up, bool vpn = true) =>
        new(name, name + " adapter", vpn ? NetworkInterfaceType.Ppp : NetworkInterfaceType.Ethernet, up, vpn, name);

    private static readonly IReadOnlyList<VpnAdapterInfo> Network = new[]
    {
        Adapter("Ethernet", up: true, vpn: false),
        Adapter("WireGuard", up: true),
        Adapter("Рабочий VPN", up: false)
    };

    // ---------------------------------------------------------------- любой VPN

    [Fact]
    public void AnyVpn_ActiveVpnAdapter_IsConnected()
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(VpnRuleMode.AnyVpn, Array.Empty<string>()), Network);

        Assert.True(snapshot.IsConnected);
        Assert.Equal(new[] { "WireGuard" }, snapshot.ActiveVpnNames);
        Assert.Equal("Обнаружен активный VPN", snapshot.Explanation);
    }

    [Fact]
    public void AnyVpn_OnlyOrdinaryNetwork_IsNotConnected()
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(VpnRuleMode.AnyVpn, Array.Empty<string>()),
            new[] { Adapter("Ethernet", up: true, vpn: false), Adapter("WireGuard", up: false) });

        Assert.False(snapshot.IsConnected);
        Assert.Empty(snapshot.ActiveVpnNames);
        Assert.Equal("Нет защищённого соединения", snapshot.Explanation);
    }

    // ---------------------------------------------------------------- один из выбранных

    [Fact]
    public void AnyOfSelected_OneSelectedIsUp_IsConnected()
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(VpnRuleMode.AnyOfSelected, new[] { "wireguard", "Рабочий VPN" }), Network);

        Assert.True(snapshot.IsConnected);
        Assert.Equal("Активен один из выбранных VPN", snapshot.Explanation);
    }

    [Fact]
    public void AnyOfSelected_NoSelectedIsUp_IsNotConnected()
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(VpnRuleMode.AnyOfSelected, new[] { "Рабочий VPN" }), Network);

        Assert.False(snapshot.IsConnected);
        Assert.Equal("Выбранные VPN не активны", snapshot.Explanation);
    }

    [Fact]
    public void AnyOfSelected_SelectedAdapterRemovedFromSystem_IsNotConnected()
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(VpnRuleMode.AnyOfSelected, new[] { "AmneziaWG" }), Network);
        Assert.False(snapshot.IsConnected);
    }

    // ---------------------------------------------------------------- все выбранные

    [Fact]
    public void AllOfSelected_AllUp_IsConnected()
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(VpnRuleMode.AllOfSelected, new[] { "WireGuard" }), Network);

        Assert.True(snapshot.IsConnected);
        Assert.Equal("Активны все выбранные VPN", snapshot.Explanation);
    }

    [Fact]
    public void AllOfSelected_OneDown_IsNotConnected_AndNamesIt()
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(VpnRuleMode.AllOfSelected, new[] { "WireGuard", "Рабочий VPN" }), Network);

        Assert.False(snapshot.IsConnected);
        Assert.Equal("Не подключены: Рабочий VPN", snapshot.Explanation);
    }

    [Theory]
    [InlineData(VpnRuleMode.AnyOfSelected)]
    [InlineData(VpnRuleMode.AllOfSelected)]
    public void SelectedModes_NothingSelected_IsNotConnected(VpnRuleMode mode)
    {
        var snapshot = VpnMonitor.Apply(new VpnRule(mode, Array.Empty<string>()), Network);

        Assert.False(snapshot.IsConnected);
        Assert.Equal("Не выбран ни один VPN-адаптер", snapshot.Explanation);
    }

    // ---------------------------------------------------------------- момент обрыва

    [Fact]
    public void JustDisconnected_OnlyWhenVpnWasUpAndWentDown()
    {
        var up = new VpnSnapshot(true, Network, new[] { "WireGuard" }, "");
        var down = new VpnSnapshot(false, Network, Array.Empty<string>(), "");

        Assert.True(new VpnStatusChangedEventArgs(up, down).JustDisconnected);
        Assert.False(new VpnStatusChangedEventArgs(down, down).JustDisconnected);
        Assert.False(new VpnStatusChangedEventArgs(down, up).JustDisconnected);
        Assert.False(new VpnStatusChangedEventArgs(null, down).JustDisconnected); // первая проверка при запуске
    }

    // ---------------------------------------------------------------- распознавание адаптеров

    [Theory]
    [InlineData("WireGuard Tunnel", "WireGuard Tunnel", NetworkInterfaceType.Unknown)]
    [InlineData("Подключение по локальной сети", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet)]
    [InlineData("OpenVPN Wintun", "Wintun Userspace Tunnel", NetworkInterfaceType.Unknown)]
    [InlineData("Ethernet 3", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", NetworkInterfaceType.Ethernet)]
    [InlineData("Рабочий VPN", "Рабочий VPN", NetworkInterfaceType.Ppp)]
    [InlineData("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Unknown)]
    public void IsVpnLike_RecognisesVpnAdapters(string name, string description, NetworkInterfaceType type) =>
        Assert.True(VpnAdapterDetector.IsVpnLike(name, description, type));

    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211)]
    [InlineData("Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel)]
    [InlineData("isatap.{1234}", "Microsoft ISATAP Adapter", NetworkInterfaceType.Tunnel)]
    [InlineData("WAN Miniport (IKEv2)", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp)]
    [InlineData("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", NetworkInterfaceType.Loopback)]
    public void IsVpnLike_IgnoresOrdinaryAndServiceAdapters(string name, string description, NetworkInterfaceType type) =>
        Assert.False(VpnAdapterDetector.IsVpnLike(name, description, type));

    [Theory]
    [InlineData("TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, "OpenVPN (TAP)")]
    [InlineData("OpenVPN Data Channel Offload", NetworkInterfaceType.Unknown, "OpenVPN (DCO)")]
    [InlineData("Wintun Userspace Tunnel", NetworkInterfaceType.Unknown, "WireGuard / OpenVPN (Wintun)")]
    [InlineData("WireGuard Tunnel", NetworkInterfaceType.Unknown, "WireGuard")]
    [InlineData("AmneziaWG Tunnel", NetworkInterfaceType.Unknown, "AmneziaWG")]
    [InlineData("Cisco AnyConnect Virtual Miniport Adapter", NetworkInterfaceType.Ethernet, "Cisco AnyConnect")]
    [InlineData("Fortinet SSL VPN Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, "FortiClient")]
    [InlineData("PANGP Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, "GlobalProtect")]
    [InlineData("Рабочий VPN", NetworkInterfaceType.Ppp, "VPN Windows (PPP)")]
    [InlineData("Some Vendor Adapter", NetworkInterfaceType.Ethernet, "Some Vendor Adapter")]
    public void GetProduct_GivesReadableName(string description, NetworkInterfaceType type, string expected) =>
        Assert.Equal(expected, VpnAdapterDetector.GetProduct(description, type));
}
