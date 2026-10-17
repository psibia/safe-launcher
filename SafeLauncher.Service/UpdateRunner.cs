using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using SafeLauncher.Logging;
using SafeLauncher.Updates;

namespace SafeLauncher.Service;

/// <summary>Обновление не принято. Сообщение — для человека, лаунчер показывает его как есть.</summary>
internal sealed class UpdateRejectedException : Exception
{
    public UpdateRejectedException(string message) : base(message)
    {
    }
}

/// <summary>Задание помощнику: job.json в папке обновления.</summary>
internal sealed class UpdateJob
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;

    /// <summary>Установщик и его подпись в папке package.</summary>
    public string Setup { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;

    /// <summary>Папка лаунчера (из записи об установке) и папка службы и ядра.</summary>
    public string AppDir { get; set; } = string.Empty;
    public string CoreDir { get; set; } = string.Empty;

    /// <summary>Лаунчер, который попросил обновление: установка начинается, когда он закроется.</summary>
    public int LauncherPid { get; set; }
    public DateTime LauncherStarted { get; set; }
}

/// <summary>
/// Обновление лаунчера через службу, без запроса прав Windows (docs/DETAILS.md, «Обновления»).
///
/// 1. Лаунчер скачал установщик и его подпись (.sig) из выпуска GitHub, закрыл приложения, остановил подключение
///    и передал службе команду update (<see cref="Prepare"/>). Служба, пока лаунчер ещё открыт и может показать ошибку:
///    проверяет подпись ключом автора и что версия новее, копирует установщик в папку обновления
///    (C:\Program Files\SafeLauncher\update\package, доступ только у системы и администраторов), сохраняет текущие файлы
///    лаунчера, службы и ядра и запись об установке для отката, копирует себя как SafeLauncher.Updater.exe
///    и запускает эту копию с --apply-update. Лаунчер закрывается.
/// 2. Помощник (<see cref="Apply"/>), отдельный процесс от имени системы: ждёт, пока лаунчер закроется, ещё раз сверяет
///    установщик с подписью, останавливает службу и ядро, запускает установщик без окон (/service=1), проверяет, что
///    файлы новой версии на месте и новая служба отвечает, а затем ждёт, пока новый лаунчер запустится и сообщит об этом
///    службе (<see cref="ConfirmStarted"/>). Не вышло — возвращает сохранённые файлы и запись об установке
///    и регистрирует прежнюю службу.
/// 3. Ход установки помощник пишет в status.json (<see cref="UpdateState"/>); его показывает окно ожидания обновления
///    (копия лаунчера во временной папке), а когда всё закончено, оно запускает лаунчер. Подробный журнал — update.log.
///
/// Помощник называется иначе, чем служба, потому что установщик закрывает все процессы SafeLauncher.Service.exe.
/// </summary>
internal static class UpdateRunner
{
    public const string ApplyArgument = "--apply-update";

    private const string HelperName = "SafeLauncher.Updater.exe";
    private const string JobFile = "job.json";
    private const string LogFile = "update.log";
    private const string SetupLogFile = "setup.log";
    private const string PackageDir = "package";
    private const string BackupDir = "backup";
    private const string UninstallBackup = "uninstall.json";

    /// <summary>Отметка «новая версия лаунчера запустилась»: её пишет новая служба по команде updateStarted, читает помощник.</summary>
    private const string StartedFile = "started";
    private const string LauncherExe = "KillSwitchLauncher.exe";
    private const string CoreExe = "launcher-core.exe";
    private const string ServiceExe = "SafeLauncher.Service.exe";

    /// <summary>Рабочая папка ядра: при обновлении и откате не трогается.</summary>
    private const string CoreRunDir = "run";

    /// <summary>Запись об установке в «Программы и компоненты» (AppId из установщика и «_is1»).</summary>
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{63FAD8AB-FDB1-4442-8A82-DCB83BC08EF2}_is1";

