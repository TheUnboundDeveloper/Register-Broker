<#
  Build the BrokerSmbus KMDF driver. Requires Visual Studio + the Windows
  Driver Kit (WDK) matching your SDK. Does NOT need elevation.
#>
param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)
$ErrorActionPreference = "Stop"

$proj = Join-Path $PSScriptRoot "..\BrokerSmbus.vcxproj"
if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Install Visual Studio + WDK." }

$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
    Select-Object -First 1
if (-not $msbuild) { throw "MSBuild.exe not found via vswhere." }

Write-Host "[driver] MSBuild: $msbuild"
& $msbuild $proj /p:Configuration=$Configuration /p:Platform=$Platform /t:Rebuild /m
if ($LASTEXITCODE -ne 0) { throw "Driver build failed ($LASTEXITCODE)." }

$sys = Join-Path $PSScriptRoot "..\$Platform\$Configuration\BrokerSmbus.sys"
Write-Host "[driver] Built: $sys"
