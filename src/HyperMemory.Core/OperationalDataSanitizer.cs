using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyperMemory.Core;

public static class OperationalDataSanitizer
{
    public static string RedactJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            var node = JsonNode.Parse(json) ?? throw new JsonException("JSON value is null.");
            RedactNode(node);
            return node.ToJsonString();
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Operational JSON must be valid.", nameof(json), error);
        }
    }

    public static IReadOnlyDictionary<string, string>? RedactMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return null;
        return metadata.ToDictionary(item => item.Key,
            item => IsSensitiveName(item.Key) ? "[REDACTED]" : SensitiveDataRedactor.Redact(item.Value).Value,
            StringComparer.Ordinal);
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToArray())
            {
                if (property.Value is null) continue;
                if (IsSensitiveName(property.Key)) objectNode[property.Key] = "[REDACTED]";
                else if (property.Key.EndsWith("Json", StringComparison.OrdinalIgnoreCase) &&
                         property.Value is JsonValue nestedValue && nestedValue.TryGetValue<string>(out var nestedJson) &&
                         TryRedactNestedJson(nestedJson, out var redactedNested))
                    objectNode[property.Key] = redactedNested;
                else RedactNode(property.Value);
            }
            return;
        }
        if (node is JsonArray arrayNode)
        {
            for (var index = 0; index < arrayNode.Count; index++)
            {
                var item = arrayNode[index];
                if (item is not null) RedactNode(item);
            }
            return;
        }
        if (node is JsonValue valueNode && valueNode.TryGetValue<string>(out var value))
            valueNode.ReplaceWith(JsonValue.Create(SensitiveDataRedactor.Redact(value).Value));
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized is "password" or "passwd" or "contrasena" or "apikey" or "accesstoken" or
            "authtoken" or "token" or "secret" or "privatekey";
    }

    private static bool TryRedactNestedJson(string value, out string redacted)
    {
        try
        {
            redacted = RedactJson(value);
            return true;
        }
        catch (ArgumentException)
        {
            redacted = value;
            return false;
        }
    }
}
