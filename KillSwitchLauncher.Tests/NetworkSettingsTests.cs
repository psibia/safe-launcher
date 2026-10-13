using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Переход с настроек лаунчера 1.x: поведение остаётся прежним.</summary>
public class SettingsMigrationTests
{
    // settings.json лаунчера 1.x: без версии и сетевых настроек
    private const string OldSettings = """
        {
          "Apps": [
            { "Id": "5d0c2b7e-8d43-4a3e-9d5a-1d2c3b4a5f60", "Name": "Discord", "ExePath": "C:\\Apps\\Discord.exe", "TrackDns": true },
            { "Id": "0f7c9a1b-2e3d-4c5b-8a69-7e6d5c4b3a21", "Name": "Telegram", "ExePath": "C:\\Apps\\Telegram.exe" }
          ],
          "KillSwitchEnabled": false,
          "RuleMode": "AnyOfSelected",
          "SelectedAdapters": [ "WireGuard" ],
          "HideBehavior": "Tray",
          "TrayHintShown": true,
          "KnownAdapters": []
        }
        """;

    private static (SettingsStorage Storage, TempDir Dir) WithOldFile()
    {
        var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, OldSettings);
        return (new SettingsStorage(path), dir);
    }

    [Fact]
    public void OldSettings_KeepEverything_AndGetInheritEverywhere()
    {
        var (storage, dir) = WithOldFile();
        using (dir)
        {
            var settings = storage.Load();

            Assert.Equal(SettingsStorage.CurrentVersion, settings.SettingsVersion);
            Assert.False(settings.KillSwitchEnabled);
            Assert.True(settings.RequireVpn);
            Assert.Equal(GlobalNetworkMode.SystemVpn, settings.NetworkMode);
            Assert.Equal(VpnRuleMode.AnyOfSelected, settings.RuleMode);
            Assert.Equal(new[] { "WireGuard" }, settings.SelectedAdapters);
            Assert.Empty(settings.Connections);
            Assert.Null(settings.ConnectionId);

            Assert.Equal(2, settings.Apps.Count);
            Assert.True(settings.Apps[0].TrackDns);
            foreach (var app in settings.Apps)
            {
                Assert.Equal(AppNetworkMode.Inherit, app.Network.Mode);
                Assert.Equal(ProtectionOverride.Inherit, app.Network.RequireVpn);
                Assert.Equal(ProtectionOverride.Inherit, app.Network.CloseOnDrop);
                Assert.Empty(app.Network.DirectDomains);
            }
        }
    }

    [Fact]
    public void OldSettings_BehaveAsBefore()
    {
        // Прежнее поведение: запуск только при VPN по общему правилу адаптеров, Kill-Switch — общий флаг.
        var (storage, dir) = WithOldFile();
        using (dir)
        {
            var settings = storage.Load();

            foreach (var app in settings.Apps)
            {
                var plan = NetworkPlan.For(app, settings);
                Assert.Equal(NetworkPlanKind.SystemAdapters, plan.Kind);
                Assert.Equal(NetworkPlanSource.Global, plan.Source);
                Assert.Equal(VpnRuleMode.AnyOfSelected, plan.AdapterRule!.Mode);
                Assert.Contains("wireguard", plan.AdapterRule.SelectedAdapters, StringComparer.OrdinalIgnoreCase);
                Assert.True(plan.RequireVpn);
                Assert.False(plan.CloseOnDrop);
                Assert.False(plan.UsesLauncherCore);
            }
        }
    }

    [Fact]
    public void OldFile_IsBackedUpOnce_AndRewrittenWithVersion()
    {
        var (storage, dir) = WithOldFile();
        using (dir)
        {
            var settings = storage.Load();
            Assert.Equal(OldSettings, File.ReadAllText(storage.FilePath + ".v0.bak"));

            storage.Save(settings);
            File.Delete(storage.FilePath + ".v0.bak");
            storage.Load();

            Assert.Contains("\"SettingsVersion\": 1", File.ReadAllText(storage.FilePath));
            Assert.False(File.Exists(storage.FilePath + ".v0.bak")); // уже новый формат — копия не нужна
        }
    }

    [Fact]
    public void NewInstall_IsCurrentVersion()
    {
        using var dir = new TempDir();
        var settings = new SettingsStorage(dir.File("settings.json")).Load();

        Assert.Equal(SettingsStorage.CurrentVersion, settings.SettingsVersion);
        Assert.True(settings.RequireVpn);
        Assert.Equal(GlobalNetworkMode.SystemVpn, settings.NetworkMode);
    }

    [Fact]
    public void HandEditedNulls_AreRepaired()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """
            { "SettingsVersion": 1, "Connections": null, "SelectedAdapters": null,
              "Apps": [ { "Name": "A", "Network": null }, { "Name": "B", "Network": { "DirectDomains": null, "SelectedAdapters": null } } ] }
            """);

        var settings = new SettingsStorage(path).Load();

        Assert.Empty(settings.Connections);
        Assert.Empty(settings.SelectedAdapters);
        Assert.All(settings.Apps, a =>
        {
            Assert.NotNull(a.Network);
            Assert.Empty(a.Network.DirectDomains);
            Assert.Empty(a.Network.SelectedAdapters);
        });
    }

    [Fact]
    public void NetworkSettings_SurviveSaveAndLoad()
    {
        using var dir = new TempDir();
        var storage = new SettingsStorage(dir.File("settings.json"));
        var connection = new VpnConnection { Name = "Мой прокси", Kind = ConnectionKind.LocalProxy, ProxyPort = 10808, ProxyProtocol = ProxyProtocol.Http };
        var app = new AppEntry
        {
            Name = "Discord",
            Network = new AppNetwork
            {
                Mode = AppNetworkMode.OwnConnection,
                ConnectionId = connection.Id,
                RequireVpn = ProtectionOverride.Off,
                CloseOnDrop = ProtectionOverride.On,
                DirectDomains = new List<string> { "cdn.discordapp.com" }
            }
        };
        var other = new AppEntry
        {
            Name = "Telegram",
            Network = new AppNetwork { Mode = AppNetworkMode.OwnAdapters, RuleMode = VpnRuleMode.AllOfSelected, SelectedAdapters = new List<string> { "Рабочий VPN" } }
        };
        storage.Save(new AppSettings
        {
            NetworkMode = GlobalNetworkMode.LauncherConnection,
            ConnectionId = connection.Id,
            RequireVpn = false,
            Connections = new List<VpnConnection> { connection },
            Apps = new List<AppEntry> { app, other }
        });

        var loaded = storage.Load();

        Assert.Equal(GlobalNetworkMode.LauncherConnection, loaded.NetworkMode);
        Assert.Equal(connection.Id, loaded.ConnectionId);
        Assert.False(loaded.RequireVpn);
        var savedConnection = Assert.Single(loaded.Connections);
        Assert.Equal("Мой прокси", savedConnection.Name);
        Assert.Equal(ConnectionKind.LocalProxy, savedConnection.Kind);
        Assert.Equal("127.0.0.1", savedConnection.ProxyHost);
        Assert.Equal(10808, savedConnection.ProxyPort);
        Assert.Equal(ProxyProtocol.Http, savedConnection.ProxyProtocol);

        var network = loaded.Apps[0].Network;
        Assert.Equal(AppNetworkMode.OwnConnection, network.Mode);
        Assert.Equal(connection.Id, network.ConnectionId);
        Assert.Equal(ProtectionOverride.Off, network.RequireVpn);
        Assert.Equal(ProtectionOverride.On, network.CloseOnDrop);
        Assert.Equal(new[] { "cdn.discordapp.com" }, network.DirectDomains);

        Assert.Equal(AppNetworkMode.OwnAdapters, loaded.Apps[1].Network.Mode);
        Assert.Equal(VpnRuleMode.AllOfSelected, loaded.Apps[1].Network.RuleMode);
        Assert.Equal(new[] { "Рабочий VPN" }, loaded.Apps[1].Network.SelectedAdapters);

        var json = File.ReadAllText(storage.FilePath);
        Assert.Contains("\"Mode\": \"OwnConnection\"", json);
        Assert.Contains("\"NetworkMode\": \"LauncherConnection\"", json);
    }
}

