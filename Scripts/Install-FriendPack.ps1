#Requires -Version 5.1
[CmdletBinding()]
param([string]$KspRoot, [string]$PlayerName)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $KspRoot) { $KspRoot = Read-Host 'Full path to your Kerbal Space Program folder' }
if (-not $PlayerName) { $PlayerName = Read-Host 'Your multiplayer name (letters, numbers, underscore or dash; max 15)' }
if ($PlayerName -notmatch '^[A-Za-z0-9_-]{1,15}$' -or $PlayerName -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') { throw 'Choose a valid multiplayer name. Each player must use a different name.' }
$KspRoot = [IO.Path]::GetFullPath($KspRoot.Trim('"'))
if (-not (Test-Path -LiteralPath (Join-Path $KspRoot 'KSP_x64.exe'))) { throw 'This is not a KSP installation folder' }
if (Get-Process -Name KSP,KSP_x64 -ErrorAction SilentlyContinue) { throw 'Close KSP before installing this pack' }
$gameData = Join-Path $KspRoot 'GameData'
$sourceData = Join-Path $PSScriptRoot 'GameData'
if ($gameData -eq $sourceData) { throw 'Extract the pack to a separate folder first, then run this installer' }
if (-not (Test-Path -LiteralPath $sourceData)) { throw 'GameData payload missing. Fully extract the pack before running the installer.' }
$buildId = Get-Content -LiteralPath (Join-Path $KspRoot 'buildID64.txt') -Raw
if ($buildId -notmatch 'build id\s*=\s*03190') { throw 'This pack requires KSP 1.12.5 (build 3190)' }
foreach ($expansion in @('MakingHistory','Serenity')) {
    if (-not (Test-Path -LiteralPath (Join-Path $gameData ('SquadExpansion\' + $expansion)))) { throw "Install your $expansion DLC before using this pack" }
}
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
function Assert-PackFiles([string]$Root, [bool]$Installed = $false) {
    $expectedFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.Files) {
        if ($file.Path -match '^(Squad|SquadExpansion)(/|\\)' -or -not $expectedFiles.Add($file.Path.Replace('\','/'))) { throw 'Invalid or duplicate manifest entry' }
        $path = [IO.Path]::GetFullPath((Join-Path $Root $file.Path))
        if (-not $path.StartsWith($Root.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path' }
        if ($Installed -and $file.Path -eq 'VladMultiplayer/Data/settings.xml') { continue }
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) { throw "Missing or damaged mod file: $($file.Path)" }
    }
    foreach ($actual in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        $relative = $actual.FullName.Substring($Root.Length + 1).Replace('\','/')
        if ($Installed -and $relative -match '^(Squad|SquadExpansion)/') { continue }
        if (-not $expectedFiles.Contains($relative)) { throw "Unexpected mod file outside the manifest: $relative" }
    }
}
# Check the complete extracted payload before changing the player's installation.
Assert-PackFiles $sourceData
$backupRoot = Join-Path $KspRoot ('VMP Backups\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '\GameData')
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($old in Get-ChildItem -LiteralPath $gameData -Force) {
    if ($old.Name -in @('Squad','SquadExpansion')) { continue }
    $oldPath = [IO.Path]::GetFullPath($old.FullName)
    if (-not $oldPath.StartsWith($gameData + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Old mod backup path escaped GameData' }
    Move-Item -LiteralPath $oldPath -Destination $backupRoot
}
try {
    Copy-Item -Path (Join-Path $sourceData '*') -Destination $gameData -Recurse -Force
    $settingsPath = Join-Path $gameData 'VladMultiplayer\Data\settings.xml'
    [xml]$settings = Get-Content -LiteralPath $settingsPath -Raw
    $settings.DocumentElement.PlayerName = $PlayerName
    $settings.Save($settingsPath)
    Assert-PackFiles $gameData $true
}
catch {
    Write-Warning "Installation did not finish. Your previous mods are backed up at $backupRoot."
    throw
}
Write-Host "Installed and verified $(@($manifest.Files).Count) files. Your previous mods are in $backupRoot"
Write-Host 'Start KSP_x64.exe, open VMP, and choose Train - Playit. ZeroTier is also saved if you prefer it.'
