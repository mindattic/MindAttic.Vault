using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Settings;

/// <summary>
/// Settings every MindAttic app shares, in <c>%APPDATA%\MindAttic\Global\settings.json</c>
/// (<c>MindAttic:Settings:Global:*</c>): exactly the values that should be reachable from everywhere. An app's own settings
/// class holds everything else.
/// <para>Global default, app override (M.VALT-T77): an app that has its own copy of a Global value treats that copy as an
/// optional override. Blank (or null) follows Global; set, it wins. Resolve it with <see cref="Resolve(string?, Func{MindAtticGlobalSettings, string?})"/>
/// so every app falls back the same way.</para>
/// </summary>
[SettingsApp("Global", "Global", Icon = "globe", Description = "Settings shared by every MindAttic app.")]
public sealed class MindAtticGlobalSettings
{
    [Setting("Theme", Group = "Appearance", Help = "Theme for every MindAttic app that follows the shared theme.", Order = 1)]
    [SettingChoices("Dark", "Light", "System")]
    public string Theme { get; set; } = "Dark";

    [Setting("Workspace root", Group = "Workspace", Help = "Folder that holds every MindAttic repo. Apps fall back to it when their own is blank.", Order = 1)]
    [PathPicker(PathKind.Folder)]
    public string WorkspaceRoot { get; set; } = "";

    [Setting("Alert recipient", Group = "Notifications",
        Help = "The Psst name or address alerts go to (a Psst contact such as ryan, or a US number). Apps fall back to it when their own is blank.", Order = 1)]
    public string AlertRecipient { get; set; } = "";

    /// <summary>The shared store.</summary>
    public static JsonSettingsStore<MindAtticGlobalSettings> Store() => JsonSettingsStore<MindAtticGlobalSettings>.ForSettingsApp();

    /// <summary>
    /// An app's value for a Global setting: <paramref name="appValue"/> when it isn't blank (the app's override), else
    /// <paramref name="global"/>'s pick from the user's Global settings (read from <see cref="Store"/> only then). Null when
    /// both are blank.
    /// </summary>
    /// <example><c>var to = MindAtticGlobalSettings.Resolve(settings.PsstAlertTo, g =&gt; g.AlertRecipient);</c></example>
    public static string? Resolve(string? appValue, Func<MindAtticGlobalSettings, string?> global)
    {
        ArgumentNullException.ThrowIfNull(global);
        return Resolve(appValue, () => Store().Load(), global);
    }

    /// <summary>Like <see cref="Resolve(string?, Func{MindAtticGlobalSettings, string?})"/>, against Global settings the caller
    /// already holds (or null for none).</summary>
    public static string? Resolve(string? appValue, MindAtticGlobalSettings? settings, Func<MindAtticGlobalSettings, string?> global)
    {
        ArgumentNullException.ThrowIfNull(global);
        return Resolve(appValue, () => settings, global);
    }

    private static string? Resolve(string? appValue, Func<MindAtticGlobalSettings?> load, Func<MindAtticGlobalSettings, string?> global)
    {
        if (!string.IsNullOrWhiteSpace(appValue)) return appValue.Trim();
        var g = load();
        return g is not null && global(g) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }
}
