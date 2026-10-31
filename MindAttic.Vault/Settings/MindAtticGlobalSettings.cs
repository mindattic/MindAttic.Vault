using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Settings;

/// <summary>
/// Settings every MindAttic app shares, in <c>%APPDATA%\MindAttic\Global\settings.json</c>
/// (<c>MindAttic:Settings:Global:*</c>). An app's own settings class holds everything else.
/// </summary>
[SettingsApp("Global", "Global", Icon = "globe", Description = "Settings shared by every MindAttic app.")]
public sealed class MindAtticGlobalSettings
{
    [Setting("Theme", Group = "Appearance", Help = "Theme for every MindAttic app that follows the shared theme.", Order = 1)]
    [SettingChoices("Dark", "Light", "System")]
    public string Theme { get; set; } = "Dark";

    [Setting("Workspace root", Group = "Workspace", Help = "Folder that holds every MindAttic repo.", Order = 1)]
    [PathPicker(PathKind.Folder)]
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>The shared store.</summary>
    public static JsonSettingsStore<MindAtticGlobalSettings> Store() => JsonSettingsStore<MindAtticGlobalSettings>.ForSettingsApp();
}
