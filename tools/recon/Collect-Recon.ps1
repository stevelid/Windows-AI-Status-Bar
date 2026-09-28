<#
.SYNOPSIS
    Phase 0 reconnaissance for Windows AI Status Bar.

.DESCRIPTION
    Captures the *structure* of the local Codex and Claude Cowork data on this machine so the
    implementation can be built against real formats without copying anyone's conversations.

    Privacy rules applied to every sample written:
      * All free-text string values are replaced with "<str:N>" (N = original length).
      * Only short, enum-like values of allow-listed keys (type, subtype, name, role, status ...)
        and timestamps are kept, because those are what the state machine needs.
      * Object keys that look like paths or sentences are replaced with "<key>".
      * Nothing is uploaded anywhere. Output is written to a local folder for you to review.

    Please read the output before sharing it.

.PARAMETER OutDir
    Where to write the report. Defaults to a timestamped folder on the Desktop.

.PARAMETER WatchSeconds
    If greater than zero, after the static report the script watches the newest Codex rollout
    files and Cowork audit files for this many seconds and records a timeline of new record
    types (no content). Use this while working through the scenarios in docs/recon/README.md.

.PARAMETER CodexHome
    Override for the Codex home directory (defaults to $env:CODEX_HOME or ~/.codex).

.PARAMETER CoworkRoot
    Override for a single Cowork sessions root (defaults to auto-discovery).

.PARAMETER IncludeLogExcerpts
    Also include redacted excerpts of Claude desktop log lines that mention notifications,
    permissions or waiting states. Off by default because logs are unstructured text.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\recon\Collect-Recon.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\recon\Collect-Recon.ps1 -WatchSeconds 900
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [int]$WatchSeconds = 0,
    [string]$CodexHome,
    [string]$CoworkRoot,
    [switch]$IncludeLogExcerpts
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------------------
# Settings
# ---------------------------------------------------------------------------

# Keys whose short, enum-like string values are kept verbatim.
$KeepValueKeys = @(
    'type', 'subtype', 'name', 'role', 'status', 'kind', 'reason', 'stop_reason',
    'sandbox_permissions', 'originator', 'source', 'cli_version', 'model', 'effort',
    'history_mode', 'permission_mode', 'permissionMode', 'is_error', 'decision',
    'approval_policy', 'sandbox_policy', 'mode', 'state', 'event', 'level'
)
# Keys whose values are timestamps (kept so we can see timing behaviour).
$TimestampKeys = @(
    'timestamp', '_audit_timestamp', 'createdAt', 'lastActivityAt', 'updatedAt',
    'updated_at', 'created_at', 'started_at', 'completed_at'
)
# Short, identifier-like values only (no spaces, no path separators).
$EnumLikePattern = '^[A-Za-z0-9_.:\-]{1,64}$'

$TailLinesForSamples = 60
$TailLinesForSequence = 250
$NewestFilesToInspect = 5

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------

