<#
  Install-SensorBrokerService.ps1

  Option C — one installer that registers the whole broker stack as Windows
  services so it auto-starts and owns the driver lifecycle:

    1. BrokerSmbus   (kernel driver service)  — the narrow Ring-0 surface.
    2. SensorBroker  (Win32 service, LocalSystem) — sensor broker on
                       \\.\pipe\SensorBroker.  Depends on the driver so SCM
                       start order is driver -> broker.
    3. BrokerControl (optional, -WithRgbControl) — the write-only RGB control
                       service on \\.\pipe\BrokerControl (rgb:write scope).
                       Add -WithHidRgb to also enable the USB-HID (MSI Mystic
                       Light) motherboard-header transport (reduced assurance,
                       opt-in; writes AllowHidRgb=true; implies -WithRgbControl).

    Add -WithGpuSensors to enable the read-only GPU sensor source (AMD ADL;
    served as gpu.* on the SensorBroker service; reduced assurance, opt-in;
    writes AllowGpuSensors=true — no control service needed).

  The broker/control services run the SAME published exe with --service; the
  process detects the SCM and hosts the long-running body under it (see
  ServiceHost.cs).  Because they run as LocalSystem the broker widens the pipe
  DACL to Authenticated Users + a medium-integrity label so non-admin clients
  can connect; the peer-identity + Authenticode signer pin is the real gate,
  which is why this installer can flip enforcement on (-RequireAuthorizedClient).

  Run ELEVATED.  Build/publish the bridge first (Build-BrokerSensorBridge.ps1)
  and build/test-sign the driver (BrokerSmbusDriver\scripts).
#>
[CmdletBinding()]
param(
    # Folder holding the published BrokerSensorBridge.exe (+ LHM dlls + appsettings.json).
    [string]$BridgeDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "publish\BrokerSensorBridge"),
    # The driver .sys to register as the kernel service.
    [string]$SysPath   = (Join-Path (Split-Path -Parent $PSScriptRoot) "BrokerSmbusDriver\x64\Release\BrokerSmbus.sys"),

    [string]$BrokerServiceName  = "SensorBroker",
    [string]$ControlServiceName = "BrokerControl",
    [string]$DriverServiceName  = "BrokerSmbus",

    [switch]$WithRgbControl,        # also install the write-only RGB control service
    # Enable the opt-in USB-HID (MSI Mystic Light) RGB transport for motherboard headers
    # (writes AllowHidRgb=true into the control service's appsettings.json). Reduced
    # assurance (user-mode, no kernel brick-guard) — see docs/SECURITY.md. Implies the
    # control service, so it turns on -WithRgbControl automatically.
    [switch]$WithHidRgb,
    # Enable the opt-in read-only GPU sensor source (AMD ADL today) on the SENSOR service
    # (writes AllowGpuSensors=true into appsettings.json). A discrete GPU's thermals are a
    # vendor user-mode API, not an SMBus device, so this is reduced assurance (no kernel
    # driver, no brick-guard) but strictly READ-ONLY. Served as gpu.* sensors. See docs/SECURITY.md.
    [switch]$WithGpuSensors,
    [switch]$SkipDriver,            # do not (re)register the kernel driver service
    [switch]$NoDriverDependency,    # do not make the broker depend on the driver service
    [switch]$NoStart,               # register but do not start the services

    # Hardened deployment: copy the bridge + driver into an admin-writable install
    # directory (Program Files) and register the services FROM there, instead of
    # running them out of the user-writable repo\publish tree (local-EoP: a non-admin
    # could replace the exe/.sys/appsettings the LocalSystem services load). Off by
    # default so the in-place dev loop (run-from-publish) is unchanged.
    [switch]$Deploy,
    [string]$InstallDir = (Join-Path $env:ProgramFiles "SensorBroker"),

    # Enforcement policy written into the published appsettings.json. With no
    # signer/path supplied this stays audit-only (the dev default) so you can't
    # lock yourself out by accident.
    [switch]$RequireAuthorizedClient,
    [string[]]$AllowedClientSigners = @(),
    [string[]]$AllowedClientPaths   = @()
)
$ErrorActionPreference = "Stop"

function Assert-Admin {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw "Run this script ELEVATED (it creates/starts services)." }
}