/// <summary>Итоговые условия приложения: «Как в общих», свои адаптеры, своё подключение и защита.</summary>
public class NetworkPlanTests
{
    private static readonly VpnConnection Shared = new() { Name = "Общее", Kind = ConnectionKind.Link };
    private static readonly VpnConnection Own = new() { Name = "Своё", Kind = ConnectionKind.WireGuard };

    private static AppSettings Settings(Action<AppSettings>? setup = null)
    {
        var settings = new AppSettings
        {
            RuleMode = VpnRuleMode.AnyOfSelected,
            SelectedAdapters = new List<string> { "WireGuard" },
            Connections = new List<VpnConnection> { Shared, Own }
        };
        setup?.Invoke(settings);
        return settings;
    }

    private static AppEntry App(Action<AppNetwork>? setup = null)
    {
        var app = new AppEntry { Name = "Discord" };
        setup?.Invoke(app.Network);
        return app;
    }

    [Fact]
    public void Inherit_SystemVpn_UsesGlobalAdapters()
    {
        var plan = NetworkPlan.For(App(), Settings());

        Assert.Equal(NetworkPlanKind.SystemAdapters, plan.Kind);
        Assert.Equal(NetworkPlanSource.Global, plan.Source);
        Assert.Equal(VpnRuleMode.AnyOfSelected, plan.AdapterRule!.Mode);
        Assert.Null(plan.Connection);
    }

