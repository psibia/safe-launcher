using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Ключ лицензии. Лаунчер работает, только если на компьютере сохранён ключ, хеш которого есть
/// в installer\license-hashes.txt: этот файл встраивается в лаунчер при сборке, в GitHub его нет.
/// Проверка идёт при запуске лаунчера и без сети: ключ сравнивается со встроенными хешами. Сеть, службы
/// и настройки Windows она не трогает.
///
/// Где хранится ключ. В обоих местах он зашифрован средствами Windows для этого компьютера (DPAPI): на другом
/// компьютере запись не расшифруется, а от пароля пользователя и домена шифр не зависит.
///   * HKLM\Software\SafeLauncher, значение LicenseKey: записывает установщик (параметр --store-license),
///     действует для всех пользователей компьютера.
///   * %LOCALAPPDATA%\KillSwitchLauncher\license.dat: если ключ ввели в окне лаунчера (без прав администратора
///     в HKLM не записать).
/// Обновление ключ не трогает. При удалении лаунчера установщик стирает обе записи.
/// </summary>
public static class License
{
    /// <summary>Установщик сохраняет ключ: «KillSwitchLauncher.exe --store-license КЛЮЧ» с правами администратора.</summary>
    public const string StoreArgument = "--store-license";

    internal const string RegistryKeyPath = @"Software\SafeLauncher";
    internal const string RegistryValueName = "LicenseKey";

    // Привязывает шифр к лаунчеру (см. ProtectedText).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SafeLauncher.License.v1");

    private static readonly Lazy<HashSet<string>> KnownHashes = new(LoadKnownHashes);

    private static string UserFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillSwitchLauncher", "license.dat");

    /// <summary>В лаунчер встроены хеши ключей. Если нет (собран без license-hashes.txt), не подойдёт ни один ключ.</summary>
    public static bool HasKnownKeys => KnownHashes.Value.Count > 0;

    /// <summary>На компьютере сохранён подходящий ключ: для всех пользователей или для текущего.</summary>
    public static bool IsActivated() => IsValid(ReadMachineKey()) || IsValid(ReadUserKey());

    public static bool IsValid(string? key) => key is not null && IsValid(key, KnownHashes.Value);

    /// <summary>
    /// Сохраняет ключ, введённый в окне лаунчера, для текущего пользователя. False — не удалось записать файл:
    /// лаунчер всё равно работает, а ключ спросит при следующем запуске.
    /// </summary>
    public static bool SaveForUser(string key)
    {
        try
        {
            var path = UserFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, ProtectedText.ProtectForMachine(Encoding.ASCII.GetBytes(Normalize(key)), Entropy));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            TechLog.Warn(SafeLauncher.Logging.LogAreas.Settings, SafeLauncher.Logging.LogCodes.SettingsChange,
                "Не удалось сохранить ключ лицензии: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Запуск с <see cref="StoreArgument"/> из установщика: ключ записывается в HKLM для всех пользователей.
    /// Код выхода: 0 — записан, 2 — ключ не подошёл, 1 — не удалось записать (лаунчер тогда спросит ключ сам).
    /// </summary>
    public static int Store(string[] args)
    {
        var key = args.Length > 1 ? args[1] : "";
        if (!IsValid(key))
            return 2;

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var settings = hklm.CreateSubKey(RegistryKeyPath, writable: true);
            settings.SetValue(RegistryValueName, ProtectedText.ProtectForMachine(Encoding.ASCII.GetBytes(Normalize(key)), Entropy),
                RegistryValueKind.Binary);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>Ключ без дефисов и пробелов, заглавными буквами: так он хешируется.</summary>
    internal static string Normalize(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key.ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>SHA-256 ключа в виде строки из 64 строчных шестнадцатеричных знаков, как в license-hashes.txt.</summary>
    internal static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Normalize(key)))).ToLowerInvariant();

    internal static bool IsValid(string key, IReadOnlySet<string> hashes) =>
        Normalize(key).Length > 0 && hashes.Contains(Hash(key));

    /// <summary>Хеши из текста license-hashes.txt: по одному в строке, строки с «;» считаются комментариями.</summary>
    internal static HashSet<string> ParseHashes(string text)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 64 && !line.Contains(';'))
                hashes.Add(line.ToLowerInvariant());
        }

        return hashes;
    }

    private static HashSet<string> LoadKnownHashes()
    {
        using var stream = typeof(License).Assembly.GetManifestResourceStream("license-hashes.txt");
        if (stream is null)
            return new HashSet<string>();
        using var reader = new StreamReader(stream);
        return ParseHashes(reader.ReadToEnd());
    }

    private static string? ReadMachineKey()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var settings = hklm.OpenSubKey(RegistryKeyPath);
            return settings?.GetValue(RegistryValueName) is byte[] data ? Decrypt(data) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadUserKey()
    {
        try
        {
            var path = UserFilePath;
            return File.Exists(path) ? Decrypt(File.ReadAllBytes(path)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Decrypt(byte[] data) =>
        ProtectedText.UnprotectForMachine(data, Entropy) is { } plain ? Encoding.ASCII.GetString(plain) : null;
}
