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

- [x] P0.1 🧑 Unchanged widget shows Codex and Claude quotas (confirmed by Steve, 2026-09-28)
- [x] P0.2 🧑 Static recon report committed (`docs/recon/ai-status-recon-20260928-095731/`)
- [ ] P0.3 🧑 Live timeline with scenario notes — partial: Codex turns and desktop Claude Code session writes observed; detailed record fields and question/interruption scenarios remain
- [ ] P0.4 FINDINGS.md, assumption statuses, recon-derived fixtures — FINDINGS.md and plan rules updated; fixtures and A-C3/A-X4 wait on the P0.3 re-run

## Phase 1 — Reshape the widget

- [x] P1.1 Product identity and data folder
- [x] P1.2 Usage model in Core
- [x] P1.3 UsageMonitor
- [x] P1.4 Usage adapters and App wiring
- [x] P1.5 Task state service and demo provider
- [x] P1.6 Docking geometry and compact strip
- [x] P1.7 Details pane
- [x] P1.8 Tray, context menu and settings
- [ ] Phase 1 acceptance 🧑

## Phase 2 — Codex tasks

- [x] P2.1 Incremental JSONL reading
- [ ] P2.2 Codex rollout parser ⚠️
- [ ] P2.3 Codex titles
- [ ] P2.4 CodexTaskProvider
- [ ] P2.5 Wiring and diagnostics
- [ ] Phase 2 acceptance 🧑

## Phase 3 — Claude Code tasks (D16)

- [ ] P3.1 Claude Code transcript parser ⚠️
- [ ] P3.2 Hook event sink (`--claude-hook`)
- [ ] P3.3 Opt-in hook installer
- [ ] P3.4 ClaudeCodeTaskProvider
- [ ] P3.5 Dismiss control
- [ ] P3.6 KNOWN_LIMITATIONS.md
- [ ] Phase 3 acceptance 🧑

## Phase 4 — Claude Cowork (optional; not wanted yet)

- [ ] P4.1 Roots and task store ⚠️
- [ ] P4.2 Audit parser ⚠️
- [ ] P4.3 ClaudeCoworkTaskProvider
- [ ] P4.4 ClaudeStateResolver and clearing rules ⚠️
- [x] P4.5 🧪 Spike S5 notification listener — decision: not needed (see D5)
- [ ] P4.6 ClaudeNotificationMonitor (only if S5 is go)
- [ ] P4.7 🧪 Desktop log spike (only if needed)
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

- Phase 1 acceptance: download the latest `status-bar-win-x64` artifact from the branch's Windows workflow and launch `AIStatusBar.exe --demo`. Confirm the strip and tray tooltip show both providers' session allowances; click the strip to open the pane, check demo tasks move between sections and counts update, then click the strip again to confirm the pane stays closed. Use the tray menu to hide/show the strip, expand/collapse the pane, refresh, open Settings, inspect Sign in, Copy diagnostics, Check for updates and Quit. In Settings, change and restart-check language, theme, refresh interval, opacity, Codex/Cowork paths, notifications, recently-completed retention, pane timeout, attention label, monitor, quota thresholds and Demo tasks. Toggle Start with Windows on and off, then leave the app idle for a minute in Task Manager and check CPU use is approximately 0%.

## Decisions and deviations

_(Date — commit ID — what changed from PLAN.md and why.)_

