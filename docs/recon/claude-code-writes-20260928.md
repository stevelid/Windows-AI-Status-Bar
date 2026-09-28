# Claude Code desktop write report (S9)

**Watch:** 2026-09-28 13:55:04–14:00:05 local time (300 seconds).

**Scenario:** Steve ran one short task in Claude desktop's Code tab and selected **Always allow** for a command permission.

**Source:** `Find-ClaudeWrites.ps1`. The raw Desktop report is not committed because its path redaction left the encoded project-folder name visible. This note contains only folder categories, file sizes, timestamps and record-type signatures. It contains no prompt or transcript text, session IDs, account identifiers, or actual session-file paths.

## Observed writes

- In Claude Desktop's MSIX `LocalCache` `claude-code-sessions` subtree, the report listed two changed session metadata JSON entries (349,034 and 349,341 bytes), updated at 13:55:24 and 13:59:37. Their top-level keys included `permissionMode`, `alwaysAllowedReasons`, and `sessionPermissionUpdates`; values were not captured.
- The user-level Claude Code `projects` area contained a 687,160-byte JSONL stream, last written at 13:59:59. The content-free record-type signatures were `attachment`, `assistant`, `user`, `last-prompt`, `custom-title`, `agent-name`, `atis-latch`, and `bridge-session`.
- Claude Code session-state JSON and shell snapshots also changed under the user-level Claude home. Claude Desktop cache, configuration, and browser storage files changed during the same window.
- No files changed under the standalone `%LOCALAPPDATA%\Claude` root.

## Findings and limits

- This confirms that a desktop Code-tab task on this machine writes a Claude Code project JSONL stream under the default user-level Claude home, supporting A-K1 for the desktop Code tab. Terminal sessions and `CLAUDE_CONFIG_DIR` overrides were not separately tested.
- The metadata key names are consistent with permission settings being kept in session metadata, but the watch did not record values and cannot verify how **Always allow** was persisted.
- The record signatures confirm `user` and `assistant` record types only. Nested fields such as `message.stop_reason`, `isSidechain`, and title payloads were not captured, so A-K2 remains unverified. The watch did not test interruption handling or user hooks; A-K3 and A-K4 remain unverified.
- This was one five-minute task. The simultaneous cache and configuration writes are not evidence that those files are task transcripts.
