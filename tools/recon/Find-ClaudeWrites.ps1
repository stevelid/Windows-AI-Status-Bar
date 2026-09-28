<#
.SYNOPSIS
    Finds where Claude Desktop writes files while a Cowork task runs.

.DESCRIPTION
    The first recon found no live Cowork activity in local-agent-mode-sessions, so Cowork may now
    store tasks elsewhere. This script notes the start time, waits while you use Cowork, then
    lists every file under Claude's folders that changed in that window.

    Privacy: folder and file names are shown only when they look like identifiers (letters,
    digits, dot, dash, underscore, no spaces). Anything else becomes <name>, GUIDs become <uuid>,
    and nothing under uploads/outputs is named. For changed .jsonl files it adds content-free
    record signatures (record types only); for small .json files, top-level key names only.
    Nothing is uploaded. Please review the output before sharing it.

.PARAMETER Seconds
    How long to wait while you use Cowork (default 300).

.PARAMETER Roots
    Override the folders to scan (for testing).

.PARAMETER OutFile
    Where to write the report (default: a timestamped file on the Desktop).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\recon\Find-ClaudeWrites.ps1
#>
[CmdletBinding()]
param(
    [int]$Seconds = 300,
    [string[]]$Roots,
    [string]$OutFile
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'

$profilePath = [Environment]::GetFolderPath('UserProfile')
$appData = [Environment]::GetFolderPath('ApplicationData')
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')

if (-not $Roots) {
    $Roots = @()
    if ($appData) { $Roots += (Join-Path $appData 'Claude') }
    if ($localAppData) {
        $Roots += (Join-Path $localAppData 'AnthropicClaude')
        $Roots += (Join-Path $localAppData 'Claude')
        $pk = Join-Path $localAppData 'Packages'
        if (Test-Path -LiteralPath $pk) {
            Get-ChildItem -LiteralPath $pk -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
                ForEach-Object { $Roots += $_.FullName }
        }
    }
    if ($profilePath) { $Roots += (Join-Path $profilePath '.claude') }
}
$Roots = @($Roots | Where-Object { $_ -and (Test-Path -LiteralPath $_) })

if (-not $OutFile) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if (-not $desktop) { $desktop = (Get-Location).Path }
    $OutFile = Join-Path $desktop ('claude-writes-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.md')
}

# Browser caches change constantly and say nothing about tasks.
$NoiseDirs = @('Cache', 'Code Cache', 'GPUCache', 'DawnCache', 'DawnGraphiteCache', 'DawnWebGPUCache',
    'GrShaderCache', 'ShaderCache', 'Crashpad', 'blob_storage', 'Service Worker')
# Folders holding the user's own files: never name anything inside them.
$PrivateDirs = @('uploads', 'uploads-tmp', 'outputs')

