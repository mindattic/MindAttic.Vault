namespace MindAttic.Vault.Settings.Manifest;

/// <summary>
/// Marks an app's settings class. <see cref="Id"/> is also the app's folder under
/// <c>%APPDATA%\MindAttic\</c> and its section under <c>MindAttic:Settings</c> (M.VALT-LAW-2).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SettingsAppAttribute : Attribute
{
    public SettingsAppAttribute(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    /// <summary>App id and folder name, e.g. <c>IdiotProof</c>.</summary>
    public string Id { get; }

    /// <summary>Tab title in Vault.Desktop.</summary>
    public string DisplayName { get; }

    /// <summary>Optional icon name for the tab.</summary>
    public string? Icon { get; set; }

    /// <summary>Optional one-line description shown at the top of the tab.</summary>
    public string? Description { get; set; }
}

/// <summary>Describes one setting's row in Vault.Desktop. Unattributed public properties still appear, labeled from their name.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SettingAttribute : Attribute
{
    public SettingAttribute(string label) => Label = label;

    public string Label { get; }

    /// <summary>Section heading the row sits under. Defaults to "General".</summary>
    public string? Group { get; set; }

    /// <summary>Help text under the row.</summary>
    public string? Help { get; set; }

    /// <summary>Sort order within its group; ties keep declaration order.</summary>
    public int Order { get; set; } = int.MaxValue;

    /// <summary>Shown but not editable.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Opt out of the secret-name check for a value that only looks like a secret by name
    /// (e.g. a local SQLite <c>ConnectionString</c>). Real secrets belong in the credential stores.
    /// </summary>
    public bool NotSecret { get; set; }
}

/// <summary>Leaves a property out of the manifest (still saved in settings.json).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SettingIgnoreAttribute : Attribute { }

/// <summary>Numeric bounds, validated before Save.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SettingRangeAttribute : Attribute
{
    public SettingRangeAttribute(double min, double max)
    {
        Min = min;
        Max = max;
    }

    public double Min { get; }
    public double Max { get; }
}

/// <summary>A string setting that must be one of these values.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SettingChoicesAttribute : Attribute
{
    public SettingChoicesAttribute(params string[] choices) => Choices = choices;

    public IReadOnlyList<string> Choices { get; }
}

/// <summary>Whether a path setting picks a file or a folder.</summary>
public enum PathKind { File, Folder }

/// <summary>A string setting that holds a file or folder path.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class PathPickerAttribute : Attribute
{
    public PathPickerAttribute(PathKind kind) => Kind = kind;

    public PathKind Kind { get; }

    /// <summary>Optional file dialog filter, e.g. <c>"Executables|*.exe"</c>.</summary>
    public string? Filter { get; set; }
}

/// <summary>A string setting that holds an absolute URL.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class UrlSettingAttribute : Attribute { }

/// <summary>A string setting edited in a multi-line box.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class MultilineAttribute : Attribute { }

/// <summary>Options for a list-of-objects setting (profiles, users, ...).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SettingCollectionAttribute : Attribute
{
    /// <summary>Property of each item used as its row title (defaults to the first string property).</summary>
    public string? ItemLabel { get; set; }

    /// <summary>Singular noun for the Add button, e.g. "profile".</summary>
    public string? ItemName { get; set; }
}

/// <summary>Declares a key pool the app uses. Drives Get Started and the app tab's Keys section.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class UsesCredentialAttribute : Attribute
{
    public UsesCredentialAttribute(string providerId) => ProviderId = providerId;

    /// <summary>Provider id or alias (normalized through <see cref="Credentials.ProviderCatalog"/>).</summary>
    public string ProviderId { get; }

    /// <summary>The app can't work without it; Get Started asks for it.</summary>
    public bool Required { get; set; }

    /// <summary>What the app uses it for, shown in Get Started.</summary>
    public string? Purpose { get; set; }
}
