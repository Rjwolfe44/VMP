<#
Runs the KerbalKonstructsSettings merge scenario against a local VMP server folder.

  powershell -File Tools\VmpScenarioClient\run-scenario.ps1 -ServerDir <published or built Server folder> [-Port 18801] [-AdminPort 18991] [-Script merge|kkshare]

-Script merge (default) replays the merge of launch site state. -Script kkshare replays shared Kerbal Konstructs statics
(docs/kk-live-sharing.md): A shares an edited launch site, an old client uploads in between, C joins late.

The folder's Config, Universe, logs and ClientLogs are wiped first, so use a scratch copy, never a real server.
Needs the SDK 10 dotnet (Server and client are net10): set DOTNET_ROOT / PATH before, or pass -DotnetRoot.
Writes a seeded Universe\Scenarios\KerbalKonstructsSettings.txt, starts Server.dll (ModControl off, no master server,
no password), runs the client, prints the "Merged scenario" server log lines, and stops the server by PID.
Exit code: the client's (0 = every step PASS).
#>
param(
    [Parameter(Mandatory = $true)][string]$ServerDir,
    [int]$Port = 18801,
    [int]$AdminPort = 18991,
    [int]$WebPort = 18992,
    [int]$SaveWaitSeconds = 60,
    [ValidateSet('merge', 'kkshare')][string]$Script = 'merge',
    [string]$DotnetRoot = $(if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Split-Path (Get-Command dotnet).Source })
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = $DotnetRoot
$env:PATH = "$DotnetRoot;$env:PATH"
$dotnet = Join-Path $DotnetRoot 'dotnet.exe'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$client = Join-Path $PSScriptRoot 'bin\Release\net10.0\VmpScenarioClient.dll'
if (-not (Test-Path $client)) { & $dotnet build (Join-Path $PSScriptRoot 'VmpScenarioClient.csproj') -c Release | Out-Null }
$ServerDir = (Resolve-Path $ServerDir).Path
if (-not (Test-Path (Join-Path $ServerDir 'Server.dll'))) { throw "No Server.dll in $ServerDir" }

foreach ($d in 'Config', 'Universe', 'logs', 'ClientLogs', 'Backups') { Remove-Item (Join-Path $ServerDir $d) -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force (Join-Path $ServerDir 'Config') | Out-Null
$settings = [ordered]@{
    general      = @{ ServerName = 'KK scenario test'; ModControl = $false; Password = ''; MaxPlayers = 10 }
    connection   = @{ Port = $Port; ListenAddress = '127.0.0.1'; Upnp = $false }
    adminApi     = @{ EnableAdminApi = $true; Port = $AdminPort }
    website      = @{ EnableWebsite = $true; Port = $WebPort }
    masterServer = @{ RegisterWithMasterServer = $false }
    interval     = @{ BackupIntervalMs = 5000; ArchiveBackupIntervalMinutes = 0 }
    log          = @{ LogLevel = 'Debug' }
}
($settings | ConvertTo-Json -Depth 4) | Set-Content (Join-Path $ServerDir 'Config\vmp-server.json') -Encoding utf8

$scenarioDir = Join-Path $ServerDir 'Universe\Scenarios'
New-Item -ItemType Directory -Force $scenarioDir | Out-Null
$scenarioFile = Join-Path $scenarioDir 'KerbalKonstructsSettings.txt'
& $dotnet $client seed $scenarioFile | Out-Null

$out = Join-Path $ServerDir 'server-stdout.txt'
$err = Join-Path $ServerDir 'server-stderr.txt'
$server = Start-Process -FilePath $dotnet -ArgumentList 'Server.dll' -WorkingDirectory $ServerDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
Write-Host "server pid $($server.Id) on UDP $Port (admin $AdminPort), dir $ServerDir"
$exit = 1
try {
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $server.HasExited; $i++) {
        Start-Sleep -Milliseconds 500
        if ((Test-Path $out) -and (Select-String -Path $out -Pattern 'Started|Server started|Listening|ready' -Quiet)) { $ready = $true; break }
        if ($i -ge 16) { $ready = $true; break }   # fall back after ~8 s
    }
    if ($server.HasExited) { Get-Content $out -Tail 20; throw "server exited early (code $($server.ExitCode))" }
    $command = if ($Script -eq 'kkshare') { 'run-kkshare' } else { 'run' }
    & $dotnet $client $command 127.0.0.1 $Port $scenarioFile $SaveWaitSeconds
    $exit = $LASTEXITCODE
    Write-Host '--- server log: scenario lines ---'
    Select-String -Path $out -Pattern 'Merged scenario|stored as is|Saving \d+ scenario|rejected|Error|\[KKShare\]' | ForEach-Object { $_.Line }
}
finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Write-Host "server pid $($server.Id) stopped"
}
exit $exit
