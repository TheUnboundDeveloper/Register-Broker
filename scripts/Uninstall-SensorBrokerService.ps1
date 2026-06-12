<#
  Uninstall-SensorBrokerService.ps1

  Tears down the services created by Install-SensorBrokerService.ps1, in
  dependency order: stop the broker/control Win32 services first (so they
  release the \\.\BrokerSmbus driver handle), then stop + delete the kernel
  driver service last. Run ELEVATED.

  By default the kernel driver service is removed too; pass -KeepDriver to
  leave it registered (e.g. while iterating only on the broker).
#>
[CmdletBinding()]
param(
    [string]$BrokerServiceName  = "SensorBroker",
    [string]$ControlServiceName = "BrokerControl",
    [string]$DriverServiceName  = "BrokerSmbus",
    [switch]$KeepDriver
)
$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script ELEVATED (it stops/deletes services)." }

function Remove-Svc([string]$Name) {
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Host "[svc] '$Name' not present."; return }
    if ($svc.Status -ne 'Stopped') {
        Write-Host "[svc] Stopping '$Name'"
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
        # Give the broker time to drain the control loop and close the driver handle.
        # WaitForStatus THROWS on timeout (it doesn't return a bool, and '2>$null' does not
        # catch a terminating .NET exception) — wrap it so a slow stop doesn't abort uninstall.
        try { (Get-Service -Name $Name).WaitForStatus('Stopped', (New-TimeSpan -Seconds 25)) }
        catch { Write-Warning "'$Name' did not reach Stopped within 25s; deleting anyway (may need a reboot to fully remove)." }
    }
    Write-Host "[svc] Deleting '$Name'"
    sc.exe delete $Name | Out-Host
}

# Win32 consumers first — they hold the driver handle that blocks the driver unload.
Remove-Svc $ControlServiceName
Remove-Svc $BrokerServiceName

if ($KeepDriver) {
    Write-Host "[driver] -KeepDriver: leaving '$DriverServiceName' registered."
} else {
    Remove-Svc $DriverServiceName
}

Write-Host ""
Write-Host "[done] Remaining matching services:" -ForegroundColor Cyan
Get-Service -Name $DriverServiceName, $BrokerServiceName, $ControlServiceName -ErrorAction SilentlyContinue |
    Format-Table -AutoSize Name, Status, StartType
