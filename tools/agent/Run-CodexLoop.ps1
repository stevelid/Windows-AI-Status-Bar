<#
.SYNOPSIS
    Drives the Codex CLI through docs/PLAN.md one commit at a time.

.DESCRIPTION
    Codex has no built-in loop command, so this script provides one. Each iteration:
      1. pulls the current branch and restores NuGet packages (Codex's sandbox has no network);
      2. runs one non-interactive `codex exec` turn with tools/agent/codex-iteration-prompt.md;
      3. reads the LOOP_STATUS line from Codex's final message;
      4. pushes any new commit.
    It stops on BLOCKED, DONE or FAILED, when an iteration makes no commit, or after
    -MaxIterations. Each iteration's final message is kept in .codex-loop/ for review.

.PARAMETER MaxIterations
    Upper bound on iterations (default 20). One iteration is one plan commit.

.PARAMETER Model
    Optional Codex model name passed to `codex exec -m`.

.PARAMETER NoPush
    Commit locally only; do not push.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\agent\Run-CodexLoop.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\agent\Run-CodexLoop.ps1 -MaxIterations 5 -NoPush
#>
[CmdletBinding()]
param(
    [int]$MaxIterations = 20,
    [string]$Model,
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'

# Run from the repository root regardless of where the script was started.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repoRoot

$promptPath = Join-Path $PSScriptRoot 'codex-iteration-prompt.md'
$prompt = Get-Content -LiteralPath $promptPath -Raw
$logDir = Join-Path $repoRoot '.codex-loop'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

foreach ($tool in 'git', 'dotnet', 'codex') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' was not found on PATH. Install it before running this script."
    }
}

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -in @('main', 'master')) {
    throw "You are on '$branch'. Create or check out a working branch first, e.g. git switch -c phase-1-status-strip"
}
if (git status --porcelain) {
    throw 'The working tree has uncommitted changes. Commit or stash them before starting the loop.'
}

Write-Host "Codex loop on branch '$branch', up to $MaxIterations iterations."

for ($i = 1; $i -le $MaxIterations; $i++) {
    Write-Host ''
    Write-Host "=== Iteration $i ==="

    # Keep the branch current (Steve may push recon results between iterations).
    git pull --ff-only --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git pull failed; resolve the branch state and rerun.' }

    # Restore packages here, because the Codex sandbox cannot reach NuGet.
    dotnet restore WindowsAIStatusBar.slnx --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    $before = (git rev-parse HEAD).Trim()
    $lastMessage = Join-Path $logDir ("iteration-{0:D2}.md" -f $i)

    $codexArgs = @('exec', '--sandbox', 'workspace-write', '--output-last-message', $lastMessage)
    if ($Model) { $codexArgs += @('-m', $Model) }
    $codexArgs += $prompt

    & codex @codexArgs
    if ($LASTEXITCODE -ne 0) { Write-Warning "codex exec exited with code $LASTEXITCODE." }

    $status = 'UNKNOWN'
    if (Test-Path -LiteralPath $lastMessage) {
        $match = Select-String -LiteralPath $lastMessage -Pattern 'LOOP_STATUS:\s*(CONTINUE|BLOCKED|DONE|FAILED)' | Select-Object -Last 1
        if ($match) { $status = $match.Matches[0].Groups[1].Value }
    }

    $after = (git rev-parse HEAD).Trim()
    $committed = $after -ne $before
    Write-Host "Status: $status; new commit: $committed"

    if (git status --porcelain) {
        Write-Warning 'Codex left uncommitted changes. Stopping so you can review them.'
        break
    }

    if ($committed -and -not $NoPush) {
        git push -u origin $branch --quiet
        if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }
        Write-Host "Pushed $($after.Substring(0, 7))."
    }

    if ($status -ne 'CONTINUE') {
        Write-Host "Stopping: LOOP_STATUS $status. See $lastMessage and docs/PROGRESS.md ('Waiting on Steve')."
        break
    }
    if (-not $committed) {
        Write-Host "Stopping: Codex reported CONTINUE but made no commit. See $lastMessage."
        break
    }
}

Write-Host ''
Write-Host 'Loop finished. Open or update the draft pull request for this branch on GitHub to run CI.'
