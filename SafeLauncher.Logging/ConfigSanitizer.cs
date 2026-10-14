using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SafeLauncher.Logging;

/// <summary>
/// Очищенная копия конфига ядра для журналов и выгрузки: ключи, пароли, UUID и секрет управления удаляются,
/// остальные строки проходят через <see cref="LogMasker"/> (адреса и серверы — по его настройке). Структура остается целиком.
/// </summary>
public static class ConfigSanitizer
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "private_key", "pre_shared_key", "preshared_key", "password", "uuid", "secret", "auth", "auth_str", "psk", "token",
        "short_id", "public_key", "key", "certificate", "key_path", "certificate_path", "obfs_password", "username"
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Sanitize(string json, LogMasker masker, bool hideAddresses)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return hideAddresses ? masker.MaskAddressesNow(json) : masker.MaskSecretsOnly(json);
        }

        if (root is null)
            return "null";
        var clean = Clean(root, null, masker, hideAddresses);
        return clean?.ToJsonString(Pretty) ?? "null";
    }

    public static JsonNode? Sanitize(JsonNode? node, LogMasker masker, bool hideAddresses) =>
        node is null ? null : Clean(node, null, masker, hideAddresses);

    private static JsonNode? Clean(JsonNode node, string? key, LogMasker masker, bool hide)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (k, v) in obj.ToList())
                {
                    if (SecretKeys.Contains(k))
                    {
                        result[k] = v is null ? null : JsonValue.Create("***");
                        continue;
                    }
                    result[k] = v is null ? null : Clean(v, k, masker, hide);
                }
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(item is null ? null : Clean(item, key, masker, hide));
                return result;
            }
            case JsonValue value when value.TryGetValue<string>(out var s):
                return JsonValue.Create(hide ? masker.MaskAddressesNow(s) : masker.MaskSecretsOnly(s));
            default:
                return node.DeepClone();
        }
    }
}
