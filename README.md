# Windows AI Status Bar

A small strip docked above the Windows taskbar that shows, at a glance:

1. How much Codex and Claude session allowance is left, with reset times.
2. How many AI tasks are running (Codex and Claude Code).
3. Whether any task needs you: an approval, a permission prompt or a question.

```
GPT 62%  Claude 41%  ● 3  ⚠ 1
```

Clicking the strip opens a small pane with reset times and the task list. Clicking a task brings the relevant app to the front.

## Install

1. Download `AIStatusBar-win-x64.zip` and `SHA256SUMS.txt` from the [Releases](../../releases) page.
2. Check the download. In PowerShell, `(Get-FileHash AIStatusBar-win-x64.zip).Hash.ToLower()` should match the value in `SHA256SUMS.txt`.
3. Unzip and run `AIStatusBar.exe`. It is self-contained; no .NET install is needed.

The app can check for and install newer releases itself, and verifies each download against `SHA256SUMS.txt`.

## First run

- **Claude sign-in.** If you used the upstream AI Usage Widget, the app copies its encrypted Claude token from `%APPDATA%\ClaudeUsageWidget` on first launch. Otherwise sign in from the app.
- **Claude Code hooks (optional).** In Settings, "Use Claude Code hooks for exact status" adds hooks for four events to your Claude settings, so permission prompts and questions show exactly. Without them, Claude Code status is inferred from transcripts. Settings previews the change first.
- **Codex.** Allowance is read through the official Codex app-server, and tasks from the session files under `%USERPROFILE%\.codex`. The app never reads Codex credentials.

Settings, tokens and the log are kept in `%APPDATA%\WindowsAIStatusBar`.

## Privacy

No prompts, transcripts, tool arguments or session paths are stored or logged. Task titles are truncated and held in memory only. Claude tokens are encrypted with Windows DPAPI. See [`docs/SECURITY.md`](docs/SECURITY.md) for the full data inventory.

## Limitations

Some task states are inferred from undocumented Codex and Claude behaviour. See [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md).

## Development

`docs/PLAN.md` holds the architecture and plan, `docs/PROGRESS.md` the status. Build with `dotnet build WindowsAIStatusBar.slnx` and test with `dotnet test tests/StatusBar.Core.Tests`.

## Origin and licence

Forked from [Kilin-570/AIUsageWidget](https://github.com/Kilin-570/AIUsageWidget) (MIT). The upstream history is preserved and its README is at [`docs/UPSTREAM_README.md`](docs/UPSTREAM_README.md). The MIT licence is in [`LICENSE`](LICENSE).
