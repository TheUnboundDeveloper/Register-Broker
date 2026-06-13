<#
  Build-All.ps1  --  one-touch build for the whole Register Broker stack.

  Builds/publishes the BRIDGE (sensor broker + RGB control services) and, on request,
  the kernel DRIVER, using ONLY the canonical locations. Every path below is absolute
  and baked in.

  Canonical locations (the single source of truth):
    Repo .......... resolved from this script's location (override with -Repo)
    Bridge build .. <repo>\scripts\Build-BrokerSensorBridge.ps1        -> <repo>\publish\BrokerSensorBridge
    Driver build .. <repo>\BrokerSmbusDriver\scripts\Build-Driver-DirectLink.ps1 -> <repo>\BrokerSmbusDriver\x64\Release\BrokerSmbus.sys (auto-test-signed)
    Services run .. the SensorBroker / BrokerControl services point at <repo>\publish\BrokerSensorBridge

  Typical use (ELEVATED -- publishing the bridge must stop the running services, which needs admin):
    cd "<repo>"
    .\scripts\Build-All.ps1                 # bridge, then restart services
    .\scripts\Build-All.ps1 -Bridge -Clean  # clean rebuild + republish the bridge, restart services
    .\scripts\Build-All.ps1 -InstallServices -WithRgbControl   # (re)create the service definitions
    .\scripts\Build-All.ps1 -Driver         # also build (+ auto-sign) the kernel driver

  NOTE: the kernel service loads the .sys FROM the build output path, and a LOADED driver
  link-locks that file -- a -Driver build requires the stack stopped (Stop-BrokerServices.ps1)
  or it fails with LNK1104. The installer refuses to act on an unsigned .sys (preflight).
#>
[CmdletBinding()]
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),

    [switch]$Bridge,            # build + publish the bridge/services binary
    [switch]$Driver,            # build (+ auto-test-sign) the kernel driver (DirectLink)
    [switch]$InstallServices,   # (re)create the SensorBroker/BrokerControl service definitions
    [switch]$WithRgbControl,    # with -InstallServices: also install the RGB control service
    # Hardened deploy: install + run the services from an admin-writable Program Files
    # directory instead of the user-writable publish\ tree (closes the local-EoP). Implies
    # (re)installing the services. Off by default so the in-place dev loop is unchanged.
    [switch]$Deploy,
    [string]$InstallDir = "",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$Scripts = Join-Path $Repo "scripts"

function Section($m) { Write-Host ""; Write-Host "==== $m ====" -ForegroundColor Cyan }
function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Default with no component switch = bridge (the usual "rebuild what I edit").
if (-not ($Bridge -or $Driver -or $InstallServices)) { $Bridge = $true }

# -Deploy (Program Files hardening) is applied by the installer, so it implies (re)install.
if ($Deploy) { $InstallServices = $true }

if (-not (Test-Path $Repo)) { throw "Repo not found: $Repo" }
if (-not (Test-Path $Scripts)) { throw "Scripts folder not found: $Scripts" }

$needAdmin = $Bridge -or $InstallServices
if ($needAdmin -and -not (Test-Admin)) {
    throw "This build touches the running services (stop to republish / (re)install), so run it ELEVATED. " +
    "For a driver-only compile use:  .\scripts\Build-All.ps1 -Driver  (no elevation needed unless the driver is loaded)."
}

$services = 'BrokerSmbus', 'SensorBroker', 'BrokerControl'

#--------------------------------------------------------------------------
# 1. Kernel driver (optional). Build-Driver-DirectLink auto-test-signs the
#    output; a LOADED driver link-locks the .sys (stop the stack first).
#--------------------------------------------------------------------------
if ($Driver) {
    Section "Driver (DirectLink)"
    & (Join-Path $Repo "BrokerSmbusDriver\scripts\Build-Driver-DirectLink.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Driver build failed (exit $LASTEXITCODE)." }
    Write-Host "[OK] Driver built + signed. (Install/refresh services: scripts\Install-SensorBrokerService.ps1)"
}

#--------------------------------------------------------------------------
# 2. Bridge / services binary. Build-BrokerSensorBridge.ps1 tears down everything
#    that locks the publish output -- it stops the SensorBroker/BrokerControl services
#    (waiting for Stopped) AND kills any stray bridge process started by hand --
#    then publishes to <repo>\publish\BrokerSensorBridge.
#    NOTE: use $bridgeArgs, NOT $args -- $args is the automatic argument array; splatting a
#    hashtable assigned over it is fragile.
#--------------------------------------------------------------------------
if ($Bridge) {
    Section "Bridge (sensor broker + RGB control)"
    $bridgeArgs = @{ Root = $Repo }
    if ($Clean) { $bridgeArgs.Clean = $true }
    & (Join-Path $Scripts "Build-BrokerSensorBridge.ps1") @bridgeArgs
    if ($LASTEXITCODE -ne 0) { throw "Bridge build failed (exit $LASTEXITCODE)." }
}

#--------------------------------------------------------------------------
# 3. (Re)install the service definitions, if asked. Otherwise just restart the
#    existing services so they pick up the freshly published binary (the service
#    binPath is unchanged, so a restart is all that's needed after a -Bridge build).
#--------------------------------------------------------------------------
if ($InstallServices) {
    Section "Install services"
    $iargs = @{}
    if ($WithRgbControl) { $iargs.WithRgbControl = $true }
    if ($Deploy) { $iargs.Deploy = $true }
    if ($InstallDir) { $iargs.InstallDir = $InstallDir }
    & (Join-Path $Scripts "Install-SensorBrokerService.ps1") @iargs
}
elseif ($Bridge) {
    Section "Restart services (pick up fresh bridge binary)"
    foreach ($svc in $services) {
        $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
        if ($s) {
            try { Start-Service -Name $svc -ErrorAction Stop; Write-Host "[OK] Started $svc" }
            catch { Write-Warning "Could not start ${svc}: $($_.Exception.Message)" }
        }
        else {
            Write-Warning "Service $svc not installed. Run:  .\scripts\Build-All.ps1 -InstallServices -WithRgbControl"
        }
    }
}

Section "Done"
if ($Deploy) {
    $deployDir = if ($InstallDir) { $InstallDir } else { Join-Path $env:ProgramFiles "SensorBroker" }
    Write-Host "Deployed (hardened): services run from $deployDir (admin-writable only)." -ForegroundColor Green
}
Write-Host "Bridge binary : $(Join-Path $Repo 'publish\BrokerSensorBridge\BrokerSensorBridge.exe')"
Write-Host ""
Write-Host "Verify (NON-admin):  BrokerSensorBridge.exe --client --op=sensor.list > out.txt 2>&1" -ForegroundColor Green
