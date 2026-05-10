# Integration Plan — GridGame2026

**Goal:** none. Document the decision to skip.

## Why skip

GridGame2026 is a Unity project. Its `SettingsManager` persists volume / resolution / fullscreen preferences via Unity `PlayerPrefs`, which is the right mechanism for a Unity host (registry on Windows, NSUserDefaults on macOS, JSON on Linux).

The project has **zero external API calls, zero credentials, and zero shared MindAttic state.** Wedging a .NET 10 NuGet dependency into a Unity 2022/6000.x project would also fight the engine's assembly compatibility rules.

## What if Unity ever needs MindAttic credentials?

If a future feature (e.g. a leaderboard backend, telemetry, or LLM-driven tutorial coach) needs a credential, the right path is:

1. Stand up a small companion .NET process that talks to the Unity game over a local socket / IPC.
2. The companion process consumes `MindAttic.Vault` normally and reads keys from `IConfiguration` (User Secrets in dev, App Service Application Settings in prod).
3. The Unity build never touches `%APPDATA%\MindAttic\` directly.

This keeps Vault free of the burden of supporting Unity's truncated BCL while still letting Unity benefit from cloud-native credential resolution.

## Status

- No package reference will be added.
- No code changes will be made.
- No Cypress tests apply (Unity uses its own `PlayMode` test framework).
- This plan exists so future contributors don't re-litigate the question.