    [Fact]
    public void Inherit_LauncherConnection_UsesGlobalConnection()
    {
        var plan = NetworkPlan.For(App(), Settings(s =>
        {
            s.NetworkMode = GlobalNetworkMode.LauncherConnection;
            s.ConnectionId = Shared.Id;
        }));

        Assert.Equal(NetworkPlanKind.LauncherConnection, plan.Kind);
        Assert.Equal(NetworkPlanSource.Global, plan.Source);
        Assert.Same(Shared, plan.Connection);
        Assert.Null(plan.AdapterRule);
        Assert.True(plan.UsesLauncherCore);
    }

    [Fact]
    public void OwnAdapters_IgnoreGlobalMode()
    {
        var plan = NetworkPlan.For(App(n =>
        {
            n.Mode = AppNetworkMode.OwnAdapters;
            n.RuleMode = VpnRuleMode.AllOfSelected;
            n.SelectedAdapters = new List<string> { "Рабочий VPN", "Outline" };
        }), Settings(s => s.NetworkMode = GlobalNetworkMode.LauncherConnection));

        Assert.Equal(NetworkPlanKind.SystemAdapters, plan.Kind);
        Assert.Equal(NetworkPlanSource.App, plan.Source);
        Assert.Equal(VpnRuleMode.AllOfSelected, plan.AdapterRule!.Mode);
        Assert.Equal(2, plan.AdapterRule.SelectedAdapters.Count);
    }

    [Fact]
    public void OwnConnection_UsesItsConnection()
    {
        var plan = NetworkPlan.For(App(n =>
        {
            n.Mode = AppNetworkMode.OwnConnection;
            n.ConnectionId = Own.Id;
        }), Settings(s =>
        {
            s.NetworkMode = GlobalNetworkMode.LauncherConnection;
            s.ConnectionId = Shared.Id;
        }));

        Assert.Equal(NetworkPlanSource.App, plan.Source);
        Assert.Same(Own, plan.Connection);
    }

    [Fact]
    public void DeletedConnection_BlocksLaunch_EvenWithRequireVpnOff()
    {
        var plan = NetworkPlan.For(App(n =>
        {
            n.Mode = AppNetworkMode.OwnConnection;
            n.ConnectionId = Guid.NewGuid();
            n.RequireVpn = ProtectionOverride.Off;
        }), Settings());

        Assert.Equal(NetworkPlanKind.MissingConnection, plan.Kind);
        Assert.True(plan.RequireVpn);
        Assert.True(plan.UsesLauncherCore);
    }

