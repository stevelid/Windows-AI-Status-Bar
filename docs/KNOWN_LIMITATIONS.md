# Known limitations

These limits describe the current Codex and Claude Code task collectors. Recon notes are from 2026-09-28; assumptions marked unverified still need a Windows check.

## Codex tasks

- The collector reads local Codex session files. Cloud tasks started on chatgpt.com are not visible.
- A Codex process that stops or crashes without writing a completion record remains Working, then becomes Stale after 20 minutes and Unknown after two hours. Unknown rows eventually expire from the pane.
- Waiting for approval is inferred from pending tool calls. Codex does not write a direct approval event in the observed setup, so some approvals may not be detected. In particular, the `request_user_input` function-call shape remains unverified (A-X4).
- “Asked you a question” checks whether the last assistant message ends with a question mark. It can miss a question followed by more text or flag a rhetorical question. This state is Inferred, can be dismissed, and expires after four hours.

## Claude Code tasks

- The default `~/.claude/projects` transcript tree is confirmed for the desktop Code tab only (A-K1). Terminal sessions and `CLAUDE_CONFIG_DIR` have not been checked on Steve’s machine; the Claude Code home setting can point the collector at another location.
- The collector scans `.jsonl` files modified in the last 24 hours. It recovers from transcript tails on start-up and keeps session state in memory; it does not retain full transcripts.
- Top-level record types were observed, but nested message fields, `message.stop_reason`, `isSidechain`, title fields and text-block handling remain provisional (A-K2). A Claude update could change those shapes and prevent a turn or title from being recognized.
- The `[Request interrupted by user` marker is unverified (A-K3), so an interrupted turn may not be shown as Stopped.
- Without Claude Code hooks, a permission prompt is not represented in the transcript and will not appear as a confirmed permission alert. Transcript activity can continue to show the task as Working.
- Hooks are opt-in. Whether Claude Code runs the configured hooks from the desktop Code tab is unverified (A-K4); terminal hook execution is also untested. When hook events arrive, the provider can show permission and input alerts and clear them on later evidence.
- Clicking a Claude Code task resolves its transcript ID to the existing desktop session and sends `claude://code/continue?session=…`. The mapping and link are undocumented (A-K7). Without a desktop match, the link uses Claude's CLI resume/import route: it opens the transcript in Desktop rather than focusing the original terminal. Claude may refuse to import a session still owned by a running CLI process, or reject it because of sign-in, workspace trust or an app setting. Custom Claude Code homes may also be unavailable to Desktop.
- A transcript question is Inferred and expires after four hours. Confirmed hook alerts are downgraded to Unknown after two hours if no later event clears them. Stale work also becomes Unknown after two hours; completed and Unknown rows then use the configured pane retention windows.

## Dismissal and saved state

- Dismissal applies to one `(taskId, evidenceKey)` pair for 24 hours. A new question or other evidence for the same task can appear immediately.
- Every task row can be dismissed, including completed, failed, working and confirmed attention rows. This hides the current update and removes it from the strip counts; it does not stop a task, answer a question or approve a permission. New evidence, activity or a changed status can show the task again.
- Each dismissal entry in `state.json` contains only a SHA-256 digest of that pair and its expiry, not the task title, transcript, path, session ID or evidence ID. If the state file is malformed or unavailable, dismissal works for the current run but cannot be saved for a restart.

## Other coverage

- Codex task clicks use the documented `codex://threads/<thread-id>` link. Windows dispatching a session link does not prove the app navigated: the app can reject it after dispatch. If the task has no valid session ID or Windows cannot launch the link, the status bar falls back to bringing the owning app forward. Content-free logs distinguish session-link dispatch from app-only focus.

- Claude Cowork task tracking is not included in this build. It remains an optional future phase.
- Claude and Codex allowance percentages come from their provider data and may lag by one refresh interval.

- A Claude Code turn that ends while background agents or shells are still running shows as Working ("Waiting for background agents") until each reports back. This relies on undocumented transcript records (A-K6). Background work that never reports back is ignored after 2 hours.
