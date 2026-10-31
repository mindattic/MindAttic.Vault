using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.Settings.Manifest;

/// <summary>Thrown when a settings class can't be described (e.g. it holds a secret).</summary>
public sealed class SettingsManifestException : Exception
{
    public SettingsManifestException(string message) : base(message) { }
}

/// <summary>Builds a <see cref="SettingsManifest"/> from a <see cref="SettingsAppAttribute"/> class by reflection.</summary>
public static class SettingsManifestBuilder
{
    private const int MaxDepth = 8;

    private static readonly Regex SecretName = new(
        "(apikey|api_key|password|passwd|secret|token|connectionstring|privatekey|clientsecret)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Builds the manifest for <typeparamref name="T"/>.</summary>
    public static SettingsManifest Build<T>(JsonSerializerOptions? jsonOptions = null) where T : class, new() =>
        Build(typeof(T), jsonOptions);

    /// <summary>Builds the manifest for <paramref name="settingsType"/>, which must carry <see cref="SettingsAppAttribute"/>.</summary>
    public static SettingsManifest Build(Type settingsType, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(settingsType);
        var app = settingsType.GetCustomAttribute<SettingsAppAttribute>()
                  ?? throw new SettingsManifestException($"{settingsType.FullName} has no [SettingsApp] attribute.");
        if (string.IsNullOrWhiteSpace(app.Id) || app.Id.Contains(':') || app.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new SettingsManifestException($"[SettingsApp] id '{app.Id}' must be a valid folder name without ':'.");

        var options = jsonOptions ?? DefaultOptions;
        var prefix = SettingsManifest.PrefixFor(app.Id);
        var instance = CreateInstance(settingsType)
                       ?? throw new SettingsManifestException($"{settingsType.FullName} needs a public parameterless constructor.");

        var manifest = new SettingsManifest
        {
            AppId = app.Id,
            DisplayName = app.DisplayName,
            Icon = app.Icon,
            Description = app.Description,
            Scope = app.Local ? SettingsManifest.LocalScope : SettingsManifest.RoamingScope,
            ConfigurationPrefix = prefix,
            Fields = BuildFields(settingsType, instance, prefix, options, depth: 0),
        };

        foreach (var use in settingsType.GetCustomAttributes<UsesCredentialAttribute>())
        {
            var info = ProviderCatalog.Get(use.ProviderId);
            if (manifest.Credentials.Any(c => c.ProviderId == info.Id)) continue;
            manifest.Credentials.Add(new CredentialUse
            {
                ProviderId = info.Id,
                DisplayName = info.DisplayName,
                Required = use.Required,
                Purpose = use.Purpose,
            });
        }

        return manifest;
    }

    private static List<SettingField> BuildFields(Type type, object? instance, string parentName, JsonSerializerOptions options, int depth)
    {
        if (depth > MaxDepth)
            throw new SettingsManifestException($"Settings nest deeper than {MaxDepth} levels at {parentName}.");

        var fields = new List<SettingField>();
        var declaration = 0;
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is not null)
                     .OrderBy(p => p.MetadataToken))
        {
            declaration++;
            if (prop.IsDefined(typeof(JsonIgnoreAttribute)) || prop.IsDefined(typeof(SettingIgnoreAttribute))) continue;
            var complex = IsComplex(prop.PropertyType);
            // A get-only scalar can't be loaded back from JSON, so it isn't a setting.
            if (prop.SetMethod is null && !complex) continue;

            var meta = prop.GetCustomAttribute<SettingAttribute>();
            var jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                           ?? options.PropertyNamingPolicy?.ConvertName(prop.Name)
                           ?? prop.Name;
            var name = $"{parentName}:{jsonName}";
            var valueType = System.Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var value = instance is null ? null : prop.GetValue(instance);

            var field = new SettingField
            {
                Name = name,
                Property = jsonName,
                Label = meta?.Label ?? Humanize(prop.Name),
                Group = string.IsNullOrWhiteSpace(meta?.Group) ? SettingsManifest.DefaultGroup : meta!.Group!,
                Help = meta?.Help,
                Order = meta?.Order is { } o && o != int.MaxValue ? o : 100000 + declaration,
                ReadOnly = meta?.ReadOnly ?? false,
                Nullable = !valueType.IsValueType || System.Nullable.GetUnderlyingType(prop.PropertyType) is not null,
                Default = Serialize(value, prop.PropertyType, options),
            };

            Classify(field, prop, valueType, value, options, depth);

            if (field.Kind is SettingKind.Text or SettingKind.Multiline && !(meta?.NotSecret ?? false) && SecretName.IsMatch(prop.Name))
                throw new SettingsManifestException(
                    $"{type.Name}.{prop.Name} looks like a secret. Keys and passwords live in the Vault credential stores, never in a settings class " +
                    "(mark it [Setting(..., NotSecret = true)] if it isn't one).");

            fields.Add(field);
        }
        return fields;
    }

