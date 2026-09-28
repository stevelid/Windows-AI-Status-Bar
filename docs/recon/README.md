# Phase 0 reconnaissance — instructions for Steve

The status bar has to read files that Codex and Claude write on your PC. Their formats are not documented, so before building the task monitors we need to see their *structure* on your machine. The script below records structure only: every prompt, answer, title, path and file content is replaced by a placeholder such as `<str:42>`. Nothing is sent anywhere.

It takes about 30 minutes in total, most of it using Codex and Claude normally.

## Step 1 — Confirm the unchanged widget works (P0.1, about 5 minutes)

1. Open the pull request for this setup work on GitHub, go to **Checks → Build and test → Artifacts**, and download `status-bar-win-x64`. (Alternatively, with the .NET 10 SDK installed: `dotnet run --project ClaudeUsageWidget.csproj`.)
2. Unzip and run `ClaudeUsageWidget.exe`. Windows SmartScreen may warn because the build is unsigned; choose *More info → Run anyway*.
3. Sign in to Claude when prompted, then switch to the ChatGPT tab and connect.
4. Note whether both show percentages. Quit it from the tray icon afterwards.

## Step 2 — Static report (P0.2, about 2 minutes)

In PowerShell, from the repository folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\recon\Collect-Recon.ps1
```

A folder `ai-status-recon-<date>` appears on your Desktop. Open `report.md` and the `samples` folder and check you are happy with the contents.

## Step 3 — Live timeline (P0.3, about 25 minutes)

Start the watcher, which records a timeline of record *types* as they are written:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\recon\Collect-Recon.ps1 -WatchSeconds 1500
```

While it runs, work through the scenarios below and write the clock time of each action in a `notes.md` file in the output folder (for example `10:02:15 approval prompt appeared`, `10:03:40 clicked Approve`). Please wait about 60 seconds before answering each prompt, so the timeline clearly shows the waiting period.

### Codex (ChatGPT app → Codex)

| ID | Scenario |
| --- | --- |
| C1 | Start a task that will take a minute or two (e.g. "summarise the files in this folder in detail"). |
| C2 | Let it finish. |
| C3 | Ask for something that needs approval, such as a command that needs network access or writing outside the workspace. Wait 60 s, then approve. |
| C4 | Ask Codex to ask you a clarifying question before it starts (e.g. "Before you begin, ask me which format I want"). Wait 60 s, then answer. |
| C5 | Start a task and stop it part-way. |
| C6 | Run two tasks at the same time. |

### Claude Cowork (Claude desktop app)

| ID | Scenario |
| --- | --- |
| K1 | Start a Cowork task that takes a minute or two. |
| K2 | Let it finish. |
| K3 | Trigger a permission prompt (for example ask it to use a folder or tool it has not been allowed yet). Wait 60 s, then allow. |
| K4 | Ask: "Before continuing, ask me a multiple-choice question about the output format." Wait 60 s, then answer. |
| K5 | Start a task and stop it part-way. |
| K6 | Repeat K4 once with the Claude window **minimised** and note whether a Windows notification appeared. |

## Step 4 — Share

Review the output folder, then either:

- copy it into `docs/recon/<yyyy-mm-dd>/` in the repository and commit it, or
- paste `report.md`, `timeline.md`, `notes.md` and the `samples` folder into the next agent session.

Please do not share the output if anything in it looks like real content; tell the agent instead so the redaction can be tightened.

## Optional

`-IncludeLogExcerpts` adds masked excerpts of Claude desktop log lines that mention notifications, permissions or waiting. Only use it if the agent asks for it, and read the excerpts before sharing.
