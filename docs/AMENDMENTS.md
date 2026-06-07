---
codex: 1
project: MindAttic.Vault
code: VLT
layer: amendments
status: living
updated: 2026-06-07
---

# MindAttic.Vault — Amendments (append-only; amendment wins over the bible)
> Never rewrite an amendment; supersede it with a new one. Beyond ~25, fold into the BIBLE and
> start a new epoch (note the git tag). History stays in git.

## VLT-A1 — User Secrets retired; APPDATA is the single local source of truth (supersedes —)
**What changed.** The local credential model moved from a User-Secrets-first chain to an
APPDATA-first single source of truth. `AddUserSecrets(...)` and `<UserSecretsId>` are removed from
all MindAttic projects; the writable `%APPDATA%\MindAttic\<Bucket>\` store is now the one local
home, surfaced through `IConfiguration` by `AddMindAtticVaultFiles()`.
**Why.** User Secrets ranked *above* the writable APPDATA store, so a stale `dotnet user-secrets`
value could silently mask a freshly-rotated key on disk — exactly the drift this convention
eliminates.
**Migration.** Move any User-Secrets values into the matching APPDATA bucket file
(folder == `MindAttic:Vault:<Bucket>`). Production is unchanged (env vars / App Service
Application Settings / Key Vault). Codified as [VLT-LAW-3](BIBLE.md#VLT-LAW-3).

## VLT-A2 — Whole-number versioning; csproj is authoritative over README prose (supersedes —)
**What changed.** The package version is governed by whole-number versioning
([HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1)): `MindAttic.Vault.csproj` carries
`<Version>1.0.0</Version>`. The README's narrative "0.3.0" status block predates this and is
**stale**; where the two disagree, the csproj wins.
**Why.** Align Vault with the org-wide major-only versioning rule and give one authoritative
version source.
**Migration.** None to code. Documentation reconciliation is tracked as
[VLT-US-X1](USER_STORIES.md#priority-backlog); the README prose should be updated to `1.0.0` when
that backlog item is taken.
