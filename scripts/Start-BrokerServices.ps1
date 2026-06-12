<#
  Start-BrokerServices.ps1

  Starts the Broker service stack in dependency order: the kernel driver FIRST,
  then the sensor broker, then the optional RGB control service. (The broker /
  control services DependsOn the driver, so the SCM would pull it up anyway --
  starting it explicitly first just gives a clearer failure if the driver can't
  load, e.g. test-signing off.)

  Run ELEVATED.

  Usage:
    .\scripts\Start-BrokerServices.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script ELEVATED (starting services edits the SCM)." }

# Kernel driver FIRST, then consumers. BrokerControl is optional (only present if installed
# with -WithRgbControl).
$ordered = @('BrokerSmbus', 'SensorBroker', 'BrokerControl')

foreach ($name in $ordered) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) {
        if ($name -eq 'BrokerControl') { Write-Host "[start] '$name' not installed (no -WithRgbControl) -- skipping." }
        else { Write-Warning "[start] '$name' is not installed. Run scripts\Install-SensorBrokerService.ps1 first." }
        continue
    }
    if ($svc.Status -eq 'Running') { Write-Host "[start] '$name' already running."; continue }

    Write-Host "[start] Starting '$name'..."
    try { Start-Service -Name $name -ErrorAction Stop }
    catch { Write-Warning "[start] Start-Service '$name' failed: $($_.Exception.Message)"; continue }

    try { (Get-Service -Name $name).WaitForStatus('Running', '00:00:20') } catch {}
    $now = (Get-Service -Name $name -ErrorAction SilentlyContinue).Status
    if ($now -eq 'Running') {
        Write-Host "[start] '$name' running."
    } else {
        Write-Warning "[start] '$name' did not reach Running (status=$now)."
        if ($name -eq 'BrokerSmbus') {
            Write-Warning "        Driver won't load? Confirm test-signing is ON (bcdedit /set testsigning on, reboot) and the .sys is test-signed."
        }
    }
}

Write-Host ""
Write-Host "[done] Current status:" -ForegroundColor Cyan
Get-Service -Name 'BrokerSmbus', 'SensorBroker', 'BrokerControl' -ErrorAction SilentlyContinue |
    Format-Table -AutoSize Name, Status, StartType
Write-Host "Verify (from a NON-admin shell):" -ForegroundColor Cyan
Write-Host '  cmd /c "publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --op=sensor.list > out.txt 2>&1" ; type out.txt'
