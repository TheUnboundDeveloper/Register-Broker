<#
  Build BrokerSmbus.sys WITHOUT the WDK Visual Studio project-system extension.

  The winget WDK package installs the kernel headers, libs, and signing tools but
  NOT the "WindowsKernelModeDriver10.0" VS platform toolset (the WDK.vsix), so the
  MSBuild .vcxproj route (Build-Driver.ps1) fails with MSB8020. This script drives
  cl.exe / link.exe directly against the installed WDK, producing the same
  x64\Release\BrokerSmbus.sys that Sign-Driver-TestCert.ps1 / Install-Driver.ps1
  expect. The driver is non-PnP and loads via `sc create type= kernel`, so no INF
  is required.

  Requires: Visual Studio (or Build Tools) with the C++ x64 toolset, and the WDK
  (winget: Microsoft.WindowsWDK.10.0.26100). Does NOT need elevation.

  The built .sys is test-signed automatically (Sign-Driver-TestCert.ps1) unless
  -NoSign is passed. The kernel service's ImagePath points AT the build output,
  so an unsigned rebuild bricks the next service start (the 2026-06-11 breakage)
  -- never leave this script's output unsigned.
#>
param(
    [string]$Configuration = "Release",
    [string]$KitRoot = "C:\Program Files (x86)\Windows Kits\10",
    [switch]$NoSign
)
$ErrorActionPreference = "Stop"
$projDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# --- Locate vcvars64.bat (latest VS with the VC++ toolset) ---------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Install Visual Studio / Build Tools." }
$vcvars = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find "VC\Auxiliary\Build\vcvars64.bat" | Select-Object -First 1
if (-not $vcvars) { throw "vcvars64.bat not found. Install the 'Desktop development with C++' workload." }

# --- Auto-detect the newest installed SDK + KMDF versions ----------------------
$sdkVer = Get-ChildItem (Join-Path $KitRoot "Include") -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "km\ntddk.h") } |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $sdkVer) { throw "No WDK kernel headers (km\ntddk.h) under $KitRoot\Include. Install the WDK." }

$kmdfVer = Get-ChildItem (Join-Path $KitRoot "Include\wdf\kmdf") -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $kmdfVer) { throw "No KMDF headers under $KitRoot\Include\wdf\kmdf. Install the WDK." }

$kmdfMajor, $kmdfMinor = $kmdfVer.Split('.')
$obj = Join-Path $env:TEMP "BrokerSmbus.obj"
New-Item -ItemType Directory -Force $obj | Out-Null

Write-Host "[driver] vcvars : $vcvars"
Write-Host "[driver] SDK    : $sdkVer"
Write-Host "[driver] KMDF   : $kmdfVer"

# --- Generate a cmd that inherits the vcvars env, then compile + link ----------
$inc = @("$KitRoot\Include\$sdkVer\km", "$KitRoot\Include\$sdkVer\km\crt",
         "$KitRoot\Include\$sdkVer\shared", "$KitRoot\Include\wdf\kmdf\$kmdfVer") -join ';'
$kmlib   = "$KitRoot\Lib\$sdkVer\km\x64"
$kmdflib = "$KitRoot\Lib\wdf\kmdf\x64\$kmdfVer"
$defs = '/D_AMD64_ /DPOOL_NX_OPTIN=1 /DNTDDI_VERSION=0x0A000010 /D_WIN32_WINNT=0x0A00 /DWINVER=0x0A00'

$bat = @"
@echo off
call "$vcvars" >nul 2>&1
set "INCLUDE=$inc;%INCLUDE%"
cd /d "$projDir"
if not exist "x64\$Configuration" mkdir "x64\$Configuration"
cl /nologo /c /W4 /WX /kernel /GS- $defs /Fo"$obj\\" SmbusAmd.c SmbusIntel.c SmuAmd.c SuperioNct.c SuperioNct6775.c Smbus.c SmbusDetect.c || exit /b 1
cl /nologo /c /W4 /WX /wd4324 /wd4201 /kernel /GS- $defs /DKMDF_VERSION_MAJOR=$kmdfMajor /DKMDF_VERSION_MINOR=$kmdfMinor /Fo"$obj\\" Driver.c || exit /b 1
link /nologo /OUT:"x64\$Configuration\BrokerSmbus.sys" /MACHINE:X64 /SUBSYSTEM:NATIVE /DRIVER /ENTRY:FxDriverEntry /NODEFAULTLIB /RELEASE /OPT:REF /OPT:ICF /LIBPATH:"$kmlib" /LIBPATH:"$kmdflib" "$obj\Driver.obj" "$obj\Smbus.obj" "$obj\SmbusDetect.obj" "$obj\SmbusAmd.obj" "$obj\SmbusIntel.obj" "$obj\SmuAmd.obj" "$obj\SuperioNct.obj" "$obj\SuperioNct6775.obj" WdfDriverEntry.lib WdfLdr.lib ntoskrnl.lib hal.lib wmilib.lib wdmsec.lib BufferOverflowFastFailK.lib || exit /b 1
"@
$batFile = Join-Path $env:TEMP "build-brokersmbus.cmd"
Set-Content -Path $batFile -Value $bat -Encoding Ascii
cmd /c "`"$batFile`""
if ($LASTEXITCODE -ne 0) { throw "Driver build failed ($LASTEXITCODE)." }

$sys = Join-Path $projDir "x64\$Configuration\BrokerSmbus.sys"
if (-not (Test-Path $sys)) { throw "Build reported success but $sys is missing." }
Write-Host "[driver] Built: $sys ($((Get-Item $sys).Length) bytes)" -ForegroundColor Green

if ($NoSign) {
    Write-Warning "-NoSign: the .sys is UNSIGNED and will fail to load if the service (re)starts. Sign before installing: Sign-Driver-TestCert.ps1"
} else {
    & (Join-Path $PSScriptRoot "Sign-Driver-TestCert.ps1") -SysPath $sys
}