if (-not $OutDir) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if (-not $desktop) { $desktop = (Get-Location).Path }
    $OutDir = Join-Path $desktop ('ai-status-recon-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$SamplesDir = Join-Path $OutDir 'samples'
New-Item -ItemType Directory -Force -Path $SamplesDir | Out-Null
$ReportPath = Join-Path $OutDir 'report.md'
$Report = New-Object System.Collections.Generic.List[string]

function Add-Line([string]$text) { $Report.Add($text) }
function Add-Heading([string]$text) { $Report.Add(''); $Report.Add($text); $Report.Add('') }

# Replace the user profile path so reported paths are not personally identifying.
$ProfilePath = [Environment]::GetFolderPath('UserProfile')
function Hide-Profile([string]$path) {
    if (-not $path) { return $path }
    if ($ProfilePath -and $path.StartsWith($ProfilePath, [StringComparison]::OrdinalIgnoreCase)) {
        return '~' + $path.Substring($ProfilePath.Length)
    }
    return $path
}

# ---------------------------------------------------------------------------
# Redaction
# ---------------------------------------------------------------------------

function Test-SafeKey([string]$key) {
    return ($key -match '^[A-Za-z0-9_$@.\-]{1,64}$')
}

function Protect-Value($value, [string]$key) {
    if ($null -eq $value) { return $null }

    if ($value -is [string]) {
        if ($TimestampKeys -contains $key) { return $value }
        if (($KeepValueKeys -contains $key) -and ($value -match $EnumLikePattern)) { return $value }
        # Codex stores tool-call arguments as a JSON string; keep its structure, redact its values.
        if (($key -eq 'arguments' -or $key -eq 'input') -and $value.TrimStart().StartsWith('{')) {
            try {
                $inner = $value | ConvertFrom-Json -ErrorAction Stop
                return (Protect-Value $inner '') | ConvertTo-Json -Depth 30 -Compress
            } catch { }
        }
        return '<str:' + $value.Length + '>'
    }

    # PowerShell 7 converts ISO-8601 strings to DateTime; timestamps are safe to keep.
    if ($value -is [datetime]) { return $value.ToString('o') }

    if ($value -is [bool] -or $value -is [int] -or $value -is [long] -or $value -is [double] -or $value -is [decimal]) {
        # Large integers inside the timestamp keys are epoch values; other numbers are harmless.
        return $value
    }

    if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [System.Management.Automation.PSCustomObject])) {
        $list = New-Object System.Collections.Generic.List[object]
        foreach ($item in $value) { $list.Add((Protect-Value $item $key)) }
        return , $list.ToArray()
    }

    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($prop in $value.PSObject.Properties) {
            $safeKey = $prop.Name
            if (-not (Test-SafeKey $safeKey)) { $safeKey = '<key:' + $result.Count + '>' }
            $result[$safeKey] = Protect-Value $prop.Value $prop.Name
        }
        return [pscustomobject]$result
    }

    return '<' + $value.GetType().Name + '>'
}

function Protect-JsonLine([string]$line) {
    if (-not $line -or -not $line.TrimStart().StartsWith('{')) { return '<non-json line>' }
    try {
        $obj = $line | ConvertFrom-Json -ErrorAction Stop
        return (Protect-Value $obj '') | ConvertTo-Json -Depth 30 -Compress
    } catch {
        return '<unparseable json: ' + $line.Length + ' chars>'
    }
}

# A compact, content-free signature for one record, e.g. "event_msg/task_started"
# or "response_item/function_call:shell_command[require_escalated]".
function Get-RecordSignature([string]$line) {
    if (-not $line -or -not $line.TrimStart().StartsWith('{')) { return '<non-json>' }
    try { $o = $line | ConvertFrom-Json -ErrorAction Stop } catch { return '<bad-json>' }

    $parts = New-Object System.Collections.Generic.List[string]
    $type = Get-Prop $o 'type'
    if ($type) { $parts.Add([string]$type) }

    $payload = Get-Prop $o 'payload'
    if ($payload) {
        $pType = [string](Get-Prop $payload 'type')
        $pName = Get-Prop $payload 'name'
        if ($pName -and ($pName -match $EnumLikePattern)) { $pType += ':' + $pName }
        $callArgs = Get-Prop $payload 'arguments'
        if ($callArgs -is [string] -and $callArgs -match '"sandbox_permissions"\s*:\s*"([A-Za-z_]+)"') { $pType += '[' + $Matches[1] + ']' }
        if ($pType) { $parts.Add($pType) }
    }

    $subtype = Get-Prop $o 'subtype'
    if ($subtype) { $parts.Add([string]$subtype) }

    # Claude audit/transcript records: summarise content block types and tool names.
    $message = Get-Prop $o 'message'
    if ($message) {
        $content = Get-Prop $message 'content'
        if ($content -is [System.Array] -or $content -is [System.Collections.IList]) {
            $blocks = @()
            foreach ($b in $content) {
                $bt = Get-Prop $b 'type'
                $bn = Get-Prop $b 'name'
                if ($bn -and ($bn -match $EnumLikePattern)) { $blocks += ($bt + ':' + $bn) } else { $blocks += $bt }
            }
            $parts.Add('{' + ($blocks -join ',') + '}')
        }
        $stop = Get-Prop $message 'stop_reason'
        if ($stop) { $parts.Add('stop=' + $stop) }
    }
    $isError = Get-Prop $o 'is_error'
    if ($null -ne $isError) { $parts.Add('is_error=' + $isError) }

    if ($parts.Count -eq 0) { return '<no type>' }
    return ($parts -join '/')
}

