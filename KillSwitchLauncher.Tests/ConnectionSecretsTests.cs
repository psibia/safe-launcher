using System;
using System.Collections.Generic;
using System.IO;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Ключи подключений в settings.json зашифрованы средствами Windows и читаются обратно.</summary>
public class ConnectionSecretsTests
{
    private const string Link = "vless://0b5c2e14-8f3a-4c1d-9e2b-7a6d5c4b3a21@vpn.example.com:443?security=reality&sni=www.microsoft.com#Нидерланды";
    private const string Subscription = "https://sub.example.com/api/v1/client/subscribe?token=SECRET123";
    private const string WireGuard = "[Interface]\nPrivateKey = cGFzc3dvcmRwYXNzd29yZHBhc3N3b3JkcGFzc3dvcmQ=\nAddress = 10.0.0.2/32\n";

    [Fact]
    public void Secrets_AreEncryptedInFile_AndReadBack()
    {
        using var dir = new TempDir();
        var storage = new SettingsStorage(dir.File("settings.json"));
        var link = new VpnConnection { Name = "Сервер", Kind = ConnectionKind.Link, Link = Link };
        var subscription = new VpnConnection
        {
            Name = "Подписка", Kind = ConnectionKind.Subscription, SubscriptionUrl = Subscription,
            SubscriptionServers = new List<string> { Link, "trojan://pass@b.example.com:443#B" },
            SelectedServer = "trojan://pass@b.example.com:443",
            SubscriptionUpdatedAt = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero)
        };
        var wireGuard = new VpnConnection { Name = "WG", Kind = ConnectionKind.WireGuard, WireGuardConfig = WireGuard };
        storage.Save(new AppSettings { Connections = new List<VpnConnection> { link, subscription, wireGuard } });

        var json = File.ReadAllText(storage.FilePath);
        Assert.DoesNotContain("0b5c2e14", json);
        Assert.DoesNotContain("SECRET123", json);
        Assert.DoesNotContain("PrivateKey", json);
        Assert.DoesNotContain("pass@", json);
        Assert.Contains(ProtectedText.Prefix, json);
        Assert.Contains("\"Name\": \"Подписка\"", json); // названия не секрет

        var loaded = storage.Load().Connections;
        Assert.Equal(Link, loaded[0].Link);
        Assert.Equal(Subscription, loaded[1].SubscriptionUrl);
        Assert.Equal(new[] { Link, "trojan://pass@b.example.com:443#B" }, loaded[1].SubscriptionServers);
        Assert.Equal("trojan://pass@b.example.com:443", loaded[1].SelectedServer);
        Assert.Equal(subscription.SubscriptionUpdatedAt, loaded[1].SubscriptionUpdatedAt);
        Assert.Equal(WireGuard, loaded[2].WireGuardConfig);
        Assert.Null(loaded[0].WireGuardConfig);
    }

    [Fact]
    public void PlainValue_WrittenByHand_IsAccepted()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, $$"""
            { "SettingsVersion": 1, "Connections": [ { "Name": "Вручную", "Kind": "Link", "Link": "{{Link}}", "SubscriptionServers": [ "ss://abc@c.example.com:8388" ] } ] }
            """);

        var connection = Assert.Single(new SettingsStorage(path).Load().Connections);

        Assert.Equal(Link, connection.Link);
        Assert.Equal(new[] { "ss://abc@c.example.com:8388" }, connection.SubscriptionServers);
    }

    [Fact]
    public void UndecryptableValue_BecomesEmpty_RestOfSettingsSurvive()
    {
        // Например, файл перенесли с другого компьютера: ключ не расшифровать, но остальные настройки целы.
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """
            { "SettingsVersion": 1, "KillSwitchEnabled": false,
              "Connections": [ { "Name": "Чужой", "Kind": "Link", "Link": "dpapi:AQIDBAUG", "SubscriptionServers": [ "dpapi:бита" ] } ] }
            """);

        var settings = new SettingsStorage(path).Load();

        Assert.False(settings.KillSwitchEnabled);
        var connection = Assert.Single(settings.Connections);
        Assert.Equal("Чужой", connection.Name);
        Assert.Null(connection.Link);
        Assert.Empty(connection.SubscriptionServers);
        Assert.False(File.Exists(path + ".broken"));
    }

    [Fact]
    public void Protect_RoundTrips_WithFreshCipherEachTime()
    {
        var protectedText = ProtectedText.Protect("секрет");

        Assert.StartsWith(ProtectedText.Prefix, protectedText);
        Assert.DoesNotContain("секрет", protectedText);
        Assert.NotEqual(ProtectedText.Protect("секрет"), protectedText); // каждый раз новый шифр
        Assert.Equal("секрет", ProtectedText.Unprotect(protectedText));
    }
}
