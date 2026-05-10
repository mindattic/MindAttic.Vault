# Integration Plan — Tutor

**Status (audit-verified 2026-05-09):** Tutor's `SettingsService` uses `ISecurePreferences` for per-key async storage — fundamentally different from the JSON Load/Save model the original plan assumed. Refresh: skip the SettingsService refactor entirely; just inherit Vault transitively through Legion 2.1.0 and add the IConfiguration chain so future code can use `LlmCredentialResolver` if it wants.

**Cloud-native impact:** Tutor's Claude key resolves through User Secrets locally / App Service Application Settings in production once Phase B.2 is in. Tutor preferences (theme, lesson state, grading scale) keep using `ISecurePreferences` — out of scope for this plan.

---

## Phase B.1 — silent inheritance via Legion 2.1.0

`Tutor.Core` references `MindAttic.Legion` via `<ProjectReference>` (audit confirmed `..\..\MindAttic.Legion\MindAttic.Legion\MindAttic.Legion.csproj`). When Legion is bumped to 2.1.0 inside the family, Tutor inherits Vault transitively at compile time. **No code change in Tutor for B.1.**

| File | Action |
| --- | --- |
| `Tutor.Blazor/Tutor.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>` so future `dotnet user-secrets` commands target the shared file. |

Verify:

```powershell
dotnet build D:\Projects\MindAttic\Tutor\Tutor.slnx
dotnet test  D:\Projects\MindAttic\Tutor\Tutor.slnx
```

Existing Tutor.Tests should pass unchanged — Legion's static credential facade still works.

---

## Phase B.2 — cloud-native config chain (forward-looking)

This unlocks future code paths in Tutor that want to read `IConfiguration["MindAttic:Vault:LLM:claude:apiKey"]` directly without going through Legion. The current `SettingsService` is left alone.

### Files

| File | Action |
| --- | --- |
| `Tutor.Blazor/Tutor.Blazor.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `NuGet.config` (repo root) | Create with `LocalNuGet` + `nuget.org` sources. |
| `Tutor.Blazor/Program.cs` | Wire the cloud-native config chain. Already has `AddLegionClient()` (line 17) — add the Vault registration above it so `IServiceProvider.GetRequiredService<LlmCredentialResolver>()` works for any future service. |

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
builder.Services.AddLegionClient();   // unchanged
```

### Verify

```powershell
dotnet build D:\Projects\MindAttic\Tutor\Tutor.slnx
dotnet user-secrets --project Tutor.Blazor set "MindAttic:Vault:LLM:claude:apiKey" "sk-ant-test"
dotnet run --project Tutor.Blazor
```

Open Tutor in the browser, exercise an LLM-backed feature (lesson generation, grading), confirm it works with the User-Secret value. Cleanup:

```powershell
dotnet user-secrets --project Tutor.Blazor remove "MindAttic:Vault:LLM:claude:apiKey"
```

### Rollback

`git restore Tutor.Blazor/Program.cs Tutor.Blazor/Tutor.Blazor.csproj` and `rm NuGet.config`. No data migration occurred; `ISecurePreferences` storage is untouched.

## Notes

- Tutor has no Azure deployment pipeline today, so nuget.org gate isn't blocking.
- `SettingsService` (716 lines, async per-key storage) deliberately untouched. A future plan could introduce a `IClaudeKeyProvider` interface that `SettingsService` implements via Vault, but that's a separate refactor.
