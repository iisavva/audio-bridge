param([string] $OutputPath)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$references += Get-ChildItem -LiteralPath $metadata -Filter '*.winmd' | ForEach-Object { '/r:' + $_.FullName }
foreach ($name in @('System.Runtime', 'System.Runtime.InteropServices.WindowsRuntime', 'System.ObjectModel', 'System.Threading.Tasks', 'System.Collections')) {
    $assembly = Get-ChildItem -LiteralPath (Join-Path (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL') $name) -Recurse -Filter ($name + '.dll') | Select-Object -First 1
    if (-not $assembly) { throw "Missing framework assembly: $name" }
    $references += '/r:' + $assembly.FullName
}
$destination = if ($OutputPath) { [System.IO.Path]::GetFullPath($OutputPath) } else { Join-Path $PSScriptRoot 'AudioBridge.exe' }
& (Join-Path $framework 'csc.exe') /nologo /utf8output /target:winexe /platform:anycpu /optimize+ "/out:$destination" $references (Join-Path $PSScriptRoot 'AudioBridge.cs') (Join-Path $PSScriptRoot 'AudioMixer.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $destination"
