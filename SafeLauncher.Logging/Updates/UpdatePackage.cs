using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SafeLauncher.Updates;

/// <summary>Что подписано в файле .sig: версия и файл установщика.</summary>
public sealed record UpdateManifest(Version Version, string FileName, long Size, string Sha256);

/// <summary>Подпись не прошла проверку. Сообщение — для пользователя.</summary>
public sealed class UpdateSignatureException : Exception
{
    public UpdateSignatureException(string message) : base(message)
    {
    }
}

/// <summary>
/// Подпись выпусков. Рядом с установщиком SafeLauncher-Setup-X.exe в выпуске GitHub лежит SafeLauncher-Setup-X.exe.sig:
/// версия, имя, размер и SHA-256 установщика и подпись этих строк закрытым ключом автора (ECDSA P-256, SHA-256).
/// Закрытый ключ есть только у автора (installer\update-signing-key.pem, в GitHub его нет), подписывает
/// build-installer.cmd. Открытый ключ (installer\update-public-key.pem) встроен в эту библиотеку, а значит в лаунчер и службу:
/// служба ставит обновление без запроса прав, только если подпись сходится и версия новее установленной.
///
/// Файл .sig — текст UTF-8:
///   SafeLauncher update 1
///   version: 2.4.0
///   file: SafeLauncher-Setup-2.4.0.exe
///   size: 123456789
///   sha256: (64 строчных шестнадцатеричных знака)
///   signature: (base64)
/// Подписаны первые пять строк, каждая с \n в конце.
/// </summary>
public static class UpdatePackage
{
    public const string SignatureExtension = ".sig";
    public const string PublicKeyResource = "update-public-key.pem";

    private const string Header = "SafeLauncher update 1";

    private static readonly Regex VersionInName = new(@"(\d+(?:\.\d+){1,3})\.exe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Lazy<string?> PublicKeyPem = new(LoadPublicKeyPem);

    /// <summary>В сборку встроен открытый ключ. Нет — обновление через службу недоступно (сборка без installer\update-public-key.pem).</summary>
    public static bool HasPublicKey => PublicKeyPem.Value is not null;

    /// <summary>Открытый ключ, встроенный в сборку; null — не встроен.</summary>
    public static ECDsa? CreatePublicKey()
    {
        if (PublicKeyPem.Value is not { } pem)
            return null;
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    /// <summary>Версия из имени установщика (SafeLauncher-Setup-2.4.0.0.exe → 2.4.0); null — в имени нет версии.</summary>
    public static Version? VersionFromFileName(string fileName)
    {
        var match = VersionInName.Match(Path.GetFileName(fileName));
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? Normalize(version) : null;
    }

    /// <summary>Версия из трёх чисел: 2.4.0.0 и 2.4 → 2.4.0.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>Текст файла .sig для установщика (подписывает build-installer.cmd через tools\UpdateSigner).</summary>
    public static string Sign(string setupPath, Version version, ECDsa privateKey)
    {
        var manifest = new UpdateManifest(Normalize(version), Path.GetFileName(setupPath), new FileInfo(setupPath).Length, FileSha256(setupPath));
        var body = Body(manifest);
        var signature = privateKey.SignData(Encoding.UTF8.GetBytes(body), HashAlgorithmName.SHA256);
        return body + "signature: " + Convert.ToBase64String(signature) + "\n";
    }

    /// <summary>
    /// Проверяет установщик по файлу .sig: подпись ключом автора, размер и SHA-256. Возвращает подписанные сведения.
    /// Не сошлось — <see cref="UpdateSignatureException"/> с объяснением для пользователя.
    /// </summary>
    public static UpdateManifest Verify(string setupPath, string signatureText, ECDsa publicKey)
    {
        var manifest = VerifySignature(signatureText, publicKey);
        VerifyFile(setupPath, manifest);
        return manifest;
    }

    /// <summary>Проверяет только подпись файла .sig (сам установщик не читается). Не сошлось — <see cref="UpdateSignatureException"/>.</summary>
    public static UpdateManifest VerifySignature(string signatureText, ECDsa publicKey)
    {
        var (manifest, signature) = Parse(signatureText);
        if (!publicKey.VerifyData(Encoding.UTF8.GetBytes(Body(manifest)), signature, HashAlgorithmName.SHA256))
            throw new UpdateSignatureException("Подпись обновления не совпала с ключом автора. Файл подменён или подписан другим ключом.");
        return manifest;
    }

    /// <summary>Установщик совпадает с подписанным: размер и SHA-256. Не совпал — <see cref="UpdateSignatureException"/>.</summary>
    public static void VerifyFile(string setupPath, UpdateManifest manifest)
    {
        var info = new FileInfo(setupPath);
        if (!info.Exists)
            throw new UpdateSignatureException("Файл обновления не найден.");
        if (info.Length != manifest.Size)
            throw new UpdateSignatureException($"Размер файла обновления не совпал с подписанным ({info.Length} вместо {manifest.Size} байт).");
        if (!string.Equals(FileSha256(setupPath), manifest.Sha256, StringComparison.Ordinal))
            throw new UpdateSignatureException("Содержимое файла обновления не совпало с подписанным: файл повреждён или подменён.");
    }

    /// <summary>Разбор файла .sig. Формат не тот — <see cref="UpdateSignatureException"/>.</summary>
    internal static (UpdateManifest Manifest, byte[] Signature) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 6 || lines[0].Trim() != Header)
            throw new UpdateSignatureException("Файл подписи обновления в неизвестном формате.");

        string Field(int index, string name)
        {
            var line = lines[index];
            var prefix = name + ": ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                throw new UpdateSignatureException("Файл подписи обновления повреждён.");
            return line[prefix.Length..].Trim();
        }

        if (!Version.TryParse(Field(1, "version"), out var version))
            throw new UpdateSignatureException("В файле подписи обновления нет версии.");
        var file = Field(2, "file");
        if (file.Length == 0 || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            throw new UpdateSignatureException("Файл подписи обновления повреждён.");
        if (!long.TryParse(Field(3, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
            throw new UpdateSignatureException("В файле подписи обновления нет размера файла.");
        var sha = Field(4, "sha256");
        if (sha.Length != 64 || !Regex.IsMatch(sha, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new UpdateSignatureException("В файле подписи обновления нет контрольной суммы.");
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Field(5, "signature"));
        }
        catch (FormatException)
        {
            throw new UpdateSignatureException("Файл подписи обновления повреждён.");
        }

        return (new UpdateManifest(Normalize(version), file, size, sha), signature);
    }

    /// <summary>Подписываемые строки.</summary>
    private static string Body(UpdateManifest manifest) =>
        Header + "\n" +
        "version: " + manifest.Version.ToString(3) + "\n" +
        "file: " + manifest.FileName + "\n" +
        "size: " + manifest.Size.ToString(CultureInfo.InvariantCulture) + "\n" +
        "sha256: " + manifest.Sha256 + "\n";

    public static string FileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? LoadPublicKeyPem()
    {
        using var stream = typeof(UpdatePackage).Assembly.GetManifestResourceStream(PublicKeyResource);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        return pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal) ? pem : null;
    }
}