    private const long MaxSignatureBytes = 16 * 1024;

    /// <summary>Запас места на диске сверх резервной копии и установщика: установщик распаковывает файлы.</summary>
    private const long FreeSpaceReserve = 512L * 1024 * 1024;

    private static readonly TimeSpan LauncherExitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Сколько ждать, пока новый лаунчер вообще запустят (его запускает окно ожидания). Не запустили — проверить запуск нечем,
    /// установка считается удачной: файлы и служба уже проверены.
    /// </summary>
    private static readonly TimeSpan LauncherAppearTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Сколько ждать от запущенного нового лаунчера сообщения, что он запустился.</summary>
    private static readonly TimeSpan LauncherConfirmTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Новый лаунчер закрылся, не сообщив о запуске, и не запущен снова столько времени: он падает при запуске.</summary>
    private static readonly TimeSpan LauncherGoneTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Версия этой службы: обновление ставится, только если оно новее.</summary>
    public static Version CurrentVersion => UpdatePackage.Normalize(typeof(UpdateRunner).Assembly.GetName().Version ?? new Version(0, 0, 0));

    // ------------------------------------------------------------------ служба: принять обновление

    /// <summary>
    /// Принять обновление от лаунчера и запустить помощника, который установит его, когда лаунчер закроется.
    /// Возвращает устанавливаемую версию. Не принято — <see cref="UpdateRejectedException"/>, при этом ничего не изменено.
    /// </summary>
    public static Version Prepare(UpdateRequest request, int clientPid, string? cid, out Process helper)
    {
        var watch = Stopwatch.StartNew();
        using var key = UpdatePackage.CreatePublicKey()
                        ?? throw new UpdateRejectedException("В службу не встроен ключ подписи выпусков, поэтому обновление ставится обычным установщиком.");

        var setupSource = CheckSourcePath(request.SetupPath, ".exe");
        var signatureSource = CheckSourcePath(request.SignaturePath, UpdatePackage.SignatureExtension);

        var appDir = InstalledAppDir() ?? throw new UpdateRejectedException("Не найдена запись об установке лаунчера. Переустановите лаунчер с GitHub.");
        var launcher = Path.Combine(appDir, LauncherExe);
        var client = Native.ProcessImagePath(clientPid);
        if (client is null || !SamePath(client, launcher))
            throw new UpdateRejectedException($"Через службу обновляется только установленный лаунчер ({launcher}).");
        DateTime launcherStarted;
        using (var process = Process.GetProcessById(clientPid))
            launcherStarted = process.StartTime.ToUniversalTime();

        var coreDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var updateDir = UpdateDirectory(coreDir);
        if (HelperRunning(updateDir))
            throw new UpdateRejectedException("Обновление уже устанавливается.");

        var signatureInfo = new FileInfo(signatureSource);
        if (!signatureInfo.Exists || signatureInfo.Length > MaxSignatureBytes)
            throw new UpdateRejectedException("Файл подписи обновления не найден или повреждён. Скачайте обновление заново.");
        UpdateManifest manifest;
        try
        {
            manifest = UpdatePackage.VerifySignature(File.ReadAllText(signatureSource, Encoding.UTF8), key);
        }
        catch (UpdateSignatureException ex)
        {
            throw new UpdateRejectedException(ex.Message + " Скачайте обновление заново.");
        }

        var current = CurrentVersion;
        if (manifest.Version <= current)
            throw new UpdateRejectedException($"Версия {manifest.Version.ToString(3)} не новее установленной {current.ToString(3)}.");

        var packageDir = Path.Combine(updateDir, PackageDir);
        var backupDir = Path.Combine(updateDir, BackupDir);
        try
        {
            // Остатки прошлого обновления: помощник не работает (проверено выше), они больше не нужны.
            UpdateFiles.DeleteDirectory(updateDir);
            Directory.CreateDirectory(updateDir);
            CreateProtectedDirectory(packageDir);

            var setup = Path.Combine(packageDir, manifest.FileName);
            var signature = setup + UpdatePackage.SignatureExtension;
            CheckFreeSpace(updateDir, manifest.Size + UpdateFiles.Size(appDir) + UpdateFiles.Size(coreDir, CoreRunDir));
            File.Copy(setupSource, setup);
            File.Copy(signatureSource, signature);
            try
            {
                UpdatePackage.VerifyFile(setup, manifest);
            }
            catch (UpdateSignatureException ex)
            {
                throw new UpdateRejectedException(ex.Message + " Скачайте обновление заново.");
            }

            var appBytes = UpdateFiles.CopyTree(appDir, Path.Combine(backupDir, "app"));
            var coreBytes = UpdateFiles.CopyTree(coreDir, Path.Combine(backupDir, "core"), CoreRunDir);
            SaveUninstallKey(Path.Combine(backupDir, UninstallBackup));

            var helperExe = Path.Combine(updateDir, HelperName);
            File.Copy(Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь службы."), helperExe);

            var job = new UpdateJob
            {
                Id = Guid.NewGuid().ToString("N"),
                Version = manifest.Version.ToString(3),
                FromVersion = current.ToString(3),
                Setup = setup,
                Signature = signature,
                AppDir = appDir,
                CoreDir = coreDir,
                LauncherPid = clientPid,
                LauncherStarted = launcherStarted
            };
            var jobPath = Path.Combine(updateDir, JobFile);
            File.WriteAllText(jobPath, JsonSerializer.Serialize(job, Json), new UTF8Encoding(false));
            var now = DateTime.UtcNow;
            new UpdateState
            {
                JobId = job.Id, Version = job.Version, FromVersion = job.FromVersion, Stage = UpdateStages.Waiting,
                Message = "Ждём, пока закроется лаунчер", Started = now, Updated = now
            }.Write(Path.Combine(updateDir, UpdateState.FileName));

            var start = new ProcessStartInfo(helperExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true, // своя невидимая консоль: Ctrl+C, которым служба останавливает ядро, помощнику не приходит
                WorkingDirectory = updateDir
            };
            start.ArgumentList.Add(ApplyArgument);
            start.ArgumentList.Add(jobPath);
            helper = Process.Start(start) ?? throw new InvalidOperationException("Помощник обновления не запустился.");

            ServiceLog.Info(LogAreas.Update, LogCodes.UpdateInstall, cid,
                $"Обновление {job.FromVersion} → {job.Version} принято от лаунчера PID {clientPid}: подпись верна, сохранено для отката " +
                $"{(appBytes + coreBytes) / (1024 * 1024)} МБ, помощник PID {helper.Id}, {watch.ElapsedMilliseconds} мс");
            return manifest.Version;
        }
        catch (Exception ex)
        {
            try
            {
                UpdateFiles.DeleteDirectory(updateDir);
            }
            catch (Exception)
            {
                // уберётся при следующем обновлении
            }

            if (ex is UpdateRejectedException)
                throw;
            ServiceLog.Error(LogAreas.Update, LogCodes.UpdateFail, cid, "Не удалось подготовить обновление: " + ex);
            throw new UpdateRejectedException("Служба не смогла подготовить обновление: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ служба: новая версия запустилась

    /// <summary>
    /// updateStarted: новая версия лаунчера запустилась после обновления. Принимается только от установленного лаунчера той
    /// версии, что ставится, и только пока помощник этого ждёт (шаг starting). null — принято, иначе причина отказа.
    /// </summary>
    public static string? ConfirmStarted(string? jobId, int clientPid)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 64 || !jobId.All(char.IsAsciiLetterOrDigit))
            return "Неверный номер обновления.";

        var updateDir = UpdateDirectory(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var state = UpdateState.Read(Path.Combine(updateDir, UpdateState.FileName));
        if (state is null || state.JobId != jobId || state.Done || state.Stage != UpdateStages.Starting)
            return "Помощник обновления не ждёт сообщения о запуске.";

        var appDir = InstalledAppDir();
        var client = Native.ProcessImagePath(clientPid);
        if (appDir is null || client is null || !SamePath(client, Path.Combine(appDir, LauncherExe)))
            return "О запуске сообщает только установленный лаунчер.";
        if (FileVersion(client)?.ToString(3) != state.Version)
            return $"Запущен лаунчер не версии {state.Version}.";

        try
        {
            File.WriteAllText(Path.Combine(updateDir, StartedFile), jobId, new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Не удалось записать отметку о запуске: " + ex.Message;
        }
    }

    private static Version? FileVersion(string path)
    {
        if (!File.Exists(path))
            return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    /// <summary>Путь от лаунчера: полный, на локальном диске, с нужным расширением.</summary>
    private static string CheckSourcePath(string? path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new UpdateRejectedException("Неверный путь к файлу обновления.");
        return Path.GetFullPath(path);
    }

    /// <summary>Папка обновления рядом с папкой службы: C:\Program Files\SafeLauncher\update.</summary>
    private static string UpdateDirectory(string coreDir) => Path.Combine(Path.GetDirectoryName(coreDir)!, "update");

    /// <summary>Папка лаунчера из записи об установке (её пишет установщик в HKLM); null — записи нет или папка не в Program Files.</summary>
    private static string? InstalledAppDir()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(UninstallKey);
        var dir = key?.GetValue("InstallLocation") as string ?? key?.GetValue("Inno Setup: App Path") as string;
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir))
            return null;
        dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var programFiles = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) + "\\";
        return dir.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, LauncherExe)) ? dir : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Работает помощник прошлого обновления.</summary>
    private static bool HelperRunning(string updateDir)
    {
        var helper = Path.Combine(updateDir, HelperName);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperName)))
        {
            using (process)
            {
                if (Native.ProcessImagePath(process.Id) is { } path && SamePath(path, helper))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Папка, в которую могут писать и из которой могут читать только система и администраторы.</summary>
    private static void CreateProtectedDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        security.CreateDirectory(path);
    }

    private static void CheckFreeSpace(string dir, long backupAndSetup)
    {
        var drive = new DriveInfo(Path.GetPathRoot(dir)!);
        var needed = backupAndSetup + FreeSpaceReserve;
        if (drive.AvailableFreeSpace < needed)
            throw new UpdateRejectedException(
                $"На диске {drive.Name.TrimEnd('\\')} не хватает места для обновления: освободите ещё {(needed - drive.AvailableFreeSpace) / (1024 * 1024) + 1} МБ.");
    }

    // ------------------------------------------------------------------ запись об установке

    private sealed class RegValue
    {
        public string Name { get; set; } = string.Empty;
        public RegistryValueKind Kind { get; set; }
        public string? Text { get; set; }
        public long? Number { get; set; }
        public string[]? Lines { get; set; }
        public string? Data { get; set; }
    }

    private static void SaveUninstallKey(string path)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(UninstallKey) ?? throw new InvalidOperationException("Нет записи об установке лаунчера.");
        var values = new List<RegValue>();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var value = new RegValue { Name = name, Kind = kind };
            switch (raw)
            {
                case string text:
                    value.Text = text;
                    break;
                case int number:
                    value.Number = number;
                    break;
                case long number:
                    value.Number = number;
                    break;
                case string[] lines:
                    value.Lines = lines;
                    break;
                case byte[] data:
                    value.Data = Convert.ToBase64String(data);
                    break;
                default:
                    continue;
            }

            values.Add(value);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(values, Json), new UTF8Encoding(false));
    }

    private static void RestoreUninstallKey(string path)
    {
        var values = JsonSerializer.Deserialize<List<RegValue>>(File.ReadAllText(path, Encoding.UTF8), Json)
                     ?? throw new InvalidOperationException("Сохранённая запись об установке пуста.");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        root.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        using var key = root.CreateSubKey(UninstallKey);
        foreach (var value in values)
        {
            object data = value.Kind switch
            {
                RegistryValueKind.DWord => unchecked((int)(value.Number ?? 0)),
                RegistryValueKind.QWord => value.Number ?? 0,
                RegistryValueKind.MultiString => value.Lines ?? Array.Empty<string>(),
                RegistryValueKind.Binary => Convert.FromBase64String(value.Data ?? string.Empty),
                _ => value.Text ?? string.Empty
            };
            key.SetValue(value.Name, data, value.Kind);
        }
    }

    // ------------------------------------------------------------------ помощник: установить

    /// <summary>SafeLauncher.Updater.exe --apply-update job.json: установить обновление (см. описание класса). 0 — установлено.</summary>
    public static int Apply(string jobPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(jobPath));
        var own = Path.GetDirectoryName(Environment.ProcessPath);
        // Задание только из своей папки: её создала служба, пишут в неё только система и администраторы.
        if (dir is null || own is null || !SamePath(dir, own))
            return 2;

        var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(jobPath, Encoding.UTF8), Json)
                  ?? throw new InvalidOperationException("Пустое задание обновления.");
        using var run = new ApplyRun(dir, job);
        return run.Execute() ? 0 : 1;
    }

    private sealed class ApplyRun : IDisposable
    {
        private readonly string _dir;
        private readonly UpdateJob _job;
        private readonly UpdateState _state;
        private readonly string _statePath;
        private readonly StreamWriter _log;
        private readonly object _sync = new();
        private readonly Timer _heartbeat;

        /// <summary>Последний рабочий шаг (не откат): на нём и не получилось, если установка не удалась.</summary>
        private string _workStage = UpdateStages.Waiting;

        public ApplyRun(string dir, UpdateJob job)
        {
            _dir = dir;
            _job = job;
            _statePath = Path.Combine(dir, UpdateState.FileName);
            _state = UpdateState.Read(_statePath) ?? new UpdateState { JobId = job.Id, Version = job.Version, FromVersion = job.FromVersion };
            _state.Started = DateTime.UtcNow;
            _log = new StreamWriter(new FileStream(Path.Combine(dir, LogFile), FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Log($"Обновление {job.FromVersion} → {job.Version}, задание {job.Id}, помощник PID {Environment.ProcessId}");
            _heartbeat = new Timer(_ => SaveState(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        public bool Execute()
        {
            Stage(UpdateStages.Waiting, "Ждём, пока закроется лаунчер");
            if (!WaitForLauncher())
                return Finish(false, false, "Лаунчер не закрылся за минуту. Обновление отменено, ничего не изменилось.");

            Stage(UpdateStages.Verifying, "Проверяем установщик по подписи");
            try
            {
                using var key = UpdatePackage.CreatePublicKey() ?? throw new UpdateSignatureException("В помощник не встроен ключ подписи.");
                var manifest = UpdatePackage.Verify(_job.Setup, File.ReadAllText(_job.Signature, Encoding.UTF8), key);
                if (manifest.Version.ToString(3) != _job.Version)
                    throw new UpdateSignatureException("Версия в подписи не совпала с заданием.");
            }
            catch (Exception ex) when (ex is UpdateSignatureException or IOException)
            {
                Log("Проверка установщика: " + ex.Message);
                return Finish(false, false, ex.Message + " Обновление отменено, ничего не изменилось.");
            }

            Stage(UpdateStages.Stopping, "Останавливаем службу и ядро");
            try
            {
                StopService();
            }
            catch (Exception ex)
            {
                Log("Не удалось остановить службу: " + ex);
                TryStartService();
                return Finish(false, false, "Не удалось остановить службу. Обновление отменено, ничего не изменилось.");
            }

            // Лаунчер, запущенный заново во время обновления, не дал бы установщику заменить свои файлы.
            CloseProcessesIn(_job.AppDir);

            string? failure;
            try
            {
                Stage(UpdateStages.Installing, $"Устанавливаем версию {_job.Version}");
                failure = RunInstaller();
                if (failure is null)
                {
                    Stage(UpdateStages.Checking, "Проверяем новую версию");
                    failure = CheckInstalled();
                }

                if (failure is null)
                {
                    Stage(UpdateStages.Starting, "Запускаем новую версию лаунчера");
                    failure = WaitForNewLauncher();
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка установки: " + ex);
                failure = "Ошибка установки: " + ex.Message;
            }

            if (failure is null)
            {
                Cleanup();
                return Finish(true, false, null);
            }

            Log("Новая версия не встала: " + failure);
            Stage(UpdateStages.RollingBack, $"Новая версия не встала, возвращаем версию {_job.FromVersion}");
            try
            {
                Rollback();
                Cleanup();
                return Finish(false, true, failure);
            }
            catch (Exception ex)
            {
                Log("Откат не удался: " + ex);
                return Finish(false, false, failure + $" Вернуть версию {_job.FromVersion} не удалось: переустановите лаунчер с GitHub.");
            }
        }

        /// <summary>Лаунчер закрылся (или это уже другой процесс с тем же PID).</summary>
        private bool WaitForLauncher()
        {
            try
            {
                using var process = Process.GetProcessById(_job.LauncherPid);
                if (Math.Abs((process.StartTime.ToUniversalTime() - _job.LauncherStarted).TotalSeconds) > 2)
                    return true;
                var watch = Stopwatch.StartNew();
                var exited = process.WaitForExit(LauncherExitTimeout);
                Log(exited ? $"Лаунчер закрылся за {watch.ElapsedMilliseconds} мс" : "Лаунчер не закрылся за минуту");
                return exited;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Log("Лаунчер уже закрыт");
                return true;
            }
        }

        private void StopService()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var service = new ServiceController(ControlProtocol.ServiceName);
                if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                    service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, ServiceTimeout);
                Log($"Служба остановлена за {watch.ElapsedMilliseconds} мс");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                Log("Служба не остановилась за 30 секунд: её процесс будет закрыт");
            }
            catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
            {
                Log("Служба не установлена");
            }

            CloseProcessesIn(_job.CoreDir);
        }

        private void TryStartService()
        {
            try
            {
                using var service = new ServiceController(ControlProtocol.ServiceName);
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
                }

                Log("Служба запущена обратно");
            }
            catch (Exception ex)
            {
                Log("Не удалось запустить службу обратно: " + ex.Message);
            }
        }

        /// <summary>Закрыть процессы, запущенные из папки (служба, ядро, лаунчер), чтобы их файлы можно было заменить.</summary>
        private void CloseProcessesIn(string dir)
        {
            var prefix = Path.TrimEndingDirectorySeparator(dir) + "\\";
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || Native.ProcessImagePath(process.Id) is not { } path ||
                        !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        process.Kill();
                        process.WaitForExit(10_000);
                        Log($"Закрыт процесс {Path.GetFileName(path)} PID {process.Id}");
                    }
                    catch (Exception ex)
                    {
                        Log($"Не удалось закрыть {Path.GetFileName(path)} PID {process.Id}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>Запустить установщик без окон. null — завершился без ошибок.</summary>
        private string? RunInstaller()
        {
            var setupLog = Path.Combine(_dir, SetupLogFile);
            // /service=1: установщик не запускает лаунчер сам (это сделает окно ожидания) и не трогает службу после себя.
            var arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /autoupdate=1 /service=1 " +
                            $"/DIR=\"{_job.AppDir}\" /LOG=\"{setupLog}\"";
            Log($"Установщик: {Path.GetFileName(_job.Setup)} {arguments}");
            var watch = Stopwatch.StartNew();
            using var process = Process.Start(new ProcessStartInfo(_job.Setup, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_job.Setup)!
            }) ?? throw new InvalidOperationException("Установщик не запустился.");
            if (!process.WaitForExit(InstallerTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // уже завершился
                }

                return "Установщик не закончил работу за 15 минут.";
            }

            Log($"Установщик завершился с кодом {process.ExitCode} за {watch.Elapsed.TotalSeconds:0.0} с (журнал: {SetupLogFile})");
            return process.ExitCode == 0 ? null : $"Установщик завершился с ошибкой (код {process.ExitCode}).";
        }

        /// <summary>Новая версия на месте и служба отвечает. null — всё в порядке.</summary>
        private string? CheckInstalled()
        {
            var target = Version.Parse(_job.Version);
            var launcher = FileVersion(Path.Combine(_job.AppDir, LauncherExe));
            if (launcher != target)
                return $"Лаунчер не обновился: в папке версия {launcher?.ToString(3) ?? "неизвестна"}.";
            var service = FileVersion(Path.Combine(_job.CoreDir, ServiceExe));
            if (service != target)
                return $"Служба не обновилась: в папке версия {service?.ToString(3) ?? "неизвестна"}.";
            if (!File.Exists(Path.Combine(_job.CoreDir, CoreExe)))
                return "После установки нет ядра launcher-core.exe.";

            try
            {
                using var controller = new ServiceController(ControlProtocol.ServiceName);
                controller.WaitForStatus(ServiceControllerStatus.Running, ServiceTimeout);
            }
            catch (Exception ex) when (ex is System.ServiceProcess.TimeoutException or InvalidOperationException)
            {
                return "Служба новой версии не запустилась.";
            }

            try
            {
                using var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName, PipeDirection.InOut);
                pipe.Connect((int)ServiceTimeout.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is System.TimeoutException or IOException or UnauthorizedAccessException)
            {
                return "Служба новой версии не отвечает.";
            }

            Log("Новая версия на месте, служба работает и отвечает");
            return null;
        }

        /// <summary>
        /// Новая версия лаунчера запустилась и сообщила об этом службе (<see cref="ConfirmStarted"/>). null — сообщила, или её
        /// так и не запустили (тогда проверить нечем, а файлы и служба уже проверены); иначе — почему считаем, что она не работает.
        /// </summary>
        private string? WaitForNewLauncher()
        {
            var started = Path.Combine(_dir, StartedFile);
            var watch = Stopwatch.StartNew();
            TimeSpan? seen = null, gone = null;
            while (true)
            {
                if (IsConfirmed(started))
                {
                    Log($"Новая версия лаунчера запустилась и сообщила об этом через {watch.Elapsed.TotalSeconds:0.0} с после установки");
                    return null;
                }

                var now = watch.Elapsed;
                if (NewLauncherRunning())
                {
                    if (seen is null)
                        Log($"Новая версия лаунчера запущена через {now.TotalSeconds:0.0} с, ждём сообщения о запуске");
                    seen ??= now;
                    gone = null;
                }
                else if (seen is not null)
                {
                    gone ??= now;
                }

                if (seen is null && now > LauncherAppearTimeout)
                {
                    Log("Новую версию лаунчера за 2 минуты так и не запустили: запуск не проверен, файлы и служба в порядке");
                    return null;
                }

                if (gone is { } goneAt && now - goneAt > LauncherGoneTimeout)
                    return "Новая версия лаунчера закрылась сразу после запуска.";
                if (seen is { } seenAt && now - seenAt > LauncherConfirmTimeout)
                    return $"Новая версия лаунчера запустилась, но за {LauncherConfirmTimeout.TotalSeconds:0} секунд так и не начала работать.";

                Thread.Sleep(500);
            }
        }

        private bool IsConfirmed(string path)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Trim() == _job.Id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false; // как раз записывается
            }
        }

        /// <summary>Запущен лаунчер из папки установки (у любого пользователя).</summary>
        private bool NewLauncherRunning()
        {
            var launcher = Path.Combine(_job.AppDir, LauncherExe);
            var running = false;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LauncherExe)))
            {
                using (process)
                {
                    if (!running && Native.ProcessImagePath(process.Id) is { } path && SamePath(path, launcher))
                        running = true;
                }
            }

            return running;
        }

        /// <summary>Вернуть сохранённые файлы лаунчера, службы и ядра и запись об установке, зарегистрировать прежнюю службу.</summary>
        private void Rollback()
        {
            var watch = Stopwatch.StartNew();
            StopService();
            CloseProcessesIn(_job.AppDir);
            var backup = Path.Combine(_dir, BackupDir);
            UpdateFiles.Mirror(Path.Combine(backup, "app"), _job.AppDir);
            UpdateFiles.Mirror(Path.Combine(backup, "core"), _job.CoreDir, CoreRunDir);
            RestoreUninstallKey(Path.Combine(backup, UninstallBackup));
            Log($"Файлы и запись об установке версии {_job.FromVersion} возвращены за {watch.ElapsedMilliseconds} мс");

            var start = new ProcessStartInfo(Path.Combine(_job.CoreDir, ServiceExe), "--install")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Прежняя служба не запустилась.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
                throw new InvalidOperationException("Регистрация прежней службы не закончилась за 2 минуты.");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Прежняя служба не зарегистрировалась (код {process.ExitCode}): {error.Result.Trim()}");
            Log("Прежняя служба зарегистрирована и запущена: " + output.Result.Trim());
        }

        /// <summary>Установщик и резервная копия больше не нужны; журналы и status.json остаются до следующего обновления.</summary>
        private void Cleanup()
        {
            foreach (var dir in new[] { PackageDir, BackupDir })
            {
                try
                {
                    UpdateFiles.DeleteDirectory(Path.Combine(_dir, dir));
                }
                catch (Exception ex)
                {
                    Log($"Не удалось удалить {dir}: {ex.Message}");
                }
            }
        }

        private void Stage(string stage, string message)
        {
            lock (_sync)
            {
                _state.Stage = stage;
                _state.Message = message;
                if (stage != UpdateStages.RollingBack)
                    _workStage = stage;
                else
                    _state.FailedStage = _workStage;
            }

            Log(message);
            SaveState();
        }

        private bool Finish(bool ok, bool rolledBack, string? error)
        {
            lock (_sync)
            {
                _state.FailedStage = ok ? null : _workStage;
                _state.Stage = UpdateStages.Done;
                _state.Done = true;
                _state.Ok = ok;
                _state.RolledBack = rolledBack;
                _state.Error = error;
                _state.Message = ok
                    ? $"Установлена версия {_job.Version}"
                    : rolledBack
                        ? $"Обновление не установилось, работает версия {_job.FromVersion}"
                        : "Обновление не установилось";
            }

            Log(_state.Message + (error is null ? string.Empty : ": " + error));
            _heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
            // Итог должен попасть в файл: по нему окно ожидания запускает лаунчер.
            for (var attempt = 0; attempt < 40 && !SaveState(); attempt++)
                Thread.Sleep(250);
            return ok;
        }

        /// <summary>Записать status.json. false — не вышло (файл как раз читают), следующая запись через секунду.</summary>
        private bool SaveState()
        {
            lock (_sync)
            {
                _state.Updated = DateTime.UtcNow;
                try
                {
                    _state.Write(_statePath);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        private void Log(string text)
        {
            lock (_sync)
            {
                try
                {
                    _log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}");
                }
                catch (IOException)
                {
                    // журнал не главное
                }
            }
        }

        public void Dispose()
        {
            _heartbeat.Dispose();
            _log.Dispose();
        }
    }
}
