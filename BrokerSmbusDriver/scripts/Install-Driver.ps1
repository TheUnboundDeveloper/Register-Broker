<#
  Load BrokerSmbus.sys as a kernel service (dev box). Run elevated.
  Requires test signing enabled (Sign-Driver-TestCert.ps1) unless the .sys is
  production-signed.
#>
param(
    [string]$SysPath,
    [string]$ServiceName = "BrokerSmbus"
)
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run elevated (creating/starting a kernel service)." }

if (-not $SysPath) { $SysPath = Join-Path $PSScriptRoot "..\x64\Release\BrokerSmbus.sys" }
if (-not (Test-Path $SysPath)) { throw "Driver .sys not found: $SysPath" }
$SysPath = (Resolve-Path $SysPath).Path

Write-Host "[install] Creating kernel service '$ServiceName' -> $SysPath"
sc.exe create $ServiceName type= kernel start= demand binPath= "$SysPath" DisplayName= "Register Broker SMBus Driver" | Out-Host
Write-Host "[install] Starting service"
sc.exe start $ServiceName | Out-Host

Write-Host ""
Write-Host "[install] Verify from the broker:" -ForegroundColor Cyan
Write-Host "          BrokerSensorBridge.exe should log 'SMBus driver present' once the"
Write-Host "          controller read path is implemented (scaffold reports CAP_READ unset)."
