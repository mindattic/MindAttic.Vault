---
codex: 1
project: MindAttic.Vault
code: VLT
layer: rfc
status: planned
updated: 2026-06-07
---

# RFC 0001 — LLM Health Dashboard

## Problem
Vault knows every LLM key the family holds, but nothing tells an operator whether those keys are
still *good*: a key can be revoked, hit a quota, or point at a deprecated model. There is no
single place to see per-provider health, no alert when a provider goes down, and no automatic
repair of the common "deprecated model id" failure.

## Options compared
1. **CLI one-shot probe** — simple, but no continuous monitoring, no UI, no alerting.
2. **Fold monitoring into the Vault library** — violates [VLT-LAW-5](../BIBLE.md#VLT-LAW-5) (would
   drag Azure/HTTP/Legion deps into the core package) and [VLT-§3](../BIBLE.md#VLT-§3) (Vault is not
   a UI). Rejected.
3. **Separate Blazor app (`MindAttic.Vault.Dashboard`)** that references the local Vault project
   for credential resolution and `MindAttic.Legion` for probing/diagnosis/model discovery, with a
   scheduled background sweep, traffic-light UI, and pluggable alert channels. **Chosen.**

## Decision
Ship a standalone `MindAttic.Vault.Dashboard` (`net10.0`, `Sdk.Web`) that:
- resolves keys via the Vault resolvers (no new credential code);
- probes each keyed provider through Legion 3.0.0 (`LlmHealthMonitor`), classifying into
  Healthy/Degraded/Down with an `LlmHealthDiagnosis`;
- runs a `MonitorBackgroundService` on a configurable interval (default hourly);
- gates an overall verdict on a *trusted panel* (`claude`, `openai`, `gemini`, `deepseek`);
- dispatches alerts on state change via `AlertDispatcher` (email/webhook) and optionally
  self-heals deprecated-model pointers via `SelfHealer`.

## What NOT to do
- Do **not** add the Dashboard or its Azure/Legion dependencies to the published `MindAttic.Vault`
  package ([VLT-LAW-5](../BIBLE.md#VLT-LAW-5)).
- Do **not** let the dashboard write secrets in production beyond the file fallback
  ([VLT-LAW-4](../BIBLE.md#VLT-LAW-4)).
- Do **not** mark D-epic stories `✅` on the strength of the live-network test
  `TrustedPanel_EveryKeyAuthenticatesLive` — it is skipped in CI and proves nothing offline.

## Phased plan (with risk)
1. **Project skeleton** (done in working tree, `feat/llm-health-dashboard`): Blazor app, services
   (`LlmHealthMonitor`, `HealthMonitorStore`, `MonitorBackgroundService`, `SelfHealer`,
   `AlertDispatcher`), Home page. *Risk: not in the solution → no CI coverage.*
2. **Add to solution + test project** — wire into `MindAttic.Vault.slnx`; add a
   `MindAttic.Vault.Dashboard.Tests` exercising the monitor/diagnosis/self-heal logic with a faked
   Legion probe. *Risk: live-network temptation — keep all CI tests offline/faked.*
3. **Promote stories** — turn [VLT-US-D1..D3](../USER_STORIES.md#epic-d-llm-health-dashboard-frontier)
   to ✅ as each gains a named offline test; record the result in [VLT-§6](../BIBLE.md#VLT-§6).

## Graduates into
- BIBLE: [VLT-§4.1](../BIBLE.md#VLT-§4) (projects), [VLT-§7](../BIBLE.md#VLT-§7) (frontier → verified).
- Stories: [Epic D](../USER_STORIES.md#epic-d-llm-health-dashboard-frontier).
