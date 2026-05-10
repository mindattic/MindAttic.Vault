# Integration Plan — IdiotProof

**Goal:** retire `IdiotProof.Engine.Settings.BrokerCredentialStore` and the four hand-rolled overlay methods on `AppSettings` in favour of Vault's cloud-native resolver chain. Highest blast-radius integration in the family — touching SQL Server, broker keys, LLM keys, and a multi-tier overlay chain — so do it second (after Legion).

**Out of scope:** the SQL Server side of credentials (`UserApiKeys`, `AppSettings` tables) stays the system of record per `IdiotProof/CLAUDE.md`. Vault only replaces the *file-and-overlay* stops in the chain. The DB stays as the highest-priority source.

**Cloud-native impact:** after the swap, IdiotProof reads Alpaca + Claude keys from User Secrets in dev, App Service Application Settings (or Key Vault references) in prod, and the legacy `%APPDATA%\MindAttic\Brokers\providers.json` file as a fallback for existing developer setups.

## Files involved

| File | Action |
| --- | --- |
| `IdiotProof.Engine/IdiotProof.Engine.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. Keep MindAttic.Legion (still required for the LLM client itself; only the credential reader is moving). |
| `IdiotProof.Blazor/IdiotProof.Blazor.csproj` and `IdiotProof.Cli/IdiotProof.Cli.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `IdiotProof.Engine/Settings/BrokerCredentialStore.cs` | **Delete.** Vault's `BrokerCredentialStore` reads the same `%APPDATA%\MindAttic\Brokers\providers.json` with the same env override (`MINDATTIC_BROKER_CREDENTIALS`); the cloud-native `BrokerCredentialResolver` adds IConfiguration on top. |
| `IdiotProof.Engine/Settings/AppSettings.cs` | Replace `OverlayFromMindAtticCredentials()` and `OverlayFromBrokerCredentials()` to delegate to Vault resolvers. Replace `OverlayFromEnvironment()` with `EnvironmentOverlay.ApplyAll(...)`. Replace `Load()`/`Save()` with `JsonSettingsStore<AppSettings>`. |
| `IdiotProof.Blazor/Program.cs` and `IdiotProof.Cli/Program.cs` | Wire the cloud-native configuration chain (User Secrets, Vault files, env vars) and call `services.AddMindAtticVault(builder.Configuration)`. |

## Step 1 — package reference + shared User Secrets ID

```xml
<PropertyGroup>
  <!-- in BOTH host csprojs -->
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
</ItemGroup>
```

## Step 2 — delete `BrokerCredentialStore.cs`

`Vault.Credentials.BrokerCredentialStore` is a drop-in replacement and `BrokerCredentialResolver` is the cloud-native wrapper. The shape on disk is identical:

```jsonc
{
  "alpaca-paper": { "type": "alpaca", "apiKey": "PK...", "secret": "...", "baseUrl": "..." },
  "alpaca-live":  { "type": "alpaca", "apiKey": "AK...", "secret": "...", "baseUrl": "..." }
}
```

`GetBrokerCreds(providerId)` returns the same `record(ApiKey, Secret, BaseUrl)` and applies the same nullability rules.

## Step 3 — slim down `AppSettings`

Replace the four overlay methods with this:

```csharp
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;

public sealed class AppSettings
{
    // ... fields unchanged ...

    public static AppSettings Load(IStorageProvider storage,
                                   JsonSettingsStore<AppSettings>? store = null)
    {
        storage.EnsureDirectories();
        store ??= new JsonSettingsStore<AppSettings>(storage.SettingsPath, "app-settings.json");
        return store.Load();
    }

    public void Save(IStorageProvider storage,
                     JsonSettingsStore<AppSettings>? store = null)
    {
        storage.EnsureDirectories();
        store ??= new JsonSettingsStore<AppSettings>(storage.SettingsPath, "app-settings.json");
        store.Save(this);
    }

    public void OverlayFromEnvironment() =>
        EnvironmentOverlay.ApplyAll(new (string, Action<string>)[]
        {
            ("AlpacaApiKeyId",     v => AlpacaApiKeyId     = v),
            ("AlpacaApiSecretKey", v => AlpacaApiSecretKey = v),
            ("PolygonApiKey",      v => PolygonApiKey      = v),
            ("ClaudeApiKey",       v => ClaudeApiKey       = v),
        });

    public void OverlayFromMindAtticCredentials(LlmCredentialResolver llm)
    {
        var key = llm.GetKey("claude");
        if (!string.IsNullOrWhiteSpace(key)) ClaudeApiKey = key;
    }

    public void OverlayFromBrokerCredentials(BrokerCredentialResolver brokers,
                                             BrokerCredentialStore brokerFileStore)
    {
        var providerId = AlpacaIsPaper ? "alpaca-paper" : "alpaca-live";

        // apiKey can come from any layer (config or file).
        var apiKey = brokers.GetKey(providerId);
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        // secret + baseUrl currently live in the file; production should set
        // MindAttic:Vault:Brokers:alpaca-paper:secret and :baseUrl in App Service config.
        var fileCreds = brokerFileStore.GetBrokerCreds(providerId);

        AlpacaApiKeyId     = apiKey;
        AlpacaApiSecretKey = fileCreds?.Secret ?? "";
        // BaseUrl is read from fileCreds elsewhere when needed.
    }
}
```

