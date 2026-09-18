<#
  Setup-Config.ps1

  Applies the installer's optional choices, which are configuration rather than
  files and so cannot be expressed in the MSI tables directly:

    * The opt-in read-only sensor backends (gpu.* / aqua.* / ups.*) are flags in
      the broker's appsettings.json, read from AppContext.BaseDirectory at
      service start.
    * The RGB control service is created by the MSI but left on demand-start;
      whether it is enabled and running follows the RGB feature selection. A
      sensors-only install therefore ends up with no write-capable RGB service
      running at all, which is the point.

  These flags must be set on the SERVER side (the service's own appsettings.json)
  - passing them on a client invocation is a no-op, a lesson the dev-box deploy
  learned the hard way.

  Run by a deferred, no-impersonate custom action as LocalSystem.

  MUST SURVIVE ConstrainedLanguage MODE. A host with WDAC or AppLocker in force
  runs PowerShell constrained, where creating or calling .NET types throws. Stick
  to cmdlets; no [Type]::Method(), no New-Object on anything but core types.
  Test it with:
      powershell -NoProfile -Command "$ExecutionContext.SessionState.LanguageMode =
        'ConstrainedLanguage'; & .\Setup-Config.ps1 -Root '<root>.' -Flags 1000"

  Windows PowerShell 5.1 compatible, ASCII only.
#>
[CmdletBinding()]
param(
    # The install root (INSTALLFOLDER); appsettings.json and the log are derived
    # from it. Short by necessity - see the note in Actions.wxs about the
    # 255-character CustomAction.Target column.
    [string]$Root = '',

    # Positional selection flags, in order: RGB control, GPU, Aquacomputer, UPS.
    # e.g. "1010" = RGB control on, GPU off, aqua on, UPS off.
    [ValidatePattern('^[01]{4}$')]
    [string]$Flags = '0000',

    [string]$ControlServiceName = 'BrokerControl'
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
# The installer passes "<dir>\." on purpose: an MSI directory property ends in a
# backslash, and a backslash right before a closing quote escapes it. Normalize
# the marker back off before building any paths.
$Root = ($Root -replace '[\\/]\.$', '').TrimEnd('\', '/')

$AppSettings = Join-Path $Root 'bin\appsettings.json'
$LogPath     = Join-Path $Root 'setup\config-setup.log'

$RgbControl  = $Flags.Substring(0, 1)
$GpuSensors  = $Flags.Substring(1, 1)
$AquaSensors = $Flags.Substring(2, 1)
$UpsSensors  = $Flags.Substring(3, 1)

function Write-Log([string]$Message) {
    $line = ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    Write-Output $line
    if ($LogPath) {
        try {
            $dir = Split-Path -Parent $LogPath
            if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
            Add-Content -Path $LogPath -Value $line -Encoding ASCII
        } catch { }
    }
}

try {
    Write-Log "=== Configure broker ==="
    Write-Log ("RgbControl={0} GpuSensors={1} AquaSensors={2} UpsSensors={3}" -f $RgbControl, $GpuSensors, $AquaSensors, $UpsSensors)

    #----------------------------------------------------------------------
    # 1. appsettings.json. A missing or unparseable file means a broken
    #    package, so that IS fatal - otherwise the user silently gets none of
    #    the backends they selected.
    #----------------------------------------------------------------------
    if (-not (Test-Path $AppSettings)) {
        Write-Log "FATAL: appsettings.json not found at '$AppSettings'."
        exit 2
    }

    $cfg = $null
    try {
        $cfg = Get-Content $AppSettings -Raw | ConvertFrom-Json
    } catch {
        Write-Log ("FATAL: appsettings.json failed to parse: {0}" -f $_.Exception.Message)
        exit 3
    }

    $cfg | Add-Member -NotePropertyName AllowGpuSensors  -NotePropertyValue ($GpuSensors  -eq '1') -Force
    $cfg | Add-Member -NotePropertyName AllowAquaSensors -NotePropertyValue ($AquaSensors -eq '1') -Force
    $cfg | Add-Member -NotePropertyName AllowUpsSensors  -NotePropertyValue ($UpsSensors  -eq '1') -Force

    # Set-Content, NOT [System.IO.File]::WriteAllText with a constructed
    # UTF8Encoding. This script runs as an MSI custom action, and a host with
    # WDAC or AppLocker in force runs PowerShell in ConstrainedLanguage mode,
    # where creating a .NET type throws "Only core types are supported in this
    # language mode". That failure used to roll the entire install back, which
    # looks from the outside exactly like an installer that does nothing: no
    # files, no services, no Add/Remove Programs entry.
    #
    # The cost is that PowerShell 5.1's -Encoding UTF8 writes a BOM, so the file
    # is no longer byte-identical to a hand-edited one. .NET's configuration
    # binder reads a BOM without complaining, and surviving a hardened host is
    # worth more than identical bytes. Depth 32 so no nested section is
    # truncated to a string.
    $json = $cfg | ConvertTo-Json -Depth 32
    Set-Content -Path $AppSettings -Value $json -Encoding UTF8

    Write-Log "appsettings.json updated: $AppSettings"

    #----------------------------------------------------------------------
    # 2. RGB control service. Best-effort: the files and the sensor service are
    #    already in place, so a service-control hiccup is logged, not fatal.
    #----------------------------------------------------------------------
    $svc = Get-Service -Name $ControlServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Log "NOTE: '$ControlServiceName' not present; skipping service configuration."
    }
    elseif ($RgbControl -eq '1') {
        try {
            Set-Service -Name $ControlServiceName -StartupType Automatic
            & sc.exe failure $ControlServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
            if ((Get-Service -Name $ControlServiceName).Status -ne 'Running') {
                Start-Service -Name $ControlServiceName
            }
            Write-Log "'$ControlServiceName' enabled (Automatic) and started."
        } catch {
            Write-Log ("WARN: could not enable/start '{0}': {1}" -f $ControlServiceName, $_.Exception.Message)
        }
    }
    else {
        try {
            if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ControlServiceName -Force }
            Set-Service -Name $ControlServiceName -StartupType Disabled
            Write-Log "'$ControlServiceName' stopped and disabled (RGB control not selected)."
        } catch {
            Write-Log ("WARN: could not disable '{0}': {1}" -f $ControlServiceName, $_.Exception.Message)
        }
    }

    Write-Log "Configuration complete."
    exit 0
}
catch {
    Write-Log ("FATAL: {0}" -f $_.Exception.Message)
    exit 1
}
