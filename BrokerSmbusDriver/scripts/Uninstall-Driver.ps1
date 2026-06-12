<#
  Stop and remove the BrokerSmbus kernel service. Run elevated.
#>
param([string]$ServiceName = "BrokerSmbus")
$ErrorActionPreference = "Continue"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run elevated (stopping/deleting a kernel service)." }

sc.exe stop $ServiceName | Out-Host
sc.exe delete $ServiceName | Out-Host
Write-Host "[uninstall] '$ServiceName' removed (a reboot fully unloads the driver image)."
