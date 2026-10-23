using System;
using System.Collections.Generic;
using System.IO;
using KillSwitchLauncher.Services;
using Xunit;
using Svc = SafeLauncher.Service;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Процессы вне контроля лаунчера, которые всё же относятся к приложению: помощники копии, открытой не из лаунчера,
/// программа приложения в другой папке с той же подписью издателя, процесс приложения вне задания (запрос UAC).
/// </summary>
public class OutsideKinTests : IDisposable
{
    private static readonly Guid Bank = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    private const string BankExe = @"C:\Apps\Bank\bank.exe";
    private const string Helper = @"C:\Users\me\AppData\Local\Bank\helper.exe";
    private const string Explorer = @"C:\Windows\explorer.exe";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Дерево процессов: PID → (путь, родитель, создан через столько секунд от T0).</summary>
    private sealed class Tree
    {
        public readonly Dictionary<int, ProcessKin.Info> Processes = new();
        public readonly Dictionary<int, Guid> Controlled = new();
        public readonly Dictionary<string, (Guid, bool)> Families = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> LinkBrowsers = new(StringComparer.OrdinalIgnoreCase);

        public Tree Add(int pid, string? image, int parent, int second)
        {
            Processes[pid] = new ProcessKin.Info(image, parent, T0.AddSeconds(second));
            return this;
        }

        public (Guid App, ProcessKin.Kind Kind) Resolve(int pid) =>
            ProcessKin.Resolve(pid,
                p => Processes.TryGetValue(p, out var info) ? info : null,
                p => Controlled.TryGetValue(p, out var app) ? app : null,
                image => Families.TryGetValue(image, out var family) ? family : null,
                (_, image) => LinkBrowsers.Contains(Path.GetFileName(image.Replace('\\', '/'))));
    }

    private static Tree BankCopy()
    {
        var tree = new Tree()
            .Add(100, Explorer, 4, 0)
            .Add(200, BankExe, 100, 10)       // копия, открытая не из лаунчера
            .Add(300, Helper, 200, 12)        // её помощник из AppData
            .Add(400, @"C:\Program Files\Tool\tool.exe", 300, 13); // и то, что запустил помощник
        tree.Families[BankExe] = (Bank, false);
        return tree;
    }

    [Fact]
    public void HelperOfOutsideCopy_GoesLikeTheCopy()
    {
        var tree = BankCopy();

        Assert.Equal((Bank, ProcessKin.Kind.Copy), tree.Resolve(300));
        Assert.Equal((Bank, ProcessKin.Kind.Copy), tree.Resolve(400));
    }

    [Fact]
    public void CopyItself_InAppFolder_IsLeftToTheCore()
    {
        // Ядро узнаёт её само по папке приложения: отвечать за неё не нужно.
        Assert.Equal(ProcessKin.Kind.None, BankCopy().Resolve(200).Kind);
    }

    [Fact]
    public void ProgramFoundBySignature_IsACopy()
    {
        var tree = new Tree().Add(100, Explorer, 4, 0).Add(500, @"D:\New\Bank\bank.exe", 100, 5);
        tree.Families[@"D:\New\Bank\bank.exe"] = (Bank, true);

        Assert.Equal((Bank, ProcessKin.Kind.Copy), tree.Resolve(500));
    }

