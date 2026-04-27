<#
Publishes a VMP GitHub release using the same local gh-CLI asset pattern as vladmod.
Uploads only the public client and dedicated-server zips.

Prerequisites:
- Run `gh auth login` once on this machine.
- Keep secrets in your credential manager or local env only; do not commit .env files.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipBuild,
    [switch]$SkipCommit,
    [switch]$SkipRelease,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$workspaceRoot = Split-Path -Parent $repoRoot
$tag = "v$Version"
$packageScript = Join-Path $repoRoot 'Scripts\PackageKspmpReleaseZips.ps1'
$distDir = Join-Path $workspaceRoot 'DIST'
$clientAssembly = Join-Path $repoRoot 'LmpClient\Properties\AssemblyInfo.cs'
$serverAssembly = Join-Path $repoRoot 'Server\Properties\AssemblyInfo.cs'
$clientZip = Join-Path $distDir 'VladMultiplayer-client.zip'
$serverZip = Join-Path $distDir 'VladMultiplayer-server.zip'

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host "`n==> $Description" -ForegroundColor Cyan
    if ($DryRun) {
        Write-Host '[dry-run] skipped' -ForegroundColor Yellow
        return
    }

    & $Action
}

function Set-AssemblyVersionInFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $content = [System.IO.File]::ReadAllText($Path, $utf8NoBom)
    $content = $content -replace 'AssemblyVersion\("[^"]+"\)', ('AssemblyVersion("' + $Version + '")')
    $content = $content -replace 'AssemblyFileVersion\("[^"]+"\)', ('AssemblyFileVersion("' + $Version + '")')
    $content = $content -replace 'AssemblyInformationalVersion\("[^"]+"\)', ('AssemblyInformationalVersion("' + $Version + '-compiled")')
    [System.IO.File]::WriteAllText($Path, $content, $utf8NoBom)
}

function Copy-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Packaged asset missing: $Source"
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

Push-Location $repoRoot
try {
    Invoke-Step "Updating AssemblyInfo.cs files to $Version" {
        Set-AssemblyVersionInFile $clientAssembly
        Set-AssemblyVersionInFile $serverAssembly
    }

    if (-not $SkipBuild) {
        Invoke-Step 'Building and packaging client/server zips' {
            & $packageScript -Configuration Release -OutputDir $distDir
            if ($LASTEXITCODE -ne 0) { throw "Build script failed with exit code $LASTEXITCODE" }

            Copy-ReleaseAsset `
                -Source (Join-Path $distDir 'VladMultiplayer-Client-Release.zip') `
                -Destination $clientZip
            Copy-ReleaseAsset `
                -Source (Join-Path $distDir 'VladMultiplayer-Server-Release.zip') `
                -Destination $serverZip
        }
    }

    if (-not (Test-Path $clientZip)) {
        throw "Release asset missing: $clientZip. Run build-and-deploy.ps1 first."
    }
    if (-not (Test-Path $serverZip)) {
        throw "Release asset missing: $serverZip. Run build-and-deploy.ps1 first."
    }

    if (-not $SkipCommit) {
        Invoke-Step "Committing and tagging $tag" {
            $existingTag = git tag --list $tag
            if ($existingTag) { throw "Git tag already exists: $tag" }

            git add -A
            $status = git status --porcelain
            if ($status) {
                git commit -m "Release $tag"
            }
            else {
                Write-Host 'No local changes to commit; tagging current HEAD.' -ForegroundColor Yellow
            }

            git tag $tag
            git push origin main
            git push origin $tag
        }
    }

    if (-not $SkipRelease) {
        Invoke-Step "Creating GitHub Release $tag" {
            if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
                throw 'GitHub CLI (gh) is not installed or not on PATH. Install it and run `gh auth login`, then rerun with -SkipCommit if the tag already exists.'
            }

            $assets = @(
                "$clientZip#VladMultiplayer-client.zip",
                "$serverZip#VladMultiplayer-server.zip"
            )

            & gh release view $tag --repo 'Rjwolfe44/VMP' *> $null
            $releaseExists = $LASTEXITCODE -eq 0

            if ($releaseExists) {
                & gh release upload $tag @assets --repo 'Rjwolfe44/VMP' --clobber
            }
            else {
                & gh release create $tag @assets `
                    --repo 'Rjwolfe44/VMP' `
                    --title "VMP $tag" `
                    --generate-notes
            }

            if ($LASTEXITCODE -ne 0) { throw "GitHub release command failed with exit code $LASTEXITCODE" }
        }
    }

    Write-Host "`nRelease flow complete for $tag" -ForegroundColor Green
}
finally {
    Pop-Location
}