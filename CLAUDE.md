# CLAUDE.md — Windows AI Status Bar

A tiny WPF strip above the Windows taskbar showing Codex/Claude allowance, running AI tasks and tasks that need Steve. Forked from Kilin-570/AIUsageWidget (MIT).

**Read first:** `docs/PLAN.md` (architecture, rules, commit list) and `docs/PROGRESS.md` (what is done). `docs/BRIEF.md` is the original product brief; the plan wins where they differ.

## Layout

- `core/StatusBar.Core/` — net10.0, **no WPF/WinForms/Windows APIs**. Models, parsers, state machines, timing, docking geometry. Put logic here whenever possible.
- Repository root — the WPF app (`ClaudeUsageWidget.csproj`, namespace `ClaudeUsageWidget`, net10.0-windows). Windows, tray, DPAPI, Win32 interop and thin adapters over upstream services.
- `tests/StatusBar.Core.Tests/` — xUnit tests for Core (run anywhere). Fixtures in `Fixtures/`.
- `tests/ClaudeUsageWidget.SmokeTests/` etc. — upstream Windows-only smoke tests.
- `tools/recon/` — PowerShell recon script Steve runs on Windows.

## Commands

```bash
dotnet build WindowsAIStatusBar.slnx --nologo
dotnet test tests/StatusBar.Core.Tests --nologo
```

WPF projects compile on Linux (`EnableWindowsTargeting`) but cannot run there. Windows behaviour is verified by CI (`windows-latest`) and by Steve using the `status-bar-win-x64-<commit>` artifact (named after the PR commit it was built from).

## Rules

- Never log, persist or put in test fixtures any *real* prompts, transcript text, tool arguments, session file paths, tokens, cookies or account identifiers. Titles are truncated and kept in memory only. Fixtures **should** contain the real field names with short made-up placeholder values (e.g. `"last_agent_message":"Which format would you like?"`), so each fixture file on its own proves the format; do not patch fixture lines inside tests.
- Every rule that relies on undocumented Codex/Claude behaviour cites its assumption ID from `docs/PLAN.md` §8 in a comment, e.g. `// ⚠️ A-C5`.
- JSON property names and log strings stay inside the provider's parser class. View code consumes `AgentTask`, `UsageSnapshot` and `StatusBarState` only.
- Use `TimeProvider` for all time in Core; tests use `FakeTimeProvider`.
- A failure in one collector must never stop another (quota vs tasks, Codex vs Claude).
- Keep the style of the surrounding upstream code: file-scoped namespaces, primary constructors where upstream uses them, small classes, XML doc comments on public types, comments that explain *why*.
- New UI strings need entries in `Localization.cs` (missing keys render as the key). English text may be copied into the zh slot.
- Do not add NuGet packages to Core beyond test helpers without a note in `PROGRESS.md`.
- Commits start with the plan ID (e.g. `P2.2 Add Codex rollout parser`). Update `docs/PROGRESS.md` in the same commit.