function Remove-ServiceIfPresent([string]$Name) {
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    if ($svc.Status -ne 'Stopped') {
        Write-Host "[svc] Stopping existing '$Name'"
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    }
    Write-Host "[svc] Removing existing '$Name'"
    sc.exe delete $Name | Out-Host
    # Poll until the SCM actually drops it. A fixed sleep races deletion: re-creating the
    # name while it is still DELETE PENDING fails with 1072 ("marked for deletion").
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    Write-Warning "Service '$Name' still present after delete (marked for deletion?). Close any Services consoles or reboot, then retry."
}

Assert-Admin

# USB-HID RGB is served by the control service, so it requires it. Turn it on automatically
# rather than silently doing nothing.
if ($WithHidRgb -and -not $WithRgbControl) {
    Write-Host "[cfg] -WithHidRgb implies the control service; enabling -WithRgbControl."
    $WithRgbControl = $true
}

#--------------------------------------------------------------------------
# PREFLIGHT: validate the driver binary BEFORE tearing anything down.
# On 2026-06-11 this script stopped/deleted the whole running stack, then
# died starting a rebuilt-but-UNSIGNED .sys (error 577), leaving no services
# at all. Refuse up front instead. "Signed at all" is the bar: a dev test
# signature reports UnknownError (untrusted root) yet loads fine under
# test-signing — only NotSigned / HashMismatch (tampered) are fatal.
#--------------------------------------------------------------------------
if (-not $SkipDriver) {
    if (-not (Test-Path $SysPath)) { throw "Driver .sys not found: $SysPath (build + test-sign it, or pass -SkipDriver). Nothing was torn down." }
    $preSig = Get-AuthenticodeSignature (Resolve-Path $SysPath).Path
    if (-not $preSig.SignerCertificate -or $preSig.Status -in @('NotSigned', 'HashMismatch')) {
        throw ("Driver '$SysPath' is not validly signed (status: $($preSig.Status)). " +
               "It would fail to load (error 577) AFTER the existing services were torn down. " +
               "Sign it first (BrokerSmbusDriver\scripts\Sign-Driver-TestCert.ps1) or pass -SkipDriver. Nothing was torn down.")
    }
    Write-Host "[preflight] Driver signature OK: $($preSig.SignerCertificate.Subject) (status: $($preSig.Status))"
}

