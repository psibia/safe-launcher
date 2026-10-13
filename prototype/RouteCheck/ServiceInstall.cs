using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KillSwitchLauncher.Services;

namespace RouteCheck;

/// <summary>
/// Установка службы «Безопасный лаунчер — сеть» для проверки, как это будет делать установщик лаунчера:
/// файлы службы (папка service рядом с программой) и ядро копируются в C:\Program Files\SafeLauncher\core,
/// затем служба регистрируется. Для этого программа перезапускает себя с правами администратора (окно Windows
/// с вопросом), сама проверка работает с обычными правами, как лаунчер.
/// </summary>
internal static class ServiceInstall
{
    public const string InstallArgument = "--install-service";
    public const string UninstallArgument = "--uninstall-service";
    private const string ServiceExe = "SafeLauncher.Service.exe";
    private const int ErrorCancelled = 1223;

    private static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, "service");

    private static string ResultFile => Path.Combine(Path.GetTempPath(), "SafeLauncher-RouteCheck-install.txt");

    /// <summary>Почему службу нужно (пере)установить, или null, если установлена именно эта версия и работает.</summary>
    public static string? WhyInstall()
    {
        var state = CoreServiceClient.QueryService();
        if (!state.Installed)
            return "служба ещё не установлена";

        var target = CoreServiceClient.InstallDirectory;
        foreach (var source in Directory.GetFiles(SourceDirectory))
        {
            var installed = Path.Combine(target, Path.GetFileName(source));
            if (!File.Exists(installed) || !SameContent(source, installed))
                return "установлена другая версия службы";
        }

        var core = Path.Combine(target, TestServer.ExeName);
        if (!File.Exists(core) || !SameContent(TestServer.Locate(AppContext.BaseDirectory), core))
            return "установлена другая версия ядра";
        return state.Running ? null : "служба установлена, но не запущена";
    }

    /// <summary>Запустить себя с правами администратора с ключом установки или удаления. Возвращает итог для отчёта.</summary>
    public static (bool Ok, string Text) RunElevated(string argument)
    {
        try
        {
            File.Delete(ResultFile);
        }
        catch (Exception)
        {
            // нет — и не надо
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, argument)
            {
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("Не удалось запустить установку.");
            process.WaitForExit();
            var text = File.Exists(ResultFile) ? File.ReadAllText(ResultFile).Trim() : "установка не оставила отчёта";
            return (process.ExitCode == 0, text);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (false, "в окне Windows не дали права администратора");
        }
    }

    /// <summary>Работает в копии с правами администратора: ставит службу и пишет итог в файл для основной копии.</summary>
    public static int Install()
    {
        var log = new StringBuilder();
        try
        {
            var target = CoreServiceClient.InstallDirectory;
            var installedExe = Path.Combine(target, ServiceExe);
            if (CoreServiceClient.QueryService().Running)
            {
                // Работающая служба держит свои файлы и ядро: сначала остановить (регистрацию --install потом обновит).
                RunTool("sc.exe", "stop " + CoreServiceClient.ServiceName, out _);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (CoreServiceClient.QueryService().Running && DateTime.UtcNow < deadline)
                    Thread.Sleep(250);
                log.AppendLine(CoreServiceClient.QueryService().Running ? "Прежняя служба не остановилась за 30 секунд." : "Прежняя служба остановлена.");
            }

            Directory.CreateDirectory(target);
            var files = Directory.GetFiles(SourceDirectory).Select(f => (From: f, To: Path.Combine(target, Path.GetFileName(f))))
                .Append((From: TestServer.Locate(AppContext.BaseDirectory), To: Path.Combine(target, TestServer.ExeName)));
            foreach (var (from, to) in files)
                CopyWithRetry(from, to);
            log.AppendLine("Файлы скопированы в " + target);

            var output = RunTool(installedExe, "--install", out var code);
            log.AppendLine($"Регистрация службы: {output}");
            File.WriteAllText(ResultFile, log.ToString(), new UTF8Encoding(true));
            return code;
        }
        catch (Exception ex)
        {
            log.AppendLine("Ошибка: " + ex.Message);
            File.WriteAllText(ResultFile, log.ToString(), new UTF8Encoding(true));
            return 1;
        }
    }

    public static int Uninstall()
    {
        var log = new StringBuilder();
        try
        {
            var target = CoreServiceClient.InstallDirectory;
            var installedExe = Path.Combine(target, ServiceExe);
            if (File.Exists(installedExe))
                log.AppendLine("Удаление службы: " + RunTool(installedExe, "--uninstall", out _));
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            log.AppendLine("Папка удалена: " + target);
            File.WriteAllText(ResultFile, log.ToString(), new UTF8Encoding(true));
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine("Ошибка: " + ex.Message);
            File.WriteAllText(ResultFile, log.ToString(), new UTF8Encoding(true));
            return 1;
        }
    }

    /// <summary>Файлы только что остановленной службы Windows может отпустить не сразу.</summary>
    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static string RunTool(string exe, string argument, out int exitCode)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, argument)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        }) ?? throw new InvalidOperationException("Не удалось запустить " + exe);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill();
            exitCode = -1;
            return "не завершилась за 2 минуты";
        }

        exitCode = process.ExitCode;
        return $"код {exitCode}. {output.Result.Trim()} {error.Result.Trim()}".Trim();
    }

    private static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return fa.Length == fb.Length && SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}
