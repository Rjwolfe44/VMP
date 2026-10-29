#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KspRoot,
    [Parameter(Mandatory)][string]$SettingsTemplate,
    [Parameter(Mandatory)][string]$OutputZip,
    [string]$Readme,
    [string]$ModList
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Get-Process -Name KSP,KSP_x64 -ErrorAction SilentlyContinue) { throw 'Close KSP before building a consistent, hashed friend pack' }
$dataRoot = [IO.Path]::GetFullPath((Join-Path $KspRoot 'GameData'))
$OutputZip = [IO.Path]::GetFullPath($OutputZip)
$partial = $OutputZip + '.partial'
$files = New-Object 'System.Collections.Generic.List[object]'
$sourceFiles = New-Object 'System.Collections.Generic.List[object]'
foreach ($file in Get-ChildItem -LiteralPath $dataRoot -Recurse -File) {
    $relative = $file.FullName.Substring($dataRoot.Length + 1).Replace('\','/')
    if ($relative -match '^(Squad|SquadExpansion)/|^ModuleManager\.(ConfigCache|ConfigSHA|Physics|TechTree)$|^VladMultiplayer/(Data|Traces|logs)/' -or $relative -match '\.(log|tmp|disabled)$') { continue }
    # ApexGuidance retains a MechJeb2-named settings directory internally.
    # Only standalone conflicting mod roots must be rejected.
    if ($relative -match '^(LunaMultiplayer|LunaCompat|MechJeb2)(/|$)') { throw "Conflicting mod still present: $relative" }
    $files.Add([pscustomobject]@{Path=$relative;Bytes=$file.Length;Sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash})
    $sourceFiles.Add([pscustomobject]@{Path=$relative;Source=$file.FullName})
}
$files.Add([pscustomobject]@{Path='VladMultiplayer/Data/settings.xml';Bytes=(Get-Item -LiteralPath $SettingsTemplate).Length;Sha256=(Get-FileHash -LiteralPath $SettingsTemplate -Algorithm SHA256).Hash})
$sourceFiles.Add([pscustomobject]@{Path='VladMultiplayer/Data/settings.xml';Source=$SettingsTemplate})
$manifest = [ordered]@{Name='VMP Train Career';CreatedUtc=[DateTime]::UtcNow.ToString('o');KspVersion='1.12.5.3190';RequiredDlc=@('MakingHistory','Serenity');Files=$files}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
if (-not (Test-Path -LiteralPath (Split-Path -Parent $OutputZip))) { New-Item -ItemType Directory -Path (Split-Path -Parent $OutputZip) -Force | Out-Null }
$archive = $null
$stream = $null
try {
    $stream = [IO.File]::Open($partial,[IO.FileMode]::Create,[IO.FileAccess]::Write)
    $archive = New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create)
    foreach ($source in $sourceFiles) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$source.Source,('GameData/' + $source.Path),[IO.Compression.CompressionLevel]::Optimal)
    }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $PSScriptRoot 'Install-FriendPack.ps1'),'Install-FriendPack.ps1')
    if ($Readme) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$Readme,'READ ME FIRST.txt') }
    if ($ModList) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$ModList,'VMP-Career-Locked.ckan') }
    foreach ($textEntry in @(
        @{Name='manifest.json';Text=$manifestJson},
        @{Name='Install Friend Pack.bat';Text="@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-FriendPack.ps1`"`r`npause`r`n"}
    )) {
        $writer = New-Object IO.StreamWriter($archive.CreateEntry($textEntry.Name).Open(),(New-Object Text.UTF8Encoding($false)))
        try { $writer.Write($textEntry.Text) } finally { $writer.Dispose() }
    }
    $archive.Dispose(); $archive=$null
    $stream.Dispose(); $stream=$null
    if (Test-Path -LiteralPath $OutputZip) { Remove-Item -LiteralPath $OutputZip -Force }
    Move-Item -LiteralPath $partial -Destination $OutputZip
    [IO.File]::WriteAllText(($OutputZip + '.manifest.json'),$manifestJson,(New-Object Text.UTF8Encoding($false)))
    $hash = Get-FileHash -LiteralPath $OutputZip -Algorithm SHA256
    [IO.File]::WriteAllText(($OutputZip + '.sha256'),($hash.Hash + '  ' + [IO.Path]::GetFileName($OutputZip)))
    [pscustomobject]@{File=$OutputZip;Files=$files.Count;SizeGB=[math]::Round((Get-Item $OutputZip).Length/1GB,2);Sha256=$hash.Hash}
}
finally {
    if ($archive) { $archive.Dispose() }
    if ($stream) { $stream.Dispose() }
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
}
