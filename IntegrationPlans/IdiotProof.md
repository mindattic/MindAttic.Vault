# Integration Plan — IdiotProof

**Status (audit-verified 2026-05-09):** medium complexity; the original plan's method signatures didn't match current source. This refresh splits the work into a low-risk file-swap (Phase B.1) and a separate cloud-native upgrade (Phase B.2).

**Cloud-native impact (after B.2):** Alpaca + Claude keys resolve from User Secrets in dev, App Service Application Settings (or Key Vault references) in prod. Existing `%APPDATA%\MindAttic\Brokers\providers.json` keeps working as fallback.

**Out of scope:** SQL Server `UserApiKeys` and `AppSettings` tables stay the system of record per `IdiotProof/CLAUDE.md`. Vault only replaces the file-and-overlay stops in the chain.

---

## Phase B.1 — drop the duplicate BrokerCredentialStore (low risk)

The 363-line LLM credential store work is already done by Legion 2.1.0; that swap is invisible here. The remaining file-side win is deleting IdiotProof's hand-rolled `BrokerCredentialStore.cs` (which mirrors what's now in Vault) and re-pointing `OverlayFromBrokerCredentials()` at `Vault.Credentials.BrokerCredentialStore.Default`.

### Files (verified)

| File | Action |
| --- | --- |
| `IdiotProof.Engine/IdiotProof.Engine.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `NuGet.config` (repo root) | Already exists pointing at `..\local-feed`. Update to point at `C:\LocalNuGet` (or to nuget.org once Vault is published). The `..\local-feed` relative path doesn't exist on GitHub Actions Linux runners — this is a real bug. |
| `IdiotProof.Engine/Settings/BrokerCredentialStore.cs` | **Delete.** Vault's `BrokerCredentialStore` is a drop-in replacement (same `%APPDATA%\MindAttic\Brokers\providers.json` path, same `MINDATTIC_BROKER_CREDENTIALS` override). |
| `IdiotProof.Engine/Settings/AppSettings.cs` | Edit `OverlayFromBrokerCredentials()` body (signature unchanged) to call Vault's broker store. |

### Diff for `OverlayFromBrokerCredentials()` (current lines 107–115)

Current:

```csharp
public void OverlayFromBrokerCredentials()
{
    var providerId = AlpacaIsPaper ? "alpaca-paper" : "alpaca-live";
    var creds = BrokerCredentialStore.Get(providerId);   // local IdiotProof type
    if (creds is null) return;
    AlpacaApiKeyId = creds.ApiKey;
    AlpacaApiSecretKey = creds.Secret;
}
```

After:

```csharp
public void OverlayFromBrokerCredentials()
{
    var providerId = AlpacaIsPaper ? "alpaca-paper" : "alpaca-live";
    // Fresh-construct so MINDATTIC_BROKER_CREDENTIALS env override is re-read
    // (matches the existing static-store behaviour).
    var store = new MindAttic.Vault.Credentials.BrokerCredentialStore(
        Environment.GetEnvironmentVariable(MindAttic.Vault.Credentials.BrokerCredentialStore.DirectoryEnvVar)
        ?? MindAttic.Vault.Paths.VaultPaths.RoamingBucket(MindAttic.Vault.Credentials.BrokerCredentialStore.Bucket));
    var creds = store.GetBrokerCreds(providerId);
    if (creds is null) return;
    AlpacaApiKeyId = creds.ApiKey;
    AlpacaApiSecretKey = creds.Secret;
}
```

`OverlayFromMindAtticCredentials()` (line 95) needs no change — it already calls `MindAttic.Legion.MindAtticCredentialStore.GetKey("claude")`, which is now the Legion 2.1.0 shim that delegates to Vault.

### Verify B.1

```powershell
dotnet build D:\Projects\MindAttic\IdiotProof\IdiotProof.slnx
dotnet test  D:\Projects\MindAttic\IdiotProof\IdiotProof.slnx
dotnet run   --project IdiotProof.Cli -- health
```

Cypress: rerun any spec under `tests/IdiotProof.Cypress/` that exercises Alpaca paper credentials.

### Rollback B.1

`git restore IdiotProof.Engine/`. Drop the new `<PackageReference>`. The legacy `BrokerCredentialStore.cs` is restored from git, no on-disk data touched.

---

## Phase B.2 — cloud-native overlay (run after B.1 verifies)

This is the IConfiguration wiring the original plan covered. Do it as a separate PR so B.1 can be tested in isolation first.

### Files

| File | Action |
| --- | --- |
| `IdiotProof.Blazor/IdiotProof.Blazor.csproj` and `IdiotProof.Cli/IdiotProof.Cli.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `IdiotProof.Engine/Settings/AppSettings.cs` | Add an optional `OverlayFromConfiguration(IConfiguration)` method. Keep B.1 methods for backward compat. |
| `IdiotProof.Blazor/Program.cs` and `IdiotProof.Cli/Program.cs` | Wire the cloud-native config chain. |