- 2026-09-28 — P0.4 (partial) — Recon confirmed Cowork `system/permission_request`/`permission_response` records, so Claude attention is Confirmed from `audit.jsonl` and the notification listener is dropped (D4, D5, §5.2). Codex runs with `approval_policy: never` and records shell calls as `custom_tool_call` `exec`, so Codex attention is limited to input requests (D3, §5.1). Codex is its own MSIX app; activation by AUMID (P5.1). Cowork metadata is ~200 KB and its write time is unreliable; recency comes from `lastActivityAt` and the audit log (P3.1).
- 2026-09-28 — P1.4 — Propagated the monitor cancellation token through Claude usage and token HTTP requests so disposal can stop in-flight fetches before shutting down; the app and adapters otherwise follow the planned provider split.
- 2026-09-28 — P1.5 — Added `--demo` and `Settings.DemoTasks` startup wiring; task state remains in Core until the pane is introduced in P1.7. Dismissal is currently in memory by task ID; evidence-key persistence remains in P4.2.
- 2026-09-28 — P1.6 — Added a Core-only geometry model, a PerMonitorV2 manifest and WPF dock controller, then replaced the floating card with the compact quota/task strip. The app manifest uses the modern `dpiAwareness` element; omitting the legacy `dpiAware` element avoids the .NET 10 WinForms DPI analyzer warning while keeping the WPF host PerMonitorV2-aware.
- 2026-09-28 — P1.7 — Added the details pane with all quota windows and in-memory task rows grouped by state; rows update in place, countdowns refresh only while visible, and `PaneAutoCollapseSeconds` defaults to 0.
- 2026-09-28 — P1.7 (review fix) — Clicking the strip to close the pane would reopen it: the strip click deactivates (closes) the pane on mouse-down, then the toggle fires on mouse-up. `App.ToggleDetailsPane` now ignores a toggle within 400 ms of the pane closing. Needs Steve's check on Windows: with the pane open, click the strip once; the pane should close and stay closed.
- 2026-09-28 — D7 revised (Steve) — The strip and tray show each provider's session (5-hour) window instead of the tightest window. `UsageWindow` gained an optional `Length`; the Claude and Codex adapters set it, and `UsageSummary.Compact` picks the shortest window. The weekly figures remain in the details pane.
- 2026-09-28 — P1.8 — Extracted tray ownership into `TrayController`, added the planned settings controls and monitor selection, and removed runtime use of legacy provider/window-placement settings while retaining those JSON properties as obsolete. `NotificationsEnabled` is saved for P5.2 alert delivery; the Codex and Cowork root overrides are saved for their planned collectors.
- 2026-09-28 — D15 (Steve) — A completed turn whose final message ends with a question shows as "needs you" (Inferred). Implemented in P2.2 (Codex, plus `QuestionDetector`) and P3.2/P4.1 (Claude). Recon also showed Codex `task_complete` can carry an `error` object, so Codex `Failed` is now a confirmed state.
- 2026-09-28 — D16 (Steve) — Steve works mainly in Claude Code and Codex, not Cowork. Phase 3 is now Claude Code tasks (transcripts plus opt-in documented hooks); the Cowork design moves to an optional Phase 4 (IDs renumbered P4.1–P4.7). Dismiss control and KNOWN_LIMITATIONS.md moved to P3.5/P3.6.
- 2026-09-28 — P0.3 / S9 — A five-minute Claude Code desktop watch observed a project JSONL stream and session metadata update during Steve's Code-tab task. A-K1 is confirmed for the desktop Code tab with its default Claude home; nested record fields, terminal sessions and user-hook behavior remain unverified. Committed only the content-free, path-redacted findings in `docs/recon/claude-code-writes-20260928.md`.
- 2026-09-28 — Phase 1 acceptance (Steve) — Items 1–3 and 8–12 passed. **Item 4 failed: the pane never opened**, caused by the P1.7 review fix (a `long.MinValue` sentinel overflowed `TickCount64 - closedAt`, so every open was treated as "just closed"). Fixed with a nullable timestamp. Claude needed a manual refresh on first start: `UsageMonitor` now retries a never-loaded provider twice after 15 s before normal back-off, records the exception type in `UsageSnapshot.ErrorType`, and `App` logs provider status transitions to `log.txt`. Items 4–7 need re-testing.
- 2026-09-28 — P2.1 — Added byte-oriented incremental JSONL reading with newline-delimited UTF-8 decoding, buffered partial lines, replacement/truncation reset detection, and a bounded tail reader. The reader consumes through EOF and retains any unterminated bytes in memory so later appends complete the same line; no transcript content is logged or persisted.
