# Integration Plan — StreetSamurai

**Status (audit-verified 2026-05-09):** `FileSystemPathProvider` exists at `v3/StreetSamurai.Core/Services/FileSystemPathProvider.cs` and `IPathProvider` at `v3/StreetSamurai.Core/Interfaces/IPathProvider.cs`. The original plan was correct on naming. `SettingsService` has an auto-save timer + ctor-time `MigrateLegacyCredentialsToSharedStore()` call — the refactor needs to preserve both.

**Cloud-native impact:** Claude key resolves through User Secrets / App Service / Key Vault once Phase B.2 is in. Story metadata and character state stay on disk under `%LOCALAPPDATA%\MindAttic\StreetSamurai\` (unchanged).

---

## Phase B.1 — silent inheritance via Legion 2.1.0

StreetSamurai consumes Legion via `<ProjectReference>`. Bump Legion to 2.1.0 → StreetSamurai gets Vault transitively. The internal `MindAtticCredentialStore` calls inside `MigrateLegacyCredentialsToSharedStore()` survive unchanged (Legion's static facade).

| File | Action |
| --- | --- |
| `v3/StreetSamurai.Blazor/StreetSamurai.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `NuGet.config` (repo root) | Create with `LocalNuGet` + `nuget.org` sources (none today). |

Verify:

```powershell
dotnet build D:\Projects\MindAttic\StreetSamurai\StreetSamurai.slnx
dotnet test  D:\Projects\MindAttic\StreetSamurai\StreetSamurai.slnx
```

---

## Phase B.2 — wire cloud-native config + `VaultPaths` adoption

### Files

| File | Action |
| --- | --- |
| `v3/StreetSamurai.Core/StreetSamurai.Core.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `v3/StreetSamurai.Core/Services/FileSystemPathProvider.cs` | Replace inline `Environment.GetFolderPath(...)` with `VaultPaths.LocalApp("StreetSamurai")` and `VaultPaths.RoamingBucket("StreetSamurai")` for the settings-only paths. **Keep** the Stories/Characters/Engine paths under `%LOCALAPPDATA%` — those are large data, not settings. |
| `v3/StreetSamurai.Core/Services/SettingsService.cs` | Leave the existing `Load()` / auto-save timer alone. Add an optional `OverlayFromConfiguration(IConfiguration)` overload (additive) that fills in `ProviderAuth.ApiKey` from `MindAttic:Vault:LLM:*:apiKey`. |
| `v3/StreetSamurai.Blazor/Program.cs` | Wire the cloud-native config chain at the top. The existing `new SettingsService()` and `new FileSystemPathProvider(settings)` calls (lines 355–356) stay. |

### Path-provider refactor (illustrative)

```csharp
using MindAttic.Vault.Paths;

public sealed class FileSystemPathProvider : IPathProvider
{
    public string LocalRoot     { get; }   // %LOCALAPPDATA%\MindAttic\StreetSamurai (big data)
    public string SettingsPath  { get; }   // %APPDATA%\MindAttic\StreetSamurai     (roaming prefs)
    public string StoriesPath   { get; }
    // ... existing properties ...

    public FileSystemPathProvider(SettingsService settings)
    {
        LocalRoot    = VaultPaths.Ensure(VaultPaths.LocalApp("StreetSamurai"));
        SettingsPath = VaultPaths.Ensure(VaultPaths.RoamingBucket("StreetSamurai"));
        StoriesPath  = VaultPaths.Ensure(Path.Combine(LocalRoot, "Stories"));
        // ... rest unchanged ...
    }
}
```

This nets ~30 fewer lines of boilerplate.

### Settings migration

If existing users have `%LOCALAPPDATA%\MindAttic\StreetSamurai\settings.json`, ship a one-time copy-on-startup that reads the old path, writes via the new `SettingsPath`, and logs a one-line warning. Defer deletion of the legacy file by one release.

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

### CLI mode

StreetSamurai has CLI subcommands. They construct `FileSystemPathProvider` independently. The `VaultPaths.LocalApp` / `RoamingBucket` helpers are host-agnostic — no separate CLI plumbing needed. CLI hosts that don't build full IConfiguration can call `services.AddMindAtticVault()` (no-arg overload) for backward compat.

### Azure deployment

StreetSamurai already has a GitHub Actions workflow on the `master` branch (`azure-deploy.yml` → App Service `streetsamurai`). Once Vault is on nuget.org, this is unblocked. Application Settings:

| Application Setting | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |

### Verify

```powershell
dotnet build D:\Projects\MindAttic\StreetSamurai\StreetSamurai.slnx
dotnet test  D:\Projects\MindAttic\StreetSamurai\StreetSamurai.slnx
dotnet run   --project v3/StreetSamurai.Blazor
```

Confirm `%LOCALAPPDATA%\MindAttic\StreetSamurai\Stories\` exists and `%APPDATA%\MindAttic\StreetSamurai\settings.json` round-trips.

### Rollback

`git restore v3/StreetSamurai.Core/ v3/StreetSamurai.Blazor/Program.cs` and `rm NuGet.config`. The `MigrateLegacyCredentialsToSharedStore()` call still runs through Legion's static facade — no data loss.
