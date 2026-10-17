using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SafeLauncher.Updates;

namespace UpdateSigner;

/// <summary>
/// Ключ подписи выпусков и подпись установщика. Команды:
///   new-key ЗАКРЫТЫЙ.pem ОТКРЫТЫЙ.pem — создать пару ключей (один раз; закрытый ключ хранить и не терять);
///   sign УСТАНОВЩИК.exe ЗАКРЫТЫЙ.pem ОТКРЫТЫЙ.pem — записать рядом УСТАНОВЩИК.exe.sig и сразу проверить его открытым ключом.
/// Код выхода 0 — готово, иначе ошибка (текст в консоли).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            switch (args.Length > 0 ? args[0] : string.Empty)
            {
                case "new-key" when args.Length == 3:
                    return NewKey(args[1], args[2]);
                case "sign" when args.Length == 4:
                    return Sign(args[1], args[2], args[3]);
                default:
                    Console.Error.WriteLine("Использование: new-key ЗАКРЫТЫЙ.pem ОТКРЫТЫЙ.pem | sign УСТАНОВЩИК.exe ЗАКРЫТЫЙ.pem ОТКРЫТЫЙ.pem");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int NewKey(string privatePath, string publicPath)
    {
        if (File.Exists(privatePath) || File.Exists(publicPath))
        {
            Console.Error.WriteLine("Ключ уже есть, новый не создаётся: с новым ключом установленные лаунчеры перестали бы принимать обновления.");
            return 1;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, key.ExportECPrivateKeyPem() + "\n");
        File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem() + "\n");
        Console.WriteLine("Создан ключ подписи выпусков: " + Path.GetFullPath(privatePath));
        return 0;
    }

    private static int Sign(string setupPath, string privatePath, string publicPath)
    {
        var version = UpdatePackage.VersionFromFileName(setupPath)
                      ?? throw new InvalidOperationException("В имени установщика нет версии: " + Path.GetFileName(setupPath));

        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(File.ReadAllText(privatePath));
        var text = UpdatePackage.Sign(setupPath, version, privateKey);

        // Проверка той же подписи открытым ключом, который встроен в лаунчер: ключи из разных пар здесь и обнаружатся.
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(File.ReadAllText(publicPath));
        UpdatePackage.Verify(setupPath, text, publicKey);

        var signaturePath = setupPath + UpdatePackage.SignatureExtension;
        File.WriteAllText(signaturePath, text, new UTF8Encoding(false));
        Console.WriteLine("Подпись: " + Path.GetFileName(signaturePath));
        return 0;
    }
}
