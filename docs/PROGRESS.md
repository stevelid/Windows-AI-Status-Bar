# Progress

The implementation agent updates this file in the same commit as the work. Tick a box only when the
commit's tests pass and it is pushed. Record deviations from `PLAN.md` under **Decisions and deviations**.

Legend: 🧑 needs Steve on Windows · 🧪 spike · ⚠️ depends on undocumented behaviour

## Setup (done)

- [x] Import AIUsageWidget v2.1.1 with history; repoint updater to this fork
- [x] `WindowsAIStatusBar.slnx`, `StatusBar.Core` + tests, `AgentTask` model
- [x] CI: Linux core job; Windows build, smoke tests, single-file artifact
- [x] `tools/recon/Collect-Recon.ps1`; cloud SessionStart hook

## Phase 0 — Reconnaissance

- [ ] P0.1 🧑 Unchanged widget shows Codex and Claude quotas
- [ ] P0.2 🧑 Static recon report committed or shared
- [ ] P0.3 🧑 Live timeline with scenario notes
- [ ] P0.4 FINDINGS.md, assumption statuses, recon-derived fixtures

## Phase 1 — Reshape the widget

- [ ] P1.1 Product identity and data folder
- [ ] P1.2 Usage model in Core
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
- [ ] P4.3 🧪 Spike S5 notification listener (only if needed) — decision: _pending_
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

- P0.1–P0.3: see `docs/recon/README.md`.

## Decisions and deviations

_(Date — commit ID — what changed from PLAN.md and why.)_
