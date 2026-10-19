using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private IReadOnlyList<StartMenuApp> _startMenu = Array.Empty<StartMenuApp>();

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
                new UpdateService(_dir.File("update-pending.json"), _dir.File("download")), guardBackend: QuietGuard.Create, systemProxy: () => null,
                startMenuApps: () => Task.FromResult(_startMenu));
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

    // ---------------------------------------------------------------- «Добавить приложение»

    [Fact]
    public void AddApp_ShowsStartMenu_AddedOnesCheckedAndLocked()
    {
        var telegram = FakeExe("Telegram");
        var discord = FakeExe("Discord");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Мой Telegram", ExePath = telegram });
        });
        _startMenu = new[] { new StartMenuApp("Discord", discord, discord), new StartMenuApp("Telegram", telegram, telegram.ToUpperInvariant()) };

        WithViewModel(vm =>
        {
            vm.AddAppCommand.Execute(null);

            Assert.True(vm.IsAddAppsPage);
            var list = vm.AddApps!.List;
            var added = list.Apps.Single(a => a.Name == "Telegram");
            Assert.True(added.IsAdded);
            Assert.True(added.IsSelected);
            Assert.False(added.CanSelect);
            added.IsSelected = false;
            Assert.True(added.IsSelected); // убрать можно только в меню плитки

            Assert.False(list.Apps.Single(a => a.Name == "Discord").IsSelected);
            Assert.Equal(0, list.SelectedCount);
            Assert.False(vm.AddApps.AddCommand.CanExecute(null));
        });
    }

    [Fact]
    public void AddApp_CheckedPrograms_AddedAtOnce()
    {
        var discord = FakeExe("Discord");
        var slack = FakeExe("Slack");
        Preset(s => s.WelcomeDone = true);
        _startMenu = new[] { new StartMenuApp("Discord", discord, discord), new StartMenuApp("Slack", slack, slack) };

        WithViewModel(vm =>
        {
            vm.AddAppCommand.Execute(null);
            vm.AddApps!.List.Apps[0].IsSelected = true;
            Assert.Equal("Добавить (1)", vm.AddApps.AddText);

            vm.AddApps.AddCommand.Execute(null);

            Assert.True(vm.IsAppsPage);
            Assert.Null(vm.AddApps);
            Assert.Equal("Discord", Assert.Single(vm.Apps).Name);
            Assert.Equal(0, _dialogs.AskSaveCalls);
        });

        Assert.Single(Storage.Load().Apps);
    }

    [Fact]
    public void AddApp_LeavingWithCheckedPrograms_AsksFirst()
    {
        var discord = FakeExe("Discord");
        Preset(s => s.WelcomeDone = true);
        _startMenu = new[] { new StartMenuApp("Discord", discord, discord) };
        _dialogs.SaveAnswer = null;

        WithViewModel(vm =>
        {
            vm.AddAppCommand.Execute(null);
            Assert.True(vm.GoBack()); // ничего не отмечено: без вопроса
            Assert.Equal(0, _dialogs.AskSaveCalls);

            vm.AddAppCommand.Execute(null);
            vm.AddApps!.List.Apps[0].IsSelected = true;

            Assert.False(vm.GoBack()); // «остаться»
            Assert.True(vm.IsAddAppsPage);

            _dialogs.SaveAnswer = false;
            Assert.True(vm.GoBack());
            Assert.Empty(vm.Apps);
            Assert.Null(vm.AddApps);
        });
    }

    [Fact]
    public void AddApp_ChooseInExplorer_OneProgram_OpensItsPage()
    {
        Preset(s => s.WelcomeDone = true);
        _dialogs.PickedProgram = FakeExe("Tool");

        WithViewModel(vm =>
        {
            vm.AddAppCommand.Execute(null);
            vm.AddApps!.BrowseCommand.Execute(null);

            Assert.True(vm.IsAppPage);
            Assert.True(vm.AppPage!.IsNew);
            Assert.Equal("Tool", vm.AppPage.Name);
            Assert.Null(vm.AddApps);
        });
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
    public void SameProgram_WithOtherArguments_IsNotAdded_SaysWhichIsThere()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Tool Work", ExePath = exe, Arguments = "--profile=Work" });
        });

        WithViewModel(vm =>
        {
            vm.AddFromPaths(new[] { exe });

            Assert.True(vm.IsAppsPage);
            Assert.Single(vm.Apps);
            Assert.Contains("Эта программа уже добавлена: «Tool Work». Одну программу можно добавить только один раз", Assert.Single(_dialogs.Warnings));
        });
    }

    [Fact]
    public void ProgramAddedTwiceBefore_IsKept_AndLauncherWarns()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.Apps.Add(new AppEntry { Name = "Tool Work", ExePath = exe, Arguments = "--profile=Work" });
            s.Apps.Add(new AppEntry { Name = "Tool Home", ExePath = exe.ToUpperInvariant(), Arguments = "--profile=Home" });
            s.Apps.Add(new AppEntry { Name = "Other", ExePath = FakeExe("Other") });
        });

        WithViewModel(vm => Assert.Equal(3, vm.Apps.Count));

        var warning = Assert.Single(new ActivityLog(_dir.File("events.log")).Snapshot(), e => e.Text.Contains("добавлена несколько раз"));
        Assert.Contains("«Tool Work», «Tool Home»", warning.Text);
        Assert.DoesNotContain("Other", warning.Text);
        Assert.Equal(3, Storage.Load().Apps.Count);
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
            page.CloseOnDrop = ProtectionOverride.On;
            page.OutsideCopies = OutsideCopiesOverride.Block;
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
        Assert.Equal(ProtectionOverride.On, app.Network.CloseOnDrop);
        Assert.Equal(OutsideCopiesOverride.Block, app.Network.OutsideCopies);
        Assert.Equal(2, app.Network.DirectDomains.Count);
    }

    [Fact]
    public void AppPage_Offline_HidesProtection_AndIsSaved()
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
            Assert.False(page.IsOffline);

            page.Mode = AppNetworkMode.Offline;
            Assert.True(page.IsOffline);
            Assert.False(page.UsesLauncherCore);
            page.SaveCommand.Execute(null);

            Assert.Null(page.Error);
            Assert.True(vm.Apps[0].IsOfflineMode);
            Assert.True(vm.Apps[0].IsOffline);
            Assert.Equal("Без сети", vm.Apps[0].Subtitle);
        });

        Assert.Equal(AppNetworkMode.Offline, Storage.Load().Apps[0].Network.Mode);
    }

    [Fact]
    public void AppPage_DomainsMode_ShowsCommonAndIsSaved()
    {
        var exe = FakeExe("Tool");
        Preset(s =>
        {
            s.WelcomeDone = true;
            s.NetworkMode = GlobalNetworkMode.LauncherConnection;
            var connection = new VpnConnection { Name = "Германия", Kind = ConnectionKind.Link, Link = "trojan://secret@5.6.7.8:443#A" };
            s.Connections.Add(connection);
            s.ConnectionId = connection.Id;
            s.DirectDomains = new List<string> { "kaspi.kz", "gosuslugi.ru" };
            s.Apps.Add(new AppEntry { Name = "Tool", ExePath = exe });
        });

        WithViewModel(vm =>
        {
            vm.Apps[0].OpenSettingsCommand.Execute(null);
            var page = vm.AppPage!;
            Assert.Equal(DomainsMode.AddToCommon, page.DomainsMode);
            Assert.Equal("Напрямую идут и общие исключения (2): kaspi.kz, gosuslugi.ru.", page.CommonDomainsHint);

            page.DomainsMode = DomainsMode.OwnOnly;
            Assert.True(page.HasChanges);
            Assert.StartsWith("Общие исключения (2) не действуют", page.CommonDomainsHint);
            page.SaveCommand.Execute(null);

            Assert.Null(page.Error);
        });

        Assert.Equal(DomainsMode.OwnOnly, Storage.Load().Apps[0].Network.DomainsMode);
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
