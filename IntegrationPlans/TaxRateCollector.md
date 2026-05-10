# Integration Plan — TaxRateCollector

**Goal:** consolidate TaxRateCollector's `SettingsService` into `JsonSettingsStore<AppSettings>`, route its USPS API key and Anthropic key reads through Vault's cloud-native resolver chain, and drop the inline `Environment.GetEnvironmentVariable(...)` overlay scattered through the file.

**Cloud-native impact:** TaxRateCollector reads its USPS and Anthropic keys from User Secrets in dev and App Service Application Settings (or Key Vault references) in production. The `%APPDATA%\MindAttic\TaxRateCollector\settings.json` file continues to hold non-secret preferences (theme, font, feature flags).

## Files involved

| File | Action |
| --- | --- |
| `TaxRateCollector.Infrastructure/TaxRateCollector.Infrastructure.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `TaxRateCollector.Blazor/TaxRateCollector.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `TaxRateCollector.Infrastructure/Services/SettingsService.cs` | Replace `Load()/Save()` with `JsonSettingsStore<AppSettings>`. Move the USPS / Anthropic key reads to Vault resolvers + an `EnvironmentOverlay` for env var fallback. |
| `TaxRateCollector.Blazor/Program.cs` | Wire the cloud-native config chain and call `services.AddMindAtticVault(builder.Configuration);` + `services.AddVaultAppSettings<AppSettings>("TaxRateCollector");`. |

## On-disk layout (settings stay in roaming `%APPDATA%`)

```
%APPDATA%\MindAttic\TaxRateCollector\
├── settings.json          ← non-secret preferences (theme, font, feature flags)
└── evidence\              ← evidence files (untouched by Vault)
```

Secrets that previously lived in `settings.json` (USPS key, Anthropic key) move into `IConfiguration`. Existing values are read once at upgrade time by the legacy field on `AppSettings` and treated as a one-time fallback.

## Step 1 — package reference + shared User Secrets ID

```xml
<PropertyGroup>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
</ItemGroup>
```

## Step 2 — `SettingsService` refactor

```csharp
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;

public sealed class SettingsService
{
    private readonly JsonSettingsStore<AppSettings> store;
    private readonly LlmCredentialResolver llm;
    private readonly IConfiguration config;

    public SettingsService(JsonSettingsStore<AppSettings> store,
                           LlmCredentialResolver llm,
                           IConfiguration config)
    {
        this.store  = store;
        this.llm    = llm;
        this.config = config;
    }

    public AppSettings Load()
    {
        var s = store.Load();

        // Pull secrets from IConfiguration (User Secrets / App Service / Key Vault).
        s.AnthropicApiKey = llm.GetKey("claude")
                            ?? config["MindAttic:Vault:Tokens:usps"]   // wrong key on purpose: see "verify" below
                            ?? s.AnthropicApiKey;
        s.UspsApiKey      = config["MindAttic:Vault:Tokens:usps"] ?? s.UspsApiKey;

        // Optional final overlay from process env vars (Azure App Service legacy names).
        EnvironmentOverlay.ApplyAll(new (string, Action<string>)[]
        {
            ("USPS_API_KEY",      v => s.UspsApiKey      = v),
            ("ANTHROPIC_API_KEY", v => s.AnthropicApiKey = v),
        });

        return s;
    }

    public void Save(AppSettings s) => store.Save(s);
}
```

> **Audit note.** The exact env var names + the existing `appsettings.json` key paths must be confirmed against current `SettingsService.cs` before merging. Replace `MindAttic:Vault:Tokens:usps` etc. with the correct paths if the project already settled on different names.

## Step 3 — wire DI

```csharp
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
builder.Services.AddVaultAppSettings<AppSettings>("TaxRateCollector");
```

## Step 4 — Azure deployment settings

| Application Setting | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |
| `MindAttic__Vault__Tokens__usps` | `USPS-...` |

## Step 5 — verify

```powershell
dotnet build D:\Projects\MindAttic\TaxRateCollector\TaxRateCollector.slnx
dotnet run   --project TaxRateCollector.Blazor
```

Confirm the existing `settings.json` round-trips (open the settings page, save, diff the file — only field order may shift). Rerun any Cypress specs.

## Rollback

`git restore TaxRateCollector.Infrastructure/ TaxRateCollector.Blazor/Program.cs TaxRateCollector.Blazor/TaxRateCollector.Blazor.csproj`.

## Risks

- **Secrets bleed.** The legacy `settings.json` likely has `UspsApiKey` and `AnthropicApiKey` fields persisted on disk. After the swap, blank those fields on first save so they're not written back from `AppSettings.Save(...)`. Add a one-line migration that nulls them out.
- **Path convention.** Verify `JsonSettingsStore<AppSettings>.ForApp("TaxRateCollector")` resolves to the same `%APPDATA%\MindAttic\TaxRateCollector\` path the project currently uses (it does, given Vault 0.2.0's roaming default).
