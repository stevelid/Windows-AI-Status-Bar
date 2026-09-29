# Manual test matrix (P6.3)

Each scenario must leave the widget running with the other collectors still working. Record Pass/Fail in Result and the build label (e.g. `3.0.0+a1b2c3d`) in Build.

| Scenario | How to trigger | Expected | Result | Build | Notes |
| --- | --- | --- | --- | --- | --- |
| ChatGPT not installed | Run on a profile or VM without ChatGPT/Codex, or rename `%USERPROFILE%\.codex` | Codex shows unavailable; Claude quota and tasks unaffected | | | |
| Claude not installed | Run without Claude desktop/Code, or point the Claude Code home setting at an empty folder | Claude shows unavailable; Codex unaffected | | | |
| One provider signed out | Sign out of Claude in the app, or of Codex | Signed-out provider shows a sign-in prompt; the other keeps updating | | | |
| Codex child process killed | End the `codex app-server` child in Task Manager | Codex quota recovers via restart with back-off; widget stays up | | | |
| Claude usage endpoint unreachable | Block `api.anthropic.com` (hosts entry or firewall rule) | Claude quota shows stale/error; Codex and tasks unaffected | | | |
| Session file truncated | Truncate a copy of a live Codex or Claude session `.jsonl` mid-line | That task is skipped or recovers; no crash; other tasks unaffected | | | |
| Malformed JSON line | Append a garbage line to a session file | Line ignored; no crash; later valid lines still parsed | | | |
| Cowork folder renamed (simulated format change) | Rename the Cowork sessions folder while running | No crash; unknown format degrades quietly (Cowork tracking is optional in this build) | | | |
| Resume from sleep | Sleep the PC, then wake it | Strip re-docks; quota and tasks refresh within a poll interval | | | |
| Network off | Disable network adapters | Quota shows stale/error; local task collectors keep working; recovers when network returns | | | |
| Taskbar moved | Move the taskbar to top/left/right, then back | Strip re-docks to the new work area or falls back safely | | | |
| Display disconnected | Unplug the monitor the strip is on | Strip moves to a remaining monitor | | | |
| DPI changed | Change display scaling in Windows Settings | Strip and pane resize correctly, no clipping | | | |
| Explorer restarted | Restart `explorer.exe` from Task Manager | Strip and tray icon reappear; widget keeps running | | | |