    [Fact]
    public void UnrelatedProcess_IsNotOurs()
    {
        var tree = BankCopy().Add(600, @"C:\Program Files\Mail\mail.exe", 100, 20);

        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(600).Kind);
        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(100).Kind);
        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(12345).Kind); // процесса уже нет
    }

    [Fact]
    public void ReusedParentPid_IsNotAParent()
    {
        // Копия закрылась, её PID достался банковской программе позже, чем запустился «потомок»: он не её.
        var tree = new Tree()
            .Add(100, Explorer, 4, 0)
            .Add(200, BankExe, 100, 50)
            .Add(300, Helper, 200, 12);
        tree.Families[BankExe] = (Bank, false);

        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(300).Kind);
    }

    [Fact]
    public void UnknownCreationTime_StopsTheSearch()
    {
        var tree = BankCopy();
        tree.Processes[300] = tree.Processes[300] with { Created = null };

        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(300).Kind);
    }

    [Fact]
    public void ChildOfControlledProcess_OutsideTheJob_GoesLikeTheApp()
    {
        // Приложение запущено из лаунчера, помощник запущен с правами администратора и в задание не попал.
        var tree = new Tree().Add(100, Explorer, 4, 0).Add(200, BankExe, 100, 10).Add(300, Helper, 200, 12);
        tree.Families[BankExe] = (Bank, false);
        tree.Controlled[200] = Bank;

        Assert.Equal((Bank, ProcessKin.Kind.Launch), tree.Resolve(300));
    }

    [Fact]
    public void ProgramOfAnotherApp_KeepsItsOwnRules()
    {
        // Копия банковского приложения открыла программу другого приложения лаунчера: у неё свои правила.
        var tree = BankCopy().Add(700, @"C:\Apps\Chat\chat.exe", 200, 30);
        tree.Families[@"C:\Apps\Chat\chat.exe"] = (Other, false);

        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(700).Kind);
    }

    [Fact]
    public void BrowserOpenedByLink_IsNotPartOfTheApp()
    {
        var tree = BankCopy().Add(800, @"C:\Program Files\Google\Chrome\Application\chrome.exe", 200, 30);
        tree.LinkBrowsers.Add("chrome.exe");

        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(800).Kind);
    }

    [Fact]
    public void Search_IsLimitedInDepth()
    {
        // Копия (PID 100) и цепочка её потомков: 101 — её помощник, 102 — его потомок и так далее.
        var tree = new Tree().Add(100, BankExe, 0, 0);
        tree.Families[BankExe] = (Bank, false);
        for (var pid = 101; pid <= 100 + ProcessKin.MaxDepth + 1; pid++)
            tree.Add(pid, $@"C:\Tools\t{pid}.exe", pid - 1, pid);

        Assert.Equal(ProcessKin.Kind.Copy, tree.Resolve(100 + ProcessKin.MaxDepth).Kind);
        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(100 + ProcessKin.MaxDepth + 1).Kind);
    }

    [Fact]
    public void ParentLoop_DoesNotHang()
    {
        var tree = new Tree().Add(10, @"C:\a.exe", 11, 5).Add(11, @"C:\b.exe", 10, 5);

        Assert.Equal(ProcessKin.Kind.None, tree.Resolve(10).Kind);
    }

    // ---- подпись издателя ----

    private string Program(string folder, string name)
    {
        var path = _dir.File(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void SamePublisherAndName_InAnotherFolder_IsTheApp()
    {
        var family = AppFamily.ForImage(Program("Bank", "bank.exe"), "ООО Банк")!;
        var moved = Program("Bank 2", "bank.exe");

        Assert.False(family.ContainsPath(moved));
        Assert.True(family.SignedBySame(moved, "ООО Банк"));
        Assert.True(family.SignedBySame(moved, " ооо банк ")); // регистр и пробелы по краям не важны
    }

    [Fact]
    public void OtherPublisherOrName_OrNoSignature_IsNotTheApp()
    {
        var family = AppFamily.ForImage(Program("Bank", "bank.exe"), "ООО Банк")!;

        Assert.False(family.SignedBySame(Program("Fake", "bank.exe"), "Другой издатель"));
        Assert.False(family.SignedBySame(Program("Fake", "bank.exe"), null)); // без подписи
        Assert.False(family.SignedBySame(Program("Bank 2", "helper.exe"), "ООО Банк")); // та же подпись, другая программа
        Assert.False(AppFamily.ForImage(Program("Bank", "bank.exe"))!.SignedBySame(Program("Bank 2", "bank.exe"), "ООО Банк"));
    }

    [Fact]
    public void PublisherLearnedLater_KeepsTheFolderRule()
    {
        var exe = Program("Bank", "bank.exe");
        var family = AppFamily.ForImage(exe)!;

        var signed = family.WithPublisher("ООО Банк");

        Assert.Null(family.Publisher);
        Assert.Equal("ООО Банк", signed.Publisher);
        Assert.Equal(family.Roots, signed.Roots);
        Assert.Equal(family.ContainsPath(exe), signed.ContainsPath(exe));
        Assert.Equal(family.Names, signed.Names);
    }

    [Fact]
    public void SharedFolder_OnlyWithPublisher()
    {
        // В общей папке («Загрузки») по папке копию не узнать: остаётся подпись издателя.
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "bank.exe");

        Assert.Null(AppFamily.ForImage(downloads));
        var family = AppFamily.ForImage(downloads, "ООО Банк")!;
        Assert.False(family.ContainsPath(downloads));
        Assert.True(family.SignedBySame(Program("Bank", "bank.exe"), "ООО Банк"));
    }

    [Fact]
    public void GenericHost_NeverByPublisher()
    {
        Assert.Null(AppFamily.ForImage(Program("Python", "python.exe"), "Python Software Foundation"));
    }

    [Fact]
    public void CopyTag_SameForLauncherAndService()
    {
        Assert.Equal(Svc.ControlProtocol.CopyTagSuffix, CoreAppRoute.CopyTagSuffix);
        Assert.Equal("a1-copy", CoreAppRoute.CopyTag("a1"));
    }
}
