#Requires -Version 5.1
# Run on the hosting PC as an administrator. Only a new server folder is accepted.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$ExpectedSha256,
    [string]$ServerRoot = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'VMP Server'),
    # Local network range that may reach the server (CIDR). Set it to the subnet of the hosting PC.
    [string]$LanSubnet = '192.168.1.0/24'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ServerRoot = [IO.Path]::GetFullPath($ServerRoot)
if (Test-Path -LiteralPath $ServerRoot) { throw 'The server folder already exists. Use the management script to stop/save it and deploy an update separately.' }
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Uploaded server archive checksum mismatch' }
Expand-Archive -LiteralPath $Archive -DestinationPath $ServerRoot
$serverExe = Join-Path $ServerRoot 'Server.exe'
if (-not (Test-Path -LiteralPath $serverExe)) { throw 'Server.exe missing from archive' }
$settingsJson = Join-Path $ServerRoot 'Config\vmp-server.json'
if (Test-Path -LiteralPath $settingsJson) {
    # Newer servers keep every setting in one file.
    $password = (Get-Content -LiteralPath $settingsJson -Raw | ConvertFrom-Json).general.Password
}
else {
    [xml]$general = Get-Content -LiteralPath (Join-Path $ServerRoot 'Config\GeneralSettings.xml') -Raw
    $password = $general.DocumentElement.Password
}
if (-not $password) { throw 'Public/overlay hosting requires a server password' }

$ruleName = 'VMP-Train-UDP-8800'
if (-not (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName 'VMP Train Career UDP 8800 (LAN and ZeroTier)' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 8800 -Program $serverExe -Profile Any -RemoteAddress '127.0.0.1',$LanSubnet,'10.37.239.0/24' | Out-Null
}
$taskName = 'VMP Train Career'
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType S4U -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$action = New-ScheduledTaskAction -Execute $serverExe -WorkingDirectory $ServerRoot
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Principal $principal -Settings $settings -Description 'Private VMP Career server. Stop with Manage-VmpServer.ps1 so the universe is saved.' | Out-Null
$backupAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $ServerRoot 'Manage-VmpServer.ps1') + '" -Action Backup') -WorkingDirectory $ServerRoot
$backupSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName 'VMP Train Career Backup' -Action $backupAction -Trigger (New-ScheduledTaskTrigger -Daily -At '4:00AM') -Principal $principal -Settings $backupSettings -Description 'Save, archive Universe and Config, and resume the VMP server daily. Archives are kept in Backups.' | Out-Null

foreach ($operation in @('Start','Stop','Restart','Status','Backup')) {
    $launcher = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Manage-VmpServer.ps1`" -Action $operation`r`npause`r`n"
    [IO.File]::WriteAllText((Join-Path $ServerRoot ($operation + ' VMP.bat')), $launcher, [Text.Encoding]::ASCII)
}
Set-Service -Name playitd -StartupType Automatic
Start-Service -Name playitd
& (Join-Path $ServerRoot 'Manage-VmpServer.ps1') -Action Start -ServerRoot $ServerRoot
Write-Host "Installed private VMP Career server: $ServerRoot"
