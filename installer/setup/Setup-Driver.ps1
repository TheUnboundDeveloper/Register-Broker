<#
  Setup-Driver.ps1

  Registers (or removes) the BrokerSmbus kernel driver service on behalf of the
  MSI. MSI's ServiceInstall table only supports Win32 services - kernel and
  file-system drivers are explicitly unsupported - so the kernel service is
  created with sc.exe from a deferred, no-impersonate custom action running as
  LocalSystem.

  This mirrors what scripts\Install-SensorBrokerService.ps1 does for a dev-box
  install, minus the build/teardown logic: the MSI owns file placement, this
  script owns only the service.

  EXIT CODES matter: the calling custom action is Return="check", so a non-zero
  exit rolls the whole install back. Deliberately, only a failure to CREATE the
  service is fatal. A driver that is registered but refuses to START (the
  expected outcome for a test-signed .sys on a machine that is not in
  test-signing mode) is recorded and reported, NOT treated as a failed install:
  the broker services and the console are still perfectly installable, they
  just report no hardware. The state lands in
  HKLM\SOFTWARE\RegisterBroker\DriverStatus so the installer log, the docs and
  the Reference Console all have one place to look.

  A package with NO driver payload (-Store builds omit the .sys, because a
  test-signed binary cannot chain to a Microsoft-trusted root) is the same
  story one step earlier: nothing to register, so record DriverStatus=Absent
  and succeed. Test-Installer.ps1 is what catches a driver that went missing by
  accident - a package that claims a sign state must carry the binary.

  Windows PowerShell 5.1 compatible, ASCII only.
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Remove')]
    [string]$Action = 'Install',

    # The install root (INSTALLFOLDER). Everything else is derived from it: the
    # MSI CustomAction.Target column is only 255 characters and truncates
    # silently, so the command line stays short and the paths are computed here.
    [string]$Root = '',

    # production | test | unsigned, stamped into the package at build time.
    [string]$SignState = 'unknown',

    [string]$ServiceName = 'BrokerSmbus',
    [string]$DisplayName = 'Register Broker SMBus Driver'
)

$ErrorActionPreference = 'Stop'
$RegKey = 'HKLM:\SOFTWARE\RegisterBroker'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
# The installer passes "<dir>\." on purpose: an MSI directory property ends in a
# backslash, and a backslash right before a closing quote escapes it. Normalize
# the marker back off before building any paths.
$Root = ($Root -replace '[\\/]\.$', '').TrimEnd('\', '/')

$SysPath = Join-Path $Root 'driver\BrokerSmbus.sys'
$LogPath = Join-Path $Root 'setup\driver-setup.log'

function Write-Log([string]$Message) {
    $line = ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    Write-Output $line
    if ($LogPath) {
        try {
            $dir = Split-Path -Parent $LogPath
            if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
            Add-Content -Path $LogPath -Value $line -Encoding ASCII
        } catch {
            # Logging must never be the thing that fails an install.
        }
    }
}

function Set-DriverStatus([string]$State, [string]$Detail) {
    try {
        if (-not (Test-Path $RegKey)) { New-Item -Path $RegKey -Force | Out-Null }
        New-ItemProperty -Path $RegKey -Name 'DriverStatus'       -Value $State  -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $RegKey -Name 'DriverStatusDetail' -Value $Detail -PropertyType String -Force | Out-Null
    } catch {
        Write-Log ("WARN: could not record driver status: {0}" -f $_.Exception.Message)
    }
}

function Remove-DriverService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return }

    if ($svc.Status -ne 'Stopped') {
        Write-Log "Stopping existing '$ServiceName'"
        & sc.exe stop $ServiceName | Out-Null
    }
    Write-Log "Deleting existing '$ServiceName'"
    & sc.exe delete $ServiceName | Out-Null

    # Poll until the SCM actually drops it: recreating a name that is still
    # DELETE PENDING fails with 1072. Same race the dev-box installer hits.
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    Write-Log "WARN: '$ServiceName' still present after delete (marked for deletion?)."
}

try {
    if ($Action -eq 'Remove') {
        Write-Log "=== Remove driver service ==="
        Remove-DriverService
        try {
            if (Test-Path $RegKey) {
                Remove-ItemProperty -Path $RegKey -Name 'DriverStatus'       -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path $RegKey -Name 'DriverStatusDetail' -ErrorAction SilentlyContinue
            }
        } catch { }
        Write-Log "Driver service removed."
        exit 0
    }

    Write-Log "=== Install driver service ==="
    Write-Log "SysPath   : $SysPath"
    Write-Log "SignState : $SignState (stamped at package build time)"

    if (-not (Test-Path $SysPath)) {
        Write-Log "No driver binary at '$SysPath' - this package ships without the kernel driver."
        Write-Log "The broker services and the console still install; the sensor catalog will report no hardware."
        Set-DriverStatus 'Absent' 'This package contains no kernel driver payload.'
        exit 0
    }
    $SysPath = (Resolve-Path $SysPath).Path

    # Report what is actually on disk as well as what the build stamped. If a
    # production .cat was dropped in later these can legitimately disagree.
    try {
        $sig = Get-AuthenticodeSignature $SysPath
        Write-Log ("Authenticode: status={0} signer={1}" -f $sig.Status, $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '<none>' }))
        if ($sig.Status -in @('NotSigned', 'HashMismatch')) {
            Write-Log "FATAL: the driver is unsigned or tampered with; refusing to register a kernel service for it."
            Set-DriverStatus 'Failed' ("Driver signature status: {0}" -f $sig.Status)
            exit 3
        }
    } catch {
        Write-Log ("WARN: could not read the driver signature: {0}" -f $_.Exception.Message)
    }

    Remove-DriverService

    Write-Log "Creating kernel service '$ServiceName' -> $SysPath"
    & sc.exe create $ServiceName type= kernel start= demand binPath= "$SysPath" DisplayName= "$DisplayName" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        # sc.exe is a native exe: a non-zero exit does not throw, so check it.
        Write-Log "FATAL: sc.exe create failed (exit $LASTEXITCODE)."
        Set-DriverStatus 'Failed' "sc.exe create failed with exit code $LASTEXITCODE"
        exit 4
    }

    Write-Log "Starting '$ServiceName'"
    & sc.exe start $ServiceName | Out-Null
    $startExit = $LASTEXITCODE

    if ($startExit -eq 0) {
        Write-Log "Driver started."
        Set-DriverStatus 'Running' 'Kernel driver service registered and running.'
        exit 0
    }

    # 577 = ERROR_INVALID_IMAGE_HASH: Windows refused the signature. That is the
    # expected result for a test-signed driver without test-signing mode on, and
    # it is a machine-policy problem, not a broken install.
    $hint = if ($startExit -eq 577) {
        "Windows refused the driver signature (577). The driver is $SignState-signed: enable test-signing mode (bcdedit /set testsigning on, then reboot; Secure Boot usually has to be off) or install a production-signed build."
    } else {
        "sc.exe start returned $startExit."
    }
    Write-Log "Driver service registered but did NOT start. $hint"
    Set-DriverStatus 'Registered' $hint
    exit 0
}
catch {
    Write-Log ("FATAL: {0}" -f $_.Exception.Message)
    Set-DriverStatus 'Failed' $_.Exception.Message
    exit 1
}
