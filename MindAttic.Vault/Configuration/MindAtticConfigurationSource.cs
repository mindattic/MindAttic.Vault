using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Configuration;

/// <summary>
/// <see cref="IConfigurationSource"/> that surfaces the existing per-bucket
/// <c>%APPDATA%\MindAttic\&lt;bucket&gt;\providers.json</c> files under the
/// <see cref="VaultConfigurationKeys.VaultSection"/> tree.
///
/// <para>This is the on-ramp for users who already have keys on disk:
/// registering it means <see cref="Credentials.ConfigurationCredentialStore"/>
/// sees their legacy keys without any migration. New keys should be set via
/// User Secrets (dev) or App Service Application Settings / Azure Key Vault
/// (prod).</para>
///
/// <para>Defaults to surfacing every canonical credential bucket
/// (<c>LLM</c>, <c>Brokers</c>, <c>Tokens</c>, <c>Subtitles</c>,
/// <c>Notifications</c>, <c>AudioStore</c>); pass <see cref="Buckets"/>
/// explicitly to narrow or add buckets.</para>
/// </summary>
public sealed class MindAtticConfigurationSource : IConfigurationSource
{
    /// <summary>
    /// Bucket folders under <c>%APPDATA%\MindAttic\</c> to read. Each bucket's folder
    /// name doubles as the final segment of its <c>MindAttic:Vault:&lt;bucket&gt;</c>
    /// configuration section — the canonical 1:1 convention.
    /// </summary>
    /// <remarks>
    /// Defaults to every canonical credential bucket so APPDATA is the single local
    /// source of truth (User Secrets is retired). Settable both via object initializer
    /// (<c>new MindAtticConfigurationSource { Buckets = new[] { "LLM", "Brokers" } }</c>)
    /// and via the configure callback supplied to
    /// <see cref="ConfigurationBuilderExtensions.AddMindAtticVaultFiles"/>.
    /// </remarks>
    public IReadOnlyList<string> Buckets { get; set; } =
        new[] { "LLM", "Brokers", "Tokens", "Subtitles", "Notifications", "AudioStore" };

    /// <summary>
    /// Override the roaming root (defaults to <c>%APPDATA%\MindAttic\</c>).
    /// Tests use this to point at a temp directory.
    /// </summary>
    public string? RoamingRoot { get; set; }

    /// <summary>
    /// If true, reload when any provider file changes on disk. Off by default —
    /// most apps load secrets once at startup; turn it on if you want a settings
    /// UI's writes to surface immediately to in-process readers.
    /// </summary>
    public bool ReloadOnChange { get; set; } = false;

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new MindAtticConfigurationProvider(this);

    /// <summary>
    /// The effective root used by <see cref="MindAtticConfigurationProvider"/>.
    /// Falls back to <see cref="VaultPaths.RoamingRoot"/> when
    /// <see cref="RoamingRoot"/> is unset.
    /// </summary>
    internal string EffectiveRoot => RoamingRoot ?? VaultPaths.RoamingRoot;
}
