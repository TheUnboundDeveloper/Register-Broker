<#
  Stop-BrokerServices.ps1

  Stops the whole Broker service stack in the SAFE order: consumers first
  (RGB control + sensor broker), then the kernel driver LAST -- so the broker's
  open handle to \\.\BrokerSmbus is released before the driver is asked to stop,
  letting it unload cleanly instead of wedging.

  Also kills any stray BrokerSensorBridge.exe started outside the SCM (e.g. by
  hand) so nothing is left holding the pipes / driver handle.

  Run ELEVATED (stopping services edits the SCM).

  Usage:
    .\scripts\Stop-BrokerServices.ps1
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script ELEVATED (stopping services edits the SCM)." }

# Consumers FIRST, kernel driver LAST.
$ordered = @(
    'BrokerControl',     # RGB control service (driver consumer)
    'SensorBroker',      # sensor broker service (driver consumer)
    'BrokerSmbus'        # kernel driver -- stopped LAST
)

foreach ($name in $ordered) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) { continue }
    if ($svc.Status -eq 'Stopped') { Write-Host "[stop] '$name' already stopped."; continue }

    Write-Host "[stop] Stopping '$name'..."
    try { Stop-Service -Name $name -Force -ErrorAction Stop }
    catch { Write-Warning "[stop] Stop-Service '$name' failed: $($_.Exception.Message)" }

    try { (Get-Service -Name $name).WaitForStatus('Stopped', '00:00:20') } catch {}
    $now = (Get-Service -Name $name -ErrorAction SilentlyContinue).Status
    if ($now -eq 'Stopped') { Write-Host "[stop] '$name' stopped." }
    else { Write-Warning "[stop] '$name' did not reach Stopped (status=$now). Close any services.msc window and retry, or reboot." }
}

# Kill stray (non-service) bridge processes from THIS repo so nothing keeps the
# pipes/driver handle open after the services are down.
$RootFull = (Resolve-Path $Root).Path.TrimEnd('\')
foreach ($img in @('BrokerSensorBridge')) {
    Get-Process -Name $img -ErrorAction SilentlyContinue | ForEach-Object {
        $proc = $_; $procPath = $null
        try { $procPath = $proc.Path } catch {}
        if ($procPath -and $procPath.StartsWith($RootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "[stop] Killing stray $img.exe (PID $($proc.Id)) from this repo."
            try { $proc.Kill(); [void]$proc.WaitForExit(5000) } catch {}
        }
    }
}

Write-Host ""
Write-Host "[done] Current status:" -ForegroundColor Cyan
Get-Service -Name 'BrokerSmbus', 'SensorBroker', 'BrokerControl' -ErrorAction SilentlyContinue |
    Format-Table -AutoSize Name, Status, StartType
