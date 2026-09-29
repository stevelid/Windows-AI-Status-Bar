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

- [x] P3.1 Claude Code transcript parser ⚠️
- [x] P3.2 Hook event sink (`--claude-hook`)
- [x] P3.3 Opt-in hook installer
- [x] P3.4 ClaudeCodeTaskProvider
- [x] P3.5 Dismiss control
- [x] P3.6 KNOWN_LIMITATIONS.md
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

- [x] P5.1 Click task to focus app
- [x] P5.2 Attention notifications
- [x] P5.3 Visual polish
- [x] P5.4 Multi-monitor and DPI
- [x] P5.5 Diagnostic bundle
- [ ] Phase 5 acceptance 🧑

## Phase 6 — Hardening

- [x] P6.1 Collector supervision
- [x] P6.2 System events
- [ ] P6.3 🧑 Manual test matrix — 12 of 14 pass (several simulated), Cowork N/A, network-off left for Steve; see `docs/TEST_MATRIX.md`
- [ ] P6.4 Release 3.0.0 — prepared (version, README, tag-triggered release workflow); tag `v3.0.0` after P6.3 passes

## Waiting on Steve

1. Download the latest Windows build artifact from draft PR #5 and record the build label shown by Copy diagnostics.
2. Run a task in both Claude Code’s desktop Code tab and a terminal. Check that each appears with a useful title, updates while it runs, and moves to Recently completed when its turn ends.
3. Have Claude finish a turn with a question and check for the inferred “Asked you a question” alert.
4. In Settings, enable Claude Code hooks. Trigger a permission prompt, check for “Permission requested,” answer it, and check that the alert clears. Disable hooks and confirm existing Claude settings hooks remain intact.
5. Hover an inferred alert and dismiss it. Restart the app and check that the same evidence stays hidden; create a new question and check that it appears. Confirmed hook alerts should not offer Dismiss. If practical, leave a session without new records until it becomes Unknown and check dismissal there too.
6. Report pass/fail for each step and the build label. For failures, include which environment (desktop or terminal) failed and whether hooks were enabled.
7. Click a Codex task row and a Claude Code task row, including one terminal-launched Claude task, and confirm the owning desktop app is focused. Record the expected Claude terminal limitation if it applies.
8. Trigger a new attention event for each provider. Confirm one tray notification per new evidence key, that clicking the balloon opens the details pane, and that restarting does not repeat the same notification. Create new evidence and confirm it notifies once.
9. Choose a secondary monitor in Settings, confirm the strip moves there, then disconnect and reconnect that monitor to check primary fallback and restoration. Repeat at the available Windows DPI scales.
10. Use the tray's “Save diagnostic bundle…” command. Confirm the ZIP contains `report.txt`, `log-tail.txt`, and `format-drift.txt`; check that the report contains the build/version, provider health, task counts and parser signatures without task titles, prompts, IDs, session references, tokens or full paths.
11. Report pass/fail for the Phase 5 checks and the build label. For failures, include the provider, launch environment, monitor/DPI setting, or bundle entry involved.
12. Phase 6: install the build from the Phase 6 PR artifact (the Startup shortcut currently points at `Downloads\status-bar-win-x64-bb23e91`; the new build repairs it on first start while "Start with Windows" is ticked). Check the log shows `Tasks Claude: … -> Ok/Ok` shortly after start.
13. Sleep the laptop for a few minutes and wake it: the log should show `System resumed…`, tasks should update without a restart, and usage should refresh within about 10 s. Turn Wi-Fi off for one refresh interval: usage greys out as stale, with no pop-up; turn it back on and it refreshes within a few seconds (`Network available…`).
14. P6.3 was run by Claude on 2026-09-29 (`docs/TEST_MATRIX.md`). Still to do by hand: a real sleep/wake, and Wi-Fi off/on (step 13). Also check that a Claude Code task waiting on background agents shows ● with "Waiting for background agents" rather than ✓ (D18). Then push tag `v3.0.0` to publish the release.

## Decisions and deviations

_(Date — commit ID — what changed from PLAN.md and why.)_

