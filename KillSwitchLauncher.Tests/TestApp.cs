using System;
using System.Diagnostics;
using System.Threading;

namespace KillSwitchLauncher.Tests;

/// <summary>
/// Точка входа KillSwitchLauncher.Tests.exe. Тесты контроля процессов запускают эту программу как
/// «приложение» вместо настоящих программ:
///   --sleep N        просто работает N секунд;
///   --spawn-child N  запускает свою копию с --sleep N и сразу выходит (как ярлык Discord: Update.exe → Discord.exe).
/// </summary>
public static class TestApp
{
    public static int Main(string[] args)
    {
        if (args.Length == 2 && int.TryParse(args[1], out var seconds))
        {
            switch (args[0])
            {
                case "--sleep":
                    Thread.Sleep(TimeSpan.FromSeconds(seconds));
                    return 0;
                case "--spawn-child":
                    using (Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--sleep {seconds}") { UseShellExecute = false }))
                        return 0;
            }
        }

        return 0;
    }
}
