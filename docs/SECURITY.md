# Security and privacy design

## Data inventory

| Data | Location | Protection | Leaves the PC through |
| --- | --- | --- | --- |
| Claude access/refresh tokens | `%APPDATA%\WindowsAIStatusBar\tokens.dat` | Windows DPAPI, current user | Anthropic OAuth and usage endpoints |
| ChatGPT/Codex tokens | Codex-managed storage | Owned by official Codex | Official Codex process only |
| Widget settings | `%APPDATA%\WindowsAIStatusBar\settings.json` | Normal user file permissions | Never |
| Widget log | `%APPDATA%\WindowsAIStatusBar\log.txt` | Normal user file permissions | Never automatically |
| TypeSafe (Jev) API key (only if you save one) | `%APPDATA%\WindowsAIStatusBar\jev.key` | Windows DPAPI, current user | TypeSafe API (`api.typesafe.ai`) |
| Tail of a finished turn's final message (only while the AI check is on) | Memory only, up to 1,500 characters, never logged or saved | Not stored | TypeSafe API, one request per finished turn (TypeSafe states it does not retain or train on it) |
| Completed-task history (only if "Keep completed history after restart" is on) | `%APPDATA%\WindowsAIStatusBar\history.json`: short task titles, provider, finish time and the session reference used to reopen the task, kept 7 days | Normal user file permissions; deleted when the setting is turned off | Never |

On first launch, the app copies only the existing DPAPI-encrypted `tokens.dat`
from `%APPDATA%\ClaudeUsageWidget` when the new token file is absent. It does
not import the old settings file. A marker prevents a later logout from
re-importing the legacy token.

The repository and release package contain no account identifiers, credentials, local settings, or real usage payloads.

## Trust boundaries

### Claude

The widget directly owns Claude OAuth tokens because the existing Claude provider requires them. Tokens are encrypted with DPAPI and are not portable to another Windows user. OAuth state and PKCE verification reduce authorization-code interception risk.

### ChatGPT / Codex

The widget deliberately delegates authentication to OpenAI Codex:

- It does not inspect `~/.codex/auth.json`.
- It does not receive an access token from the app-server API.
- It uses the documented local JSONL protocol and the read-only `account/rateLimits/read` method.
- If Codex is in API-key mode, the widget warns before starting a ChatGPT login that would replace Codex's primary auth mode.

## Process execution safety

- `.exe` launches use `ProcessStartInfo.ArgumentList` with `UseShellExecute=false`.
- `.cmd` and `.bat` paths are resolved to an existing file, stripped of quote characters, quoted, and invoked only with the fixed `app-server --stdio` arguments.
- No usage value or network response is interpolated into a command.
- The child process is terminated when the widget exits.

## Logging

- No telemetry is implemented.
- Codex stdout notifications and stderr diagnostics are discarded rather than copied to the widget log.
- JSON-RPC errors are reduced to the documented numeric code and message.
- Users should still review `log.txt` before attaching it to a public issue because operating-system paths may appear in normal application diagnostics.

## Update boundary

GitHub Actions builds the release commit, runs the local mock app-server smoke tests, and publishes `AIStatusBar-win-x64.zip` together with `SHA256SUMS.txt`.

The updater downloads the asset named `AIStatusBar-win-x64.zip` and its `SHA256SUMS.txt` companion from this repository's latest GitHub Release. It verifies the expected SHA-256 checksum before extracting or replacing the executable, and rejects packages that do not contain exactly one `AIStatusBar.exe`.

Updater temporary directories are deleted after success, cancellation, or failure. Startup cleanup only removes directories older than seven days whose names match the widget's exact GUID-based pattern, are direct children of the Windows temporary directory, and are not reparse points.

The **Copy diagnostic information** command is intentionally redacted. It reports application/OS versions, provider status categories, last-success timestamps, and the Codex executable name/version, but excludes credentials, account identifiers, usage values, log contents, and full filesystem paths.

The checksum protects against corrupted or mismatched downloads. Releases are not currently code-signed, so users who require stronger supply-chain assurance should build from a reviewed commit and verify the published checksum independently.

## Reporting a security issue

Open a GitHub issue for non-sensitive problems. Do not paste tokens, `auth.json`, `tokens.dat`, browser cookies, or unredacted private logs into a public issue. For a sensitive report, contact the repository owner privately through their GitHub profile until a dedicated security advisory channel is enabled.
