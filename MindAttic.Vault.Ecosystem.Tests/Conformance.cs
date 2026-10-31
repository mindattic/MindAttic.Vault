using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>
/// Checks stored JSON against a manifest's fields: kind, choices, range, nested objects and collection items.
/// Problems name the field (its full name, with the item index for collection items), never the value.
/// </summary>
public static class Conformance
{
    /// <summary>The kinds Vault.Desktop has an editor for (Unknown renders read-only).</summary>
    public static readonly IReadOnlySet<SettingKind> RenderableKinds = Enum.GetValues<SettingKind>().Where(k => k != SettingKind.Unknown).ToHashSet();

    /// <summary>Checks every field of <paramref name="fields"/> present in <paramref name="obj"/>.</summary>
    public static void CheckObject(IEnumerable<SettingField> fields, JsonObject obj, string path, List<string> problems)
    {
        foreach (var field in fields)
        {
            var key = KeyOf(obj, field.Property);
            if (key is null) continue; // missing = the app's default applies
            CheckValue(field, obj[key], path + ":" + field.Property, problems);
        }
    }

    /// <summary>Checks one stored value against its field.</summary>
    public static void CheckValue(SettingField field, JsonNode? node, string name, List<string> problems)
    {
        if (node is null)
        {
            if (!field.Nullable) problems.Add($"{name}: null, but the setting isn't nullable");
            return;
        }

        switch (field.Kind)
        {
            case SettingKind.Text:
            case SettingKind.Multiline:
            case SettingKind.Path:
            case SettingKind.Url:
                if (!IsString(node)) problems.Add($"{name}: not a string ({field.Kind})");
                break;
            case SettingKind.Bool:
                if (node.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) problems.Add($"{name}: not a boolean");
                break;
            case SettingKind.Integer:
                if (!TryNumber(node, out var i)) problems.Add($"{name}: not a number (Integer)");
                else if (i != Math.Floor(i)) problems.Add($"{name}: not a whole number (Integer)");
                else CheckRange(field, i, name, problems);
                break;
            case SettingKind.Decimal:
                if (!TryNumber(node, out var d)) problems.Add($"{name}: not a number (Decimal)");
                else CheckRange(field, d, name, problems);
                break;
            case SettingKind.Choice:
                if (field.Choices is not { Count: > 0 }) problems.Add($"{name}: Choice with no choices in the manifest");
                else if (!field.Choices.Any(c => SameChoice(c.Value, node))) problems.Add($"{name}: not one of the manifest's {field.Choices.Count} choices");
                break;
            case SettingKind.StringList:
                if (node is not JsonArray list) problems.Add($"{name}: not a list");
                else if (list.Any(e => e is not null && !IsString(e))) problems.Add($"{name}: a list item isn't a string");
                break;
            case SettingKind.TimeSpan:
                if (!IsString(node) || !TimeSpan.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, out _))
                    problems.Add($"{name}: not a TimeSpan string");
                break;
            case SettingKind.DateTime:
                if (!IsString(node) || !DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    problems.Add($"{name}: not a date/time string");
                break;
            case SettingKind.Object:
                if (node is not JsonObject child) problems.Add($"{name}: not an object");
                else CheckObject(field.Fields ?? new(), child, name, problems);
                break;
            case SettingKind.Collection:
                if (node is not JsonArray items) { problems.Add($"{name}: not a list"); break; }
                for (var n = 0; n < items.Count; n++)
                {
                    if (items[n] is JsonObject item) CheckObject(field.Fields ?? new(), item, $"{name}:{n}", problems);
                    else problems.Add($"{name}:{n}: item isn't an object");
                }
                break;
            case SettingKind.Dictionary:
                if (node is not JsonObject map) problems.Add($"{name}: not an object (Dictionary)");
                else if (map.Any(p => p.Value is not null && !IsString(p.Value))) problems.Add($"{name}: a dictionary value isn't a string");
                break;
            case SettingKind.Unknown:
                break;
        }
    }

