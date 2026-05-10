# Integration Plan — ThinkTank

**Goal:** drop ThinkTank's wrapper of Legion's `MindAtticCredentialStore` and let it consume `LlmCredentialResolver` directly via DI. Trim the `SettingsService` overlap that today re-reads `appsettings.json` for provider defaults.

**Cloud-native impact:** ThinkTank's per-provider voter panels read keys from User Secrets locally and App Service Application Settings (or Key Vault references) in production. Existing `%APPDATA%\MindAttic\LLM\providers.json` keys keep working as a backward-compat fallback.

## Files involved

| File | Action |
| --- | --- |
| `ThinkTank.Core/ThinkTank.Core.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `ThinkTank.Blazor/ThinkTank.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `ThinkTank.Core/Services/SettingsService.cs` | Replace internal calls to `MindAtticCredentialStore.GetKey(...)` with the injected `LlmCredentialResolver`. Replace any custom JSON load/save with `JsonSettingsStore<ThinkTankSettings>`. |
| `ThinkTank.Blazor/Program.cs` | Wire the cloud-native config chain and call `services.AddMindAtticVault(builder.Configuration);` plus `services.AddVaultAppSettings<ThinkTankSettings>("ThinkTank");`. |

## Step 1 — package reference + shared User Secrets ID

```xml
<PropertyGroup>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
</ItemGroup>
```

Keep the existing `MindAttic.Legion` reference — ThinkTank still talks to Legion for LLM calls; we're only swapping the *credential reader*.

## Step 2 — refactor `SettingsService`

Replace any field that looks like:

```csharp
public string? GetClaudeKey() => MindAtticCredentialStore.GetKey("claude");
```

with constructor-injected resolver usage:

```csharp
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Settings;

public sealed class SettingsService
{
    private readonly LlmCredentialResolver llm;
    private readonly JsonSettingsStore<ThinkTankSettings> settings;

    public SettingsService(LlmCredentialResolver llm,
                           JsonSettingsStore<ThinkTankSettings> settings)
    {
        this.llm = llm;
        this.settings = settings;
    }

    public string? GetClaudeKey() => llm.GetKey("claude");

    public ThinkTankSettings Load() => settings.Load();
    public void              Save(ThinkTankSettings s) => settings.Save(s);
}
```

If `SettingsService` reads a provider-defaults section from `appsettings.json` and merges into a Legion config object, leave that mapping intact — Vault doesn't replace `IConfiguration`, it sits on top of it. The `ProviderDefaults` section becomes a non-secret companion to `MindAttic:Vault:LLM:*`.

## Step 3 — wire DI in `Program.cs`

```csharp
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
builder.Services.AddVaultAppSettings<ThinkTankSettings>("ThinkTank");
builder.Services.AddLegionClient();   // unchanged
```

## Step 4 — Azure deployment settings

In App Service Configuration:

| Name | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |
| `MindAttic__Vault__LLM__gemini__apiKey` | `AIza...` |
| `MindAttic__Vault__LLM__grok__apiKey`   | `xai-...` (optional) |

Or set them as Key Vault references for production-grade rotation.

## Step 5 — verify

```powershell
dotnet build D:\Projects\MindAttic\ThinkTank\ThinkTank.slnx
dotnet test  D:\Projects\MindAttic\ThinkTank\ThinkTank.slnx
dotnet run   --project ThinkTank.Blazor
```

Confirm the multi-LLM roundtable still resolves the same provider keys (open the settings UI; the masked key fingerprints should match the previous run). Rerun any Cypress specs under `tests/ThinkTank.Cypress/` that exercise the credentials panel.

## Rollback

`git restore ThinkTank.Core/Services/SettingsService.cs ThinkTank.Core/ThinkTank.Core.csproj ThinkTank.Blazor/Program.cs ThinkTank.Blazor/ThinkTank.Blazor.csproj`. No on-disk state is touched.
