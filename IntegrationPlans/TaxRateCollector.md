# Integration Plan — TaxRateCollector

**Status (audit-verified 2026-05-09):** the audit found `SettingsService` is parameterless and self-managing — incompatible with the original plan's DI-injected refactor. Refresh: minimal-touch approach. Add the IConfiguration chain. Add an `OverlayFromConfiguration(IConfiguration)` method to `SettingsService` instead of refactoring its constructor. Existing `MindAtticCredentialStore.GetKey/SetKey` calls inside `SettingsService` survive Legion 2.1.0 unchanged.

**Cloud-native impact:** USPS + Anthropic keys resolve through User Secrets / App Service Application Settings / Key Vault. The on-disk `%APPDATA%\MindAttic\TaxRateCollector\settings.json` keeps holding non-secret preferences; secrets read from `IConfiguration` are NOT written back to that file.

---

## Phase B.1 — silent inheritance via Legion 2.1.0

`SettingsService.cs` calls `MindAtticCredentialStore.GetKey("claude")` and `SetKey()` directly (audit confirmed). After Legion 2.1.0, those calls delegate to Vault's file-backed store — no code change needed.

| File | Action |
| --- | --- |
| Bump Legion package reference (or ProjectReference is already there) | Verify Legion 2.1.0 resolves. |
| `NuGet.config` (repo root) | Create with `LocalNuGet` + `nuget.org` sources (none today). |
| `TaxRateCollector.Blazor/TaxRateCollector.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |

Verify the existing `dotnet build` and Razor pages still work.

---

## Phase B.2 — additive cloud-native overlay

### Files

| File | Action |
| --- | --- |
| `TaxRateCollector.Infrastructure/TaxRateCollector.Infrastructure.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `TaxRateCollector.Infrastructure/Services/SettingsService.cs` | Add `OverlayFromConfiguration(IConfiguration)`. **Do not** change the existing `Load()` / `Save()` / `SetTheme()` signatures or constructor. |
| `TaxRateCollector.Blazor/Program.cs` | Wire the cloud-native config chain. Call the overlay AFTER `Load()`. |

### `SettingsService.OverlayFromConfiguration` (additive method)

```csharp
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;

public partial class SettingsService
{
    /// <summary>
    /// Layers IConfiguration values (User Secrets, App Service Application Settings,
    /// Key Vault) on top of the loaded <see cref="Current"/> settings. Call after
    /// <see cref="Load"/>. Does NOT persist the overlaid values back to the local
    /// settings.json — secrets are kept in-memory only.
    /// </summary>
    public void OverlayFromConfiguration(IConfiguration config)
    {
        if (config is null || Current is null) return;

        var anthropic = config[VaultConfigurationKeys.ProviderApiKeyPath(
            VaultConfigurationKeys.LlmSection, "claude")];
        if (!string.IsNullOrWhiteSpace(anthropic)) Current.AnthropicApiKey = anthropic;

        var usps = config[$"{VaultConfigurationKeys.TokensSection}:usps"];
        if (!string.IsNullOrWhiteSpace(usps)) Current.UspsApiKey = usps;
    }
}
```

### Persistence safety: don't write secrets back

After loading config-overlaid secrets into `Current`, the existing `Save()` would write them back to disk. Two options to prevent this leak:

1. **Snapshot pattern (recommended).** Add a separate `OverlaidView` that returns a `Current` clone with secrets, while `Save()` continues to write the unaltered base. Requires a small ctor split.
2. **Strip on save.** Wrap `Save()` to clear `AnthropicApiKey` / `UspsApiKey` before serialization, then restore after. Simpler but feels brittle.

Pick (1) for the implementation; the snippet above leaves the choice to the implementer. Document the chosen pattern in the PR.

### Program.cs additions

```csharp
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
```

Update the existing `SettingsService` registration (Program.cs lines 118–120):

```csharp
builder.Services.AddSingleton(sp =>
{
    var settings = new SettingsService();
    settings.Load();
    settings.OverlayFromConfiguration(sp.GetRequiredService<IConfiguration>());
    return settings;
});
```

(Service-locator pattern is acceptable here for a singleton initialiser.)

### Azure deployment

Existing GitHub Actions workflow targets App Service `taxratecollector`. Application Settings:

| Application Setting | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |
| `MindAttic__Vault__Tokens__usps` | `USPS-...` |
| `ConnectionStrings__DefaultConnection` | unchanged |

### Verify

```powershell
dotnet build D:\Projects\MindAttic\TaxRateCollector\TaxRateCollector.slnx
dotnet user-secrets --project TaxRateCollector.Blazor set "MindAttic:Vault:LLM:claude:apiKey" "sk-ant-test"
dotnet run --project TaxRateCollector.Blazor
```

Open the settings page; confirm the masked Claude key reflects the User-Secret value. Save the page; re-open `%APPDATA%\MindAttic\TaxRateCollector\settings.json` and confirm `anthropicApiKey` is empty (snapshot pattern working).

### Rollback

Drop `OverlayFromConfiguration`, the Program.cs additions, and the new package reference. `git restore` brings the parameterless service back; on-disk state is untouched.
