using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Издатель из цифровой подписи программы (Authenticode): имя владельца сертификата, которым она подписана, например
/// «Discord Inc.». Только если подпись проверена Windows. Сертификат продлевают раз в год-три, и его отпечаток меняется,
/// а имя издателя остаётся, поэтому сравнивается имя. Отзыв сертификата не проверяется: проверка ходила бы в интернет
/// мимо VPN. Результат запоминается по пути, размеру и времени изменения файла.
/// </summary>
public static class Signatures
{
    private readonly record struct Entry(DateTime Written, long Length, string? Publisher);

    private const int MaxEntries = 4096;

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Издатель программы. Проверка подписи большого файла занимает до секунды, поэтому вызывать не из потока интерфейса
    /// и не под блокировкой. null — подписи нет, она не прошла проверку или файл не прочитать. Любой поток.
    /// </summary>
    public static string? PublisherOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;
            if (Cache.TryGetValue(path, out var known) && Fresh(file, known))
                return known.Publisher;

            var publisher = Verify(path) ? SignerName(path) : null;
            if (Cache.Count >= MaxEntries)
                Cache.Clear();
            Cache[path] = new Entry(file.LastWriteTimeUtc, file.Length, publisher);
            return publisher;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Издатель, если подпись этой версии файла уже проверена. Иначе false, а проверка запускается в фоне и к следующему
    /// вопросу будет готова. Для мест, где ждать нельзя: проход наблюдения и ответ ядру «чей процесс». Любой поток.
    /// </summary>
    public static bool TryPeek(string path, out string? publisher)
    {
        publisher = null;
        try
        {
            if (Cache.TryGetValue(path, out var known) && Fresh(new FileInfo(path), known))
            {
                publisher = known.Publisher;
                return true;
            }
        }
        catch (Exception)
        {
            return true;
        }

        if (Pending.TryAdd(path, 0))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    PublisherOf(path);
                }
                finally
                {
                    Pending.TryRemove(path, out _);
                }
            });
        }

        return false;
    }

    private static bool Fresh(FileInfo file, Entry known) =>
        file.Exists && file.LastWriteTimeUtc == known.Written && file.Length == known.Length;

    /// <summary>Имя владельца сертификата подписи (CN, иначе первое понятное имя).</summary>
    private static string? SignerName(string path)
    {
        using var signer = X509Certificate.CreateFromSignedFile(path);
        using var certificate = new X509Certificate2(signer);
        var name = certificate.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    // ---------------------------------------------------------------- WinVerifyTrust

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionIgnore = 0;
    private const uint RevocationCheckNone = 0x00000010;
    private const uint CacheOnlyUrlRetrieval = 0x00001000;
    private const uint DisableMd2Md4 = 0x00002000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);

    /// <summary>Подпись файла цела и сертификат ведёт к доверенному корню Windows (без проверки отзыва).</summary>
    private static bool Verify(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = filePtr,
                dwStateAction = StateActionIgnore,
                dwProvFlags = RevocationCheckNone | CacheOnlyUrlRetrieval | DisableMd2Md4
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
