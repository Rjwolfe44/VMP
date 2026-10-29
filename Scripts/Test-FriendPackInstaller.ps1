#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('VmpFriendPackTest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $pack = Join-Path $testRoot 'pack'
    $game = Join-Path $testRoot 'KSP'
    foreach ($folder in @('pack\GameData\ExampleMod','pack\GameData\VladMultiplayer\Data','KSP\GameData\Squad','KSP\GameData\SquadExpansion\MakingHistory','KSP\GameData\SquadExpansion\Serenity','KSP\GameData\OldMod')) {
        New-Item -ItemType Directory -Path (Join-Path $testRoot $folder) -Force | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $game 'KSP_x64.exe'),'fixture')
    [IO.File]::WriteAllText((Join-Path $game 'buildID64.txt'),'build id = 03190')
    [IO.File]::WriteAllText((Join-Path $game 'GameData\Squad\stock.cfg'),'stock')
    [IO.File]::WriteAllText((Join-Path $game 'GameData\OldMod\old.dll'),'old')
    $payload = Join-Path $pack 'GameData\ExampleMod\plugin.dll'
    [IO.File]::WriteAllText($payload,'new mod')
    [IO.File]::WriteAllText((Join-Path $pack 'GameData\VladMultiplayer\Data\settings.xml'),'<SettingStructure><PlayerName>Player</PlayerName></SettingStructure>')
    $manifest = @{Files=@(@{Path='ExampleMod/plugin.dll';Sha256=(Get-FileHash $payload).Hash},@{Path='VladMultiplayer/Data/settings.xml';Sha256=(Get-FileHash (Join-Path $pack 'GameData\VladMultiplayer\Data\settings.xml')).Hash})} | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $pack 'manifest.json'),$manifest)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-FriendPack.ps1') -Destination $pack
    & (Join-Path $pack 'Install-FriendPack.ps1') -KspRoot $game -PlayerName Friend
    if ((Get-Content (Join-Path $game 'GameData\Squad\stock.cfg') -Raw) -ne 'stock') { throw 'Stock content changed' }
    if (-not (Test-Path (Join-Path $game 'VMP Backups\*\GameData\OldMod\old.dll'))) { throw 'Old mod backup missing' }
    if (([xml](Get-Content (Join-Path $game 'GameData\VladMultiplayer\Data\settings.xml') -Raw)).DocumentElement.PlayerName -ne 'Friend') { throw 'Player name not set' }
    [IO.File]::WriteAllText($payload,'corrupt')
    $rejected = $false
    try { & (Join-Path $pack 'Install-FriendPack.ps1') -KspRoot $game -PlayerName Friend } catch { $rejected = $true }
    if (-not $rejected) { throw 'Corrupt source payload was accepted' }
    if ((Get-Content (Join-Path $game 'GameData\ExampleMod\plugin.dll') -Raw) -ne 'new mod') { throw 'Rejected install modified game files' }
    Write-Output 'PASS: payload hashes, separate player names, stock preservation, old-mod backup, corruption rejection before modification'
}
finally {
    $absolute = [IO.Path]::GetFullPath($testRoot)
    if (-not $absolute.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'VmpFriendPackTest-*') { throw 'Unsafe fixture cleanup path' }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
