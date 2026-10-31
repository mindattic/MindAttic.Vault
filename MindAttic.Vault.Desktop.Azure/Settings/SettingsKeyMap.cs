using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Azure.Settings;

/// <summary>
/// Maps an app's settings document to flat App Configuration key-values and back, the way IConfiguration names
/// them: every leaf is <c>MindAttic:Settings:{AppId}:{path}</c>, arrays by index (<c>...:tags:0</c>), collection items
/// by index then property (<c>...:profiles:1:name</c>), dictionaries by entry key.
/// <para>App Configuration stores strings, so <see cref="Read"/> rebuilds JSON types from the manifest kinds:
/// <see cref="SettingKind.Bool"/> → JSON bool, <see cref="SettingKind.Integer"/>/<see cref="SettingKind.Decimal"/> →
/// JSON number, <see cref="SettingKind.Choice"/> → the matching choice's own JSON value, everything else a string.</para>
/// <para>Markers: a JSON <c>null</c> is a key whose value is null; an empty list, collection, dictionary or object
/// is its own key with an empty string (no children). Both are distinct from a missing key, which means
/// "use the manifest default".</para>
/// </summary>
public static class SettingsKeyMap
{
    /// <summary>The prefix every key of this app's settings starts with (<c>MindAttic:Settings:{AppId}</c>).</summary>
    public static string PrefixOf(SettingsManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.ConfigurationPrefix) ? SettingsManifest.PrefixFor(manifest.AppId) : manifest.ConfigurationPrefix;

    /// <summary>The key of a top-level field.</summary>
    public static string PathOf(SettingsManifest manifest, SettingField field) => $"{PrefixOf(manifest)}:{field.Property}";

    // ------------------------------------------------------------------ write side

