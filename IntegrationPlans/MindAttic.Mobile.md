# Integration Plan — MindAttic.Mobile

**Goal:** move the MindAttic.Mobile bearer token out of `D:\Projects\MindAttic\settings.json` into the Vault, so the secret stops sitting in a file that's tracked in git history and starts inheriting Vault's source-precedence chain (User Secrets dev / App Service settings prod).

**Status (2026-05-10):** code currently reads the token from `settings.json > Mobile.Token`. A `// TODO: migrate to Vault` comment is already in `MindAttic.Mobile.Core\MobileSettings.cs` and the bearer middleware reads `IConfiguration["Mobile:Token"]`, so the migration is a configuration-source change, not a refactor.

**Why low-priority:** the value gates a Tailscale-only HTTP service, not internet-facing. The risk is committing it to the public git remote, which is solved by adding `Mobile.Token` to a `.gitignore`'d Vault file rather than rewriting code paths.

## Files involved

| File | Action |
| --- | --- |
| `MindAttic.Mobile/MindAttic.Mobile.Server/MindAttic.Mobile.Server.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `MindAttic.Mobile/MindAttic.Mobile.Server/Program.cs` | Add `builder.Configuration.AddMindAtticVaultFiles().AddUserSecrets<Program>(optional: true)` BEFORE the existing parent-`settings.json` discovery, then `builder.Services.AddMindAtticVault(builder.Configuration)`. |
| `D:\Projects\MindAttic\settings.json` | Remove the `Mobile.Token` field. (The other Mobile.* fields stay — they're not secrets.) |
| `%APPDATA%\MindAttic\Vault\mobile.json` (or shared `vault.json`) | New: holds `{ "MindAttic": { "Mobile": { "Token": "<token>" } } }`. Generated once via `Generate-Token.ps1`. |
| `MindAttic.Mobile/console-launcher.ps1` companion (PowerShell) | Currently reads `Mobile.Token` from `settings.json`. After migration it must read it from the Vault file (PS can `Get-Content $env:APPDATA\MindAttic\Vault\mobile.json | ConvertFrom-Json`). |

## Step 1 — wire Vault config in the Server

```csharp
// Program.cs, before the existing ResolveSettingsPath() block:
builder.Configuration
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>(optional: true);
builder.Services.AddMindAtticVault(builder.Configuration);
```

The Vault's resolution order — Vault file → User Secrets → env var — already wins over the parent `settings.json` because that file is added afterwards. So the existing `BearerTokenAuth` reads `Mobile:Token` and gets the new source automatically.

## Step 2 — move the token

```powershell
$token = D:\Projects\MindAttic\MindAttic.Mobile\Generate-Token.ps1 | Select-Object -First 1
$vaultFile = "$env:APPDATA\MindAttic\Vault\mobile.json"
New-Item -ItemType Directory -Path (Split-Path $vaultFile) -Force | Out-Null
@{ MindAttic = @{ Mobile = @{ Token = $token } } } | ConvertTo-Json -Depth 5 | Set-Content $vaultFile -Encoding UTF8
```

Then edit `D:\Projects\MindAttic\settings.json` and delete the `Mobile.Token` line. Leave `Mobile.ServerUrl`, `Mobile.Enabled`, `Mobile.AllProjects` in place.

## Step 3 — update console-launcher.ps1

The `Should-UseMobileBridge` and `Invoke-AgentViaMobileBridge` helpers currently read `Mobile.Token` from `settings.json`. Replace that lookup with:

```powershell
function Get-MobileToken {
    $vaultFile = Join-Path $env:APPDATA "MindAttic\Vault\mobile.json"
    if (-not (Test-Path $vaultFile)) { return $null }
    return ((Get-Content $vaultFile -Raw | ConvertFrom-Json).MindAttic.Mobile.Token)
}
```

and call it instead of `Get-JsonPropertyValue $mobile "Token"`.

## Step 4 — verify

```powershell
dotnet build  D:\Projects\MindAttic\MindAttic.Mobile\MindAttic.Mobile.slnx
dotnet test   D:\Projects\MindAttic\MindAttic.Mobile\MindAttic.Mobile.slnx
dotnet run    --project D:\Projects\MindAttic\MindAttic.Mobile\MindAttic.Mobile.Server -c Release
# In another shell:
curl -i -H "Authorization: Bearer <token from vault file>" http://127.0.0.1:7780/healthz
```

200 with the new token, 401 when omitted — same contract as before, different storage.

## Step 5 — User Secrets parity (dev quality-of-life)

```powershell
dotnet user-secrets --project MindAttic.Mobile.Server set "Mobile:Token" "sk-mobile-test"
```

Confirm the test value beats the file (Vault chain puts User Secrets last, so it wins). Useful when the Mobile.Server runs from a different machine that doesn't have the Vault file populated.

## Rollback

```powershell
git restore D:\Projects\MindAttic\MindAttic.Mobile\MindAttic.Mobile.Server\Program.cs
git restore D:\Projects\MindAttic\MindAttic.Mobile\MindAttic.Mobile.Server\MindAttic.Mobile.Server.csproj
git restore D:\Projects\MindAttic\console-launcher.ps1
git restore D:\Projects\MindAttic\settings.json
Remove-Item "$env:APPDATA\MindAttic\Vault\mobile.json"
```

No data migration; the previous token value lives in `settings.json` git history if it must be recovered.

## Verification checklist

- [ ] `dotnet build MindAttic.Mobile.slnx` is green.
- [ ] `dotnet test MindAttic.Mobile.slnx` is green (17/17 today).
- [ ] `/healthz` returns 200 with the Vault-sourced token, 401 without.
- [ ] Opening an agent tab via `MindAttic.ps1 → Open Project Tab` while `Mobile.Enabled = true` still routes through `MindAttic.Mobile.AgentHost.exe`.
- [ ] `git diff settings.json` shows the `Mobile.Token` line removed.
