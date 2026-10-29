#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$KspRoot,[Parameter(Mandatory)][string]$ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$assemblies=New-Object 'System.Collections.Generic.List[object]'
$known=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$paths=@(Get-ChildItem -LiteralPath (Join-Path $KspRoot 'KSP_x64_Data\Managed') -Filter '*.dll' -File)
$paths+=@(Get-ChildItem -LiteralPath (Join-Path $KspRoot 'GameData') -Filter '*.dll' -File -Recurse)
foreach($file in $paths){
    $stream=[IO.File]::OpenRead($file.FullName)
    $pe=$null
    try {
        $pe=[Reflection.PortableExecutable.PEReader]::new($stream)
        if(-not $pe.HasMetadata){continue}
        $reader=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $definition=$reader.GetAssemblyDefinition()
        $name=$reader.GetString($definition.Name)
        [void]$known.Add($name)
        $refs=@(foreach($handle in $reader.AssemblyReferences){$reference=$reader.GetAssemblyReference($handle);$reader.GetString($reference.Name)})
        $assemblies.Add([pscustomobject]@{Name=$name;Path=$file.FullName;References=$refs})
    }
    finally {if($pe){$pe.Dispose()};$stream.Dispose()}
}
foreach($name in @('mscorlib','System','System.Core','System.Configuration','System.Xml','System.Xml.Linq','System.Net.Http','Microsoft.CSharp','netstandard')){[void]$known.Add($name)}
$missing=@(foreach($assembly in $assemblies){foreach($reference in $assembly.References){if(-not $known.Contains($reference)){[pscustomobject]@{Assembly=$assembly.Name;File=$assembly.Path.Substring($KspRoot.Length+1);MissingReference=$reference}}}})
$result=[ordered]@{ManagedAssemblies=$assemblies.Count;MissingReferences=$missing;Note='Static reference inventory. Optional integrations can reference absent mods; a clean inventory does not prove Unity or multiplayer behavior.'}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
$missing | Format-Table -AutoSize
Write-Output "Managed assemblies inspected: $($assemblies.Count). Missing reference records: $($missing.Count)."