    /// <summary>Top-level (and nested object) properties in the file that the manifest doesn't declare.</summary>
    public static void Undeclared(IEnumerable<SettingField> fields, JsonObject obj, string path, List<string> found)
    {
        var list = fields.ToList();
        foreach (var (key, value) in obj)
        {
            var field = list.FirstOrDefault(f => string.Equals(f.Property, key, StringComparison.OrdinalIgnoreCase));
            if (field is null) { found.Add($"{path}:{key}"); continue; }
            if (field.Kind == SettingKind.Object && value is JsonObject child) Undeclared(field.Fields ?? new(), child, $"{path}:{key}", found);
            if (field.Kind == SettingKind.Collection && value is JsonArray items)
                for (var n = 0; n < items.Count; n++)
                    if (items[n] is JsonObject item) Undeclared(field.Fields ?? new(), item, $"{path}:{key}:{n}", found);
        }
    }

    public static string? KeyOf(JsonObject obj, string property)
    {
        if (obj.ContainsKey(property)) return property;
        foreach (var (key, _) in obj)
            if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }

    public static bool SameChoice(JsonNode? choice, JsonNode? value)
    {
        if (JsonNode.DeepEquals(choice, value)) return true;
        // Enum names compare case-insensitively when the app reads them that way (JsonStringEnumConverter does).
        return choice is not null && value is not null && IsString(choice) && IsString(value)
               && string.Equals(choice.GetValue<string>(), value.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckRange(SettingField field, double value, string name, List<string> problems)
    {
        if (field.Min is { } min && value < min) problems.Add($"{name}: below the manifest's minimum");
        if (field.Max is { } max && value > max) problems.Add($"{name}: above the manifest's maximum");
    }

    public static bool IsString(JsonNode node) => node.GetValueKind() == JsonValueKind.String;

    public static bool TryNumber(JsonNode node, out double value)
    {
        value = 0;
        if (node.GetValueKind() != JsonValueKind.Number) return false;
        return double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Finds stored strings that look like API keys or tokens. Reports where, never what.</summary>
public static class SecretScanner
{
    private static readonly (string Kind, Regex Pattern)[] Known =
    {
        ("Anthropic/OpenAI-style key (sk-)", new Regex(@"\bsk-[A-Za-z0-9_\-]{16,}", RegexOptions.CultureInvariant)),
        ("Google API key (AIza)", new Regex(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.CultureInvariant)),
        ("xAI key (xai-)", new Regex(@"\bxai-[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)),
        ("GitHub token", new Regex(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|\bgithub_pat_[A-Za-z0-9_]{30,}", RegexOptions.CultureInvariant)),
        ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", RegexOptions.CultureInvariant)),
        ("AWS access key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        ("JWT", new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant)),
        ("Groq key (gsk_)", new Regex(@"\bgsk_[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant)),
        ("Private key block", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)),
    };

    private static readonly Regex TokenShape = new(@"^[A-Za-z0-9_\-+/=]{32,}$", RegexOptions.CultureInvariant);
    private static readonly Regex Guid = new(@"^[{(]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}[)}]?$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex = new(@"^[0-9a-fA-F]+$", RegexOptions.CultureInvariant);

    /// <summary>What kind of secret <paramref name="value"/> looks like, or null.</summary>
    public static string? Classify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 16) return null;
        foreach (var (kind, pattern) in Known)
            if (pattern.IsMatch(value)) return kind;
        var trimmed = value.Trim();
        if (TokenShape.IsMatch(trimmed) && !Guid.IsMatch(trimmed) && !Hex.IsMatch(trimmed)
            && trimmed.Any(char.IsUpper) && trimmed.Any(char.IsLower) && trimmed.Any(char.IsDigit)
            && Entropy(trimmed) >= 4.0)
            return "long high-entropy token";
        return null;
    }

    /// <summary>Every string leaf under <paramref name="node"/> that looks like a secret: (path, kind).</summary>
    public static IEnumerable<(string Path, string Kind)> Scan(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    // A secret-looking name holding any non-empty string is a secret stored in settings, whatever its shape.
                    if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetValue<string>())
                        && SettingsManifestBuilder.LooksLikeSecretName(key))
                    {
                        yield return ($"{path}:{key}", "value under a secret-looking name");
                        continue;
                    }
                    foreach (var hit in Scan(value, $"{path}:{key}")) yield return hit;
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    foreach (var hit in Scan(arr[i], $"{path}:{i}")) yield return hit;
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                if (Classify(value.GetValue<string>()) is { } kind) yield return (path, kind);
                break;
        }
    }

    private static double Entropy(string s)
    {
        var counts = s.GroupBy(c => c).Select(g => (double)g.Count());
        return -counts.Sum(c => c / s.Length * Math.Log2(c / s.Length));
    }
}