function Get-Prop($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    if (-not ($obj -is [System.Management.Automation.PSCustomObject])) { return $null }
    $p = $obj.PSObject.Properties[$name]
    # The leading comma stops PowerShell unrolling a one-element array into a scalar.
    if ($p) { return , $p.Value }
    return $null
}

# ---------------------------------------------------------------------------
# File helpers
# ---------------------------------------------------------------------------

# Reads the last N lines without loading very large files fully into memory.
function Get-TailLines([string]$path, [int]$count) {
    try {
        $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
        try {
            $maxBytes = [Math]::Min($fs.Length, 4MB)
            $fs.Seek(-$maxBytes, 'End') | Out-Null
            $buffer = New-Object byte[] $maxBytes
            $read = $fs.Read($buffer, 0, $maxBytes)
            $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
        } finally { $fs.Dispose() }
        $lines = $text -split "`r?`n" | Where-Object { $_ -ne '' }
        if ($maxBytes -lt (Get-Item -LiteralPath $path).Length -and $lines.Count -gt 0) {
            $lines = $lines | Select-Object -Skip 1   # first line may be partial
        }
        return @($lines | Select-Object -Last $count)
    } catch {
        return @()
    }
}

function Get-HeadLines([string]$path, [int]$count) {
    try {
        $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
        $reader = New-Object System.IO.StreamReader($fs)
        try {
            $lines = @()
            for ($i = 0; $i -lt $count; $i++) {
                $l = $reader.ReadLine()
                if ($null -eq $l) { break }
                $lines += $l
            }
            return $lines
        } finally { $reader.Dispose() }
    } catch { return @() }
}

function Write-Sample([string]$fileName, [string[]]$lines) {
    $path = Join-Path $SamplesDir $fileName
    $out = foreach ($l in $lines) { Protect-JsonLine $l }
    Set-Content -LiteralPath $path -Value $out -Encoding UTF8
    return $fileName
}

function Get-Histogram([string[]]$signatures) {
    $h = @{}
    foreach ($s in $signatures) { if ($h.ContainsKey($s)) { $h[$s]++ } else { $h[$s] = 1 } }
    return $h.GetEnumerator() | Sort-Object Value -Descending
}

