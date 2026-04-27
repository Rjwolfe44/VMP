<#
Publishes a VMP GitHub release using the same local gh-CLI asset pattern as vladmod.

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
$buildScript = Join-Path $workspaceRoot 'build-and-deploy.ps1'
$clientAssembly = Join-Path $repoRoot 'LmpClient\Properties\AssemblyInfo.cs'
$serverAssembly = Join-Path $repoRoot 'Server\Properties\AssemblyInfo.cs'
$clientZip = Join-Path $workspaceRoot 'DIST\VladMultiplayer-client.zip'

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

    $content = Get-Content -Raw -Path $Path
    $content = $content -replace 'AssemblyVersion\("[^"]+"\)', ('AssemblyVersion("' + $Version + '")')
    $content = $content -replace 'AssemblyFileVersion\("[^"]+"\)', ('AssemblyFileVersion("' + $Version + '")')
    $content = $content -replace 'AssemblyInformationalVersion\("[^"]+"\)', ('AssemblyInformationalVersion("' + $Version + '-compiled")')
    Set-Content -Path $Path -Value $content -Encoding UTF8
}

Push-Location $repoRoot
try {
    Invoke-Step "Updating AssemblyInfo.cs files to $Version" {
        Set-AssemblyVersionInFile $clientAssembly
        Set-AssemblyVersionInFile $serverAssembly
    }

    if (-not $SkipBuild) {
        Invoke-Step 'Building, deploying, and packaging client zip' {
            & $buildScript
            if ($LASTEXITCODE -ne 0) { throw "Build script failed with exit code $LASTEXITCODE" }
        }
    }

    if (-not (Test-Path $clientZip)) {
        throw "Release asset missing: $clientZip. Run build-and-deploy.ps1 first."
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

            gh release create $tag `
                "$clientZip#VladMultiplayer-client.zip" `
                --repo 'Rjwolfe44/VMP' `
                --title "VMP $tag" `
                --generate-notes
        }
    }

    Write-Host "`nRelease flow complete for $tag" -ForegroundColor Green
}
finally {
    Pop-Location
}