### `AppSettings.OverlayFromConfiguration` (new method, additive)

```csharp
public void OverlayFromConfiguration(IConfiguration config)
{
    var claude = config["MindAttic:Vault:LLM:claude:apiKey"];
    if (!string.IsNullOrWhiteSpace(claude)) ClaudeApiKey = claude;

    var providerId = AlpacaIsPaper ? "alpaca-paper" : "alpaca-live";
    var apiKey  = config[$"MindAttic:Vault:Brokers:{providerId}:apiKey"];
    var secret  = config[$"MindAttic:Vault:Brokers:{providerId}:secret"];
    if (!string.IsNullOrWhiteSpace(apiKey)) AlpacaApiKeyId     = apiKey;
    if (!string.IsNullOrWhiteSpace(secret)) AlpacaApiSecretKey = secret;
}
```

### Program.cs additions (both Blazor and CLI hosts)

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

Update the existing AppSettings factory (Blazor Program.cs lines ~82–88):

```csharp
builder.Services.AddSingleton(sp =>
{
    var storage = sp.GetRequiredService<IStorageProvider>();
    var config  = sp.GetRequiredService<IConfiguration>();

    var settings = AppSettings.Load(storage);
    settings.OverlayFromEnvironment();
    settings.OverlayFromMindAtticCredentials();   // file fallback (B.1)
    settings.OverlayFromBrokerCredentials();       // file fallback (B.1)
    settings.OverlayFromConfiguration(config);     // cloud-native, wins last
    return settings;
});
```

### Azure deployment settings

| Application Setting | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |
| `MindAttic__Vault__Brokers__alpaca-paper__apiKey` | `PK...` |
| `MindAttic__Vault__Brokers__alpaca-paper__secret` | `S...` |
| `MindAttic__Vault__Brokers__alpaca-paper__baseUrl` | `https://paper-api.alpaca.markets` |
| `MindAttic__Vault__Brokers__alpaca-live__apiKey` | (when going live) |
| `MindAttic__Vault__Brokers__alpaca-live__secret` | (when going live) |
| `ConnectionStrings__IdiotProof` | unchanged |

### Verify B.2

Same build + test commands. For the cloud-native path specifically:

```powershell
dotnet user-secrets --project IdiotProof.Blazor set "MindAttic:Vault:LLM:claude:apiKey" "sk-ant-test"
dotnet run --project IdiotProof.Blazor
```

Confirm the dashboard shows the User Secret value taking precedence over the legacy file. Then `dotnet user-secrets remove ...` to clean up.

### Rollback B.2

Drop `OverlayFromConfiguration` and the Program.cs additions. B.1's behaviour is preserved.

## Risks

- **DB still wins.** The repository layer that loads `UserApiKeys` from SQL must run AFTER the AppSettings overlays so user-specific keys override system defaults. Preserve this order.
- **NuGet feed.** The existing `..\local-feed` reference in `nuget.config` is broken on Linux CI. The fix (point at `C:\LocalNuGet` for local, nuget.org for CI) is part of B.1 and is required before any GitHub Actions deploy succeeds.
