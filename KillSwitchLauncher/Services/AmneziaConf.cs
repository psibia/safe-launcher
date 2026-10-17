using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace KillSwitchLauncher.Services;

/// <summary>
/// Параметры AmneziaWG из раздела [Interface] файла .conf (версии 1.0, 1.5, 2.0, 3 и 3.1) → объект «amnezia» подключения ядра.
/// Проверяет всё, что проверяет ядро, а кое-где строже: файл, который ядро не примет, не должен до него дойти,
/// иначе ядро не запустится и вместе с ним остановятся все приложения.
/// </summary>
internal static class AmneziaConf
{
    /// <summary>Самый большой пакет, который ядро соглашается отправить: 65507 байт UDP по IPv4 без 8 служебных.</summary>
    private const int MaxPacket = 65499;

    /// <summary>Размеры сообщений WireGuard (инициация, ответ, cookie, данные), к которым S1–S4 добавляют случайные байты.</summary>
    private static readonly int[] MessageSizes = { 148, 92, 64, 16 };

    /// <summary>Ключи AmneziaWG в [Interface].</summary>
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5", "J1", "J2", "J3", "Itime",
        "HeaderProtectionKey", "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout",
        "MaxHandshakeAttempts", "RandomTrailers", "DisableCookies"
    };

    /// <summary>Сроки и попытки (AmneziaWG 3) и их имена в настройках ядра.</summary>
    private static readonly (string Key, string Core)[] Timings =
    {
        ("RekeyAfterTime", "rekey_after_time"), ("RekeyTimeout", "rekey_timeout"), ("RejectAfterTime", "reject_after_time"),
        ("KeepaliveTimeout", "keepalive_timeout"), ("MaxHandshakeAttempts", "max_handshake_attempts")
    };

    public static bool IsKey(string key) => Keys.Contains(key);

    /// <summary>
    /// Объект «amnezia» для ядра; null — в файле нет параметров AmneziaWG, это обычный WireGuard. Пустое значение
    /// (так программа AmneziaVPN пишет неиспользуемые I1–I5, J1–J3 и Itime) означает «не задано».
    /// </summary>
    public static JsonObject? Parse(IReadOnlyDictionary<string, string> iface)
    {
        string? Value(string key) => iface.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value.Trim() : null;

        uint Number(string key, uint max)
        {
            if (Value(key) is not { } text)
                return 0;
            if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > max)
                throw Invalid(key, text, $"Нужно целое число от 0 до {max}.");
            return number;
        }

        // Пакеты-помехи перед рукопожатием.
        var jc = Number("Jc", 1000);
        var jmin = Number("Jmin", MaxPacket);
        var jmax = Number("Jmax", MaxPacket);
        if (jc > 0 && jmax < jmin)
            throw new ConnectionParseException($"В файле AmneziaWG Jmax ({jmax}) меньше Jmin ({jmin}).");

        // Случайные байты перед сообщениями.
        var paddings = new uint[4];
        for (var i = 0; i < paddings.Length; i++)
            paddings[i] = Number($"S{i + 1}", (uint)(MaxPacket + 8 - 32 - MessageSizes[i]));

        // Заголовки сообщений: без H1–H4 остаются обычные 1–4, и диапазоны не должны пересекаться.
        var headers = new (uint Lo, uint Hi)[4];
        var headerTexts = new string?[4];
        for (var i = 0; i < headers.Length; i++)
        {
            var key = $"H{i + 1}";
            headers[i] = ((uint)i + 1, (uint)i + 1);
            if (Value(key) is not { } text)
                continue;
            headers[i] = Range(text) ?? throw Invalid(key, text, "Нужно число или диапазон чисел «от-до».");
            headerTexts[i] = RangeText(headers[i]);
        }

        for (var i = 0; i < headers.Length; i++)
        {
            for (var j = i + 1; j < headers.Length; j++)
            {
                if (headers[i].Lo <= headers[j].Hi && headers[j].Lo <= headers[i].Hi)
                    throw new ConnectionParseException($"В файле AmneziaWG пересекаются H{i + 1} и H{j + 1}: у каждого вида пакетов должен быть свой заголовок.");
            }
        }

        // Пакеты-подписи I1–I5 и, в AmneziaWG 1.5, J1–J3 с Itime.
        var chains = new (string Key, string? Spec)[8];
        for (var i = 0; i < chains.Length; i++)
        {
            var key = i < 5 ? $"I{i + 1}" : $"J{i - 4}";
            chains[i] = (key, Value(key) is { } spec && Chain(key, spec) ? spec : null);
        }

        var itime = Number("Itime", uint.MaxValue);

        // AmneziaWG 3: защита заголовков. Ключ из нулей — то же, что без ключа.
        string? headerKey = null;
        if (Value("HeaderProtectionKey") is { } keyText)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(keyText);
            }
            catch (FormatException)
            {
                key = Array.Empty<byte>();
            }

            if (key.Length != 32)
                throw new ConnectionParseException("В файле AmneziaWG повреждён ключ HeaderProtectionKey.");
            if (key.Any(b => b != 0))
            {
                for (var i = 0; i < paddings.Length; i++)
                {
                    if (paddings[i] < 12)
                        throw new ConnectionParseException(
                            $"В файле AmneziaWG задан HeaderProtectionKey, поэтому S1–S4 должны быть не меньше 12, а S{i + 1} = {paddings[i]}.");
                }

                headerKey = Convert.ToBase64String(key);
            }
        }

        string? NonZeroRange(string key)
        {
            if (Value(key) is not { } text)
                return null;
            var range = Range(text) ?? throw Invalid(key, text, "Нужно число или диапазон чисел «от-до».");
            return range.Hi == 0 ? null : RangeText(range);
        }

        var contentPadding = NonZeroRange("ContentPaddingAddition");
        var timings = Timings.Select(t => (t.Core, Text: NonZeroRange(t.Key))).ToList();

        bool Flag(string key)
        {
            if (Value(key) is not { } text)
                return false;
            return text.ToLowerInvariant() switch
            {
                "on" or "true" or "yes" or "t" => true,
                "off" or "false" or "no" or "f" => false,
                _ when text.All(char.IsAsciiDigit) => text.Any(c => c != '0'),
                _ => throw Invalid(key, text, "Нужно on или off.")
            };
        }

        var randomTrailers = Flag("RandomTrailers");
        var disableCookies = Flag("DisableCookies");

        // Порядок полей — как в настройках ядра.
        var result = new JsonObject();
        if (jc > 0)
            result["jc"] = jc;
        if (jmin > 0)
            result["jmin"] = jmin;
        if (jmax > 0)
            result["jmax"] = jmax;
        for (var i = 0; i < paddings.Length; i++)
        {
            if (paddings[i] > 0)
                result[$"s{i + 1}"] = paddings[i];
        }

        for (var i = 0; i < headerTexts.Length; i++)
        {
            if (headerTexts[i] is { } text)
                result[$"h{i + 1}"] = text;
        }

        foreach (var (key, spec) in chains)
        {
            if (spec is not null)
                result[key.ToLowerInvariant()] = spec;
        }

        if (itime > 0)
            result["itime"] = itime;
        if (headerKey is not null)
            result["header_protection_key"] = headerKey;
        if (contentPadding is not null)
            result["content_padding_addition"] = contentPadding;
        foreach (var (core, text) in timings)
        {
            if (text is not null)
                result[core] = text;
        }

        if (randomTrailers)
            result["random_trailers"] = true;
        if (disableCookies)
            result["disable_cookies"] = true;
        return result.Count > 0 ? result : null;
    }

    /// <summary>«100» или «100-200», пробелы не важны; null — это не диапазон.</summary>
    internal static (uint Lo, uint Hi)? Range(string text)
    {
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        var dash = compact.IndexOf('-');
        var loText = dash < 0 ? compact : compact[..dash];
        var hiText = dash < 0 ? compact : compact[(dash + 1)..];
        if (!uint.TryParse(loText, NumberStyles.None, CultureInfo.InvariantCulture, out var lo) ||
            !uint.TryParse(hiText, NumberStyles.None, CultureInfo.InvariantCulture, out var hi) || hi < lo)
            return null;
        return (lo, hi);
    }

    internal static string RangeText((uint Lo, uint Hi) range) =>
        range.Lo == range.Hi
            ? range.Lo.ToString(CultureInfo.InvariantCulture)
            : $"{range.Lo.ToString(CultureInfo.InvariantCulture)}-{range.Hi.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Пакет I1–I5 или J1–J3: теги вроде &lt;b 0x0102&gt;&lt;r 16&gt;&lt;t&gt;, как их разбирает ядро (текст между тегами оно
    /// пропускает). false — тегов нет, такой пакет ядро не отправляет.
    /// </summary>
    private static bool Chain(string key, string spec)
    {
        // Ядро не примет разрыв строки внутри значения.
        if (spec.Any(c => c != '\t' && char.IsControl(c)))
            throw Invalid(key, spec, "В нём есть невидимый символ, например разрыв строки.");

        var tags = 0;
        long size = 0;
        for (var position = 0; ;)
        {
            var start = spec.IndexOf('<', position);
            if (start < 0)
                break;
            var end = spec.IndexOf('>', start);
            if (end < 0)
                throw Invalid(key, spec, "Не хватает закрывающей скобки «>».");
            position = end + 1;
            var parts = spec[(start + 1)..end].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                throw Invalid(key, spec, "В нём пустой тег «<>».");

            var name = parts[0].ToLowerInvariant();
            var value = parts.Length > 1 ? parts[1] : "";
            size += name switch
            {
                "b" => HexBytes(value) ?? throw Invalid(key, spec, "В теге <b> нужны байты в шестнадцатеричном виде, например <b 0x0102>."),
                "t" => 4,
                "r" or "rc" or "rd" or "dz" => TagNumber(value, 65507) ?? throw Invalid(key, spec, $"В теге <{parts[0]}> нужна длина от 0 до 65507."),
                "d" or "ds" => 0,
                "c" => value.Length == 0 ? 8 : throw Invalid(key, spec, "Тег <c> пишется без числа."),
                "wt" => TagNumber(value, 5000) is not null ? 0 : throw Invalid(key, spec, "В теге <wt> нужна пауза от 0 до 5000 мс."),
                _ => throw Invalid(key, spec, $"Неизвестный тег <{parts[0]}>.")
            };
            tags++;
        }

        if (size > MaxPacket)
            throw Invalid(key, spec, $"Пакет получается больше {MaxPacket} байт.");
        return tags > 0;
    }

    /// <summary>Сколько байт в «0x0102» (нечётное число цифр — как с нулём впереди); null — не байты.</summary>
    private static int? HexBytes(string value)
    {
        if (value.StartsWith("0x", StringComparison.Ordinal))
            value = value[2..];
        if (value.StartsWith("0X", StringComparison.Ordinal))
            value = value[2..];
        if (value.Length == 0 || !value.All(char.IsAsciiHexDigit))
            return null;
        return (value.Length + 1) / 2;
    }

    private static int? TagNumber(string value, int max) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number >= 0 && number <= max ? number : null;

    private static ConnectionParseException Invalid(string key, string value, string hint) =>
        new($"В файле AmneziaWG неверный параметр {key}: «{(value.Length > 40 ? value[..40] + "…" : value)}». {hint}");
}
