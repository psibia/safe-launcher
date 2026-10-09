using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>settings.json: сохранение и загрузка всех настроек, повреждённый и отсутствующий файл.</summary>
public class SettingsStorageTests
{
    [Fact]
    public void MissingFile_GivesDefaults()
    {
        using var dir = new TempDir();
        var settings = new SettingsStorage(dir.File("settings.json")).Load();

        Assert.True(settings.KillSwitchEnabled);
        Assert.Equal(VpnRuleMode.AnyVpn, settings.RuleMode);
        Assert.Equal(HideBehavior.Ask, settings.HideBehavior);
        Assert.False(settings.TrayHintShown);
        Assert.Empty(settings.Apps);
        Assert.Empty(settings.SelectedAdapters);
    }

    [Fact]
    public void SaveAndLoad_KeepsEverything()
    {
        using var dir = new TempDir();
        var storage = new SettingsStorage(dir.File("sub", "settings.json"));
        var id = Guid.NewGuid();
        storage.Save(new AppSettings
        {
            KillSwitchEnabled = false,
            RuleMode = VpnRuleMode.AllOfSelected,
            SelectedAdapters = new List<string> { "WireGuard", "Рабочий VPN" },
            HideBehavior = HideBehavior.Tray,
            TrayHintShown = true,
            KnownAdapters = new List<KnownAdapter> { new() { Name = "WireGuard", Description = "WireGuard Tunnel", Product = "WireGuard", IsVpnLike = true } },
            Apps = new List<AppEntry>
            {
                new()
                {
                    Id = id, Name = "Discord", ExePath = @"C:\Users\u\AppData\Local\Discord\Update.exe",
                    Arguments = "--processStart Discord.exe", WorkingDirectory = @"C:\Users\u\AppData\Local\Discord",
                    SourcePath = @"C:\Users\u\Desktop\Discord.lnk"
                },
                new() { Name = "Telegram", AppUserModelId = "TelegramMessengerLLP.TelegramDesktop_t4vj0pshhgkwm!Telegram" }
            }
        });

        var loaded = storage.Load();

        Assert.False(loaded.KillSwitchEnabled);
        Assert.Equal(VpnRuleMode.AllOfSelected, loaded.RuleMode);
        Assert.Equal(new[] { "WireGuard", "Рабочий VPN" }, loaded.SelectedAdapters);
        Assert.Equal(HideBehavior.Tray, loaded.HideBehavior);
        Assert.True(loaded.TrayHintShown);
        Assert.Equal("WireGuard Tunnel", loaded.KnownAdapters.Single().Description);

        Assert.Equal(2, loaded.Apps.Count);
        var discord = loaded.Apps[0];
        Assert.Equal(id, discord.Id);
        Assert.Equal("--processStart Discord.exe", discord.Arguments);
        Assert.Equal(@"C:\Users\u\Desktop\Discord.lnk", discord.ShortcutPath);
        Assert.False(discord.IsPackaged);

        var telegram = loaded.Apps[1];
        Assert.True(telegram.IsPackaged);
        Assert.Null(telegram.ShortcutPath);
    }

    [Fact]
    public void Enums_AreStoredAsReadableNames()
    {
        using var dir = new TempDir();
        var storage = new SettingsStorage(dir.File("settings.json"));
        storage.Save(new AppSettings { HideBehavior = HideBehavior.Exit, RuleMode = VpnRuleMode.AnyOfSelected });

        var json = File.ReadAllText(storage.FilePath);

        Assert.Contains("\"HideBehavior\": \"Exit\"", json);
        Assert.Contains("\"RuleMode\": \"AnyOfSelected\"", json);
        Assert.False(File.Exists(storage.FilePath + ".tmp"));
    }

    [Fact]
    public void OldSettingsWithoutNewFields_LoadWithDefaults()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ \"KillSwitchEnabled\": false, \"RuleMode\": \"AnyVpn\", \"Apps\": [] }");

        var settings = new SettingsStorage(path).Load();

        Assert.False(settings.KillSwitchEnabled);
        Assert.Equal(HideBehavior.Ask, settings.HideBehavior);
        Assert.False(settings.TrayHintShown);
    }

    [Fact]
    public void BrokenFile_GivesDefaults_AndKeepsACopy()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ это не JSON");

        var settings = new SettingsStorage(path).Load();

        Assert.True(settings.KillSwitchEnabled);
        Assert.Equal(HideBehavior.Ask, settings.HideBehavior);
        Assert.Equal("{ это не JSON", File.ReadAllText(path + ".broken"));
    }
}
