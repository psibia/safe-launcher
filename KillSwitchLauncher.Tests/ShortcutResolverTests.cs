using System;
using System.IO;
using KillSwitchLauncher.Services;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Ярлыки .lnk: лаунчер берёт из них программу, аргументы и рабочую папку.</summary>
public class ShortcutResolverTests
{
    /// <summary>Создаёт ярлык так же, как его создаёт установщик программы (через WScript.Shell).</summary>
    private static void CreateShortcut(string lnkPath, string target, string arguments, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Save();
    }

    [Fact]
    public void Resolve_ReadsTargetArgumentsAndWorkingFolder()
    {
        using var dir = new TempDir();
        var appDir = dir.File("Discord");
        Directory.CreateDirectory(appDir);
        var target = Path.Combine(appDir, "Update.exe");
        File.WriteAllBytes(target, Array.Empty<byte>());
        var lnk = dir.File("Discord.lnk");

        Sta.Run(() =>
        {
            CreateShortcut(lnk, target, "--processStart Discord.exe", appDir);

            var resolved = ShortcutResolver.Resolve(lnk);

            Assert.Equal(target, resolved.TargetPath, ignoreCase: true);
            Assert.Equal("--processStart Discord.exe", resolved.Arguments);
            Assert.Equal(appDir, resolved.WorkingDirectory, ignoreCase: true);
            Assert.Null(resolved.AppUserModelId);
        });
    }

    [Fact]
    public void Resolve_MissingShortcut_Throws() =>
        Assert.Throws<FileNotFoundException>(() => ShortcutResolver.Resolve(@"C:\нет\такого\ярлыка.lnk"));
}
