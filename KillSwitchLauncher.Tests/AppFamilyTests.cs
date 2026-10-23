using System;
using System.IO;
using KillSwitchLauncher.Models;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// «Семейство» приложения: какие процессы лаунчер считает своими, даже если их запустил не он
/// (Discord из автозагрузки, перезапуск после обновления). Раскладка папок как у Discord (Squirrel).
/// </summary>
public class AppFamilyTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _root;

    public AppFamilyTests()
    {
        _root = _dir.File("Discord");
        Directory.CreateDirectory(Path.Combine(_root, "app-1.0.9"));
        Directory.CreateDirectory(Path.Combine(_root, "app-1.0.10"));
        Directory.CreateDirectory(Path.Combine(_root, "packages"));
        File.WriteAllText(Path.Combine(_root, "Update.exe"), "");
        File.WriteAllText(Path.Combine(_root, "app-1.0.9", "Discord.exe"), "");
        File.WriteAllText(Path.Combine(_root, "app-1.0.10", "Discord.exe"), "");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SquirrelShortcut_KnowsTheRealProgram()
    {
        // Ярлык Discord: Update.exe --processStart Discord.exe
        var family = AppFamily.For(new AppEntry { ExePath = Path.Combine(_root, "Update.exe"), Arguments = "--processStart Discord.exe" });

        Assert.NotNull(family);
        Assert.True(family!.HasName("Discord.exe"));
        Assert.True(family.HasName("update.exe"));
        Assert.False(family.HasName("Telegram.exe"));
        Assert.True(family.ContainsPath(Path.Combine(_root, "app-1.0.10", "Discord.exe")));
    }

    [Fact]
    public void ExeInsideVersionFolder_CoversAllVersions()
    {
        var family = AppFamily.For(new AppEntry { ExePath = Path.Combine(_root, "app-1.0.9", "Discord.exe") });

        Assert.NotNull(family);
        Assert.True(family!.ContainsPath(Path.Combine(_root, "app-1.0.10", "Discord.exe")));
        Assert.False(family.ContainsPath(_dir.File("Другая программа", "Discord.exe")));
    }

    [Fact]
    public void FindCurrentSquirrelExe_AfterUpdate_FindsNewestVersion()
    {
        // Старая папка удалена обновлением; 1.0.10 новее 1.0.9, хотя по алфавиту наоборот.
        var oldPath = Path.Combine(_root, "app-1.0.8", "Discord.exe");

        Assert.Equal(Path.Combine(_root, "app-1.0.10", "Discord.exe"), AppFamily.FindCurrentSquirrelExe(oldPath));
    }

    [Fact]
    public void FindCurrentSquirrelExe_NotSquirrel_IsNull()
    {
        File.Delete(Path.Combine(_root, "Update.exe"));
        Assert.Null(AppFamily.FindCurrentSquirrelExe(Path.Combine(_root, "app-1.0.8", "Discord.exe")));
        Assert.Null(AppFamily.FindCurrentSquirrelExe(_dir.File("app.exe")));
    }

    [Fact]
    public void OrdinaryApp_IsRecognisedByNameAndFolder()
    {
        var exe = _dir.File("Telegram Desktop", "Telegram.exe");
        var family = AppFamily.For(new AppEntry { ExePath = exe });

        Assert.NotNull(family);
        Assert.True(family!.HasName("Telegram.exe"));
        Assert.True(family.ContainsPath(exe));
        Assert.False(family.ContainsPath(_dir.File("Telegram Desktop Copy", "Telegram.exe")));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("python.exe")]
    [InlineData("javaw.exe")]
    public void GenericHost_HasNoFamily(string exe) =>
        // По имени «движка» нельзя понять, какое приложение в нём работает.
        Assert.Null(AppFamily.For(new AppEntry { ExePath = _dir.File("Tools", exe) }));

    [Fact]
    public void ProgramInSharedFolder_HasNoFamily()
    {
        // Из общей папки (Temp, Загрузки, Windows…) чужие процессы не подбираем.
        Assert.Null(AppFamily.For(new AppEntry { ExePath = Path.Combine(Path.GetTempPath(), "setup.exe") }));
        Assert.Null(AppFamily.For(new AppEntry { ExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe") }));
        Assert.Null(AppFamily.For(new AppEntry { ExePath = Path.Combine(Path.GetPathRoot(_dir.Root)!, "app.exe") }));
    }

    [Fact]
    public void ForImage_StoreAppProcess()
    {
        var image = _dir.File("WindowsApps", "Telegram_5.0_x64", "Telegram.exe");
        var family = AppFamily.ForImage(image);

        Assert.NotNull(family);
        Assert.True(family!.HasName("Telegram.exe"));
        Assert.True(family.ContainsPath(image));
    }

    [Fact]
    public void ForImage_StoreApp_AnyVersionOfItsPackage()
    {
        // Приложение Store, обновляясь, переезжает в папку новой версии пакета и перезапускается оттуда.
        var image = _dir.File("WindowsApps", "Claude_1.0.100.0_x64__pzs8sxrjxfjjc", "app", "claude.exe");
        var family = AppFamily.ForImage(image)!;

        Assert.True(family.ContainsPath(_dir.File("WindowsApps", "Claude_1.0.200.0_x64__pzs8sxrjxfjjc", "app", "claude.exe")));
        Assert.True(family.ContainsPath(_dir.File("WindowsApps", "claude_1.1.0.0_x64__PZS8SXRJXFJJC", "app", "resources", "claude.exe")));
        Assert.False(family.ContainsPath(_dir.File("WindowsApps", "Claude_1.0.200.0_x64__aaaaaaaaaaaaa", "app", "claude.exe")), "другой издатель");
        Assert.False(family.ContainsPath(_dir.File("WindowsApps", "ClaudeX_1.0.200.0_x64__pzs8sxrjxfjjc", "app", "claude.exe")), "другой пакет");
        Assert.False(family.ContainsPath(_dir.File("WindowsApps", "Claude_1.0.200.0_x64__pzs8sxrjxfjjc", "other", "claude.exe")), "другая папка пакета");
        Assert.True(family.Overlaps(AppFamily.ForImage(_dir.File("WindowsApps", "Claude_1.0.200.0_x64__pzs8sxrjxfjjc", "app", "claude.exe"))!));
    }
}
