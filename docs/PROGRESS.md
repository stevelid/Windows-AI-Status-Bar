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
- [x] Phase 1 acceptance 🧑 (Steve, 2026-09-28, build `+c3e03d0`: strip, pane open/close, demo tasks, tray, settings, docking, idle CPU, Claude start-up)

## Phase 2 — Codex tasks

- [x] P2.1 Incremental JSONL reading
- [x] P2.2 Codex rollout parser ⚠️
- [x] P2.3 Codex titles
- [x] P2.4 CodexTaskProvider
- [x] P2.5 Wiring and diagnostics
- [x] Phase 2 acceptance 🧑 (Steve, 2026-09-28, build `+a213f57`: two concurrent tasks, question ⚠, completion, restart recovery, diagnostics)

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

- Phase 2 acceptance: download the latest `status-bar-win-x64` artifact from this branch's Windows workflow and run `AIStatusBar.exe` with Demo tasks off. Start two Codex tasks and confirm both appear as Working. Trigger a task that asks for input, confirm it shows ⚠ within about 5 seconds, then answer and confirm it returns to Working. Complete a task and confirm it moves to Recently completed and disappears after the configured retention time. Quit and relaunch while another task is active and confirm its state recovers. Use Copy diagnostics and check it reports task health, sessions tracked, files watched, last event age, parse errors, drift signatures, and watcher overflows without titles, session IDs, or paths.

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
- 2026-09-28 — P2.1 review note, resolved in P2.4 — `TailReader.ReadWithOffset` reports the byte position after its last complete line, and `IncrementalJsonlReader.StartAt(long offset)` resumes with the current file id. Session recovery now continues from the tail offset; tests cover an unfinished line and verify a large file's prefix is not reread.
- 2026-09-28 — P2.2 — Added the Codex rollout state parser, task mapper, question-ending heuristic and format-drift counters. Codex fixtures remain provisional pending P0.4 and use real field names with short fabricated placeholder values; no fixture contains real transcript or tool data.
- 2026-09-28 — P2.2 review — Fixtures for the question and approval cases were identical to other fixtures and only worked because the test rewrote lines at read time. The fixtures now carry the real fields (`last_agent_message`, JSON-string `arguments`) with placeholder values, the rewriting is removed, and a test covers string and object `arguments`. The fixture rule in CLAUDE.md/AGENTS.md is clarified: real data is forbidden, placeholder values in real field names are required.
- 2026-09-28 — Phase 1 re-test (Steve) — Pane still did not open, but Steve's log had no `Usage …:` lines, so the build he ran predates the fix (several downloaded copies). Builds now log and report their commit (`AppBuild.Label`, e.g. `3.0.0-alpha.1+4f23747`) in `log.txt` and Copy diagnostics. The update check treats 404 (no releases yet) as "no update" instead of logging a stack trace on every start.
- 2026-09-28 — P2.3 — Added incremental session-index title resolution and a shared title sanitizer. Index names take precedence; user-message candidates are reduced to a short in-memory title, and injected context is skipped.
- 2026-09-28 — Phase 1 accepted by Steve on build `c3e03d0` after the pane and start-up fixes.
- 2026-09-28 — P2.4 — Added Codex path resolution, dated/recent rollout discovery, head-and-tail recovery with incremental offsets, a debounced JSONL watcher with overflow recovery, and periodic provider snapshots with independent health. Provider tests cover two-date discovery, appends, restart recovery, disabled-watcher reconciliation, missing-directory recovery, compressed-file exclusion and sub-agent attention folding. No private data is persisted or logged.
- 2026-09-28 — P2.4 CI fix — `core-linux` failed (Windows passed): `IncrementalJsonlReader` used `File.GetCreationTimeUtc` as the file identity, but on Unix .NET reports a time that changes on every write, so each append looked like a replaced file and reading restarted at 0. The default identity now uses creation time on Windows only; elsewhere only truncation is detected. Tests that need replacement detection inject `fileIdProvider`.
- 2026-09-28 — P2.5 — Registered the Codex task provider outside demo mode, using the settings override, `CODEX_HOME`, or the user-profile default. Copy diagnostics now include collector health and content-free counts for tracked sessions, watched files, event age, parse errors, sanitized drift signatures, and watcher overflow; overflow also writes a content-free log entry. Phase 2 now needs Steve's Windows acceptance run above.
- 2026-09-28 — Phase 2 accepted by Steve. His diagnostics showed 749 format-drift records, all ordinary Codex records (`response_item/reasoning` 649, agent/user messages, compaction, sub-agent metadata, tool search). The parser now treats Codex's persisted record types as activity, so the drift counter again flags only genuinely new formats.
- 2026-09-28 — D15 revision (Steve's Phase 2 test) — Two Codex questions were pending but only one showed ⚠. `QuestionDetector` only checked the last line, so questions followed by an option list or a closing sentence were missed; it now checks the final paragraph after skipping trailing option lines. Also, a Codex session whose `session_meta` was not read got the id `codex:unknown`, so two such sessions would merge into one task; the id now falls back to the rollout file's uuid.
