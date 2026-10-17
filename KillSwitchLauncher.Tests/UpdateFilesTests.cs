using System.IO;
using SafeLauncher.Service;
using Xunit;

namespace KillSwitchLauncher.Tests;

/// <summary>Резервная копия папки перед обновлением и возврат к ней при откате.</summary>
public class UpdateFilesTests
{
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void CopyTree_CopiesEverything_ExceptSkippedTopFolder()
    {
        using var dir = new TempDir();
        Write(dir.File("core", "sing-box.exe"), "12345");
        Write(dir.File("core", "rules", "geo.db"), "abc");
        Write(dir.File("core", "run", "config.json"), "секрет");
        Write(dir.File("core", "runner.txt"), "1");

        var bytes = UpdateFiles.CopyTree(dir.File("core"), dir.File("backup"), skip: "run");

        Assert.Equal("12345", File.ReadAllText(dir.File("backup", "sing-box.exe")));
        Assert.Equal("abc", File.ReadAllText(dir.File("backup", "rules", "geo.db")));
        Assert.True(File.Exists(dir.File("backup", "runner.txt"))); // похожее имя — не та папка
        Assert.False(Directory.Exists(dir.File("backup", "run")));
        Assert.Equal(9, bytes);
    }

    [Fact]
    public void Mirror_ReturnsFolderToTheBackup_AndKeepsTheKeptFolder()
    {
        using var dir = new TempDir();
        Write(dir.File("backup", "app.exe"), "старая");
        Write(dir.File("backup", "lib", "a.dll"), "a");

        // Новая версия успела заменить и добавить файлы.
        Write(dir.File("app", "app.exe"), "новая");
        Write(dir.File("app", "lib", "a.dll"), "a2");
        Write(dir.File("app", "lib", "b.dll"), "b");
        Write(dir.File("app", "extra", "c.txt"), "c");
        Write(dir.File("app", "run", "config.json"), "рабочий");

        UpdateFiles.Mirror(dir.File("backup"), dir.File("app"), keep: "run");

        Assert.Equal("старая", File.ReadAllText(dir.File("app", "app.exe")));
        Assert.Equal("a", File.ReadAllText(dir.File("app", "lib", "a.dll")));
        Assert.False(File.Exists(dir.File("app", "lib", "b.dll")));
        Assert.False(Directory.Exists(dir.File("app", "extra")));
        Assert.Equal("рабочий", File.ReadAllText(dir.File("app", "run", "config.json")));
    }

    [Fact]
    public void Size_CountsFiles_WithoutSkippedFolder_AndMissingFolderIsZero()
    {
        using var dir = new TempDir();
        Write(dir.File("core", "a.bin"), "1234");
        Write(dir.File("core", "run", "b.bin"), "123456");

        Assert.Equal(4, UpdateFiles.Size(dir.File("core"), "run"));
        Assert.Equal(10, UpdateFiles.Size(dir.File("core")));
        Assert.Equal(0, UpdateFiles.Size(dir.File("нет")));
    }

    [Fact]
    public void DeleteDirectory_RemovesReadOnlyFiles_AndMissingFolderIsFine()
    {
        using var dir = new TempDir();
        Write(dir.File("old", "x.txt"), "x");
        File.SetAttributes(dir.File("old", "x.txt"), FileAttributes.ReadOnly);

        UpdateFiles.DeleteDirectory(dir.File("old"));
        UpdateFiles.DeleteDirectory(dir.File("old"));

        Assert.False(Directory.Exists(dir.File("old")));
    }
}
