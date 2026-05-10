using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Configuration;

/// <summary>
/// IConfiguration source that surfaces the existing per-bucket
/// <c>%APPDATA%\MindAttic\&lt;bucket&gt;\providers.json</c> files under the
/// <see cref="VaultConfigurationKeys.VaultSection"/> tree.
///
/// <para>This is the on-ramp for users who already have keys on disk: registering
/// it means <see cref="ConfigurationCredentialStore"/> sees their legacy keys
/// without any migration. New keys should be set via User Secrets (dev) or
/// App Service Application Settings / Azure Key Vault (prod).</para>
///
/// <para>Buckets defaulted to <c>"LLM"</c>, <c>"Brokers"</c>; pass <see cref="Buckets"/>
/// explicitly to add custom buckets.</para>
/// </summary>
public sealed class MindAtticConfigurationSource : IConfigurationSource
{
    /// <summary>Bucket folders under <c>%APPDATA%\MindAttic\</c> to read.</summary>
    public IReadOnlyList<string> Buckets { get; init; } = new[] { "LLM", "Brokers" };

    /// <summary>
    /// Override the roaming root (defaults to <c>%APPDATA%\MindAttic\</c>).
    /// Tests use this to point at a temp directory.
    /// </summary>
    public string? RoamingRoot { get; init; }

    /// <summary>If true, reload when any provider file changes on disk.</summary>
    public bool ReloadOnChange { get; init; } = false;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new MindAtticConfigurationProvider(this);

    /// <summary>The effective root used by <see cref="MindAtticConfigurationProvider"/>.</summary>
    internal string EffectiveRoot => RoamingRoot ?? VaultPaths.RoamingRoot;
}