function Get-SafeSegment([string]$segment) {
    if ($segment -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') { return '<uuid>' }
    if ($segment -match '[0-9a-fA-F\-]{16,}') { return ($segment -replace '[0-9a-fA-F\-]{16,}', '<id>') }
    if ($segment -match '^[A-Za-z0-9_.\-]{1,48}$') { return $segment }
    return '<name>'
}

# Returns the redacted relative path, or $null when the file sits in a noise folder.
function Get-SafeRelativePath([string]$root, [string]$fullPath) {
    $rel = $fullPath.Substring($root.Length).TrimStart('\', '/')
    $parts = $rel -split '[\\/]'
    $out = @()
    foreach ($p in $parts) {
        if ($NoiseDirs -contains $p) { return $null }
        $out += (Get-SafeSegment $p)
        if ($PrivateDirs -contains $p) { $out += '<private>'; break }
    }
    return ($out -join '/')
}

function Get-RootLabel([string]$root) {
    if ($localAppData -and $root.StartsWith($localAppData, [StringComparison]::OrdinalIgnoreCase)) { return '%LOCALAPPDATA%' + $root.Substring($localAppData.Length) }
    if ($appData -and $root.StartsWith($appData, [StringComparison]::OrdinalIgnoreCase)) { return '%APPDATA%' + $root.Substring($appData.Length) }
    if ($profilePath -and $root.StartsWith($profilePath, [StringComparison]::OrdinalIgnoreCase)) { return '~' + $root.Substring($profilePath.Length) }
    return $root
}

# Content-free signature of one JSON line: enum-like type/subtype/status/tool values only.
function Get-Signature([string]$line) {
    if (-not $line -or -not $line.TrimStart().StartsWith('{')) { return '<non-json>' }
    try { $o = $line | ConvertFrom-Json -ErrorAction Stop } catch { return '<bad-json>' }
    $parts = @()
    foreach ($k in 'type', 'subtype', 'status', 'state', 'tool_name', 'kind', 'event') {
        $p = $o.PSObject.Properties[$k]
        if ($p -and $p.Value -is [string] -and $p.Value -match '^[A-Za-z0-9_.:\-]{1,64}$') { $parts += ($k + '=' + $p.Value) }
    }
    if ($parts.Count -eq 0) {
        $keys = $o.PSObject.Properties | Select-Object -First 8 | ForEach-Object { Get-SafeSegment $_.Name }
        return 'keys: ' + ($keys -join ',')
    }
    return ($parts -join ' ')
}

function Get-TailLines([string]$path, [int]$count) {
    try {
        $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite, Delete')
        try {
            $max = [int][Math]::Min($fs.Length, 2MB)
            $fs.Seek(-$max, 'End') | Out-Null
            $buf = New-Object byte[] $max
            $n = $fs.Read($buf, 0, $max)
        } finally { $fs.Dispose() }
        $lines = [System.Text.Encoding]::UTF8.GetString($buf, 0, $n) -split "`r?`n" | Where-Object { $_ -ne '' }
        return @($lines | Select-Object -Last $count)
    } catch { return @() }
}

$start = Get-Date
Write-Host ('Watching ' + $Roots.Count + ' Claude folder(s) for ' + $Seconds + ' s.')
Write-Host 'Now use Cowork: start a short task and let it finish. If you can, also trigger a permission prompt.'
for ($left = $Seconds; $left -gt 0; $left -= 10) {
    Write-Host ('  ' + $left + ' s remaining...')
    Start-Sleep -Seconds ([Math]::Min(10, $left))
}

$report = New-Object System.Collections.Generic.List[string]
$report.Add('# Claude write locations')
$report.Add('')
$report.Add('Window: ' + $start.ToString('HH:mm:ss') + ' to ' + (Get-Date).ToString('HH:mm:ss') + ' (' + $Seconds + ' s). Names redacted; review before sharing.')

foreach ($root in $Roots) {
    $report.Add('')
    $report.Add('## `' + (Get-RootLabel $root) + '`')
    $report.Add('')

    $changed = @()
    foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue)) {
        if ($f.LastWriteTime -lt $start) { continue }
        $safe = Get-SafeRelativePath $root $f.FullName
        if ($null -ne $safe) { $changed += [pscustomobject]@{ File = $f; Safe = $safe } }
    }

    if ($changed.Count -eq 0) { $report.Add('No changed files.'); continue }

    foreach ($c in ($changed | Sort-Object { $_.File.LastWriteTime })) {
        $f = $c.File
        $report.Add('- `' + $c.Safe + '` - ' + $f.Length + ' bytes, last write ' + $f.LastWriteTime.ToString('HH:mm:ss'))
        if ($c.Safe -match '<private>') { continue }

        if ($f.Extension -eq '.jsonl') {
            foreach ($line in (Get-TailLines $f.FullName 25)) { $report.Add('  - `' + (Get-Signature $line) + '`') }
        } elseif ($f.Extension -eq '.json' -and $f.Length -lt 4MB) {
            try {
                $o = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
                if ($o -is [System.Management.Automation.PSCustomObject]) {
                    $keys = $o.PSObject.Properties | Select-Object -First 40 | ForEach-Object { Get-SafeSegment $_.Name }
                    $report.Add('  - keys: ' + ($keys -join ', '))
                }
            } catch { }
        }
    }
}

Set-Content -LiteralPath $OutFile -Value $report -Encoding UTF8
Write-Host ''
Write-Host ('Report written to ' + $OutFile)
Write-Host 'Please review it before sharing.'