#--------------------------------------------------------------------------
# 0a. Optional hardened deploy: copy bridge + driver into an admin-writable
#     install directory and re-point the service paths there. Files copied
#     INTO Program Files inherit its admin-only ACL, dropping the user-write
#     that makes the in-place layout a local-EoP (swap the LocalSystem exe,
#     .sys, or appsettings). Default off -> dev runs from publish\ in place.
#--------------------------------------------------------------------------
if ($Deploy) {
    $srcBridge = (Resolve-Path $BridgeDir).Path
    if (-not (Test-Path (Join-Path $srcBridge "BrokerSensorBridge.exe"))) {
        throw "Bridge exe not found under -BridgeDir '$srcBridge' (build it first)."
    }

    # Sanity: the install dir must be admin-only. Warn loudly if it sits in a
    # user-writable tree (e.g. someone pointed -InstallDir back at AppData).
    $pf  = $env:ProgramFiles.TrimEnd('\') + '\'
    $pf6 = if ($env:ProgramW6432) { $env:ProgramW6432.TrimEnd('\') + '\' } else { $pf }
    if (-not ($InstallDir.StartsWith($pf, 'OrdinalIgnoreCase') -or $InstallDir.StartsWith($pf6, 'OrdinalIgnoreCase'))) {
        Write-Warning "InstallDir '$InstallDir' is not under Program Files; the EoP hardening relies on an admin-only target."
    }

    # Stop any services already running from a prior deploy so their files unlock.
    foreach ($svc in @($ControlServiceName, $BrokerServiceName)) {
        $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
        if ($s -and $s.Status -ne 'Stopped') { Write-Host "[deploy] Stopping '$svc' to refresh files"; Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue }
    }

    $driverDst = Join-Path $InstallDir "driver"
    Write-Host "[deploy] Copying bridge -> $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir, $driverDst | Out-Null
    Copy-Item (Join-Path $srcBridge '*') $InstallDir -Recurse -Force

    if (-not $SkipDriver) {
        if (-not (Test-Path $SysPath)) { throw "Driver .sys not found: $SysPath (build + test-sign it, or pass -SkipDriver)." }
        $sysName = Split-Path $SysPath -Leaf
        Write-Host "[deploy] Copying driver -> $driverDst"
        Copy-Item (Resolve-Path $SysPath).Path (Join-Path $driverDst $sysName) -Force
        $SysPath = Join-Path $driverDst $sysName
    }

    # Re-point the remaining install steps at the hardened copies.
    $BridgeDir = $InstallDir
}

$Exe = Join-Path $BridgeDir "BrokerSensorBridge.exe"
if (-not (Test-Path $Exe)) { throw "Bridge exe not found: $Exe (build it first with Build-BrokerSensorBridge.ps1)" }
$Exe = (Resolve-Path $Exe).Path

#--------------------------------------------------------------------------
# 0. Optional: write the enforcement policy (signer/path allowlist) into the
#    published appsettings.json. The broker is named-pipe only — there is no
#    HTTP/TCP surface to configure.
#--------------------------------------------------------------------------
$AppSettings = Join-Path $BridgeDir "appsettings.json"
$setAuth = ($RequireAuthorizedClient -or $AllowedClientSigners.Count -or $AllowedClientPaths.Count)
if ((Test-Path $AppSettings) -and ($setAuth -or $WithHidRgb -or $WithGpuSensors)) {
    Write-Host "[cfg] Updating $AppSettings"

    $cfg = Get-Content $AppSettings -Raw | ConvertFrom-Json

    # Auth/enforcement fields are written ONLY when an auth flag was supplied, so enabling
    # -WithHidRgb alone never resets a deployment's existing RequireAuthorizedClient setting.
    if ($setAuth) {
        # Validate inputs up front: a malformed thumbprint/path would silently produce a
        # dead allowlist that locks out every client while RequireAuthorizedClient is ON.
        foreach ($s in $AllowedClientSigners) {
            if (($s -replace '[\s:-]', '') -notmatch '^([0-9A-Fa-f]{40}|[0-9A-Fa-f]{64})$') {
                throw "AllowedClientSigners entry '$s' is not a 40-hex SHA-1 or 64-hex SHA-256 thumbprint."
            }
        }
        foreach ($p in $AllowedClientPaths) {
            if (-not [System.IO.Path]::IsPathRooted($p)) {
                throw "AllowedClientPaths entry '$p' must be a full (rooted) image path."
            }
        }
        $cfg | Add-Member -NotePropertyName RequireAuthorizedClient -NotePropertyValue ([bool]$RequireAuthorizedClient) -Force
        $cfg | Add-Member -NotePropertyName AllowedClientSigners    -NotePropertyValue (@($AllowedClientSigners)) -Force
        $cfg | Add-Member -NotePropertyName AllowedClientPaths      -NotePropertyValue (@($AllowedClientPaths))   -Force
        if ($RequireAuthorizedClient -and -not ($AllowedClientSigners.Count -or $AllowedClientPaths.Count)) {
            Write-Warning "RequireAuthorizedClient is ON but no signers/paths were supplied — every client will be rejected."
        }
    }

    # USB-HID RGB transport (motherboard headers). Reduced assurance (no kernel guard); opt-in.
    if ($WithHidRgb) {
        $cfg | Add-Member -NotePropertyName AllowHidRgb -NotePropertyValue $true -Force
        Write-Host "[cfg] AllowHidRgb=true (USB-HID motherboard-header RGB enabled — reduced assurance, no kernel brick-guard)."
    }

    # Read-only GPU sensor source (vendor user-mode API, AMD ADL today). Reduced assurance
    # (no kernel driver/brick-guard) but read-only; served as gpu.* on the sensor service.
    if ($WithGpuSensors) {
        $cfg | Add-Member -NotePropertyName AllowGpuSensors -NotePropertyValue $true -Force
        Write-Host "[cfg] AllowGpuSensors=true (read-only GPU sensors via vendor API — reduced assurance, no kernel guard)."
    }

    # Write BOM-less UTF-8 deterministically. Windows PowerShell 5.1 'Set-Content -Encoding UTF8'
    # emits a BOM; .NET tolerates it, but BOM-less keeps the file byte-identical across shells.
    # Depth 32 (well past any real appsettings nesting) so ConvertTo-Json never silently
    # truncates a deep section into a string.
    [System.IO.File]::WriteAllText($AppSettings, ($cfg | ConvertTo-Json -Depth 32), (New-Object System.Text.UTF8Encoding($false)))
}

#--------------------------------------------------------------------------
# 1. Kernel driver service (the Ring-0 surface). Non-PnP, no INF.
#--------------------------------------------------------------------------
if (-not $SkipDriver) {
    if (-not (Test-Path $SysPath)) { throw "Driver .sys not found: $SysPath (build + test-sign it, or pass -SkipDriver)." }
    $SysPath = (Resolve-Path $SysPath).Path
    Remove-ServiceIfPresent $DriverServiceName
    Write-Host "[driver] Creating kernel service '$DriverServiceName' -> $SysPath"
    sc.exe create $DriverServiceName type= kernel start= demand binPath= "$SysPath" DisplayName= "Register Broker SMBus Driver" | Out-Host
    # sc.exe is a native exe — a non-zero exit does NOT throw. The old service was already
    # removed above, so a silent create failure (e.g. 1072 DELETE_PENDING from a lingering
    # handle) would otherwise leave NO driver service, especially under -NoStart. Check it.
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create '$DriverServiceName' failed (exit $LASTEXITCODE). The previous driver service was already removed; resolve the cause (a lingering DELETE_PENDING handle / open SCM console usually clears on a retry) and re-run this installer."
    }
    if (-not $NoStart) {
        Write-Host "[driver] Starting '$DriverServiceName'"
        sc.exe start $DriverServiceName | Out-Host
        # sc.exe is a native exe — a non-zero exit does NOT throw. Check it explicitly:
        # the broker DependsOn this driver, so a silent driver-start failure otherwise
        # surfaces only as a confusing "depends on a service which failed to start" later.
        if ($LASTEXITCODE -ne 0) {
            throw "Driver service '$DriverServiceName' failed to start (sc.exe exit $LASTEXITCODE). " +
                  "Is the .sys test-signed AND test-signing enabled (bcdedit /set testsigning on, reboot)? " +
                  "Fix this before the broker can start."
        }
    }
} else {
    Write-Host "[driver] -SkipDriver: leaving kernel driver service as-is."
}

#--------------------------------------------------------------------------
# 2. Sensor broker service (LocalSystem). Hosts \\.\pipe\SensorBroker.
#--------------------------------------------------------------------------
Remove-ServiceIfPresent $BrokerServiceName
# The inner quotes around the exe path are load-bearing: the repo path contains spaces, and
# New-Service stores BinaryPathName verbatim into ImagePath. Do NOT remove the quotes.
$brokerBin = '"{0}" --service' -f $Exe
$depend = if ($NoDriverDependency -or $SkipDriver) { $null } else { $DriverServiceName }
Write-Host "[broker] Creating service '$BrokerServiceName'"
$newSvc = @{
    Name           = $BrokerServiceName
    BinaryPathName = $brokerBin
    DisplayName    = "Register Broker Sensor Service"
    Description    = "Brokers PC sensor reads to non-admin clients over \\.\pipe\SensorBroker (no WinRing0)."
    StartupType    = "Automatic"
}
if ($depend) { $newSvc.DependsOn = $depend }
New-Service @newSvc | Out-Null
# Auto-restart on crash (SCM recovery): restart after 5s, three times, reset window 1 day.
sc.exe failure $BrokerServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Host
if (-not $NoStart) {
    Write-Host "[broker] Starting '$BrokerServiceName'"
    Start-Service -Name $BrokerServiceName
}

#--------------------------------------------------------------------------
# 3. Optional write-only RGB control service. Hosts \\.\pipe\BrokerControl.
#--------------------------------------------------------------------------
if ($WithRgbControl) {
    Remove-ServiceIfPresent $ControlServiceName
    $controlBin = '"{0}" --control --service' -f $Exe
    Write-Host "[control] Creating service '$ControlServiceName'"
    $newCtl = @{
        Name           = $ControlServiceName
        BinaryPathName = $controlBin
        DisplayName    = "Register Broker RGB Control"
        Description    = "Write-only RGB-over-SMBus control for non-admin clients (rgb:write) on \\.\pipe\BrokerControl."
        StartupType    = "Automatic"
    }
    if ($depend) { $newCtl.DependsOn = $depend }
    New-Service @newCtl | Out-Null
    sc.exe failure $ControlServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Host
    if (-not $NoStart) {
        Write-Host "[control] Starting '$ControlServiceName'"
        Start-Service -Name $ControlServiceName
    }
}

Write-Host ""
Write-Host "[done] Installed:" -ForegroundColor Cyan
Get-Service -Name $DriverServiceName, $BrokerServiceName, $ControlServiceName -ErrorAction SilentlyContinue |
    Format-Table -AutoSize Name, Status, StartType
Write-Host ""
Write-Host "Verify (from a NON-admin shell):" -ForegroundColor Cyan
Write-Host "  BrokerSensorBridge.exe --client --op=sensor.list > out.txt 2>&1"
Write-Host "Logs: %LOCALAPPDATA%\BrokerSensorBridge\bridge.log + audit.log (under the service profile)."
