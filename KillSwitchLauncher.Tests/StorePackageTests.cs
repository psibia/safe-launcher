using System.Collections.Generic;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Папка пакета Microsoft Store: приложение узнаётся в любой версии пакета (обновление переносит его в новую папку).</summary>
public class StorePackageTests
{
    private const string App = @"C:\Program Files\WindowsApps\Claude_1.0.100.0_x64__pzs8sxrjxfjjc\app";

    [Fact]
    public void Find_ReadsNameAndPublisher()
    {
        var package = StorePackage.Find(App)!;

        Assert.Equal(@"C:\Program Files\WindowsApps\", package.Parent);
        Assert.Equal("Claude", package.Name);
        Assert.Equal("pzs8sxrjxfjjc", package.Publisher);
        Assert.Equal(@"\app\", package.Rest);
        Assert.Equal(@"\", StorePackage.Find(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe")!.Rest);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Claude\app")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0_x64")]
    [InlineData(@"C:\Users\u\AppData\Local\Claude_1.0.100.0_x64__pzs8sxrjxfjjc\app")]
    public void Find_NotAPackage(string dir) => Assert.Null(StorePackage.Find(dir));

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.200.0_x64__pzs8sxrjxfjjc\app\claude.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.200.0_arm64__pzs8sxrjxfjjc\app\sub\claude.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.200.0_x64__aaaaaaaaaaaaa\app\claude.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Claude.Beta_1.0.200.0_x64__pzs8sxrjxfjjc\app\claude.exe", false)]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.200.0_x64__pzs8sxrjxfjjc\claude.exe", false)]
    [InlineData(@"D:\WindowsApps\Claude_1.0.200.0_x64__pzs8sxrjxfjjc\app\claude.exe", false)]
    public void Contains_SamePackageInAnyVersion(string image, bool expected) =>
        Assert.Equal(expected, StorePackage.Find(App)!.Contains(image));

    [Fact]
    public void WithoutVersion_SamePackageSamePath()
    {
        Assert.Equal(@"C:\Program Files\WindowsApps\Claude_*_pzs8sxrjxfjjc\app", StorePackage.WithoutVersion(App));
        Assert.Equal(StorePackage.WithoutVersion(App),
            StorePackage.WithoutVersion(@"C:\Program Files\WindowsApps\Claude_1.0.200.0_x64__pzs8sxrjxfjjc\app"));
        Assert.Equal(@"C:\Apps\Discord", StorePackage.WithoutVersion(@"C:\Apps\Discord"));
    }

    [Fact]
    public void NewVersionOfStoreApp_IsNotAnUrgentChangeForCopies()
    {
        // Служба и так узнаёт копии в любой версии пакета: перезапускать ядро из-за новой версии не нужно.
        var before = new List<CoreProgram> { new() { Folder = App, Name = "claude.exe", Subfolders = true } };
        var after = new List<CoreProgram> { new() { Folder = App.Replace("1.0.100.0", "1.0.200.0"), Name = "claude.exe", Subfolders = true } };

        Assert.True(CorePlanDiff.SamePrograms(before, after));
        Assert.False(CorePlanDiff.SamePrograms(before, new List<CoreProgram> { new() { Folder = @"C:\Apps\Claude", Name = "claude.exe", Subfolders = true } }));
    }
}