function Get-ShortId([string]$text) {
    # Stable, non-reversible label for a file so samples can be correlated.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
        return ([BitConverter]::ToString($bytes, 0, 4) -replace '-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

# ---------------------------------------------------------------------------
# 1. Environment
# ---------------------------------------------------------------------------

Add-Line '# Windows AI Status Bar - Phase 0 recon report'
Add-Line ''
Add-Line ('Generated: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))
Add-Line ('Script privacy: free text redacted to <str:N>; review before sharing.')

Add-Heading '## 1. Environment'
Add-Line ('- OS: ' + [Environment]::OSVersion.VersionString)
Add-Line ('- PowerShell: ' + $PSVersionTable.PSVersion)
try {
    $dev = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction Stop
    Add-Line ('- Developer mode (AllowDevelopmentWithoutDevLicense): ' + $dev.AllowDevelopmentWithoutDevLicense)
} catch { Add-Line '- Developer mode: unknown (key not found)' }

if (Get-Command Get-AppxPackage -ErrorAction SilentlyContinue) {
    $pkgs = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Claude|OpenAI|ChatGPT|Codex|Anthropic' }
    if ($pkgs) {
        foreach ($p in $pkgs) { Add-Line ('- MSIX package: ' + $p.Name + ' ' + $p.Version + ' (family ' + $p.PackageFamilyName + ')') }
    } else { Add-Line '- MSIX packages: none matching Claude/OpenAI/ChatGPT/Codex' }
}

$procs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^(claude|chatgpt|codex)' } |
    Group-Object ProcessName | ForEach-Object { $_.Name + ' x' + $_.Count }
Add-Line ('- Running processes: ' + ($(if ($procs) { $procs -join ', ' } else { 'none' })))

# Notification registrations (AUMIDs) help identify Claude/ChatGPT toasts for a listener.
try {
    $notifRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
    $aumids = Get-ChildItem $notifRoot -ErrorAction Stop | Where-Object { $_.PSChildName -match 'Claude|OpenAI|ChatGPT|Anthropic|Codex' }
    foreach ($a in $aumids) { Add-Line ('- Notification AUMID: `' + $a.PSChildName + '`') }
    if (-not $aumids) { Add-Line '- Notification AUMIDs: none matching' }
} catch { Add-Line '- Notification AUMIDs: registry not readable' }

# ---------------------------------------------------------------------------
# 2. Codex
# ---------------------------------------------------------------------------

Add-Heading '## 2. Codex'
if (-not $CodexHome) {
    if ($env:CODEX_HOME) { $CodexHome = $env:CODEX_HOME } else { $CodexHome = Join-Path $ProfilePath '.codex' }
}
Add-Line ('- Codex home: `' + (Hide-Profile $CodexHome) + '` exists=' + (Test-Path -LiteralPath $CodexHome))

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
if ($localAppData) {
    $binRoot = Join-Path $localAppData 'OpenAI\Codex\bin'
    Add-Line ('- ChatGPT-bundled Codex bin: exists=' + (Test-Path -LiteralPath $binRoot))
}

$codexRollouts = @()
if (Test-Path -LiteralPath $CodexHome) {
    Add-Line ''
    Add-Line 'Top-level entries (names only):'
    Add-Line ''
    Get-ChildItem -LiteralPath $CodexHome -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '^(auth\.json|.*token.*|.*credential.*)$' } |
        ForEach-Object {
            if ($_.PSIsContainer) { Add-Line ('- `' + $_.Name + '/`') }
            else { Add-Line ('- `' + $_.Name + '` (' + $_.Length + ' bytes, modified ' + $_.LastWriteTime.ToString('s') + ')') }
        }

    $sessionsDir = Join-Path $CodexHome 'sessions'
    if (Test-Path -LiteralPath $sessionsDir) {
        $all = Get-ChildItem -LiteralPath $sessionsDir -Recurse -File -ErrorAction SilentlyContinue
        Add-Line ''
        Add-Line ('Session files: ' + $all.Count + ' total')
        $all | Group-Object { if ($_.Name -match '\.jsonl\.zst$') { '.jsonl.zst' } else { $_.Extension } } |
            ForEach-Object { Add-Line ('- ' + $_.Name + ': ' + $_.Count) }

        # Directory depth pattern, e.g. sessions/YYYY/MM/DD/rollout-*.jsonl
        $example = $all | Select-Object -First 1
        if ($example) {
            $rel = $example.FullName.Substring($sessionsDir.Length).TrimStart('\', '/')
            $pattern = ($rel -replace '\d{4}', 'YYYY' -replace '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', '<uuid>' -replace '\d{2}', 'NN')
            Add-Line ('- Path pattern: `sessions/' + $pattern + '`')
        }

        $codexRollouts = @($all | Where-Object { $_.Name -like '*.jsonl' } | Sort-Object LastWriteTime -Descending | Select-Object -First $NewestFilesToInspect)
        Add-Line ''
        Add-Line ('Newest ' + $codexRollouts.Count + ' rollout files:')
        foreach ($f in $codexRollouts) {
            $id = Get-ShortId $f.FullName
            Add-Line ''
            Add-Line ('### Codex rollout `' + $id + '`')
            Add-Line ('- size ' + $f.Length + ' bytes, modified ' + $f.LastWriteTime.ToString('s') + ' (' + [int]((Get-Date) - $f.LastWriteTime).TotalMinutes + ' min ago)')

            $head = Get-HeadLines $f.FullName 3
            $tail = Get-TailLines $f.FullName $TailLinesForSequence
            $sampleName = Write-Sample ('codex-' + $id + '-head.jsonl') $head
            $tailSample = Write-Sample ('codex-' + $id + '-tail.jsonl') ($tail | Select-Object -Last $TailLinesForSamples)
            Add-Line ('- redacted samples: `samples/' + $sampleName + '`, `samples/' + $tailSample + '`')

            $sigs = @($tail | ForEach-Object { Get-RecordSignature $_ })
            Add-Line '- record histogram (tail):'
            foreach ($h in (Get-Histogram $sigs | Select-Object -First 40)) { Add-Line ('  - `' + $h.Key + '` x' + $h.Value) }
            Add-Line '- last 40 record signatures (oldest first):'
            foreach ($s in ($sigs | Select-Object -Last 40)) { Add-Line ('  - `' + $s + '`') }
        }
    } else {
        Add-Line '- sessions/ directory not found'
    }

    $index = Join-Path $CodexHome 'session_index.jsonl'
    if (Test-Path -LiteralPath $index) {
        $lines = Get-TailLines $index 3
        Add-Line ''
        Add-Line ('session_index.jsonl present; last entries (redacted): ')
        foreach ($l in $lines) { Add-Line ('- `' + (Protect-JsonLine $l) + '`') }
    }
}

# ---------------------------------------------------------------------------
# 3. Claude Cowork
# ---------------------------------------------------------------------------

Add-Heading '## 3. Claude Cowork (local-agent-mode-sessions)'
$roots = @()
if ($CoworkRoot) { $roots += $CoworkRoot }
else {
    $appData = [Environment]::GetFolderPath('ApplicationData')
    if ($appData) { $roots += (Join-Path $appData 'Claude\local-agent-mode-sessions') }
    if ($localAppData) {
        $pk = Join-Path $localAppData 'Packages'
        if (Test-Path -LiteralPath $pk) {
            Get-ChildItem -LiteralPath $pk -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
                $roots += (Join-Path $_.FullName 'LocalCache\Roaming\Claude\local-agent-mode-sessions')
            }
        }
    }
}

$coworkAudits = @()
foreach ($root in $roots) {
    $exists = Test-Path -LiteralPath $root
    Add-Line ('- Root `' + (Hide-Profile $root) + '` exists=' + $exists)
    if (-not $exists) { continue }
    try {
        $item = Get-Item -LiteralPath $root -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Add-Line '  - root is a reparse point (MSIX redirect)' }
    } catch { }

    $metaFiles = @(Get-ChildItem -LiteralPath $root -Recurse -Depth 2 -File -Filter 'local_*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    $accounts = @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)
    Add-Line ('  - account dirs: ' + $accounts.Count + '; task metadata files: ' + $metaFiles.Count)

    $otherTop = Get-ChildItem -LiteralPath $root -Recurse -Depth 1 -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name -Unique
    if ($otherTop) { Add-Line ('  - other files near root: ' + (($otherTop | ForEach-Object { '`' + $_ + '`' }) -join ', ')) }

    foreach ($m in ($metaFiles | Select-Object -First $NewestFilesToInspect)) {
        $id = Get-ShortId $m.FullName
        Add-Line ''
        Add-Line ('### Cowork task `' + $id + '`')
        Add-Line ('- metadata modified ' + $m.LastWriteTime.ToString('s') + ' (' + [int]((Get-Date) - $m.LastWriteTime).TotalMinutes + ' min ago), ' + $m.Length + ' bytes')
        try {
            $raw = Get-Content -LiteralPath $m.FullName -Raw -ErrorAction Stop
            $obj = $raw | ConvertFrom-Json -ErrorAction Stop
            Add-Line ('- metadata keys: ' + (($obj.PSObject.Properties | ForEach-Object { '`' + $_.Name + '`' }) -join ', '))
            $red = (Protect-Value $obj '') | ConvertTo-Json -Depth 30
            $metaSample = 'cowork-' + $id + '-meta.json'
            Set-Content -LiteralPath (Join-Path $SamplesDir $metaSample) -Value $red -Encoding UTF8
            Add-Line ('- redacted metadata: `samples/' + $metaSample + '`')
        } catch { Add-Line '- metadata could not be parsed' }

        $taskDir = Join-Path $m.DirectoryName ([IO.Path]::GetFileNameWithoutExtension($m.Name))
        if (Test-Path -LiteralPath $taskDir) {
            $entries = Get-ChildItem -LiteralPath $taskDir -Force -ErrorAction SilentlyContinue
            Add-Line ('- task dir entries: ' + (($entries | ForEach-Object { if ($_.PSIsContainer) { '`' + $_.Name + '/`' } else { '`' + $_.Name + '`' } }) -join ', '))
            $transcripts = @(Get-ChildItem -LiteralPath (Join-Path $taskDir '.claude\projects') -Recurse -File -Filter '*.jsonl' -ErrorAction SilentlyContinue)
            Add-Line ('- .claude/projects transcripts: ' + $transcripts.Count)
            $audit = Join-Path $taskDir 'audit.jsonl'
            if (Test-Path -LiteralPath $audit) {
                $af = Get-Item -LiteralPath $audit
                $coworkAudits += $af
                Add-Line ('- audit.jsonl: ' + $af.Length + ' bytes, modified ' + $af.LastWriteTime.ToString('s'))
                $tail = Get-TailLines $audit $TailLinesForSequence
                $s = Write-Sample ('cowork-' + $id + '-audit-tail.jsonl') ($tail | Select-Object -Last $TailLinesForSamples)
                Add-Line ('- redacted audit tail: `samples/' + $s + '`')
                $sigs = @($tail | ForEach-Object { Get-RecordSignature $_ })
                Add-Line '- audit histogram (tail):'
                foreach ($h in (Get-Histogram $sigs | Select-Object -First 40)) { Add-Line ('  - `' + $h.Key + '` x' + $h.Value) }
                Add-Line '- last 40 audit signatures (oldest first):'
                foreach ($sig in ($sigs | Select-Object -Last 40)) { Add-Line ('  - `' + $sig + '`') }
            } else { Add-Line '- audit.jsonl: not present in this root' }
        } else {
            Add-Line '- task dir: not present in this root'
        }
    }
}

# ---------------------------------------------------------------------------
# 4. Claude desktop logs and hooks
# ---------------------------------------------------------------------------

Add-Heading '## 4. Claude desktop logs'
$logDirs = @()
$appDataDir = [Environment]::GetFolderPath('ApplicationData')
if ($appDataDir) { $logDirs += (Join-Path $appDataDir 'Claude\logs') }
if ($localAppData) {
    $pk = Join-Path $localAppData 'Packages'
    if (Test-Path -LiteralPath $pk) {
        Get-ChildItem -LiteralPath $pk -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
            $logDirs += (Join-Path $_.FullName 'LocalCache\Roaming\Claude\logs')
        }
    }
}
$keywordPattern = 'notif|permission|waiting|needs input|AskUserQuestion|approval|toast|result'
foreach ($d in $logDirs) {
    Add-Line ('- `' + (Hide-Profile $d) + '` exists=' + (Test-Path -LiteralPath $d))
    if (-not (Test-Path -LiteralPath $d)) { continue }
    Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 15 | ForEach-Object {
        $count = 0
        try { $count = @(Select-String -LiteralPath $_.FullName -Pattern $keywordPattern -ErrorAction Stop).Count } catch { }
        Add-Line ('  - `' + $_.Name + '` ' + $_.Length + ' bytes, modified ' + $_.LastWriteTime.ToString('s') + ', keyword lines: ' + $count)
        if ($IncludeLogExcerpts -and $count -gt 0) {
            $hits = Select-String -LiteralPath $_.FullName -Pattern $keywordPattern -ErrorAction SilentlyContinue | Select-Object -Last 20
            foreach ($mm in $hits) {
                # Mask quoted strings, paths, numbers and long words, keep the log's fixed vocabulary.
                $t = $mm.Line -replace '"[^"]*"', '"<s>"' -replace "'[^']*'", "'<s>'" -replace '[A-Za-z]:\\[^\s]+', '<path>' -replace '/[^\s]+/[^\s]+', '<path>' -replace '\b[0-9a-f\-]{16,}\b', '<id>'
                if ($t.Length -gt 240) { $t = $t.Substring(0, 240) + '...' }
                Add-Line ('    - `' + $t + '`')
            }
        }
    }
}

$claudeSettings = Join-Path $ProfilePath '.claude\settings.json'
if (Test-Path -LiteralPath $claudeSettings) {
    try {
        $cs = Get-Content -LiteralPath $claudeSettings -Raw | ConvertFrom-Json
        Add-Line ('- ~/.claude/settings.json keys: ' + (($cs.PSObject.Properties | ForEach-Object { '`' + $_.Name + '`' }) -join ', '))
    } catch { Add-Line '- ~/.claude/settings.json present but unreadable' }
}

Set-Content -LiteralPath $ReportPath -Value $Report -Encoding UTF8
Write-Host ('Static report written to ' + $ReportPath)

# ---------------------------------------------------------------------------
# 5. Optional live timeline
# ---------------------------------------------------------------------------

if ($WatchSeconds -gt 0) {
    $timelinePath = Join-Path $OutDir 'timeline.md'
    $timeline = New-Object System.Collections.Generic.List[string]
    $timeline.Add('# Live timeline (content-free record signatures)')
    $timeline.Add('')
    $timeline.Add('Columns: local time | source | file id | signature')
    $timeline.Add('')

    # Track every rollout/audit file modified in the last day plus any created during the watch.
    $offsets = @{}
    function Get-WatchedFiles {
        $files = @()
        $sd = Join-Path $CodexHome 'sessions'
        if (Test-Path -LiteralPath $sd) {
            $files += Get-ChildItem -LiteralPath $sd -Recurse -File -Filter '*.jsonl' -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-1) } | ForEach-Object { @{ Path = $_.FullName; Source = 'codex' } }
        }
        foreach ($r in $roots) {
            if (Test-Path -LiteralPath $r) {
                $files += Get-ChildItem -LiteralPath $r -Recurse -File -Filter 'audit.jsonl' -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-1) } | ForEach-Object { @{ Path = $_.FullName; Source = 'cowork' } }
                $files += Get-ChildItem -LiteralPath $r -Recurse -Depth 2 -File -Filter 'local_*.json' -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-1) } | ForEach-Object { @{ Path = $_.FullName; Source = 'cowork-meta' } }
            }
        }
        return $files
    }

    foreach ($f in (Get-WatchedFiles)) {
        try { $offsets[$f.Path] = (Get-Item -LiteralPath $f.Path).Length } catch { }
    }
    $metaStamps = @{}

    Write-Host ('Watching for ' + $WatchSeconds + ' s. Work through the scenarios in docs/recon/README.md. Press Ctrl+C to stop early.')
    $deadline = (Get-Date).AddSeconds($WatchSeconds)
    try {
        while ((Get-Date) -lt $deadline) {
            foreach ($f in (Get-WatchedFiles)) {
                $path = $f.Path
                $id = Get-ShortId $path
                try { $info = Get-Item -LiteralPath $path -ErrorAction Stop } catch { continue }

                if ($f.Source -eq 'cowork-meta') {
                    if (-not $metaStamps.ContainsKey($path)) { $metaStamps[$path] = $info.LastWriteTimeUtc; continue }
                    if ($metaStamps[$path] -ne $info.LastWriteTimeUtc) {
                        $metaStamps[$path] = $info.LastWriteTimeUtc
                        $keys = ''
                        try {
                            $o = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                            $keys = ((Protect-Value $o '') | ConvertTo-Json -Depth 5 -Compress)
                        } catch { $keys = '<unreadable>' }
                        $timeline.Add((Get-Date -Format 'HH:mm:ss') + ' | cowork-meta | ' + $id + ' | metadata changed: `' + $keys + '`')
                    }
                    continue
                }

                $start = 0
                if ($offsets.ContainsKey($path)) { $start = $offsets[$path] }
                if ($info.Length -lt $start) { $start = 0; $timeline.Add((Get-Date -Format 'HH:mm:ss') + ' | ' + $f.Source + ' | ' + $id + ' | <file truncated or replaced>') }
                if ($info.Length -eq $start) { continue }

                try {
                    $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
                    try {
                        $fs.Seek($start, 'Begin') | Out-Null
                        $len = [int]($info.Length - $start)
                        $buf = New-Object byte[] $len
                        $read = $fs.Read($buf, 0, $len)
                    } finally { $fs.Dispose() }
                    $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
                    # Only consume complete lines; keep a partial trailing line for the next pass.
                    $lastNl = $text.LastIndexOf("`n")
                    if ($lastNl -lt 0) { continue }
                    $complete = $text.Substring(0, $lastNl + 1)
                    $offsets[$path] = $start + [System.Text.Encoding]::UTF8.GetByteCount($complete)
                    foreach ($line in ($complete -split "`r?`n" | Where-Object { $_ -ne '' })) {
                        $timeline.Add((Get-Date -Format 'HH:mm:ss') + ' | ' + $f.Source + ' | ' + $id + ' | `' + (Get-RecordSignature $line) + '`')
                    }
                } catch { }
            }
            Set-Content -LiteralPath $timelinePath -Value $timeline -Encoding UTF8
            Start-Sleep -Seconds 2
        }
    } finally {
        Set-Content -LiteralPath $timelinePath -Value $timeline -Encoding UTF8
        Write-Host ('Timeline written to ' + $timelinePath)
    }
}

Write-Host ''
Write-Host 'Done. Please review the output folder before sharing it:'
Write-Host ('  ' + $OutDir)
