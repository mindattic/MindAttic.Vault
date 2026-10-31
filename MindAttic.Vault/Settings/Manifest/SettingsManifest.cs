using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MindAttic.Vault.Settings.Manifest;

/// <summary>How Vault.Desktop edits a field. Unknown kinds render read-only.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingKind>))]
public enum SettingKind
{
    Text,
    Multiline,
    Bool,
    Integer,
    Decimal,
    Choice,
    StringList,
    Path,
    Url,
    TimeSpan,
    DateTime,
    Object,
    Collection,
    Dictionary,
    Unknown,
}

/// <summary>One allowed value of a <see cref="SettingKind.Choice"/> field: the stored JSON value and its label.</summary>
public sealed class SettingChoice
{
    public JsonNode? Value { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>One row in an app's settings tab.</summary>
public sealed class SettingField
{
    /// <summary>
    /// Full configuration name, identical in the UI, settings.json, App Configuration and IConfiguration,
    /// e.g. <c>MindAttic:Settings:IdiotProof:maxLossPerTrade</c>. Fields of collection items use <c>*</c>
    /// for the item index.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The JSON property name inside its parent object.</summary>
    public string Property { get; set; } = "";

    public string Label { get; set; } = "";
    public string Group { get; set; } = SettingsManifest.DefaultGroup;
    public string? Help { get; set; }
    public int Order { get; set; }
    public SettingKind Kind { get; set; }
    public bool ReadOnly { get; set; }
    public bool Nullable { get; set; }

    /// <summary>The value <c>new T()</c> produces, as stored JSON.</summary>
    public JsonNode? Default { get; set; }

    public double? Min { get; set; }
    public double? Max { get; set; }
    public List<SettingChoice>? Choices { get; set; }
    public PathKind? PathKind { get; set; }
    public string? PathFilter { get; set; }

    /// <summary>For <see cref="SettingKind.Object"/> and <see cref="SettingKind.Collection"/>: the nested fields.</summary>
    public List<SettingField>? Fields { get; set; }

    /// <summary>For collections: the item property used as each item's title.</summary>
    public string? ItemLabel { get; set; }

    /// <summary>For collections: singular noun for the Add button.</summary>
    public string? ItemName { get; set; }

    /// <summary>For collections: a fresh item's JSON (the item class's defaults).</summary>
    public JsonNode? ItemDefault { get; set; }
}

/// <summary>A key pool the app declared with <see cref="UsesCredentialAttribute"/>.</summary>
public sealed class CredentialUse
{
    public string ProviderId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Required { get; set; }
    public string? Purpose { get; set; }
}

/// <summary>
/// What an app writes next to its settings.json so Vault.Desktop can render its tab without
/// referencing the app's assemblies. Holds names, kinds and defaults only; never a key value.
/// </summary>
public sealed class SettingsManifest
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "settings.manifest.json";
    public const string ConfigurationRoot = "MindAttic:Settings";
    public const string DefaultGroup = "General";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Icon { get; set; }
    public string? Description { get; set; }

    /// <summary><see cref="RoamingScope"/> (default) or <see cref="LocalScope"/>: which AppData root holds the app's folder.</summary>
    public string Scope { get; set; } = RoamingScope;

    public const string RoamingScope = "Roaming";
    public const string LocalScope = "Local";

    /// <summary>Settings file name inside the app's folder.</summary>
    public string SettingsFile { get; set; } = "settings.json";

    /// <summary><c>MindAttic:Settings:{AppId}</c>: the prefix of every field name.</summary>
    public string ConfigurationPrefix { get; set; } = "";

    public List<SettingField> Fields { get; set; } = new();
    public List<CredentialUse> Credentials { get; set; } = new();

    /// <summary>The configuration prefix for an app id.</summary>
    public static string PrefixFor(string appId) => $"{ConfigurationRoot}:{appId}";

    /// <summary>Every field, depth-first, including nested object and collection-item fields.</summary>
    public IEnumerable<SettingField> AllFields() => Walk(Fields);

    private static IEnumerable<SettingField> Walk(IEnumerable<SettingField> fields)
    {
        foreach (var f in fields)
        {
            yield return f;
            if (f.Fields is { Count: > 0 })
                foreach (var child in Walk(f.Fields)) yield return child;
        }
    }

    /// <summary>The field with this full name (case-insensitive), or null.</summary>
    public SettingField? Find(string name) =>
        AllFields().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}
