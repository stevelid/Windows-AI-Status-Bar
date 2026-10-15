<#
.SYNOPSIS
Installs the shared status-bar build and enables encrypted live visibility between these PCs.
#>
[CmdletBinding()]
param([string]$DeviceLabel = $env:COMPUTERNAME)

$ErrorActionPreference = 'Stop'
$bundle = $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $bundle 'release.json') -Raw | ConvertFrom-Json
$sourceExe = Join-Path $bundle 'AIStatusBar.exe'
if ((Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash -ne $manifest.sha256) {
    throw 'The app has not finished syncing, or its checksum is wrong. Let Drive finish and try again.'
}
if ($manifest.revision -notmatch '^[a-f0-9]{7,40}$') { throw 'Invalid release identity.' }
$installDirectory = Join-Path $env:LOCALAPPDATA ('WindowsAIStatusBar\releases\' + $manifest.revision)
$destinationExe = Join-Path $installDirectory 'AIStatusBar.exe'
$feedDirectory = Join-Path $bundle 'feed'
$dataDirectory = Join-Path $env:APPDATA 'WindowsAIStatusBar'
$settingsPath = Join-Path $dataDirectory 'settings.json'

# Read the current settings after stopping only this monitor, so its shutdown cannot overwrite the change.
$running = @(Get-Process AIStatusBar -ErrorAction SilentlyContinue)
foreach ($statusBar in $running) { Stop-Process -Id $statusBar.Id }
New-Item -ItemType Directory -Path $installDirectory, $dataDirectory, $feedDirectory -Force | Out-Null
if (!(Test-Path -LiteralPath $destinationExe) -or
    (Get-FileHash -LiteralPath $destinationExe -Algorithm SHA256).Hash -ne $manifest.sha256) {
    Copy-Item -LiteralPath $sourceExe -Destination $destinationExe -Force
}
if (Test-Path -LiteralPath $settingsPath) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
} else { $settings = [pscustomobject]@{} }
$settings | Add-Member -MemberType NoteProperty -Name TaskSyncFolder -Value $feedDirectory -Force
$settings | Add-Member -MemberType NoteProperty -Name TaskSyncLabel -Value $DeviceLabel -Force
$pendingSettings = $settingsPath + '.sync-pending'
[IO.File]::WriteAllText($pendingSettings, ($settings | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $pendingSettings -Destination $settingsPath -Force
Start-Process -FilePath $destinationExe -WorkingDirectory $installDirectory -WindowStyle Hidden
Write-Output ('Installed status bar ' + $manifest.revision + '. Live sharing is enabled. Leave Drive running on both PCs.')
