using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Tests;

[SettingsApp("Alpha", "Alpha App", Description = "Sample app one")]
[UsesCredential("claude", Required = true, Purpose = "Summaries")]
public sealed class AlphaSettings
{
    [Setting("Name", Order = 1)]
    public string Name { get; set; } = "alpha";

    [Setting("Count", Order = 2)]
    [SettingRange(1, 10)]
    public int Count { get; set; } = 3;

    public AlphaConnection Connection { get; set; } = new();
}

public sealed class AlphaConnection
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 80;
}

[SettingsApp("Beta", "Beta App")]
[UsesCredential("openai", Required = false, Purpose = "Optional extras")]
public sealed class BetaSettings
{
    public bool Enabled { get; set; } = true;
}

/// <summary>Builds the pieces a view-model test needs.</summary>
public static class Samples
{
    public static SettingsManifest Alpha => SettingsManifestBuilder.Build<AlphaSettings>();
    public static SettingsManifest Beta => SettingsManifestBuilder.Build<BetaSettings>();

    public const string AlphaName = "MindAttic:Settings:Alpha:name";
    public const string AlphaCount = "MindAttic:Settings:Alpha:count";
    public const string AlphaHost = "MindAttic:Settings:Alpha:connection:host";

    /// <summary>Writes <typeparamref name="T"/>'s manifest (and seeded settings) into its app folder under the roaming root.</summary>
    public static string Install<T>(string roamingRoot) where T : class, new()
    {
        var manifest = SettingsManifestBuilder.Build<T>();
        var dir = Path.Combine(roamingRoot, manifest.AppId);
        Directory.CreateDirectory(dir);
        SettingsManifestFile.WriteIfChanged(dir, manifest);
        return dir;
    }

    public static DesktopContext Context(FakeDialogs? dialogs = null, IUiDispatcher? ui = null, string? localRoot = null,
        IAuditLog? audit = null, TimeProvider? time = null)
    {
        var local = localRoot ?? Path.Combine(Path.GetTempPath(), "vault-desktop-health-" + Guid.NewGuid().ToString("N"));
        return new(dialogs ?? new FakeDialogs(), ui ?? new InlineDispatcher(), new KeyHealthStore(Path.Combine(local, "Vault")),
            time ?? TimeProvider.System, audit ?? new SqliteAuditLog(Path.Combine(local, "Vault", SqliteAuditLog.FileName)));
    }

    public static MainViewModel Main(FakeCatalog catalog, DesktopContext context, params Profiles.IProfile[] profiles) =>
        new(profiles, catalog, context);
}
