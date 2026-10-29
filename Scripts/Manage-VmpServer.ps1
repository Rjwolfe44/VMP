#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Start','Stop','Restart','Status','Backup')][string]$Action = 'Status',
    [string]$ServerRoot = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'VMP Server'),
    [string]$TaskName = 'VMP Train Career'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ServerRoot = [IO.Path]::GetFullPath($ServerRoot)
$serverExe = Join-Path $ServerRoot 'Server.exe'
if (-not (Test-Path -LiteralPath $serverExe)) { throw "VMP server not found: $serverExe" }
function Get-VmpProcess {
    @(Get-CimInstance Win32_Process -Filter "Name='Server.exe'" | Where-Object { $_.ExecutablePath -eq $serverExe })
}
function Stop-Vmp {
    $running = @(Get-VmpProcess)
    if (-not $running.Count) { return }
    [IO.File]::WriteAllText((Join-Path $ServerRoot 'shutdown.request'), 'Save and stop')
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (@(Get-VmpProcess).Count -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (@(Get-VmpProcess).Count) { throw 'VMP has not finished saving. Inspect logs; it has not been force-killed.' }
}
function Start-Vmp {
    if (@(Get-VmpProcess).Count) { return }
    $request = Join-Path $ServerRoot 'shutdown.request'
    if (Test-Path -LiteralPath $request) { Remove-Item -LiteralPath $request -Force }
    Start-ScheduledTask -TaskName $TaskName
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not @(Get-VmpProcess).Count -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (-not @(Get-VmpProcess).Count) { throw 'VMP did not start. Inspect Task Scheduler and the server logs.' }
}
if ($Action -eq 'Stop' -or $Action -eq 'Restart') { Stop-Vmp }
if ($Action -eq 'Start' -or $Action -eq 'Restart') { Start-Vmp }
if ($Action -eq 'Backup') {
    $wasRunning = @(Get-VmpProcess).Count -gt 0
    try {
        Stop-Vmp
        $backupDir = Join-Path $ServerRoot 'Backups'
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $backup = Join-Path $backupDir ('Universe-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.zip')
        Compress-Archive -LiteralPath (Join-Path $ServerRoot 'Universe'),(Join-Path $ServerRoot 'Config') -DestinationPath $backup
        Write-Host "Saved backup: $backup"
    }
    finally { if ($wasRunning) { Start-Vmp } }
}
$running = @(Get-VmpProcess)
[pscustomobject]@{ Running = ($running.Count -gt 0); ProcessId = @($running | ForEach-Object { $_.ProcessId }); ServerRoot = $ServerRoot; Task = (Get-ScheduledTask -TaskName $TaskName).State } | ConvertTo-Json