    /// <summary>Every key-value of the whole document (fields the document lacks are skipped).</summary>
    public static Dictionary<string, string?> Flatten(SettingsManifest manifest, JsonObject document)
    {
        var into = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in manifest.Fields)
            if (TryGet(document, field.Property, out var value))
                Flatten(field, value, PathOf(manifest, field), into);
        return into;
    }

    /// <summary>The key-values of one field's value under <paramref name="path"/>.</summary>
    public static void Flatten(SettingField field, JsonNode? value, string path, IDictionary<string, string?> into)
    {
        if (value is null) { into[path] = null; return; }

        switch (field.Kind)
        {
            case SettingKind.Object when value is JsonObject obj && field.Fields is { Count: > 0 }:
                FlattenObject(field.Fields, obj, path, into);
                return;

            case SettingKind.Collection when value is JsonArray items:
                if (items.Count == 0) { into[path] = ""; return; }
                for (var i = 0; i < items.Count; i++)
                {
                    var itemPath = $"{path}:{i.ToString(CultureInfo.InvariantCulture)}";
                    if (items[i] is JsonObject item && field.Fields is { Count: > 0 }) FlattenObject(field.Fields, item, itemPath, into);
                    else FlattenGeneric(items[i], itemPath, into);
                }
                return;

            case SettingKind.StringList when value is JsonArray list:
                if (list.Count == 0) { into[path] = ""; return; }
                for (var i = 0; i < list.Count; i++)
                    into[$"{path}:{i.ToString(CultureInfo.InvariantCulture)}"] = list[i] is null ? null : Scalar(list[i]!);
                return;

            case SettingKind.Dictionary when value is JsonObject map:
                if (map.Count == 0) { into[path] = ""; return; }
                foreach (var (key, entry) in map)
                {
                    if (key.Contains(':'))
                        throw new ArgumentException($"{field.Name}: entry names can't contain ':' ('{key}').");
                    into[$"{path}:{key}"] = entry is null ? null : Scalar(entry);
                }
                return;
        }

        FlattenGeneric(value, path, into);
    }

    private static void FlattenObject(List<SettingField> fields, JsonObject obj, string path, IDictionary<string, string?> into)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in fields)
        {
            known.Add(child.Property);
            if (TryGet(obj, child.Property, out var childValue))
                Flatten(child, childValue, $"{path}:{child.Property}", into);
        }
        // Properties the manifest doesn't describe still round-trip (as strings).
        foreach (var (name, extra) in obj)
            if (!known.Contains(name)) FlattenGeneric(extra, $"{path}:{name}", into);
        if (obj.Count == 0) into[path] = "";
    }

    private static void FlattenGeneric(JsonNode? value, string path, IDictionary<string, string?> into)
    {
        switch (value)
        {
            case null:
                into[path] = null;
                return;
            case JsonObject obj:
                if (obj.Count == 0) into[path] = "";
                foreach (var (name, child) in obj) FlattenGeneric(child, $"{path}:{name}", into);
                return;
            case JsonArray arr:
                if (arr.Count == 0) into[path] = "";
                for (var i = 0; i < arr.Count; i++) FlattenGeneric(arr[i], $"{path}:{i.ToString(CultureInfo.InvariantCulture)}", into);
                return;
            default:
                into[path] = Scalar(value);
                return;
        }
    }

    /// <summary>A JSON scalar as the string IConfiguration would hold: strings raw, bools lowercase, numbers invariant.</summary>
    public static string Scalar(JsonNode node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (v.TryGetValue<JsonElement>(out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString()!;
                if (e.ValueKind is JsonValueKind.True or JsonValueKind.False) return e.GetBoolean() ? "true" : "false";
            }
        }
        return node.ToJsonString();
    }

    // ------------------------------------------------------------------ read side

    /// <summary>The settings document for <paramref name="manifest"/> from <paramref name="values"/> (key → value).</summary>
    public static JsonObject Read(SettingsManifest manifest, IReadOnlyDictionary<string, string?> values)
    {
        var index = new KeyIndex(values);
        var result = new JsonObject();
        foreach (var field in manifest.Fields)
        {
            var node = Read(field, PathOf(manifest, field), index, out var present);
            result[field.Property] = present ? node : field.Default?.DeepClone();
        }
        return result;
    }

    private static JsonNode? Read(SettingField field, string path, KeyIndex index, out bool present)
    {
        present = index.HasAny(path);
        if (!present) return null;

        var children = index.Children(path);
        if (children.Count == 0)
        {
            var raw = index.Value(path);
            if (raw is null) return null;
            if (raw.Length == 0 && IsContainer(field, raw)) return EmptyContainer(field);
            return field.Kind switch
            {
                SettingKind.Object or SettingKind.Collection or SettingKind.StringList or SettingKind.Dictionary => JsonValue.Create(raw),
                SettingKind.Unknown => ParseLike(field.Default, raw),
                _ => ParseScalar(field, raw),
            };
        }

        switch (field.Kind)
        {
            case SettingKind.Object when field.Fields is { Count: > 0 }:
                return ReadObject(field.Fields, path, index, field.Default as JsonObject);

            case SettingKind.Collection:
            {
                var arr = new JsonArray();
                foreach (var i in Indices(children))
                    arr.Add(field.Fields is { Count: > 0 }
                        ? ReadObject(field.Fields, $"{path}:{i}", index, field.ItemDefault as JsonObject)
                        : ReadGeneric($"{path}:{i}", index, null));
                return arr;
            }

            case SettingKind.StringList:
            {
                var arr = new JsonArray();
                foreach (var i in Indices(children))
                    arr.Add(index.Value($"{path}:{i}") is { } s ? JsonValue.Create(s) : null);
                return arr;
            }

            case SettingKind.Dictionary:
            {
                var obj = new JsonObject();
                foreach (var name in children)
                    obj[name] = index.Value($"{path}:{name}") is { } s ? JsonValue.Create(s) : null;
                return obj;
            }

            default:
                return ReadGeneric(path, index, field.Default);
        }
    }

    private static JsonObject ReadObject(List<SettingField> fields, string path, KeyIndex index, JsonObject? defaults)
    {
        var obj = new JsonObject();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in fields)
        {
            known.Add(child.Property);
            var node = Read(child, $"{path}:{child.Property}", index, out var present);
            obj[child.Property] = present
                ? node
                : (defaults is not null && TryGet(defaults, child.Property, out var d) ? d : child.Default)?.DeepClone();
        }
        foreach (var extra in index.Children(path))
            if (!known.Contains(extra)) obj[extra] = ReadGeneric($"{path}:{extra}", index, null);
        return obj;
    }

    /// <summary>Rebuilds a value the manifest doesn't type, using <paramref name="shape"/> (the default) as the guide.</summary>
    private static JsonNode? ReadGeneric(string path, KeyIndex index, JsonNode? shape)
    {
        var children = index.Children(path);
        if (children.Count == 0)
        {
            var raw = index.Value(path);
            if (raw is null) return null;
            if (raw.Length == 0 && shape is JsonArray) return new JsonArray();
            if (raw.Length == 0 && shape is JsonObject) return new JsonObject();
            return ParseLike(shape, raw);
        }

        var asArray = shape is JsonArray || (shape is not JsonObject && children.All(c => int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out _)));
        if (asArray)
        {
            var element = (shape as JsonArray)?.FirstOrDefault();
            var arr = new JsonArray();
            foreach (var i in Indices(children)) arr.Add(ReadGeneric($"{path}:{i}", index, element));
            return arr;
        }

        var obj = new JsonObject();
        foreach (var name in children)
        {
            JsonNode? childShape = null;
            if (shape is JsonObject so) TryGet(so, name, out childShape);
            obj[name] = ReadGeneric($"{path}:{name}", index, childShape);
        }
        return obj;
    }

    private static JsonNode? ParseScalar(SettingField field, string raw)
    {
        switch (field.Kind)
        {
            case SettingKind.Bool:
                return bool.TryParse(raw, out var b) ? JsonValue.Create(b) : JsonValue.Create(raw);
            // Numbers come back as parsed JSON (not a CLR long/double), so GetValue<int>/<long>/<decimal> all work.
            case SettingKind.Integer:
            case SettingKind.Decimal:
                return ParseNumber(raw) ?? JsonValue.Create(raw);
            case SettingKind.Choice:
                if (field.Choices is { Count: > 0 })
                    foreach (var choice in field.Choices)
                        if (choice.Value is not null && string.Equals(Scalar(choice.Value), raw, StringComparison.Ordinal))
                            return choice.Value.DeepClone();
                return ParseLike(field.Choices?.FirstOrDefault()?.Value ?? field.Default, raw);
            default:
                return JsonValue.Create(raw);
        }
    }

    /// <summary>Parses <paramref name="raw"/> as the same JSON kind as <paramref name="shape"/> (string when unknown).</summary>
    private static JsonNode? ParseLike(JsonNode? shape, string raw)
    {
        var kind = shape is JsonValue v && v.TryGetValue<JsonElement>(out var e) ? e.ValueKind
            : shape is JsonValue v2 ? KindOf(v2) : JsonValueKind.String;
        return kind switch
        {
            JsonValueKind.True or JsonValueKind.False => bool.TryParse(raw, out var b) ? JsonValue.Create(b) : JsonValue.Create(raw),
            JsonValueKind.Number => ParseNumber(raw) ?? JsonValue.Create(raw),
            _ => JsonValue.Create(raw),
        };
    }

    private static JsonValueKind KindOf(JsonValue v)
    {
        if (v.TryGetValue<bool>(out _)) return JsonValueKind.True;
        if (v.TryGetValue<string>(out _)) return JsonValueKind.String;
        return JsonValueKind.Number;
    }

    /// <summary>A JSON number keeping the stored text exactly (so <c>0.10</c> stays <c>0.10</c>), or null.</summary>
    private static JsonNode? ParseNumber(string raw)
    {
        var t = raw.Trim();
        if (t.Length == 0 || !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d))
            return null;
        try { return JsonNode.Parse(t) is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n : null; }
        catch (JsonException) { return JsonValue.Create(d); }
    }

    private static bool IsContainer(SettingField field, string raw) =>
        field.Kind is SettingKind.Object or SettingKind.Collection or SettingKind.StringList or SettingKind.Dictionary
        || (field.Kind == SettingKind.Unknown && field.Default is JsonArray or JsonObject);

    private static JsonNode EmptyContainer(SettingField field) => field.Kind switch
    {
        SettingKind.Collection or SettingKind.StringList => new JsonArray(),
        SettingKind.Unknown when field.Default is JsonArray => new JsonArray(),
        _ => new JsonObject(),
    };

    private static IEnumerable<int> Indices(IEnumerable<string> segments) =>
        segments.Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1)
            .Where(i => i >= 0).Distinct().OrderBy(i => i);

    internal static bool TryGet(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(name, out value)) return true;
        foreach (var (k, v) in obj)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { value = v; return true; }
        value = null;
        return false;
    }

    /// <summary>Case-insensitive lookup over flat keys (IConfiguration semantics).</summary>
    private sealed class KeyIndex
    {
        private readonly Dictionary<string, string?> values;
        private readonly List<string> keys;

        public KeyIndex(IReadOnlyDictionary<string, string?> source)
        {
            values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in source) values[k] = v;
            keys = values.Keys.ToList();
        }

        public bool HasAny(string path) =>
            values.ContainsKey(path) || keys.Any(k => k.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase));

        public string? Value(string path) => values.TryGetValue(path, out var v) ? v : null;

        /// <summary>Distinct next segments below <paramref name="path"/>, in first-seen order (ordinal-sorted keys).</summary>
        public List<string> Children(string path)
        {
            var prefix = path + ":";
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = k.Substring(prefix.Length);
                var colon = rest.IndexOf(':');
                var segment = colon < 0 ? rest : rest[..colon];
                if (seen.Add(segment)) result.Add(segment);
            }
            return result;
        }
    }
}