    private static void Classify(SettingField field, PropertyInfo prop, Type t, object? value, JsonSerializerOptions options, int depth)
    {
        var range = prop.GetCustomAttribute<SettingRangeAttribute>();
        if (range is not null) { field.Min = range.Min; field.Max = range.Max; }

        if (t == typeof(string))
        {
            var choices = prop.GetCustomAttribute<SettingChoicesAttribute>();
            var path = prop.GetCustomAttribute<PathPickerAttribute>();
            if (choices is not null)
            {
                field.Kind = SettingKind.Choice;
                field.Choices = choices.Choices.Select(c => new SettingChoice { Value = JsonValue.Create(c), Label = c }).ToList();
            }
            else if (path is not null) { field.Kind = SettingKind.Path; field.PathKind = path.Kind; field.PathFilter = path.Filter; }
            else if (prop.IsDefined(typeof(UrlSettingAttribute))) field.Kind = SettingKind.Url;
            else if (prop.IsDefined(typeof(MultilineAttribute))) field.Kind = SettingKind.Multiline;
            else field.Kind = SettingKind.Text;
            return;
        }
        if (t == typeof(bool)) { field.Kind = SettingKind.Bool; return; }
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ushort))
        { field.Kind = SettingKind.Integer; return; }
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) { field.Kind = SettingKind.Decimal; return; }
        if (t == typeof(TimeSpan)) { field.Kind = SettingKind.TimeSpan; return; }
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) { field.Kind = SettingKind.DateTime; return; }
        if (t == typeof(Guid)) { field.Kind = SettingKind.Text; return; }
        if (t.IsEnum)
        {
            field.Kind = SettingKind.Choice;
            field.Choices = Enum.GetValues(t).Cast<object>()
                .Select(v => new SettingChoice { Value = Serialize(v, t, options), Label = Humanize(v.ToString()!) })
                .ToList();
            return;
        }

        if (IsStringDictionary(t)) { field.Kind = SettingKind.Dictionary; return; }

        var element = ElementType(t);
        if (element is not null)
        {
            var inner = System.Nullable.GetUnderlyingType(element) ?? element;
            if (inner == typeof(string)) { field.Kind = SettingKind.StringList; return; }
            if (IsComplex(inner))
            {
                var meta = prop.GetCustomAttribute<SettingCollectionAttribute>();
                var item = CreateInstance(inner);
                field.Kind = SettingKind.Collection;
                field.Fields = BuildFields(inner, item, field.Name + ":*", options, depth + 1);
                field.ItemDefault = Serialize(item, inner, options);
                field.ItemName = meta?.ItemName ?? Humanize(inner.Name).ToLowerInvariant();
                field.ItemLabel = meta?.ItemLabel is { } label
                    ? options.PropertyNamingPolicy?.ConvertName(label) ?? label
                    : field.Fields.FirstOrDefault(f => f.Kind == SettingKind.Text)?.Property;
                return;
            }
            field.Kind = SettingKind.Unknown;
            field.ReadOnly = true;
            return;
        }

        if (IsComplex(t))
        {
            field.Kind = SettingKind.Object;
            field.Fields = BuildFields(t, value ?? CreateInstance(t), field.Name, options, depth + 1);
            return;
        }

        field.Kind = SettingKind.Unknown;
        field.ReadOnly = true;
    }

    private static bool IsComplex(Type t) =>
        t.IsClass && t != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null;

    private static bool IsStringDictionary(Type t) =>
        t.GetInterfaces().Concat(new[] { t })
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                      && i.GetGenericArguments()[0] == typeof(string) && i.GetGenericArguments()[1] == typeof(string));

    private static Type? ElementType(Type t)
    {
        if (t == typeof(string)) return null;
        if (t.IsArray) return t.GetElementType();
        if (!typeof(IEnumerable).IsAssignableFrom(t)) return null;
        var enumerable = t.GetInterfaces().Concat(new[] { t })
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static object? CreateInstance(Type t)
    {
        try { return t.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(t); }
        catch { return null; }
    }

    private static JsonNode? Serialize(object? value, Type type, JsonSerializerOptions options)
    {
        if (value is null) return null;
        try { return JsonSerializer.SerializeToNode(value, type, options); }
        catch { return null; }
    }

    /// <summary><c>MaxLossPerTrade</c> → <c>Max loss per trade</c>.</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var spaced = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_", " ").Trim();
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => i == 0 ? char.ToUpperInvariant(w[0]) + w[1..] : IsAcronym(w) ? w : w.ToLowerInvariant());
        return string.Join(' ', words);
    }

    private static bool IsAcronym(string w) => w.Length > 1 && w.All(c => char.IsUpper(c) || char.IsDigit(c));
}
