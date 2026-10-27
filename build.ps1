param([string] $OutputPath, [switch] $BuildTests)
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
$defaultOutput = if ($BuildTests) { 'tests\AudioBridge.Tests.exe' } else { 'AudioBridge.exe' }
$destination = if ($OutputPath) { [System.IO.Path]::GetFullPath($OutputPath) } else { Join-Path $PSScriptRoot $defaultOutput }
$sources = @('AudioBridge.cs', 'AudioMixer.cs', 'PhoneAmplifier.cs', 'NativePhoneCapture.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$options = @('/nologo', '/utf8output', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$destination")
if ($BuildTests) {
    $options = @('/nologo', '/utf8output', '/target:exe', '/platform:anycpu', '/optimize+', "/out:$destination", '/main:AudioBridge.Tests.Program')
    $sources += Join-Path $PSScriptRoot 'tests\Program.cs'
}
& (Join-Path $framework 'csc.exe') $options $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $destination"