    [Fact]
    public void GlobalLauncherConnection_NotChosen_IsMissing()
    {
        var plan = NetworkPlan.For(App(), Settings(s => s.NetworkMode = GlobalNetworkMode.LauncherConnection));

        Assert.Equal(NetworkPlanKind.MissingConnection, plan.Kind);
        Assert.Equal(NetworkPlanSource.Global, plan.Source);
    }

    [Theory]
    [InlineData(ProtectionOverride.Inherit, true, true)]
    [InlineData(ProtectionOverride.Inherit, false, false)]
    [InlineData(ProtectionOverride.On, false, true)]
    [InlineData(ProtectionOverride.Off, true, false)]
    public void Protection_OverridesGlobal(ProtectionOverride value, bool global, bool expected)
    {
        var settings = Settings(s =>
        {
            s.KillSwitchEnabled = global;
            s.RequireVpn = global;
        });

        var plan = NetworkPlan.For(App(n =>
        {
            n.CloseOnDrop = value;
            n.RequireVpn = value;
        }), settings);

        Assert.Equal(expected, plan.CloseOnDrop);
        Assert.Equal(expected, plan.RequireVpn);
    }

    [Fact]
    public void DirectDomains_OnlyWithLauncherConnection()
    {
        void Domains(AppNetwork n) => n.DirectDomains = new List<string> { "cdn.discordapp.com" };

        var adapters = NetworkPlan.For(App(Domains), Settings());
        var launcher = NetworkPlan.For(App(n =>
        {
            Domains(n);
            n.Mode = AppNetworkMode.OwnConnection;
            n.ConnectionId = Own.Id;
        }), Settings());

        Assert.Empty(adapters.DirectDomains);
        Assert.Equal(new[] { "cdn.discordapp.com" }, launcher.DirectDomains);
    }

    [Fact]
    public void ConnectionsInUse_CountsInheritAndOwn()
    {
        var settings = Settings(s =>
        {
            s.NetworkMode = GlobalNetworkMode.LauncherConnection;
            s.ConnectionId = Shared.Id;
            s.Apps = new List<AppEntry>
            {
                App(),
                App(n => { n.Mode = AppNetworkMode.OwnConnection; n.ConnectionId = Own.Id; }),
                App(n => n.Mode = AppNetworkMode.OwnAdapters)
            };
        });

        Assert.Equal(new[] { Shared.Id, Own.Id }.OrderBy(i => i), NetworkPlan.ConnectionsInUse(settings).OrderBy(i => i));
    }

    [Fact]
    public void ConnectionsInUse_NoApps_IsEmpty()
    {
        var settings = Settings(s =>
        {
            s.NetworkMode = GlobalNetworkMode.LauncherConnection;
            s.ConnectionId = Shared.Id;
        });

        Assert.Empty(NetworkPlan.ConnectionsInUse(settings));
    }
}

/// <summary>Домены-исключения: как бы их ни ввели, хранятся одинаково.</summary>
public class DomainListTests
{
    [Theory]
    [InlineData("discord.com", "discord.com")]
    [InlineData("  Discord.COM ", "discord.com")]
    [InlineData("https://discord.com/channels/@me", "discord.com")]
    [InlineData("*.discordapp.net", "discordapp.net")]
    [InlineData(".cdn.discordapp.com.", "cdn.discordapp.com")]
    [InlineData("user@media.discordapp.net:443", "media.discordapp.net")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("_dmarc.example.com", "_dmarc.example.com")]
    public void Valid(string input, string expected)
    {
        Assert.True(DomainList.TryNormalize(input, out var domain));
        Assert.Equal(expected, domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("192.168.0.1")]
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("a b.com")]
    [InlineData("a..b.com")]
    public void Invalid(string input)
    {
        Assert.False(DomainList.TryNormalize(input, out _));
    }

    [Fact]
    public void Normalize_DropsInvalidAndDuplicates_KeepsOrder()
    {
        var result = DomainList.Normalize(new[] { "B.com", "a.com", "b.com", "мусор", "*.a.com" });

        Assert.Equal(new[] { "b.com", "a.com" }, result);
    }
}
