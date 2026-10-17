using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Форма «Добавить подключение»: лаунчер сам понимает, что вставили.</summary>
public class ConnectionFactoryTests
{
    [Fact]
    public void Link_BecomesLinkConnection_NamedAfterServer()
    {
        var c = ConnectionFactory.FromText("  " + SampleConnections.VlessReality + "\n", Array.Empty<VpnConnection>());

        Assert.Equal(ConnectionKind.Link, c.Kind);
        Assert.Equal(SampleConnections.VlessReality, c.Link);
        Assert.Contains("Германия", c.Name);
    }

    [Fact]
    public void WireGuardText_BecomesWireGuard()
    {
        var c = ConnectionFactory.FromText(SampleConnections.WireGuard, Array.Empty<VpnConnection>());

        Assert.Equal(ConnectionKind.WireGuard, c.Kind);
        Assert.Equal("Финляндия", c.Name);
        Assert.Contains("[Peer]", c.WireGuardConfig);
    }

    [Fact]
    public void SubscriptionUrl_IsNotFetchedHere()
    {
        var c = ConnectionFactory.FromText("https://sub.example.com/api/v1/client/abc", Array.Empty<VpnConnection>());

        Assert.Equal(ConnectionKind.Subscription, c.Kind);
        Assert.Equal("Подписка sub.example.com", c.Name);
        Assert.Empty(c.SubscriptionServers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("привет")]
    [InlineData("ftp://example.com/file")]
    public void Garbage_ExplainsWhatFits(string text)
    {
        var ex = Assert.Throws<ConnectionParseException>(() => ConnectionFactory.FromText(text, Array.Empty<VpnConnection>()));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void SeveralLinks_AskForSubscription()
    {
        var text = SampleConnections.VlessReality + "\n" + SampleConnections.Trojan;
        var ex = Assert.Throws<ConnectionParseException>(() => ConnectionFactory.FromText(text, Array.Empty<VpnConnection>()));
        Assert.Contains("подписки", ex.Message);
    }

    [Fact]
    public void SameName_GetsNumber()
    {
        var existing = new[] { new VpnConnection { Name = "Trojan" }, new VpnConnection { Name = "Trojan 2" } };

        var c = ConnectionFactory.FromText(SampleConnections.Trojan, existing);

        Assert.Equal("Trojan 3", c.Name);
    }

    [Fact]
    public void SameConnection_Found_WhateverTheName()
    {
        var none = Array.Empty<VpnConnection>();
        var link = ConnectionFactory.FromText(SampleConnections.VlessReality, none);
        var wireGuard = ConnectionFactory.FromText(SampleConnections.WireGuard, none);
        var subscription = ConnectionFactory.FromText("https://sub.example.com/api/v1/client/abc", none);
        var proxy = ConnectionFactory.LocalProxy("127.0.0.1", "10808", ProxyProtocol.Socks5, none);
        var existing = new[] { link, wireGuard, subscription, proxy };

        var renamed = SampleConnections.VlessReality[..SampleConnections.VlessReality.IndexOf('#')] + "#Другое";
        Assert.Same(link, ConnectionFactory.FindSame(ConnectionFactory.FromText(renamed, existing), existing));
        Assert.Same(wireGuard, ConnectionFactory.FindSame(ConnectionFactory.FromText(SampleConnections.WireGuard, existing, "копия"), existing));
        Assert.Same(subscription, ConnectionFactory.FindSame(ConnectionFactory.FromText(" https://sub.example.com/api/v1/client/abc ", existing), existing));
        Assert.Same(proxy, ConnectionFactory.FindSame(ConnectionFactory.LocalProxy(" 127.0.0.1", "10808", ProxyProtocol.Socks5, existing), existing));

        Assert.Null(ConnectionFactory.FindSame(ConnectionFactory.FromText(SampleConnections.Trojan, existing), existing));
        Assert.Null(ConnectionFactory.FindSame(ConnectionFactory.FromText("https://sub.example.com/api/v1/client/xyz", existing), existing));
        Assert.Null(ConnectionFactory.FindSame(ConnectionFactory.LocalProxy("127.0.0.1", "10808", ProxyProtocol.Http, existing), existing));
        Assert.Null(ConnectionFactory.FindSame(link, existing)); // само с собой не сравнивается
    }

    [Fact]
    public async Task AddForm_SameConnection_NotAdded_SaysWhichIsThere()
    {
        var existing = new List<VpnConnection> { ConnectionFactory.FromText(SampleConnections.Trojan, Array.Empty<VpnConnection>()) };
        existing[0].Name = "Мой сервер";
        var added = 0;
        var form = new AddConnectionViewModel(() => existing, _ => { added++; return Task.CompletedTask; }, new FakeDialogs());
        form.Text = SampleConnections.Trojan.Replace("#Trojan", "#Другое название");

        Assert.Null(await form.AddAsync());
        Assert.Equal(0, added);
        Assert.Equal("Такое подключение уже есть: «Мой сервер».", form.Error);
    }

    [Fact]
    public void UniqueName_IgnoresItselfWhenRenaming()
    {
        var self = new VpnConnection { Name = "Дом" };
        Assert.Equal("Дом", ConnectionFactory.UniqueName("Дом", new[] { self }, self.Id));
        Assert.Equal("дом 2", ConnectionFactory.UniqueName("дом", new[] { self })); // занято без учёта регистра
        Assert.Equal("Подключение", ConnectionFactory.UniqueName("  ", Array.Empty<VpnConnection>()));
        Assert.Equal(60, ConnectionFactory.UniqueName(new string('я', 100), Array.Empty<VpnConnection>()).Length);
    }

    [Fact]
    public void LocalProxy_ChecksPort()
    {
        var c = ConnectionFactory.LocalProxy(" 127.0.0.1 ", "10808", ProxyProtocol.Socks5, Array.Empty<VpnConnection>());
        Assert.Equal(ConnectionKind.LocalProxy, c.Kind);
        Assert.Equal("127.0.0.1", c.ProxyHost);
        Assert.Equal(10808, c.ProxyPort);

        Assert.Throws<ConnectionParseException>(() => ConnectionFactory.LocalProxy("127.0.0.1", "", ProxyProtocol.Socks5, Array.Empty<VpnConnection>()));
        Assert.Throws<ConnectionParseException>(() => ConnectionFactory.LocalProxy("127.0.0.1", "70000", ProxyProtocol.Http, Array.Empty<VpnConnection>()));
        Assert.Throws<ConnectionParseException>(() => ConnectionFactory.LocalProxy("", "1080", ProxyProtocol.Http, Array.Empty<VpnConnection>()));
    }

    [Fact]
    public void LooksLikeConnection_OnlyForWhatFits()
    {
        Assert.True(ConnectionFactory.LooksLikeConnection(SampleConnections.Vmess));
        Assert.True(ConnectionFactory.LooksLikeConnection(SampleConnections.WireGuard));
        Assert.True(ConnectionFactory.LooksLikeConnection("https://sub.example.com/x"));
        Assert.False(ConnectionFactory.LooksLikeConnection("купить хлеба"));
        Assert.False(ConnectionFactory.LooksLikeConnection(null));
    }

    [Theory]
    [InlineData(1, "1 сервер")]
    [InlineData(3, "3 сервера")]
    [InlineData(5, "5 серверов")]
    [InlineData(12, "12 серверов")]
    [InlineData(21, "21 сервер")]
    [InlineData(112, "112 серверов")]
    public void ServersText_RussianPlural(int count, string expected) =>
        Assert.Equal(expected, ConnectionFactory.ServersText(count));

    [Fact]
    public void Describe_ShowsKind()
    {
        Assert.StartsWith("VLESS", ConnectionFactory.Describe(new VpnConnection { Kind = ConnectionKind.Link, Link = SampleConnections.VlessReality }));
        Assert.Equal("Подписка · ещё не загружена", ConnectionFactory.Describe(new VpnConnection { Kind = ConnectionKind.Subscription }));
        Assert.Contains("SOCKS5 127.0.0.1:2080", ConnectionFactory.Describe(new VpnConnection
        {
            Kind = ConnectionKind.LocalProxy, ProxyHost = "127.0.0.1", ProxyPort = 2080, ProxyProtocol = ProxyProtocol.Socks5
        }));
    }
}

/// <summary>Программы из меню «Пуск» в мастере первого запуска.</summary>
public class StartMenuAppsTests
{
    private const string Windows = @"C:\Windows";
    private static bool Exists(string _) => true;

    [Fact]
    public void Program_IsPicked()
    {
        var app = StartMenuApps.Pick("Telegram", @"C:\menu\Telegram.lnk", @"C:\Users\me\AppData\Roaming\Telegram Desktop\Telegram.exe", Windows, Exists);

        Assert.NotNull(app);
        Assert.Equal("Telegram", app!.Name);
    }

    [Theory]
    [InlineData("Uninstall Telegram")]
    [InlineData("Удалить Steam")]
    [InlineData("Справка")]
    [InlineData("Firefox (безопасный режим)")]
    public void ServiceShortcuts_AreSkipped(string name) =>
        Assert.Null(StartMenuApps.Pick(name, @"C:\menu\x.lnk", @"C:\Program Files\X\x.exe", Windows, Exists));

    [Fact]
    public void DiscordUpdateExe_IsKept_BecauseOnlyTheNameIsChecked() =>
        Assert.NotNull(StartMenuApps.Pick("Discord", @"C:\menu\Discord.lnk", @"C:\Users\me\AppData\Local\Discord\Update.exe", Windows, Exists));

    [Fact]
    public void WindowsTools_DocumentsAndMissingFiles_AreSkipped()
    {
        Assert.Null(StartMenuApps.Pick("Блокнот", @"C:\menu\n.lnk", @"C:\Windows\System32\notepad.exe", Windows, Exists));
        Assert.Null(StartMenuApps.Pick("Руководство", @"C:\menu\m.lnk", @"C:\Program Files\X\manual.pdf", Windows, Exists));
        Assert.Null(StartMenuApps.Pick("Старое", @"C:\menu\o.lnk", @"C:\Program Files\Old\old.exe", Windows, _ => false));
        Assert.Null(StartMenuApps.Pick("Пусто", @"C:\menu\e.lnk", "", Windows, Exists));
    }

    [Fact]
    public void SameProgramInBothMenus_ShownOnce_Alphabetically()
    {
        var list = StartMenuApps.Distinct(new[]
        {
            new StartMenuApp("Telegram", @"C:\all\Telegram.lnk", @"C:\T\Telegram.exe"),
            new StartMenuApp("Discord", @"C:\me\Discord.lnk", @"C:\D\Update.exe"),
            new StartMenuApp("Telegram Desktop", @"C:\me\Telegram Desktop.lnk", @"c:\t\telegram.exe")
        });

        Assert.Equal(new[] { "Discord", "Telegram" }, list.Select(a => a.Name));
    }
}

public class LocalProxiesTests
{
    [Fact]
    public void KnownProgram_SuggestsItsPort()
    {
        var hint = LocalProxies.Detect(new[] { "explorer", "v2rayN", "chrome" });

        Assert.NotNull(hint);
        Assert.Equal("v2rayN", hint!.Program);
        Assert.Equal(10808, hint.Port);
        Assert.Equal(ProxyProtocol.Socks5, hint.Protocol);
    }

    [Fact]
    public void ProcessNames_AreCaseInsensitive() =>
        Assert.Equal(7897, LocalProxies.Detect(new[] { "VERGE-MIHOMO" })!.Port);

    [Fact]
    public void NothingKnown_NoHint() =>
        Assert.Null(LocalProxies.Detect(new[] { "explorer", "chrome" }));
}

/// <summary>Мастер первого запуска показывается только новым пользователям.</summary>
public class WelcomeSettingsTests
{
    [Fact]
    public void NewInstall_ShowsWelcome()
    {
        using var dir = new TempDir();
        Assert.False(new SettingsStorage(dir.File("settings.json")).Load().WelcomeDone);
    }

    [Fact]
    public void OldSettings_SkipWelcome()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ \"KillSwitchEnabled\": true, \"Apps\": [] }");

        Assert.True(new SettingsStorage(path).Load().WelcomeDone);
    }

    [Fact]
    public void WelcomeDone_IsSaved()
    {
        using var dir = new TempDir();
        var storage = new SettingsStorage(dir.File("settings.json"));
        var settings = storage.Load();
        settings.WelcomeDone = true;
        storage.Save(settings);

        Assert.True(storage.Load().WelcomeDone);
    }
}
