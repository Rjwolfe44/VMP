#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release',
    [string]$Dotnet = $(if ($env:DOTNET_EXE) { $env:DOTNET_EXE } elseif (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'VMPBuild\dotnet\dotnet.exe')) { Join-Path $env:LOCALAPPDATA 'VMPBuild\dotnet\dotnet.exe' } else { 'dotnet' }),
    [string]$Runtime = '',
    [switch]$SelfContained
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $repoRoot "Build\$Configuration"

function Reset-BuildFolder([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build cleanup must stay under $buildRoot"
    }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
    New-Item -ItemType Directory -Path $absolute -Force | Out-Null
}

$sdkVersion = & $Dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($sdkVersion.Split('.')[0]) -lt 10) { throw 'Building the server requires the .NET 10 SDK. Pass -Dotnet or set DOTNET_EXE if it is not on PATH.' }
Push-Location $repoRoot
try {
    & $Dotnet build 'LmpClient\LmpClient.csproj' -c $Configuration --nologo -v quiet /p:RunPostBuildEvent=Never /p:PostBuildEvent=
    if ($LASTEXITCODE -ne 0) { throw 'VMP client build failed' }
    $clientOut = Join-Path $buildRoot 'Client'
    Reset-BuildFolder $clientOut
    foreach ($folder in @('Plugins','Button','Localization','PartSync','Icons','Flags','MasterServersList','LoadingScreens')) {
        New-Item -ItemType Directory -Path (Join-Path $clientOut $folder) -Force | Out-Null
    }
    $plugins = Join-Path $clientOut 'Plugins'
    foreach ($source in @((Join-Path $repoRoot 'External\Dependencies'), (Join-Path $repoRoot "LmpClient\bin\$Configuration"))) {
        Get-ChildItem -LiteralPath $source -File -Filter '*.dll' | Where-Object { $_.Name -notin @('DotNetZip.dll','Ionic.Zip.dll') } | Copy-Item -Destination $plugins -Force
    }
    $copies = @{
        'LmpClient\Resources\*.png' = 'Button'
        'LmpClient\Localization\XML\*' = 'Localization'
        'LmpClient\ModuleStore\XML\*' = 'PartSync'
        'LmpClient\Resources\Icons\*' = 'Icons'
        'LmpClient\Resources\Flags\*' = 'Flags'
        'MasterServersList\*' = 'MasterServersList'
        'LmpClient\Resources\LoadingScreens\*' = 'LoadingScreens'
    }
    foreach ($source in $copies.Keys) {
        if (-not (Test-Path -Path (Join-Path $repoRoot $source))) {
            if ($copies[$source] -eq 'LoadingScreens') { continue }
            throw "Missing client content: $source"
        }
        Copy-Item -Path (Join-Path $repoRoot $source) -Destination (Join-Path $clientOut $copies[$source]) -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'VladMultiplayer.version') -Destination $clientOut
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $clientOut 'LICENSE.txt')
    $serverOut = Join-Path $buildRoot 'Server'
    Reset-BuildFolder $serverOut
    $publishArgs = @('publish','Server\Server.csproj','-c',$Configuration,'-o',$serverOut,'--nologo','-v','quiet')
    if ($Runtime) { $publishArgs += @('-r',$Runtime,'--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant()) }
    & $Dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'VMP server publish failed' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'MasterServersList') -Destination $serverOut -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $serverOut 'LICENSE.txt')
    Write-Host "Built client and server: $buildRoot"
}
finally { Pop-Location }
