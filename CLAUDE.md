# MindAttic.Vault Project Rules

## Conversation
- A bare "do" / "do it" / "yes" from the user means "continue", "keep going", "proceed". Resume the current task without asking for clarification.

## Credentials (single source of truth)
- **The APPDATA Vault store is the one local home for every MindAttic credential.** Each bucket lives at `%APPDATA%\MindAttic\<Bucket>\` and the folder name **equals** its config section `MindAttic:Vault:<Bucket>` (e.g. `LLM`, `Brokers`, `Tokens`, `Subtitles`, `Notifications`, `AudioStore`). Keyrings/structured creds use `providers.json`; the flat token bag uses `tokens.json`. Each file is a faithful image of its config subtree.
- **User Secrets is retired — do not reintroduce it.** Never add `AddUserSecrets(...)` to a `Program.cs` or `<UserSecretsId>` to a `.csproj`. User Secrets ranked *above* the writable APPDATA store, so a stale CLI value could silently mask a freshly-rotated key — that drift is exactly what this convention eliminates.
- **Production** resolves the same `MindAttic:Vault:*` keys from environment variables / App Service Application Settings / Azure Key Vault — unchanged. Only local dev changed.
- When adding a new credential to any project, put it in the matching APPDATA bucket (create the bucket folder if needed, name == `MindAttic:Vault:<Bucket>`) so it surfaces through `AddMindAtticVaultFiles()` automatically; never split a credential across two stores.
