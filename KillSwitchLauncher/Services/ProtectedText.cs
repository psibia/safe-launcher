using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Шифрование ключей подключений в settings.json средствами Windows (DPAPI): расшифровать их может только
/// этот пользователь на этом компьютере. В файле зашифрованное значение выглядит как «dpapi:…».
/// </summary>
public static class ProtectedText
{
    public const string Prefix = "dpapi:";

    // Привязывает шифр к лаунчеру: другая программа того же пользователя без этой строки его не расшифрует.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SafeLauncher.Settings.v1");
    private const int CryptProtectUiForbidden = 0x1;

    public static string Protect(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        return Prefix + Convert.ToBase64String(Transform(data, protect: true));
    }

    /// <summary>
    /// Расшифровывает «dpapi:…»; строку без префикса возвращает как есть (значение вписали в файл вручную).
    /// Null — не удалось расшифровать: например, файл настроек перенесли с другого компьютера.
    /// </summary>
    public static string? Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        try
        {
            var data = Convert.FromBase64String(stored[Prefix.Length..]);
            return Encoding.UTF8.GetString(Transform(data, protect: false));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicFailure)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            DataBlob outBlob;
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
            if (!ok)
                throw new CryptographicFailure(Marshal.GetLastWin32Error());

            try
            {
                var output = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, output, 0, outBlob.Size);
                return output;
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    private sealed class CryptographicFailure : Exception
    {
        public CryptographicFailure(int error) : base($"DPAPI: ошибка {error}")
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>Строка в settings.json в зашифрованном виде (см. <see cref="ProtectedText"/>).</summary>
public sealed class ProtectedStringConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : ProtectedText.Unprotect(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(ProtectedText.Protect(value));
    }
}

/// <summary>Список строк в settings.json, каждая в зашифрованном виде. Нерасшифрованные пропускаются.</summary>
public sealed class ProtectedStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<string>();
        if (reader.TokenType == JsonTokenType.Null)
            return list;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Ожидался список строк.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String && ProtectedText.Unprotect(reader.GetString()!) is { } text)
                list.Add(text);
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(ProtectedText.Protect(item));
        writer.WriteEndArray();
    }
}
