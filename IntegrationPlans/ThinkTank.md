# Integration Plan — ThinkTank

**Status (audit-verified 2026-05-09):** medium complexity. The original plan only covered `GetKey` calls; the audit found `LoadAllRaw()`, `SaveRaw()`, and `ProvidersFileExists()` calls in `SettingsService.SyncLocalToSharedStore()` and `OverlaySharedCredentials()` that the plan glossed over. After Legion 2.1.0, those calls survive unchanged through the static facade, so a two-phase approach is safe.

**Cloud-native impact:** ThinkTank's voter-panel keys resolve through User Secrets locally / App Service Application Settings in production once Phase B.2 is in.

---

## Phase B.1 — silent (no work required)

After Legion 2.1.0 is consumed, ThinkTank's `MindAtticCredentialStore.LoadAllRaw() / SaveRaw() / ProvidersFileExists() / GetKey()` calls all delegate transparently to Vault's `LlmCredentialStore`. **No code change needed in ThinkTank for the file-store swap.** Just bump the Legion version in csproj.

| File | Action |
| --- | --- |
| `ThinkTank.Core/ThinkTank.Core.csproj` | Bump `<PackageReference Include="MindAttic.Legion" Version="2.0.0" />` to `2.1.0`. |
| `NuGet.config` (repo root) | Create with `LocalNuGet` + `nuget.org` sources (none today). |

Verify:

```powershell
dotnet build D:\Projects\MindAttic\ThinkTank\ThinkTank.slnx
dotnet test  D:\Projects\MindAttic\ThinkTank\ThinkTank.slnx
```

Expectation: 19 existing test files pass unchanged.

---

## Phase B.2 — cloud-native voter-panel resolution

This is the IConfiguration upgrade. Run as a separate PR.

### Files

| File | Action |
| --- | --- |
| `ThinkTank.Core/ThinkTank.Core.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `ThinkTank.Blazor/ThinkTank.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>` and the same package reference. |
| `ThinkTank.Core/Services/SettingsService.cs` | Add a constructor overload that accepts `IConfiguration`. Layer config-backed reads on top of the existing local-store + shared-store fallback chain. **Do not** touch `SyncLocalToSharedStore()` or `OverlaySharedCredentials()` — the static `MindAtticCredentialStore` facade calls keep working as the file-store layer. |
| `ThinkTank.Blazor/Program.cs` | Wire the cloud-native config chain and call `services.AddMindAtticVault(builder.Configuration)`. The factory at lines 15–34 (which casts to `ThinkTankSettingsService` and populates `ProviderDefaults` from config) stays — IConfiguration just gains additional sources. |

### Suggested SettingsService overlay (additive)

```csharp
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;

public partial class ThinkTankSettingsService
{
    /// <summary>
    /// Cloud-native overlay: fills in missing apiKey on each ProviderAuth entry
    /// from <c>MindAttic:Vault:LLM:&lt;providerId&gt;:apiKey</c>. Run AFTER the
    /// existing local-store and shared-store loads so config always wins.
    /// </summary>
    public void OverlayFromConfiguration(IConfiguration config)
    {
        if (config is null) return;
        var section = config.GetSection(VaultConfigurationKeys.LlmSection);
        if (!section.Exists()) return;

        foreach (var providerId in ProviderAuth.Keys.ToList())
        {
            var key = section[$"{providerId}:{VaultConfigurationKeys.ApiKeyProperty}"];
            if (string.IsNullOrWhiteSpace(key)) continue;
            ProviderAuth[providerId] = ProviderAuth[providerId] with { ApiKey = key };
        }
    }
}
```

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

Then in the existing SettingsService factory, after `Load()` and the shared-store overlay, call `service.OverlayFromConfiguration(builder.Configuration)`.

### Azure deployment settings

| Application Setting | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |
| `MindAttic__Vault__LLM__gemini__apiKey` | `AIza...` |
| `MindAttic__Vault__LLM__grok__apiKey` | `xai-...` (optional) |

### Verify

```powershell
dotnet build D:\Projects\MindAttic\ThinkTank\ThinkTank.slnx
dotnet test  D:\Projects\MindAttic\ThinkTank\ThinkTank.slnx
dotnet user-secrets --project ThinkTank.Blazor set "MindAttic:Vault:LLM:claude:apiKey" "sk-ant-test"
dotnet run --project ThinkTank.Blazor
```

The settings UI should show the User-Secret value masked. Cleanup:

```powershell
dotnet user-secrets --project ThinkTank.Blazor remove "MindAttic:Vault:LLM:claude:apiKey"
```

### Rollback

`git restore ThinkTank.Core/Services/SettingsService.cs ThinkTank.Core/ThinkTank.Core.csproj ThinkTank.Blazor/Program.cs ThinkTank.Blazor/ThinkTank.Blazor.csproj` and `rm NuGet.config`. No on-disk state is touched.

## Notes

- ThinkTank has no Azure deployment pipeline today, so the nuget.org publish gate is not blocking — Phase B.1 + B.2 can be piloted with the local feed alone.
- The existing `SyncLocalToSharedStore()` still writes through the static facade, which lands in `%APPDATA%\MindAttic\LLM\providers.json`. That's correct behaviour during the transition.
