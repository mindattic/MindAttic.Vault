# Integration Plan — MindAttic.Deploy

**Status:** implemented. Unlike the other consumers in this folder, MindAttic.Deploy's
actual FTP logic lives in **Node.js** (`src/deploy.js`), not C# — the `MindAttic.Deploy.Cli`
project (`net10.0`, Spectre.Console) is a thin process wrapper that shells out to
`node src/deploy.js` and had zero credential-handling code of its own before this plan.

**Why this is a bridge, not a rewrite:** `deploy.js` already supports a `MINDATTIC_FTP_JSON`
environment variable as a first-class credential source (used today by CI, which sets it
from a GitHub Actions secret) — checked *before* falling back to `secrets/ftp.json` on
disk. That existing seam is exactly what a C#→Node bridge needs: `MindAttic.Deploy.Cli`
resolves credentials from Vault and, when found, sets that same env var on the child
`node` process. `deploy.js` itself needed **zero changes**.

## Files

| File | Action |
| --- | --- |
| `NuGet.config` (repo root) | Create with `local-packages` (`./lib/local-packages`) + `nuget.org`, matching Prose/IdiotProof/ThinkTank/etc. |
| `lib/local-packages/MindAttic.Vault.2.0.0.nupkg` | Vendor the packed nupkg so GitHub-hosted CI (no `C:\LocalNuGet`) can restore. |
| `MindAttic.Deploy.Cli/MindAttic.Deploy.Cli.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="2.0.0" />`. |
| `MindAttic.Deploy.Cli/Services/DeployRunner.cs` | In `RunNode`, resolve `FtpCredentialStore.Default.TryGetJson()`; when non-null, set it on `psi.EnvironmentVariables["MINDATTIC_FTP_JSON"]` before starting `node`. When null (Vault has no `Ftp\ftp.json` yet, e.g. CI runners with no `%APPDATA%\MindAttic`), the child process env is left untouched — CI's own `MINDATTIC_FTP_JSON` (set directly from the GitHub Actions secret) and a developer's `secrets/ftp.json` fallback both keep working exactly as before. |
| `secrets/ftp.json.template`, `README.md`, `CLAUDE.md` | Document the new resolution order: Vault (`%APPDATA%\MindAttic\Ftp\ftp.json`) → `MINDATTIC_FTP_JSON` env (still the CI path) → `secrets/ftp.json` on disk (still the final local fallback — nothing removed). |

## Resolution order (after this change)

1. **MindAttic.Vault** — `%APPDATA%\MindAttic\Ftp\ftp.json` via `FtpCredentialStore`. Bridged into the child `node` process as `MINDATTIC_FTP_JSON` by `DeployRunner`.
2. **`MINDATTIC_FTP_JSON` env var already set on the process** — untouched when Vault has nothing (this is how CI provides its secret today, and keeps working unmodified).
3. **`secrets/ftp.json`** on disk (repo-local, gitignored) — `deploy.js`'s existing final fallback, unchanged.

## Why no `IConfiguration`/cloud-native wiring

`Ftp` is intentionally **not** one of `MindAtticConfigurationSource`'s scanned buckets (see
Vault's README). This credential only ever needs to exist on a developer's laptop or as a
CI env var — there's no Azure App Service/Key Vault deployment of MindAttic.Deploy itself
that would need it surfaced through `IConfiguration`. `FtpCredentialStore` is a plain
file-only store, matching `TokenStore`'s posture rather than `LlmCredentialResolver`'s.

## Side benefit — MindAttic.Bob

MindAttic.Bob's `bob.ps1` already zips the entire `%APPDATA%\MindAttic` tree for its
pack/bug-out backup. Once real credentials live at `%APPDATA%\MindAttic\Ftp\ftp.json`,
they're swept into that backup automatically — no MindAttic.Bob code needed for the backup
side. `bob.ps1`'s own `Get-FtpConfig` (used when *bob itself* needs to FTP-upload a pack)
was updated to add this path as its first candidate, ahead of `secrets\ftp.json`.

## Rollback

`git restore NuGet.config MindAttic.Deploy.Cli/ lib/local-packages/` (or `rm` the new
files). `secrets/ftp.json` and the `MINDATTIC_FTP_JSON` CI secret are untouched by this
change, so deploys keep working immediately with no data loss.
