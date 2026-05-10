# Integration Plan — MindAttic.Legion

**Goal:** retire `MindAttic.Legion.MindAtticCredentialStore` (363 lines) in favour of `MindAttic.Vault.Credentials.LlmCredentialResolver`, with a thin static shim left behind so existing call sites don't have to change in lockstep.

**Why first:** every Blazor app (Tutor, ThinkTank, IdiotProof, StreetSamurai, TaxRateCollector) consumes Legion. Doing Legion first means downstream apps inherit the Vault transparently the moment they bump the Legion package version.

**Cloud-native impact:** once Legion runs through Vault, its `LegionClient` reads keys from User Secrets (dev) and Azure App Service Application Settings (prod) automatically — no Legion-side changes required. Today Legion is file-only; after this plan it inherits the full source-precedence chain documented in the [README](../README.md).

## Files involved

| File | Action |
| --- | --- |
| `MindAttic.Legion/MindAttic.Legion.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. Bump `<Version>` to `2.1.0`. |
| `MindAttic.Legion/Services/MindAtticCredentialStore.cs` | Delete the 363-line implementation. Replace with a thin static facade that delegates to `LlmCredentialStore.Default`. |
| `MindAttic.Legion/Services/LegionClient.cs` and friends | Optional follow-up: inject `LlmCredentialResolver` instead of calling the static facade. Not required for the swap to work. |
| `MindAttic.Legion.Tests/` | Tests already use `MINDATTIC_LLM_CREDENTIALS` env override, which Vault honours unchanged. No edits expected. |

## Step 1 — package reference + shared User Secrets ID

Edit `MindAttic.Legion/MindAttic.Legion/MindAttic.Legion.csproj`:

```xml
<PropertyGroup>
  <!-- existing properties unchanged -->
  <Version>2.1.0</Version>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
  <!-- existing references unchanged -->
</ItemGroup>
```

The shared User Secrets ID matches `VaultConfigurationKeys.SharedUserSecretsId` so any project that pins it sees the same dev secrets.

## Step 2 — replace `MindAtticCredentialStore.cs` with a shim

```csharp
using MindAttic.Vault.Credentials;

namespace MindAttic.Legion;

/// <summary>
/// Backward-compatible facade. The real implementation now lives in
/// MindAttic.Vault.Credentials.LlmCredentialStore; this static class keeps
/// the existing Legion + downstream call sites working until they migrate
/// to inject <see cref="LlmCredentialResolver"/> via DI.
/// </summary>
public static class MindAtticCredentialStore
{
    public static string CredentialDirectory  => LlmCredentialStore.Default.Directory;
    public static string ProvidersFilePath    => LlmCredentialStore.Default.ProvidersFilePath;
    public static bool   ProvidersFileExists() => LlmCredentialStore.Default.ProvidersFileExists();

    public static string?                     GetKey(string providerId)             => LlmCredentialStore.Default.GetKey(providerId);
    public static void                        SetKey(string providerId, string key) => LlmCredentialStore.Default.SetKey(providerId, key);
    public static Dictionary<string, string>  LoadAll()                              => LlmCredentialStore.Default.LoadAll();
    public static List<string>                ListProviders()                        => LlmCredentialStore.Default.ListProviders();
    public static Dictionary<string, string>  LoadAllRaw()                           => LlmCredentialStore.Default.LoadAllRaw();
    public static void                        SaveAllRaw(IDictionary<string, string> providers) => LlmCredentialStore.Default.SaveAllRaw(providers);
    public static void                        SaveRaw(string providerId, string raw) => LlmCredentialStore.Default.SaveRaw(providerId, raw);
}
```

Diff stat: ~+25 / −363 lines.

## Step 3 — opt downstream LegionClient hosts into the cloud-native flow

Each consumer that registers Legion via `services.AddLegionClient()` should now also call:

```csharp
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>()      // optional, dev only
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
builder.Services.AddLegionClient();
```

The `AddMindAtticVault(builder.Configuration)` call wires the resolver chain; `AddLegionClient()` continues to consume the static facade until the follow-up DI migration in Step 5.

## Step 4 — verify Legion's existing test suite

```powershell
dotnet test D:\Projects\MindAttic\MindAttic.Legion\MindAttic.Legion.slnx
```

The Legion tests already exercise `MINDATTIC_LLM_CREDENTIALS`. They should pass unchanged because:

- Vault honours the same env var.
- Resolution order is identical (`.key` → `providers.json` → `credentials.json`).
- `SetKey` produces the same JSON shape (verified by `LlmCredentialStoreTests.SetKey_Preserves_Model_And_MaxTokens`).

## Step 5 — repack Legion to the local NuGet feed

```powershell
dotnet pack D:\Projects\MindAttic\MindAttic.Legion\MindAttic.Legion\MindAttic.Legion.csproj -c Release -o C:\LocalNuGet
```

## Step 6 — (optional follow-up, separate PR) migrate Legion's own internals

`LegionClient` and `LLMVotingService` reach for `MindAtticCredentialStore` directly. After the shim is in, file a follow-up to inject `LlmCredentialResolver` so the static call sites disappear and Legion benefits from cloud-native sources internally without consumer wiring. Not required for the swap.

## Rollback

```powershell
git restore MindAttic.Legion/MindAttic.Legion.csproj
git restore MindAttic.Legion/Services/MindAtticCredentialStore.cs
```

Then revert the local NuGet pack by restoring the previous `.nupkg` (every prior version is still in `C:\LocalNuGet`).

## Verification checklist

- [ ] `dotnet build MindAttic.Legion.slnx` succeeds.
- [ ] `dotnet test MindAttic.Legion.slnx` is green.
- [ ] `MindAttic.Legion.Cli health` resolves the same Claude/Gemini keys it did before.
- [ ] `%APPDATA%\MindAttic\LLM\providers.json` is unchanged on disk.
- [ ] `dotnet user-secrets set "MindAttic:Vault:LLM:claude:apiKey" "test"` from a Legion-hosting consumer surfaces through `LegionClient`.