- 2026-09-29 — P6.3 (run by Claude at Steve's request) — 12 of 14 scenarios pass, including simulated resume, taskbar work-area change and monitor disconnect, plus a real DPI change and Explorer restart. Cowork is N/A. Network-off was not run because it would disconnect the session. The run also found the demo provider's age bug (the phase start ignored completed cycles, so demo rows showed hours-old ages), now fixed.
- 2026-09-29 — D18 (Steve) — "The system reads complete when Claude is waiting on subagents." When Claude launches background agents it ends its turn (`end_turn`) and waits, so the parser showed Complete. The parser now tracks background launches (`async_launched` agent ids, `backgroundTaskId` shells) until their `task-notification` arrives. That can be a user record, or a `queue-operation`/`queued_command` attachment when absorbed mid-turn. Subagent transcripts (`<session>/subagents/agent-*.jsonl`) are folded into the parent: a still-running one keeps the parent Working and its writes count as activity. Such turns show `Working/Inferred` with "Waiting for background agents". Launches older than 2 h are ignored, and a `Stop` hook does not override. New assumption A-K6. A replay of six real transcripts (structure only, nothing kept) showed Waiting at each turn that ended with agents out, clearing when they reported. Fixtures added; 196 Core tests pass.
- 2026-09-29 — P6.1 (review fix) — Providers publish on the thread pool, so a snapshot queued by a replaced provider could arrive after a restart and overwrite the new provider's state. Each subscription is now tagged with its provider and stale events are dropped; test added (188 pass).
- 2026-09-29 — P6.2 (review fix) — Two bugs found while running the app on Steve's machine. (1) Start-up race: `ConfigureAgentTaskService` read the task state, then subscribed to changes, so a provider update landing in between (typically the Claude Code provider's first pass) was neither shown nor logged until the next change. Steve's logs since 28 Sep show `Tasks Claude: … -> Starting/NotStarted` with no later transition. The app now subscribes first and reads the state once; the log shows `Tasks Claude: none/- -> Ok/Ok`. (2) Running as `dotnet AIStatusBar.dll` rewrote the Startup shortcut, and would have rewritten the Claude hook command, to point at `dotnet.exe`. `AppPaths.LaunchableExecutable` now returns null under the dotnet host, and auto-start and hook refresh are skipped. Steve's shortcut was restored to his `status-bar-win-x64-bb23e91` build.
- 2026-09-29 — P6.4 (prepared) — Version is `3.0.0`. `release.yml` now runs only on a `v*` tag push or a manual run (previously it created a release whenever the csproj version changed on main): it runs Core tests, publishes the same single-file exe as `build.yml`, and attaches `AIStatusBar-win-x64.zip` and `SHA256SUMS.txt`, the asset names `UpdateService` expects. README rewritten for this product. `docs/TEST_MATRIX.md` holds the P6.3 checklist. Not yet tagged: release after Steve's P6.3 run.
- 2026-09-29 — P5.3 (UI review on Windows) — Ran the demo build on Steve's machine and reviewed screenshots. The pane now stays at least 97% opaque whatever the strip's transparency (text behind it was bleeding through at 10%). Task rows show a compact age beside the provider (`Claude · 4m`) and, for Needs you/Failed rows, the app's fixed reason text as a subtitle. The strip's summary tooltip is suppressed while the pane is open. Settings are grouped into General, Strip and pane, Alerts and Data sources; they have a dark title bar and app icon, and readable slider values and hooks checkbox. The hooks JSON preview is collapsed behind "Show what will be added". The unused Cowork folder field is hidden (Phase 4 not built). The duplicate arrow in "Sign in ▸" is removed. Hook settings are written with relaxed JSON escaping, so the command's quotes no longer appear as `"` in Claude's `settings.json`.
- 2026-09-29 — P6.2 — Added `SystemEventCoordinator` (Core) and a thin `SystemEventsAdapter`. Resume reconciles both task providers at once (watchers can miss changes across sleep) and refreshes usage 10 s later so Wi-Fi can reconnect first; network-available events are coalesced into one usage refresh after 3 s. Network loss needs no extra handling: failed fetches already mark usage Stale with no pop-up. Re-docking on resume, display/DPI change and Explorer restart (`TaskbarCreated`) was already in `DockController` from P1.6. Build clean; 187 Core tests pass.
- 2026-09-29 — P6.1 — Added `SupervisedTaskProvider` (Core). The app builds the Codex and Claude Code providers through factories; when one reports `ReconcileFailed`, or its creation, `Start` or `ReconcileAsync` throws, the supervisor keeps its last tasks visible with `Degraded` health, logs a content-free `Supervisor:` line, and recreates only that provider after 5 s, doubling to 5 min, resetting on the next healthy snapshot. Expected conditions (`WatcherUnavailable`, missing folders) do not trigger restarts. Build clean; 184 Core tests pass.
- 2026-09-28 — P5.4 — Monitor settings now show the device name and resolution for every display, and docking preserves a disconnected monitor selection for automatic fallback and restoration; 152 Core tests pass.
- 2026-09-28 — P5.5 — Added a Core redacted report builder and ZIP writer with fixed `report.txt`, `log-tail.txt`, and `format-drift.txt` entries; task titles, IDs and session references are excluded by construction, and app-owned logs are sanitized before inclusion. Build clean; 154 Core tests pass.
- 2026-09-28 — P5.3 — Added a one-shot 400 ms attention-pill fade that respects Windows animation settings, plus an amber tray-icon attention dot; 152 Core tests pass.
- 2026-09-28 — P5.2 — Added a restart-safe notification gate keyed by hashed task/evidence identifiers, seeded existing attention on startup, and connected gated events to tray balloons that open the details pane. The shared state file preserves dismissal entries; 152 Core tests pass.
- 2026-09-28 — P5.1 — Task rows now focus the owning Codex or Claude desktop app through the resolved MSIX AUMID, with a process-window fallback; Claude terminal sessions focus the desktop app as documented. Build clean with apphost generation disabled for the synced workspace; 169 Core tests pass.

