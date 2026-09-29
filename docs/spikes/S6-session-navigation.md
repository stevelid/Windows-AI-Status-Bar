# S6 — Open the selected conversation

2026-09-29. Requested by Steve: task clicks must select the task's conversation, and every task row must be dismissible.

## Codex

The [official command reference](https://learn.chatgpt.com/docs/reference/commands#deep-links) documents `codex://threads/<thread-id>` for an existing local conversation. The rollout parser already reads this ID; the task mapper now exposes it as the in-memory `SessionReference`. File-derived fallback IDs are not navigation targets.

## Claude Code (A-K7)

The [official desktop link guide](https://support.claude.com/en/articles/14729294-open-claude-desktop-with-a-link) documents the `claude://` scheme, chat links and new Code sessions, but does not document opening an existing local Code session.

Read-only inspection of the installed Claude 2.9939.4.0 application bundle found:

- `claude://code/continue?session=<local-id>` selects an existing, non-archived desktop Code session by its desktop `sessionId`. The handler accepts `local_` followed by 1–64 letters, digits or hyphens. This route can be disabled by the app's entry-point or enterprise settings.
- `claude://resume?session=<UUID>` asks Desktop to import/resume a CLI transcript. It is not a command to focus the original terminal. Import can be refused for live ownership, sign-in, workspace trust or other app conditions.
- Desktop metadata lives under `Claude/claude-code-sessions/<account>/<workspace>/local_*.json`, in the ordinary roaming data root or MSIX LocalCache equivalent. `cliSessionId` identifies the collected transcript; `sessionId` identifies the existing desktop session. These IDs must not be confused.

Field-shape checks on 131 local metadata files found 131 `local_*` desktop IDs, all different from their CLI IDs; 126 had UUID CLI IDs. Only aggregate counts and field names were emitted; no real IDs, account names, paths, titles or transcript content are retained here. Synthetic fixtures capture the fields independently.

The Core reader skips missing/unreadable roots, malformed/oversized files, archived sessions and invalid identifiers. If multiple installs retain a match, it prefers `lastActivityAt` rather than file modification time. It reads only the mapping, archive flag and activity timestamp, keeps no cache or persisted IDs, and does not change Claude's files. Metadata reads happen off the WPF UI thread.

## Result and remaining acceptance

Use validated session links first; if no usable reference exists or Windows rejects launch, retain the existing package/process app-focus fallback. Logs report only provider, status and dispatch/fallback outcome. Dispatch success cannot confirm navigation inside either app.

Local build, Core tests and Windows smoke tests pass. Steve should click rows for two different sessions per app while another conversation is selected, then repeat for a completed row. Claude CLI import/resume must be checked separately, including a running-session refusal. The step-by-step checklist is in `PROGRESS.md`.
