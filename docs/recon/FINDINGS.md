# Phase 0 findings

Source: `docs/recon/ai-status-recon-20260928-095731/` (static report, redacted samples and a partial live timeline captured on Steve's PC, 2026-09-28). A privacy check of the upload found only placeholders, timestamps, tool names, model names and file names; no free text, paths or account details.

Status keys: **Confirmed** (seen in the data), **Refuted** (data contradicts it), **Unverified** (not exercised yet).

## Summary

The recon changes three things in the plan.

1. **Claude attention has a direct signal.** Cowork's `audit.jsonl` records `system/permission_request` when Claude asks for permission or asks a question, and `system/permission_response` when Steve answers. This covers `AskUserQuestion` as well as tool permissions. `NeedsAttention` for Claude can therefore be *Confirmed* rather than inferred, and the Windows notification listener (spike S5) is no longer needed for v1.
2. **Codex will rarely need approval on this machine.** Steve's turns run with `approval_policy: never`, `sandbox_policy: danger-full-access` and the permission profile disabled. Shell work is recorded as `custom_tool_call` named `exec` with a free-form `input`, so the reference implementation's `require_escalated` rule would not match anyway. Codex attention reduces to "waiting for your input", which is still to be observed.
3. **The Codex desktop app is its own MSIX package** (`OpenAI.Codex`), separate from ChatGPT. Clicking a Codex task should activate the Codex app.

## Environment

| Item | Value |
| --- | --- |
| Windows | 10.0.26200 (Windows 11), Windows PowerShell 5.1 |
| Claude | MSIX `Claude` 2.9939.2.0, family `Claude_pzs8sxrjxfjjc`, AUMID `Claude_pzs8sxrjxfjjc!Claude`, process `claude` |
| Codex | MSIX `OpenAI.Codex` 26.924.2738.0, family `OpenAI.Codex_2p2nqsd0c76g0`, AUMID `OpenAI.Codex_2p2nqsd0c76g0!App`, processes `codex`, `codex-*` helpers |
| ChatGPT | processes `ChatGPT` running (no MSIX package matched) |
| Developer mode | not enabled (registry key absent) |
| Upstream widget | Unchanged build showed both Codex and Claude percentages (confirmed by Steve, P0.1) |

## Codex

| ID | Assumption | Status | Evidence and consequence |
| --- | --- | --- | --- |
| A-X1 | Rollouts in `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-*.jsonl` | **Confirmed** | `~\.codex\sessions`, 834 `.jsonl` files, several 0.5–24 MB. |
| A-X2 | `task_started` / `task_complete` / `turn_aborted` mark turns | **Confirmed** | One `task_started` … `task_complete` pair per user message, both in the timeline and the samples. `turn_aborted` carries `reason: "interrupted"`, `turn_id`, `duration_ms`. A new turn is preceded by `event_msg/thread_settings_applied`. |
| A-X3 | Escalations carry `sandbox_permissions: require_escalated` | **Refuted for this setup** | Shell calls are `response_item/custom_tool_call` with `name: "exec"` and a free-form `input` string; other calls are `function_call` `name: "js"` with JSON `arguments` (`code`, `title`). No escalation fields seen. With `approval_policy: never`, approvals do not occur. Keep the rule for other configurations but expect it to be inert here. |
| A-X4 | `request_user_input` recorded as a `function_call` | **Unverified** | No question scenario was captured. Needed: scenario C4. |
| A-X5 | `session_index.jsonl` holds thread names | **Confirmed** | 65 KB; entries `{id, thread_name, updated_at}`. |
| A-X6 | Sub-agent sessions identifiable | **Unverified** | Only `source: "vscode"` observed. |
| A-X7 | Compression after 7 days | **Not observed** | No `.jsonl.zst` files among 834 sessions. The rule (ignore `.zst`) stays harmless. |

Additional Codex observations:

- `session_meta.payload.history_mode` is `paginated`. Files contain **both** `response_item/*` records and `event_msg/item_completed` records. Item types seen: `AgentMessage`, `CommandExecution`, `DynamicToolCall`, `FileChange`, `McpToolCall`, `Reasoning`, `UserMessage`. `CommandExecution` items carry `status` and `exit_code`.
- Every record has an `ordinal` (monotonic line number). Useful for duplicate detection.
- New record types: `world_state`, `token_usage_record` (top level), `event_msg/token_count`. They count as activity and are otherwise ignored.
- `session_meta.payload` includes `originator` (e.g. `codex-chrome-extension-sidepanel`), `source`, `cwd`, `thread_source`, `base_instructions` (very large). **The first line of a rollout can be tens of kilobytes**, so head reads must allow for that (read up to 256 KB for the title search).
- `session_meta.payload` also includes `creator_user_id` and `creator_account_id`: never log them.
- Custom tool outputs can embed images as base64 (a 367 KB string was seen on one line). The incremental reader must handle multi-hundred-kilobyte lines without trouble and the parser must not copy them.
- Turn cadence in the timeline: records every 2–5 s while working; 20–200 s idle gaps between turns while Steve types.

## Claude Cowork

| ID | Assumption | Status | Evidence and consequence |
| --- | --- | --- | --- |
| A-C1 | Roots under `%APPDATA%` and MSIX `LocalCache` | **Confirmed (MSIX only)** | Only `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\local-agent-mode-sessions` exists from outside the package; the `%APPDATA%` path does not. 2 account folders, 14 tasks. |
| A-C2 | Metadata has `title`, `lastActivityAt`, `isArchived`, `error` | **Confirmed**, except `error` not seen | Also `createdAt`, `model`, `sessionId`, `cliSessionId`, `sessionType`, `parentSessionId`, `dispatchParentOrigin`, `processName`, `permissionMode`, `accountName`, `emailAddress`, `systemPrompt`, and more. **Files are 170–220 KB** because they embed configuration; parse with `Utf8JsonReader`/`JsonDocument` and read only the needed properties. **Metadata write times can change in bulk** (two August tasks were rewritten at 09:30 today), so recency must come from `lastActivityAt` and the audit log, never from metadata file times. |
| A-C3 | `audit.jsonl` written live during a run | **Unverified** | No Cowork activity happened during the watch. Needed: scenarios K1–K4. |
| A-C4 | `result` ends every run | **Confirmed** | `{"type":"result","subtype":"success","is_error":false,"stop_reason":"end_turn",...}` ends each run; `system/init` starts each run. |
| A-C5 | `AskUserQuestion` pending as `tool_use` | **Confirmed, with a better signal** | Sequence: `assistant/{tool_use:AskUserQuestion}` → `system/permission_request` → (4 min 34 s later) `system/permission_response` (`decision: "once"`, `granted: true`) → `user/{tool_result}`. |
| A-C6 | Permission prompts leave a trace | **Confirmed** | `system/permission_request` with `tool_name`, `tool_input`, `uuid`; `system/permission_response` with `tool_name`, `decision`, `granted`. |
| A-C7 | Toast AUMID and body contain task title | **Not needed** | AUMIDs recorded above. Notification listener dropped from v1 (see summary). |
| A-C8 | Claude window belongs to process `Claude` | **Confirmed** (`claude`) | Activation via AUMID is simpler; see plan P5.1. |

Additional Cowork observations:

- Activity records during a run: `system/status` with `status: "requesting"` (each model request), `system/thinking_tokens`, `assistant/*`, `user/{tool_result}`, `command_lifecycle` with `state: "queued"`/`"started"`, `rate_limit_event`.
- Every audit record has `_audit_timestamp` and `_audit_hmac` (the log is tamper-evident; we only read it).
- Tool names include `mcp__workspace__bash`, `Read`, `Edit`, `TaskUpdate`, `ToolSearch`, `mcp__cowork__present_files`, and connector tools `mcp__<uuid>__<tool>`.
- Each task folder has `.claude/projects/.../*.jsonl` (one transcript), `outputs/`, sometimes `uploads/`, and `.audit-key`. ClaudeLift's note that the transcript folder is empty on Windows is out of date. We do not need the transcript.
- Claude desktop logs live under the same MSIX `LocalCache\Roaming\Claude\logs` folder; `main.log` was last written 2026-08-28, so logs are not a reliable live source. Not needed.

## Follow-up from Steve (2026-09-28)

- **Cowork produced nothing in the watched folder during a live task.** Together with the static report (newest `audit.jsonl` files from August although Cowork is in daily use), this indicates that **current Cowork tasks are no longer written to `local-agent-mode-sessions` in the MSIX `LocalCache`**. Possible explanations: a new storage location, a different layout, or tasks running remotely (new metadata keys `sessionType`, `dispatchParentOrigin` and `outboundCCRRemoteId` point that way). A-C3 is therefore **Refuted for the current location**, and Phase 3/4 are blocked until the live location is found.
- **Neither Cowork nor Codex used a structured question.** Both asked their question in ordinary text at the end of the turn. Such a turn simply completes (`result` / `task_complete`), so the structured-question rules (A-C5, A-X4) will rarely fire in practice. See the open design question below.

## Claude Code desktop write watch (S9, 2026-09-28)

Steve ran a short task in Claude desktop's Code tab during the five-minute `Find-ClaudeWrites.ps1` watch and selected **Always allow** for a command permission. The sanitized observations are recorded in [`claude-code-writes-20260928.md`](claude-code-writes-20260928.md); the raw Desktop report was not committed because its path redaction left the encoded project-folder name visible.

The watch confirms A-K1 for the desktop Code tab on this machine's default Claude home: a project JSONL stream and session metadata changed during the task. The metadata included permission-related key names, but the script captured no values. Nested transcript fields (A-K2) and interruption records (A-K3) remain unverified. No user hook was configured or observed, so A-K4 remains unverified.

## Still needed from Steve

Steve clarified (2026-09-28) that most of his Claude work is in **Claude Code** (desktop Code tab and terminal), not Cowork. The plan now tracks Claude Code (D16). Spike S9 confirmed the desktop Code tab's default project-log location; see the redacted report above. A separate terminal run and hook test have not been done.

## Design decision (formerly open)

Steve decided on 2026-09-28 that a completed turn whose final message ends with a question should show as "needs you". This is plan design change D15. Both terminal records carry the final message: Codex `event_msg/task_complete.last_agent_message` and Cowork `result.result`. The same Codex record can carry `error: {message, codex_error_info}` for a failed turn.
