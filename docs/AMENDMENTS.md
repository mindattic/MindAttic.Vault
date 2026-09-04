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

## VLT-A3 — Root resolution is cross-platform and never throws (supersedes —)
**What changed (2026-09-04).** `VaultPaths` resolves the roaming and local roots through an ordered
chain instead of a single `Environment.GetFolderPath` call that threw when the host had no answer:

1. `MINDATTIC_VAULT_ROAMING_ROOT` / `MINDATTIC_VAULT_LOCAL_ROOT`, used verbatim.
2. The matching `Environment.SpecialFolder` — the normal answer on Windows, macOS, iOS and Android.
3. The platform convention read from the environment: `%APPDATA%`/`%LOCALAPPDATA%` on Windows,
   `~/Library/Application Support` on Apple, `$XDG_CONFIG_HOME`/`$XDG_DATA_HOME` (else `~/.config`
   and `~/.local/share`) on Linux and Android.
4. `$HOME`/`%USERPROFILE%` → `.mindattic/{config,data}`.
5. `{AppContext.BaseDirectory}/.mindattic/{config,data}`.

`ResolveRoaming()` / `ResolveLocal()` return the path **and** the `VaultRootSource` that produced it,
and `Describe()` prints both for startup diagnostics.

**Why.** `Environment.GetFolderPath` returns an empty string — it does not throw — on hosts with no
user profile, and Vault turned that into an `InvalidOperationException`. Because Vault is wired into
the `IConfiguration` chain, that throw happened during **host construction**: on a Linux App Service
worker the process aborted with SIGABRT before a single line of application code ran, and the stack
trace pointed at `ConfigurationBuilder`, not at a missing folder. MindAttic.Ideas hit exactly this on
its first Azure deployment. Requiring every Linux/container consumer to set an env var made the
library the odd one out; resolving properly on every OS is the library's job.

**Compatibility.** Steps 3–5 only run where step 2 previously **threw**, so no host that already
worked resolves anywhere new — Windows still lands on `%APPDATA%\MindAttic`. The public surface is
additive (`VaultRootSource`, `VaultRootResolution`, `ResolveRoaming`, `ResolveLocal`, `Describe`);
the documented `InvalidOperationException` is simply no longer thrown.

**Migration.** None. Consumers that added `MINDATTIC_VAULT_ROAMING_ROOT` purely to get past the
crash can drop it; as an explicit override it still wins where it is genuinely wanted. Package goes
to **3.0.0** ([HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1)).
