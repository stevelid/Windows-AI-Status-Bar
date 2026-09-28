# Progress

The implementation agent updates this file in the same commit as the work. Tick a box only when the
commit's tests pass and it is pushed. Record deviations from `PLAN.md` under **Decisions and deviations**.

Legend: 🧑 needs Steve on Windows · 🧪 spike · ⚠️ depends on undocumented behaviour

## Setup (done)

- [x] Import AIUsageWidget v2.1.1 with history; repoint updater to this fork
- [x] `WindowsAIStatusBar.slnx`, `StatusBar.Core` + tests, `AgentTask` model
- [x] CI: Linux core job; Windows build, smoke tests, single-file artifact
- [x] `tools/recon/Collect-Recon.ps1`; cloud SessionStart hook
- [x] Codex loop script (`tools/agent/Run-CodexLoop.ps1`); `Microsoft.Extensions.TimeProvider.Testing` added to Core tests so sandboxed agents need no downloads

## Phase 0 — Reconnaissance

- [ ] P0.1 🧑 Unchanged widget shows Codex and Claude quotas
- [x] P0.2 🧑 Static recon report committed (`docs/recon/ai-status-recon-20260928-095731/`)
- [ ] P0.3 🧑 Live timeline with scenario notes — partial: Codex turns captured; Cowork and question scenarios still needed
- [ ] P0.4 FINDINGS.md, assumption statuses, recon-derived fixtures — FINDINGS.md and plan rules updated; fixtures and A-C3/A-X4 wait on the P0.3 re-run

## Phase 1 — Reshape the widget

- [x] P1.1 Product identity and data folder
- [x] P1.2 Usage model in Core
- [ ] P1.3 UsageMonitor
- [ ] P1.4 Usage adapters and App wiring
- [ ] P1.5 Task state service and demo provider
- [ ] P1.6 Docking geometry and compact strip
- [ ] P1.7 Details pane
- [ ] P1.8 Tray, context menu and settings
- [ ] Phase 1 acceptance 🧑

## Phase 2 — Codex tasks

- [ ] P2.1 Incremental JSONL reading
- [ ] P2.2 Codex rollout parser ⚠️
- [ ] P2.3 Codex titles
- [ ] P2.4 CodexTaskProvider
- [ ] P2.5 Wiring and diagnostics
- [ ] Phase 2 acceptance 🧑

## Phase 3 — Claude Cowork discovery

- [ ] P3.1 Roots and task store ⚠️
- [ ] P3.2 Audit parser ⚠️
- [ ] P3.3 ClaudeCoworkTaskProvider
- [ ] Phase 3 acceptance 🧑

## Phase 4 — Claude attention (requires P0.4)

- [ ] P4.1 ClaudeStateResolver and clearing rules ⚠️
- [ ] P4.2 Dismiss control
- [x] P4.3 🧪 Spike S5 notification listener — decision: not needed for v1 (audit `permission_request` records confirmed)
- [ ] P4.4 ClaudeNotificationMonitor (only if S5 is go)
- [ ] P4.5 🧪 Desktop log spike (only if needed)
- [ ] P4.6 KNOWN_LIMITATIONS.md
- [ ] Phase 4 acceptance 🧑

## Phase 5 — Interaction polish

- [ ] P5.1 Click task to focus app
- [ ] P5.2 Attention notifications
- [ ] P5.3 Visual polish
- [ ] P5.4 Multi-monitor and DPI
- [ ] P5.5 Diagnostic bundle
- [ ] Phase 5 acceptance 🧑

## Phase 6 — Hardening

- [ ] P6.1 Collector supervision
- [ ] P6.2 System events
- [ ] P6.3 🧑 Manual test matrix
- [ ] P6.4 Release 3.0.0

## Waiting on Steve

_(The agent lists here, in plain steps, anything that needs Steve's Windows machine.)_

- P0.1: confirm whether the upstream widget showed both Codex and Claude percentages.
- P0.3: a short (about 10 minute) watch re-run covering Cowork scenarios K1–K4 and Codex C4. Steps are at the end of `docs/recon/FINDINGS.md`.

## Decisions and deviations

_(Date — commit ID — what changed from PLAN.md and why.)_

- 2026-09-28 — P0.4 (partial) — Recon confirmed Cowork `system/permission_request`/`permission_response` records, so Claude attention is Confirmed from `audit.jsonl` and the notification listener is dropped (D4, D5, §5.2). Codex runs with `approval_policy: never` and records shell calls as `custom_tool_call` `exec`, so Codex attention is limited to input requests (D3, §5.1). Codex is its own MSIX app; activation by AUMID (P5.1). Cowork metadata is ~200 KB and its write time is unreliable; recency comes from `lastActivityAt` and the audit log (P3.1).
