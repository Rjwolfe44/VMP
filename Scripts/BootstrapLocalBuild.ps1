#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $KspRoot = "",
    [switch] $SkipRestore,
    [switch] $SkipLegacyNuget,
    [switch] $SkipKspRefs
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$nugetRoot = Join-Path $repoRoot "External\Nuget"
$kspLibRoot = Join-Path $repoRoot "External\KSPLibraries"

function Invoke-DotnetRestore {
    param([string[]] $Targets)

    foreach ($target in $Targets) {
        Write-Host "==> Restoring $target" -ForegroundColor Cyan
        & dotnet restore (Join-Path $repoRoot $target)
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for $target"
        }
    }
}

function Resolve-KspInstall {
    param([string] $PreferredRoot)

    $candidates = @()

    if ($PreferredRoot) {
        $candidates += $PreferredRoot
    }

    if ($env:VMP_KSP_ROOT) {
        $candidates += $env:VMP_KSP_ROOT
    }

    $desktop = [Environment]::GetFolderPath("Desktop")
    $candidates += @(
        (Join-Path $desktop "Kerbal Space Program"),
        "C:\Kerbal Space Program",
        "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program",
        "C:\Program Files\Steam\steamapps\common\Kerbal Space Program",
        "D:\SteamLibrary\steamapps\common\Kerbal Space Program",
        "E:\SteamLibrary\steamapps\common\Kerbal Space Program"
    )

    foreach ($candidate in $candidates | Where-Object { $_ } | Select-Object -Unique) {
        $probe = Join-Path $candidate "KSP_x64_Data\Managed\Assembly-CSharp.dll"
        if (Test-Path -LiteralPath $probe) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "Could not find a KSP install. Pass -KspRoot or set VMP_KSP_ROOT."
}

function Sync-KspReferenceAssemblies {
    param([string] $InstallRoot)

    $managedRoot = Join-Path $InstallRoot "KSP_x64_Data\Managed"
    $dlls = @(
        "Assembly-CSharp.dll",
        "System.dll",
        "System.Xml.dll",
        "UnityEngine.dll",
        "UnityEngine.AnimationModule.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEngine.ImageConversionModule.dll",
        "UnityEngine.IMGUIModule.dll",
        "UnityEngine.InputLegacyModule.dll",
        "UnityEngine.PhysicsModule.dll",
        "UnityEngine.TextRenderingModule.dll",
        "UnityEngine.UI.dll",
        "UnityEngine.UnityWebRequestModule.dll"
    )

    New-Item -ItemType Directory -Path $kspLibRoot -Force | Out-Null

    foreach ($dll in $dlls) {
        Copy-Item -LiteralPath (Join-Path $managedRoot $dll) -Destination (Join-Path $kspLibRoot $dll) -Force
    }

    Write-Host "Copied KSP managed DLLs from $InstallRoot to External\\KSPLibraries" -ForegroundColor Green
}

function Sync-LegacyNugetLayout {
    $packageMap = @(
        @{ ExternalFolder = "CachedQuickLz.1.3.1"; PackageId = "cachedquicklz"; SourceVersion = "1.3.1" },
        @{ ExternalFolder = "DotNetZip.1.15.0"; PackageId = "dotnetzip"; SourceVersion = "1.15.0" },
        @{ ExternalFolder = "MSTest.TestAdapter.2.2.10"; PackageId = "mstest.testadapter"; SourceVersion = "2.2.10" },
        @{ ExternalFolder = "MSTest.TestFramework.2.2.10"; PackageId = "mstest.testframework"; SourceVersion = "2.2.10" },
        @{ ExternalFolder = "log4net.2.0.12"; PackageId = "log4net"; SourceVersion = "2.0.12" },
        @{ ExternalFolder = "Microsoft.Bcl.AsyncInterfaces.5.0.0"; PackageId = "microsoft.bcl.asyncinterfaces"; SourceVersion = "5.0.0" },
        @{ ExternalFolder = "Microsoft.VisualStudio.Threading.16.10.56"; PackageId = "microsoft.visualstudio.threading"; SourceVersion = "16.10.56" },
        @{ ExternalFolder = "Microsoft.VisualStudio.Threading.Analyzers.16.10.56"; PackageId = "microsoft.visualstudio.threading.analyzers"; SourceVersion = "16.10.56" },
        @{ ExternalFolder = "Microsoft.VisualStudio.Validation.16.9.32"; PackageId = "microsoft.visualstudio.validation"; SourceVersion = "16.9.32" },
        @{ ExternalFolder = "Microsoft.Win32.Registry.5.0.0"; PackageId = "microsoft.win32.registry"; SourceVersion = "5.0.0" },
        @{ ExternalFolder = "Newtonsoft.Json.12.0.3"; PackageId = "newtonsoft.json"; SourceVersion = "12.0.3" },
        @{ ExternalFolder = "Open.NAT.2.1.0.0"; PackageId = "open.nat"; SourceVersion = "2.1.0" },
        @{ ExternalFolder = "System.Buffers.4.5.1"; PackageId = "system.buffers"; SourceVersion = "4.5.1" },
        @{ ExternalFolder = "System.Memory.4.5.4"; PackageId = "system.memory"; SourceVersion = "4.5.4" },
        @{ ExternalFolder = "System.Numerics.Vectors.4.5.0"; PackageId = "system.numerics.vectors"; SourceVersion = "4.5.0" },
        @{ ExternalFolder = "System.Runtime.CompilerServices.Unsafe.4.5.3"; PackageId = "system.runtime.compilerservices.unsafe"; SourceVersion = "4.5.3" },
        @{ ExternalFolder = "System.Security.AccessControl.5.0.0"; PackageId = "system.security.accesscontrol"; SourceVersion = "5.0.0" },
        @{ ExternalFolder = "System.Security.Principal.Windows.5.0.0"; PackageId = "system.security.principal.windows"; SourceVersion = "5.0.0" },
        @{ ExternalFolder = "System.Threading.Tasks.Extensions.4.5.4"; PackageId = "system.threading.tasks.extensions"; SourceVersion = "4.5.4" }
    )

    New-Item -ItemType Directory -Path $nugetRoot -Force | Out-Null

    foreach ($package in $packageMap) {
        $source = Join-Path $env:USERPROFILE ".nuget\packages\$($package.PackageId)\$($package.SourceVersion)"
        $target = Join-Path $nugetRoot $package.ExternalFolder

        if (-not (Test-Path -LiteralPath $source)) {
            throw "Missing package cache entry: $source. Run restore first or verify the package version map."
        }

        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }

        Copy-Item -LiteralPath $source -Destination $target -Recurse -Force
    }

    Write-Host "Synced legacy package folders into External\\Nuget" -ForegroundColor Green
}

if (-not $SkipRestore) {
    Invoke-DotnetRestore -Targets @(
        "VladMultiplayer.sln",
        "LmpCommonTest\LmpCommonTest.csproj"
    )
}

if (-not $SkipLegacyNuget) {
    Sync-LegacyNugetLayout
}

if (-not $SkipKspRefs) {
    $resolvedKspRoot = Resolve-KspInstall -PreferredRoot $KspRoot
    Sync-KspReferenceAssemblies -InstallRoot $resolvedKspRoot
}

Write-Host "Bootstrap complete." -ForegroundColor Green