# Integration Plan — StreetSamurai

**Goal:** route StreetSamurai's per-app data and settings through `JsonSettingsStore<T>` and the Vault path helpers, retiring the local `FileSystemPathProvider` boilerplate. LLM credentials already flow through Legion; once Legion swaps to Vault, StreetSamurai inherits the cloud-native chain.

**Cloud-native impact:** StreetSamurai's Claude key resolves through User Secrets / App Service / Key Vault automatically once the configuration chain is wired. Story metadata and character state continue to live on disk (large per-machine data — kept in `%LOCALAPPDATA%`).

**Path-convention note:** today StreetSamurai's settings live under `%LOCALAPPDATA%\MindAttic\StreetSamurai\`. Per the family-wide rule "settings stay roaming", consider moving the *settings* portion (theme, story prefs) to `%APPDATA%\MindAttic\StreetSamurai\` while leaving stories/character state in `%LOCALAPPDATA%`. Keep the legacy reader for one release so existing users migrate transparently.

## Files involved

| File | Action |
| --- | --- |
| `StreetSamurai.Core/StreetSamurai.Core.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `v3/StreetSamurai.Blazor/StreetSamurai.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `StreetSamurai.Core/Storage/FileSystemPathProvider.cs` | Replace the hand-rolled `Path.Combine(LocalApplicationData, "MindAttic", "StreetSamurai", ...)` logic with `VaultPaths.LocalApp("StreetSamurai")` + `VaultPaths.Ensure(...)`. Switch the *settings* path to `VaultPaths.RoamingBucket("StreetSamurai")`. |
| `StreetSamurai.Core/Services/SettingsService.cs` | Swap any hand-rolled JSON read/write with `JsonSettingsStore<StreetSamuraiSettings>` (which now defaults to roaming `%APPDATA%`). |
| `v3/StreetSamurai.Blazor/Program.cs` | Wire the cloud-native config chain and call `services.AddMindAtticVault(builder.Configuration);` + `services.AddVaultAppSettings<StreetSamuraiSettings>("StreetSamurai");`. |

## Step 1 — package reference + shared User Secrets ID

```xml
<PropertyGroup>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
</ItemGroup>
```

## Step 2 — refactor `FileSystemPathProvider`

```csharp
using MindAttic.Vault.Paths;

public sealed class FileSystemPathProvider : IPathProvider
{
    public string LocalRoot     { get; }   // %LOCALAPPDATA%\MindAttic\StreetSamurai
    public string SettingsPath  { get; }   // %APPDATA%\MindAttic\StreetSamurai
    public string StoriesPath   { get; }   // %LOCALAPPDATA%\MindAttic\StreetSamurai\Stories

    public FileSystemPathProvider()
    {
        LocalRoot    = VaultPaths.Ensure(VaultPaths.LocalApp("StreetSamurai"));
        SettingsPath = VaultPaths.Ensure(VaultPaths.RoamingBucket("StreetSamurai"));
        StoriesPath  = VaultPaths.Ensure(Path.Combine(LocalRoot, "Stories"));
    }
}
```

This replaces ~30 lines of `Environment.GetFolderPath` boilerplate.

## Step 3 — refactor `SettingsService`

Same pattern as `Tutor.md` — wrap a `JsonSettingsStore<StreetSamuraiSettings>` registered via `AddVaultAppSettings<...>("StreetSamurai")`. The store points at `%APPDATA%\MindAttic\StreetSamurai\settings.json` (roaming, matching the family default).

If existing users have `%LOCALAPPDATA%\MindAttic\StreetSamurai\settings.json`, ship a one-time migration on startup that copies the file across and logs a one-line warning, deferring deletion of the legacy file by one release.

## Step 4 — wire DI

```csharp
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
builder.Services.AddVaultAppSettings<StreetSamuraiSettings>("StreetSamurai");
builder.Services.AddLegionClient();
```

## Step 5 — CLI mode

StreetSamurai has CLI subcommands that bypass the web host. They construct `IPathProvider` independently. After the swap, any host (CLI or Blazor) gets the same paths because `VaultPaths.LocalApp` and `RoamingBucket` are host-agnostic. CLI hosts that don't build a full `IConfiguration` can still register the file-only `services.AddMindAtticVault()` overload for backward compat.

## Step 6 — verify

```powershell
dotnet build D:\Projects\MindAttic\StreetSamurai\StreetSamurai.slnx
dotnet test  D:\Projects\MindAttic\StreetSamurai\StreetSamurai.slnx
dotnet run   --project v3/StreetSamurai.Blazor
```

Check `%LOCALAPPDATA%\MindAttic\StreetSamurai\Stories\` (should exist with stories) and `%APPDATA%\MindAttic\StreetSamurai\settings.json` (should round-trip). Rerun any Cypress specs that exercise the editor / settings.

## Rollback

`git restore StreetSamurai.Core/ v3/StreetSamurai.Blazor/Program.cs v3/StreetSamurai.Blazor/StreetSamurai.Blazor.csproj`. No data migration occurred (or, if option-2 migration ran, the legacy `%LOCALAPPDATA%` settings.json is still there for one release).