Diff stat: ~−40 lines on `AppSettings.cs`. The `JsonOptions` static field comes out too — Vault owns it.

## Step 4 — wire DI in both hosts (cloud-native)

`IdiotProof.Blazor/Program.cs` and `IdiotProof.Cli/Program.cs`:

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
```

The existing AppSettings registration becomes:

```csharp
builder.Services.AddSingleton(sp =>
{
    var storage  = sp.GetRequiredService<IStorageProvider>();
    var llm      = sp.GetRequiredService<LlmCredentialResolver>();
    var brokers  = sp.GetRequiredService<BrokerCredentialResolver>();
    var brokerFs = sp.GetRequiredService<BrokerCredentialStore>();

    var settings = AppSettings.Load(storage);
    settings.OverlayFromEnvironment();
    settings.OverlayFromMindAtticCredentials(llm);
    settings.OverlayFromBrokerCredentials(brokers, brokerFs);
    return settings;
});
```

## Step 5 — Azure App Service production setup

In the Azure portal → **Configuration** → **Application settings**, add (or set as Key Vault references):

- `MindAttic__Vault__LLM__claude__apiKey`
- `MindAttic__Vault__Brokers__alpaca-paper__apiKey`
- `MindAttic__Vault__Brokers__alpaca-paper__secret`
- `MindAttic__Vault__Brokers__alpaca-paper__baseUrl`
- `MindAttic__Vault__Brokers__alpaca-live__apiKey` (when going live)
- `MindAttic__Vault__Brokers__alpaca-live__secret`
- `ConnectionStrings__IdiotProof` (unchanged; CLAUDE.md keeps SQL config in App Service connection strings)

The legacy env vars (`AlpacaApiKeyId`, `ClaudeApiKey`, …) keep working through `OverlayFromEnvironment()` for transition compatibility.

## Step 6 — verify

1. `dotnet build IdiotProof.slnx` clean.
2. `dotnet test` (NUnit) green for `IdiotProof.Engine.Tests` and `IdiotProof.Blazor.Tests`.
3. **Cypress:** rerun `tests/IdiotProof.Cypress/` specs that exercise the broker dashboard and LLM voting card. Vault preserves on-disk shape, so no spec changes expected.
4. Manual smoke: launch the Blazor app, confirm the dashboard loads with Alpaca paper creds resolved (no "API key missing" banner).
5. CLI smoke: `dotnet run --project IdiotProof.Cli -- health` reports the same broker + LLM credential states as before.

## Rollback

`git restore IdiotProof.Engine/` reverts `AppSettings.cs` and brings back `BrokerCredentialStore.cs`. Drop the `<PackageReference>` line. No data on disk is affected — Vault reads the same files.

## Risks

- **Order-of-operations regression.** Preserve overlay order: file → env → MindAttic → Broker. Vault doesn't impose its own ordering.
- **Test seam.** Engine tests that previously stubbed broker creds via env vars continue to work because Vault's broker store honours `MINDATTIC_BROKER_CREDENTIALS`.
- **DB still wins.** SQL `UserApiKeys` rows must remain the highest-priority source per CLAUDE.md. The DI registration above does file/env/Vault overlays *before* the SQL repository merges its values on top — preserve that ordering or risk silently bypassing per-user overrides.
