# Integration Plan — Tutor

**Goal:** smallest swap in the family. Tutor delegates all LLM credential reading to Legion already, so once Legion's swap is in (see `MindAttic.Legion.md`), Tutor inherits Vault transparently. The remaining work is to make Tutor's per-app settings persistence go through `JsonSettingsStore<T>` and to opt into the cloud-native configuration chain.

**Cloud-native impact:** Tutor reads its Claude key from User Secrets locally and App Service Application Settings (or Key Vault references) in production. Tutor preferences (theme, last lesson, etc.) keep roaming in `%APPDATA%\MindAttic\Tutor\settings.json`.

## Files involved

| File | Action |
| --- | --- |
| `Tutor.Core/Tutor.Core.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `Tutor.Blazor/Tutor.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `Tutor.Core/Services/SettingsService.cs` | If/where it persists JSON to disk, swap the hand-rolled `File.ReadAllText / WriteAllText` calls for `JsonSettingsStore<TutorSettings>`. |
| `Tutor.Blazor/Program.cs` | Wire the cloud-native config chain and call `services.AddMindAtticVault(builder.Configuration);` + `services.AddVaultAppSettings<TutorSettings>("Tutor");`. |

## Why so small

Tutor's audit shows: no `appsettings.json` credentials, no overlay methods, no broker keys, no ApiKey property bag. The only ingest of credentials is `LegionClient` (DI-injected via `AddLegionClient()`). Once Legion goes through Vault, Tutor's chain is automatically `IConfiguration → Vault file fallback → Legion → Tutor` with zero changes here.

## Step 1 — package reference + shared User Secrets ID

```xml
<PropertyGroup>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
</ItemGroup>
```

## Step 2 — `SettingsService` refactor (pattern only — apply to any Load/Save it currently has)

Replace:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, ... };

public TutorSettings Load()
{
    if (!File.Exists(path)) return new TutorSettings();
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<TutorSettings>(json, JsonOptions) ?? new TutorSettings();
}

public void Save(TutorSettings s)
{
    File.WriteAllText(path, JsonSerializer.Serialize(s, JsonOptions));
}
```

with:

```csharp
using MindAttic.Vault.Settings;

public sealed class SettingsService
{
    private readonly JsonSettingsStore<TutorSettings> store;

    public SettingsService(JsonSettingsStore<TutorSettings> store) => this.store = store;

    public TutorSettings Load()                  => store.Load();
    public void          Save(TutorSettings s)   => store.Save(s);
    public TutorSettings Update(Action<TutorSettings> mutate) => store.Update(mutate);
}
```

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
builder.Services.AddVaultAppSettings<TutorSettings>("Tutor");
builder.Services.AddLegionClient();   // unchanged
```

## Step 4 — verify

```powershell
dotnet build D:\Projects\MindAttic\Tutor\Tutor.slnx
dotnet run   --project Tutor.Blazor
```

Open Tutor in the browser. The persisted settings should round-trip exactly like before (look for `%APPDATA%\MindAttic\Tutor\settings.json` — note this is now roaming, matching the rest of the family). Rerun any Cypress specs that exercise the settings or LLM panels.

## Rollback

`git restore Tutor.Core/ Tutor.Blazor/Program.cs Tutor.Blazor/Tutor.Blazor.csproj`. No data migration occurred.
