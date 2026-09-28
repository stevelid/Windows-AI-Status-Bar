# Product brief — Windows AI Status Bar

This is Steve's original brief, lightly reformatted. Where `docs/PLAN.md` departs from it, the plan says so explicitly in its "Design changes" section. The plan wins where they differ.

## 1. Objective

A very small, always-available Windows utility that gives an ambient overview of Steve's running AI work across the ChatGPT/Codex and Claude desktop applications. It is not a dashboard to manage. It answers three questions without opening either application:

1. How much of my Codex and Claude subscription allowance is left?
2. How many AI tasks are currently running?
3. Does anything require my attention?

The normal interface occupies almost no screen space. Detail appears only when the status bar is clicked.

## 2. Starting point

Fork `Kilin-570/AIUsageWidget` (MIT, C# / .NET 10 / WPF). It already provides: floating widget, compact mode, tray icon, positioning, start with Windows, refresh scheduling, Claude OAuth, DPAPI token storage, ChatGPT/Codex allowance via the local Codex `app-server` (`account/rateLimits/read`, no OpenAI credentials handled by the widget — preserve this), reset countdowns, stale-data behaviour, updater, diagnostics.

## 3. Technology

C#, .NET 10 LTS, WPF, XAML, `System.Text.Json`, `FileSystemWatcher`, Windows APIs/WinRT selectively. No Electron/React/Python/WinUI rewrite. No heavy frameworks; a full MVVM framework is not required. Clean interfaces separating collectors, state and UI, but keep the app small.

## 4. Core UX — collapsed state

A small horizontal strip immediately above the taskbar, about one toolbar row high:

```
GPT 62%  Claude 41%  ● 3  ⚠ 1
```

GPT/Claude = relevant allowance remaining; ● = tasks working; ⚠ = tasks needing Steve. Do not permanently display reset times, weekly breakdowns, token counts, costs, graphs, history, model names or verbose status text.

Position: anchor to the monitor work area immediately above the taskbar (do not draw over the taskbar). Default bottom-right of the primary work area. Other anchors/monitors may come later but must not complicate v1.

## 5. Attention behaviour

Attention matters more than percentages. With attention: `GPT 62% Claude 41% ● 2 ⚠ STEVE 2` or equivalent. Noticeable, not flashing, no continuous animation; a restrained transition when a task first becomes `NeedsAttention` is acceptable. Optionally one Windows notification on the first `Working → NeedsAttention` transition. Never repeat.

## 6. Expanded pane

Clicking the bar expands a small pane upwards:

```
┌─────────────────────────────────────────┐
│ CODEX                                   │
│ 62% remaining           resets 12:40    │
│ CLAUDE                                  │
│ Session 41% remaining   resets 13:10    │
│ Weekly  68% remaining   resets Tuesday  │
├─────────────────────────────────────────┤
│ NEEDS YOU                               │
│ ⚠ 6595 quote review          Claude     │
│ WORKING                                 │
│ ● 6585 report                ChatGPT    │
│ ● Venta daily brief          ChatGPT    │
│ ● Planning research          Claude     │
├─────────────────────────────────────────┤
│ Recently completed                      │
│ ✓ DTM research               ChatGPT    │
└─────────────────────────────────────────┘
```

Attention first, running second, recently completed (auto-removed after a configurable 5–15 minutes) last. Collapses on header click, click elsewhere, or optional inactivity. Monitoring never depends on the pane being open.

## 7. Unified task model

The UI must not understand Codex JSONL, Claude logs, notifications or other provider details. Provider adapters produce normalized state (`AgentProvider`, `AgentTaskStatus {Working, NeedsAttention, Complete, Failed, Unknown}`, `StateConfidence {Confirmed, Inferred, Stale}`, `AgentTask {Id, Provider, Title, Status, Confidence, LastActivity, AttentionReason, SessionReference}`); the UI consumes it.

## 8. Provider architecture

`IUsageProvider` (Codex, Claude) and `IAgentTaskProvider` (Codex, Claude Cowork) feed an `AgentStateService` consumed by the UI. Quota and task collectors are independent: a failure in one never stops another.

## 9–10. Allowances

Codex: reuse the app-server approach; show whatever windows Codex returns; the compact bar shows the principal allowance. Claude: reuse the OAuth/DPAPI implementation behind an interface (the endpoint is undocumented). Failure shows `Claude —` or a stale indicator, never a crash or loss of the last good value.

## 11–12. Codex tasks

Monitor the local Codex session JSONL (see ProjectNeura/agent-dashboard as reference): `FileSystemWatcher` → incremental JSONL parser → per-session state → `AgentTask`. Never reparse whole multi-MB files; keep offsets, turn state, pending approval/input, last activity and a title. Reconcile the directory every 15–30 s. States: Working, NeedsAttention (input/approval/permission/decision), Complete, Failed (reliable terminal error only). Titles from real metadata, else derived from the first request; concise; no AI calls.

## 13–17. Claude Cowork

Use the local Cowork task data (see ClaudeLift) for discovery: ID, title, last modification, activity, existence, transcript metadata. Watcher plus reconciliation. Cowork has no documented status feed; community tools infer from task files, desktop logs and Windows notifications. Some prompts do not toast when Claude is visible. Design around this. Evidence order: (A) Cowork task data, (B) `UserNotificationListener` (supported API; decide in a spike whether packaging for the capability is worthwhile), (C) desktop logs behind an isolated adapter only if needed, (D) recency heuristic → `Working/Inferred`, never "definitely executing".

Clearing stale attention: clear on evidence of resumed activity; clear on completion; allow manual dismissal; expire unresolved inferred alerts to `Unknown` (not `Complete`). Never report a stale prompt as unresolved indefinitely. `Unknown` is a valid state; do not invent certainty.

## 18. Clicking tasks

v1: clicking a Codex task focuses ChatGPT; a Claude task focuses Claude. Exact-session navigation only via a stable supported mechanism. No mouse-coordinate automation.

## 19–22. Positioning, tray, notifications, thresholds

Sit against the work area above the taskbar; no AppBar, no covering the taskbar; handle DPI. Tray: show/hide, expand/collapse, refresh allowances, settings, start with Windows, quit — not a second UI. Notifications only on transition into `NeedsAttention` (e.g. `Claude needs you — 6595 quote review`), with a setting to disable. Low allowance may be flagged subtly (normal / approaching / low); reset countdowns live in the pane.

## 23–26. Refresh, persistence, privacy, logging

Usage every 60–90 s; countdowns local; tasks via watcher plus 15–30 s reconciliation; UI updates only on state change; negligible idle CPU. Persist only position, expanded preference, monitor/anchor, settings, notification preference, refresh frequency, autostart, minimal dedup state. No history database, no transcripts. Local-first: no backend, telemetry, cloud DB or OpenAI API key; do not copy Codex tokens; keep DPAPI; never transmit or store prompts/transcripts. Log provider availability, session counts, state transitions, parser errors, watcher failures, quota failures — never tokens, cookies, prompts, transcripts or secrets. Provide a diagnostic report Steve can hand to an AI coding assistant.

## 27. Fragile integrations

Every undocumented dependency sits behind a narrow adapter (`CoworkTaskStoreReader`, `ClaudeNotificationMonitor`, `ClaudeLogMonitor`, `CodexSessionReader`). No JSON property names or log strings in view models.

## 28. Not in v1

Usage graphs, token analytics, cost tracking, prompt history, model switching, starting conversations, replying from the overlay, workflow management, remote control, mobile, database server, web dashboard, AI summaries, multi-user, plugins.

## 29–30. Phases and tests

Phase 0 reconnaissance → 1 reshape widget with fake data → 2 Codex tasks → 3 Cowork discovery → 4 Claude attention → 5 interaction polish → 6 hardening. Automated tests for provider parsing with sanitized minimal fixtures (never real transcripts): Codex active turn, pending input, approval, completion, malformed/truncated JSONL, duplicate file events, Claude discovery, recent-activity calculation, transitions, stale expiry, duplicate-notification suppression. Keep a provider contract suite so upstream format changes are obvious.

## 31–32. Success and principle

While working in Excel, Word or CADNA, Steve glances at the strip and sees `GPT 63% Claude 47% ● 4 ⚠ 1`: allowance is fine, four jobs are active, one needs him; one click shows which. It is an ambient AI inbox, not a management platform. Optimise for low noise, low maintenance, fast recognition, reliable alerts, graceful uncertainty, local operation and minimal resource use.