- 2026-09-28 — P0.4 (partial) — Recon confirmed Cowork `system/permission_request`/`permission_response` records, so Claude attention is Confirmed from `audit.jsonl` and the notification listener is dropped (D4, D5, §5.2). Codex runs with `approval_policy: never` and records shell calls as `custom_tool_call` `exec`, so Codex attention is limited to input requests (D3, §5.1). Codex is its own MSIX app; activation by AUMID (P5.1). Cowork metadata is ~200 KB and its write time is unreliable; recency comes from `lastActivityAt` and the audit log (P3.1).
- 2026-09-28 — P1.4 — Propagated the monitor cancellation token through Claude usage and token HTTP requests so disposal can stop in-flight fetches before shutting down; the app and adapters otherwise follow the planned provider split.
- 2026-09-28 — P1.5 — Added `--demo` and `Settings.DemoTasks` startup wiring; task state remains in Core until the pane is introduced in P1.7. Dismissal began in memory by task ID and was replaced with evidence-key persistence in P3.5.
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
- 2026-09-28 — P2.5 — Registered the Codex task provider outside demo mode, using the settings override, `CODEX_HOME`, or the user-profile default. Copy diagnostics now include collector health and content-free counts for tracked sessions, watched files, event age, parse errors, sanitized drift signatures, and watcher overflow; overflow also writes a content-free log entry.
- 2026-09-28 — Phase 2 accepted by Steve. His diagnostics showed 749 format-drift records, all ordinary Codex records (`response_item/reasoning` 649, agent/user messages, compaction, sub-agent metadata, tool search). The parser now treats Codex's persisted record types as activity, so the drift counter again flags only genuinely new formats.
- 2026-09-28 — P3.1 — Added Claude Code path resolution, an in-memory transcript state/parser and task mapper, plus synthetic provisional fixtures. A-K2/A-K3 remain unverified; nested message fields, title field names and the interruption marker are isolated behind assumption comments. Only sanitized short title candidates and pending tool-use IDs are retained.
- 2026-09-28 — P3.2 — Added the `--claude-hook` early-startup sink. It accepts only the four configured hook events, copies only the event/session ID/notification type plus an ingestion timestamp, caps stdin at 1 MB, and rotates the app-owned file to the last 200 lines above 256 KB. All hook errors remain silent with exit code 0.
- 2026-09-28 — P3.3 — Added the opt-in hook setting and command preview, a JSON merger that preserves unrelated settings/hooks, a one-time settings backup and atomic replacement. Enabling hooks repairs partial installs; startup refreshes the command when the executable path changes. Malformed settings are refused without overwriting the file.
- 2026-09-28 — P3.4 — Added recent transcript discovery, tail recovery, optional hook evidence, debounced file watching and periodic reconciliation. Registered Claude Code beside Codex outside demo mode; provider tests cover multiple sessions, hook attention clearing, transcript-only behavior, sidechain filtering and restart recovery. Build clean; 145 Core tests pass.
- 2026-09-28 — P3.5 — Added hover dismissal for inferred attention and unknown rows. Dismissal keys are scoped to the current evidence, expire after 24 hours, and are stored as hashes in `state.json`; unrelated state sections are preserved. Build clean; 149 Core tests pass.
- 2026-09-28 — P3.5 privacy decision — Persist only a SHA-256 digest of `(taskId, evidenceKey)` plus its expiry so task/session IDs and evidence IDs do not appear in `state.json`.
- 2026-09-28 — P3.6 — Added `KNOWN_LIMITATIONS.md` from §9 and the Claude Code recon findings. Phase 3 acceptance remains for Steve’s Windows test; steps are listed above.
- 2026-09-28 — P0.2 refresh — Added the reviewed static recon collection at `docs/recon/ai-status-recon-20260928-171201/`; GUID-shaped connector IDs and temporary filenames were redacted. The separate Claude watcher report is not included.
- 2026-09-28 — D15 revision (Steve's Phase 2 test) — Two Codex questions were pending but only one showed ⚠. `QuestionDetector` only checked the last line, so questions followed by an option list or a closing sentence were missed; it now checks the final paragraph after skipping trailing option lines. Also, a Codex session whose `session_meta` was not read got the id `codex:unknown`, so two such sessions would merge into one task; the id now falls back to the rollout file's uuid.
- 2026-09-28 — Debug logging (Steve) — `log.txt` now records content-free task debugging: task added/changed/removed with short id, status, confidence and the app's fixed reason text (`TaskChangeLog`, Core, tested never to include titles); working/attention counts; task-provider health changes; and Codex session events (tracked with/without `session_meta`, turn/question/pending changes, file resets, read failures, sessions aged out) via `CodexTaskProvider.Trace`. The log now rolls over to `log.old.txt` at 1 MB instead of deleting itself at 512 KB. Phase 3 should add the same `Trace` event to the Claude Code provider.
- 2026-09-28 — D17 (Steve) — The strip had no sign that a task had finished (completions were visible only in the pane). It now shows `✓ N` for recently completed tasks and `✕ N` for failed turns, counted from the task list so they clear after `RecentlyCompletedMinutes`. The log's `Counts:` line includes done and failed. Log short ids now use the last eight characters of the id: Codex thread ids are UUIDv7, whose leading characters are a timestamp, so several sessions started close together shared one short id in the log.
- 2026-09-28 — CI build labels (Steve's test) — Steve's log showed build `c42017f`, which matched no PR commit: pull-request runs build GitHub's temporary merge commit, and the SDK labels the build with that. The Windows job now passes the PR head commit as `SourceRevisionId` and names the artifact `status-bar-win-x64-<commit>`, so the downloaded zip, `log.txt` and diagnostics all show the commit listed on the PR.
- 2026-09-28 — Structured questions (Steve) — The done indicator works, but a structured question did not show ⚠. Two causes. Claude Code: the transcript parser recorded pending tool calls without their name, so a pending `AskUserQuestion` looked like any running tool. It now records the tool kind (from the name only) and maps a pending `AskUserQuestion`/`ExitPlanMode` to "Waiting for your answer"/"Plan needs approval" (new assumption A-K5, awaiting Steve's recon). Codex: the `approval_policy: never` suppression also hid `request_user_input` questions, contrary to D3; it now suppresses approvals only. Tray pop-ups for attention and for finished tasks are recorded under P5.2 at Steve's request.
- 2026-09-28 — Structured questions, second pass (Steve's test of `bb23e91`) — Claude Code worked: ⚠ "Waiting for your answer" appeared within seconds and cleared on answering (A-K5 confirmed). Codex did not: the recon timeline shows the Codex app posts question cards with `request_user_input_async`, whose output is written at once, after which the turn completes with the card still open. The parser now flags such a turn and the mapper shows "Waiting for your answer" until the next turn starts (A-X4 partly confirmed; clearing on answer still to verify). The recon recorder now also watches Claude Code (`~/.claude/projects` transcript signatures and the `status` value of `~/.claude/sessions/*.json`), which it previously skipped.
