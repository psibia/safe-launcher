using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using KillSwitchLauncher.ViewModels;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Страницы главного окна: мастер первого запуска, уход со страницы с несохранённым, добавление приложения
/// через его страницу (выбор файла и перетаскивание) и сама страница приложения.
/// </summary>
public class PagesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeDialogs _dialogs = new();

    public void Dispose() => _dir.Dispose();

    private SettingsStorage Storage => new(_dir.File("settings.json"));

    private void Preset(Action<AppSettings> change)
    {
        var settings = Storage.Load();
        change(settings);
        Storage.Save(settings);
    }

    private void WithViewModel(Action<MainViewModel> test)
    {
        Sta.Run(() =>
        {
            var vm = new MainViewModel(Storage, _dialogs, Dispatcher.CurrentDispatcher, new ActivityLog(_dir.File("events.log")),
                new UpdateService(_dir.File("update-pending.json"), _dir.File("download")), guardBackend: QuietGuard.Create, systemProxy: () => null);
            try
            {
                test(vm);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    /// <summary>«Программа» для добавления: файл .exe во временной папке.</summary>
    private string FakeExe(string name)
    {
        var path = _dir.File(name + ".exe");
        File.WriteAllText(path, "MZ");
        return path;
    }

    // ---------------------------------------------------------------- мастер первого запуска

    [Fact]
    public void NewUser_SeesWelcome_OnlyOnce()
    {
        WithViewModel(vm =>
        {
            Assert.True(vm.IsWelcomePage);
            Assert.NotNull(vm.Welcome);
            Assert.Equal(1, vm.Welcome!.Step);

            vm.Welcome.SkipCommand.Execute(null);

            Assert.True(vm.IsAppsPage);
            Assert.Null(vm.Welcome);
        });

        Assert.True(Storage.Load().WelcomeDone);
        WithViewModel(vm => Assert.True(vm.IsAppsPage));
    }

    [Fact]
    public void ExistingUser_GoesStraightToApps()
    {
        Preset(s => s.Apps.Add(new AppEntry { Name = "Discord", ExePath = @"C:\Apps\Discord.exe" }));

        WithViewModel(vm =>
        {
            Assert.True(vm.IsAppsPage);
            Assert.Null(vm.Welcome);
        });
    }

    [Fact]
    public void Welcome_HaveVpnProgram_ChoosesSystemVpn_AndGoesToApps()
    {
        Preset(s => s.NetworkMode = GlobalNetworkMode.LauncherConnection);

        WithViewModel(vm =>
        {
            vm.Welcome!.UseSystemVpnCommand.Execute(null);

            Assert.Equal(3, vm.Welcome.Step);
            Assert.Equal(GlobalNetworkMode.SystemVpn, Storage.Load().NetworkMode);
        });
    }

    [Fact]
    public void Welcome_OffersConnectionFromClipboard()
    {
        _dialogs.ClipboardText = SampleConnections.Trojan;

        WithViewModel(vm =>
        {
            Assert.Equal(SampleConnections.Trojan, vm.Welcome!.AddConnection.Text);
            Assert.NotNull(vm.Welcome.AddConnection.Recognized);
        });
    }

    // ---------------------------------------------------------------- страница настроек

    [Fact]
    public void Settings_LeavingWithChanges_StayKeepsThem()
    {
        Preset(s => s.WelcomeDone = true);
        _dialogs.SaveAnswer = null;

        WithViewModel(vm =>
        {
            vm.OpenSettings(SettingsSection.Window);
            vm.HideBehavior = HideBehavior.Exit;

            Assert.False(vm.GoBack());

            Assert.True(vm.IsSettingsOpen);
            Assert.True(vm.HasPendingChanges);
            Assert.Equal(1, _dialogs.AskSaveCalls);
        });

        Assert.Equal(HideBehavior.Ask, Storage.Load().HideBehavior);
    }

    [Fact]
    public void Settings_LeavingWithChanges_SaveApplies()
    {
        Preset(s => s.WelcomeDone = true);
        _dialogs.SaveAnswer = true;

        WithViewModel(vm =>
        {
            vm.OpenSettings();
            vm.HideBehavior = HideBehavior.Tray;

            Assert.True(vm.GoBack());
            Assert.True(vm.IsAppsPage);
        });

        Assert.Equal(HideBehavior.Tray, Storage.Load().HideBehavior);
    }

    [Fact]
    public void Settings_WithoutChanges_LeaveWithoutQuestion()
    {
        Preset(s => s.WelcomeDone = true);

        WithViewModel(vm =>
        {
            vm.OpenSettings();
            Assert.True(vm.GoBack());
            Assert.Equal(0, _dialogs.AskSaveCalls);
        });
    }

    [Fact]
    public void Settings_LauncherConnectionWithoutConnection_IsNotApplied()
    {
        Preset(s => s.WelcomeDone = true);

        WithViewModel(vm =>
        {
            vm.OpenSettings(SettingsSection.Connection);
            vm.NetworkMode = GlobalNetworkMode.LauncherConnection;
            Assert.True(vm.NeedsConnectionSelection);

            vm.ApplySettingsCommand.Execute(null);

            Assert.True(vm.HasPendingChanges);
        });

        Assert.Equal(GlobalNetworkMode.SystemVpn, Storage.Load().NetworkMode);
    }

    // ---------------------------------------------------------------- добавление приложения

    [Fact]
    public void OneProgram_OpensItsPage_AndAddsOnlyOnSave()
    {
        Preset(s => s.WelcomeDone = true);
        var exe = FakeExe("Tool");

        WithViewModel(vm =>
        {
            vm.AddFromPaths(new[] { exe });

            Assert.True(vm.IsAppPage);
            Assert.True(vm.AppPage!.IsNew);
            Assert.Equal("Добавить", vm.AppPage.SaveText);
            Assert.Empty(vm.Apps);

            vm.AppPage.SaveCommand.Execute(null);

            Assert.True(vm.IsAppsPage);
            Assert.Equal("Tool", Assert.Single(vm.Apps).Name);
        });

        Assert.Single(Storage.Load().Apps);
    }

    [Fact]
    public void NewAppPage_LeftWithoutSaving_AddsNothing()
    {
        Preset(s => s.WelcomeDone = true);
        _dialogs.SaveAnswer = false;
        var exe = FakeExe("Tool");

        WithViewModel(vm =>
        {
            vm.AddFromPaths(new[] { exe });
            Assert.True(vm.GoBack());

            Assert.Equal(1, _dialogs.AskSaveCalls); // новое приложение — всегда спрашиваем
            Assert.Empty(vm.Apps);
            Assert.Null(vm.AppPage);
        });

        Assert.Empty(Storage.Load().Apps);
    }

    [Fact]
    public void SameProgramTwice_IsNotAdded()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Tool", ExePath = exe });
        });

        WithViewModel(vm =>
        {
            vm.AddFromPaths(new[] { exe });

            Assert.True(vm.IsAppsPage);
            Assert.Single(vm.Apps);
        });
    }

    [Fact]
    public void SeveralPrograms_AreAddedAtOnce_OthersIgnored()
    {
        Preset(s => s.WelcomeDone = true);
        var a = FakeExe("Alpha");
        var b = FakeExe("Beta");
        var text = _dir.File("readme.txt");
        File.WriteAllText(text, "");

        WithViewModel(vm =>
        {
            vm.AddFromPaths(new[] { a, b, text });

            Assert.True(vm.IsAppsPage);
            Assert.Equal(new[] { "Alpha", "Beta" }, vm.Apps.Select(t => t.Name));
        });

        Assert.Equal(2, Storage.Load().Apps.Count);
    }

    // ---------------------------------------------------------------- страница приложения

    [Fact]
    public void AppPage_OwnConnectionWithoutConnections_IsNotSaved()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Tool", ExePath = exe });
        });

        WithViewModel(vm =>
        {
            vm.Apps[0].OpenSettingsCommand.Execute(null);
            var page = vm.AppPage!;
            Assert.False(page.IsNew);
            Assert.False(page.HasChanges);
            Assert.True(page.IsInherit);

            page.Mode = AppNetworkMode.OwnConnection;
            Assert.True(page.IsAddTab); // подключений нет — сразу «Добавить подключение»
            page.SaveCommand.Execute(null);

            Assert.NotNull(page.Error);
            Assert.True(vm.IsAppPage);
        });

        Assert.Equal(AppNetworkMode.Inherit, Storage.Load().Apps[0].Network.Mode);
    }

    [Fact]
    public void AppPage_Save_WritesConditionProtectionAndExceptions()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Tool", ExePath = exe });
        });

        WithViewModel(vm =>
        {
            vm.Apps[0].OpenSettingsCommand.Execute(null);
            var page = vm.AppPage!;
            page.Name = "Мой Tool";
            page.Mode = AppNetworkMode.OwnAdapters;
            page.RuleMode = VpnRuleMode.AnyVpn;
            page.RequireVpn = ProtectionOverride.On;
            page.DomainsText = "kaspi.kz\nhttps://www.Gosuslugi.ru/help";
            Assert.True(page.HasChanges);

            page.SaveCommand.Execute(null);

            Assert.Null(page.Error);
            Assert.True(vm.IsAppsPage);
            Assert.Equal("Мой Tool", vm.Apps[0].Name);
        });

        var app = Storage.Load().Apps[0];
        Assert.Equal("Мой Tool", app.Name);
        Assert.Equal(AppNetworkMode.OwnAdapters, app.Network.Mode);
        Assert.Equal(ProtectionOverride.On, app.Network.RequireVpn);
        Assert.Equal(2, app.Network.DirectDomains.Count);
    }

    [Fact]
    public void AppPage_WrongDomain_Explains()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Tool", ExePath = exe });
        });

        WithViewModel(vm =>
        {
            vm.Apps[0].OpenSettingsCommand.Execute(null);
            vm.AppPage!.DomainsText = "не домен!";

            vm.AppPage.SaveCommand.Execute(null);

            Assert.Contains("не похоже на домен", vm.AppPage.Error);
        });
    }

    [Fact]
    public void AppPage_OwnAdaptersWithoutAdapter_IsNotSaved()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Tool", ExePath = exe });
        });

        WithViewModel(vm =>
        {
            vm.Apps[0].OpenSettingsCommand.Execute(null);
            var page = vm.AppPage!;
            page.Mode = AppNetworkMode.OwnAdapters;
            page.RuleMode = VpnRuleMode.AnyOfSelected;
            foreach (var adapter in page.Adapters)
                adapter.IsSelected = false;

            Assert.True(page.NeedsAdapterSelection);
            page.SaveCommand.Execute(null);
            Assert.NotNull(page.Error);
        });
    }
}
