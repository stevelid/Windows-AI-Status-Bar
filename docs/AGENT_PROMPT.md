# Prompts for the implementation agent

Two ways to drive a smaller implementation agent (for example Claude Sonnet in Claude Code, locally or on claude.ai/code). Both rely on `CLAUDE.md`, `docs/PLAN.md` and `docs/PROGRESS.md` in the repository, so the prompts stay short.

## Option A — Loop prompt (recommended)

Start a Claude Code session in this repository and paste the following. `/loop` without an interval lets the agent pace itself: each iteration completes one commit from the plan, and the loop ends when it reaches work that needs Steve.

```text
/loop Continue implementing Windows AI Status Bar, one plan commit per iteration.

Each iteration:
1. Read CLAUDE.md, docs/PROGRESS.md and the relevant section of docs/PLAN.md. Pull the latest changes on your branch first.
2. Choose the first unticked commit ID in PROGRESS.md whose prerequisites are met. Skip items marked 🧑 (they need Steve) and items that PLAN.md gates behind P0.4 if recon has not arrived; Phase 1 and the parsing parts of Phases 2–3 may proceed with fixtures named provisional-*.
3. Implement exactly that commit as PLAN.md specifies: the listed files, interfaces and tests. Put logic in core/StatusBar.Core; keep WPF code thin. Do not redesign the architecture. If the plan is ambiguous, choose the simplest option consistent with it and record the choice under "Decisions and deviations" in PROGRESS.md.
4. Verify: `dotnet build WindowsAIStatusBar.slnx --nologo` with no errors and `dotnet test tests/StatusBar.Core.Tests --nologo` all green. Re-read your diff for privacy (no prompt text, paths or tokens in logs, fixtures or persisted state) and for assumption-ID comments on undocumented rules.
5. Tick the item in PROGRESS.md, commit with the ID as the message prefix, and push. Keep one draft pull request open per phase and add a short "Steve can test" note to it when a phase's 🧑 acceptance becomes possible.
6. If the next item needs Steve (🧑), or a spike needs his decision, write plain step-by-step instructions under "Waiting on Steve" in PROGRESS.md, push, report them, and stop the loop.

Stop the loop when all Phase 6 items are ticked, when blocked on Steve, or if the same verification fails three times (report the error output instead of guessing).
```

## Option B — Goal prompt (one phase per session)

Use this when you prefer to review each phase before the next starts. Replace `<N>` with the phase number.

```text
Goal: complete Phase <N> of docs/PLAN.md for the Windows AI Status Bar repository.

Context: read CLAUDE.md first, then docs/PLAN.md (Sections 4–6 and 8 in particular) and docs/PROGRESS.md. The architecture, interfaces, state rules and commit list are already decided; implement them rather than redesigning.

Work through the Phase <N> commit IDs in order. For each one: implement the listed files and tests, run `dotnet build WindowsAIStatusBar.slnx --nologo` and `dotnet test tests/StatusBar.Core.Tests --nologo`, tick it in PROGRESS.md, and commit with the ID as the message prefix. Push after each commit and keep a single draft pull request for the phase.

Rules: logic goes in core/StatusBar.Core with tests; WPF code stays thin. Never log or store prompt text, transcripts, session paths or tokens. Every rule based on undocumented Codex/Claude behaviour cites its assumption ID (e.g. `// ⚠️ A-X3`). Record any deviation from the plan, with the reason, in PROGRESS.md.

Done when: every non-🧑 item in Phase <N> is ticked, CI is green on the pull request, and PROGRESS.md "Waiting on Steve" lists the phase's 🧑 acceptance checks as short numbered steps he can follow using the status-bar-win-x64 CI artifact.
```

## Tips

- A new session per phase keeps the context small, which suits smaller models well.
- If the agent reports a recon-dependent question, run the recon steps in `docs/recon/README.md` and paste the output into the session.
- If a phase goes wrong, ask the agent to revert to the last ticked commit rather than patching forward.
