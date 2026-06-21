param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$TargetFramework = "net10.0-windows",
    [switch]$SelfContained,
    [switch]$Clean,
    # Bring-up ONLY: compiles in the raw hardware probes (smbus-read/smu-read/superio-read/
    # ene-read/ene-set) and stamps a loud DEV BUILD banner. NEVER use for a deployment build.
    [switch]$DevProbes
)

$ErrorActionPreference = "Stop"

# No third-party hardware libraries to vendor: LibreHardwareMonitor (and HidSharp) were
# fully removed 2026-06-11 — all sensor/RGB access goes through the BrokerSmbus driver.

$ProjectDir = Join-Path $Root "BrokerSensorBridge"
$ProjectFile = Join-Path $ProjectDir "BrokerSensorBridge.csproj"

if (-not (Test-Path $ProjectFile)) {
    throw "Project file not found: $ProjectFile"
}

# Resolve dotnet to an ABSOLUTE path under a system (admin-writable) directory rather
# than trusting %PATH%. This build is run elevated (Build-All -Bridge), so a planted
# dotnet.exe earlier on PATH would otherwise run as admin (PATH-hijack EoP).
function Resolve-Dotnet {
    $known = @(
        (Join-Path $env:ProgramW6432 "dotnet\dotnet.exe"),
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($known.Count -gt 0) { return (Resolve-Path $known[0]).Path }

    # Fall back to PATH only if it resolves inside a trusted (non-user-writable) root.
    $cmd = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if ($cmd) {
        $trusted = @($env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:SystemRoot) |
            Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') + '\' }
        foreach ($root in $trusted) {
            if ($cmd.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { return $cmd }
        }
        throw "dotnet on PATH resolved to '$cmd', which is outside trusted system directories. Refusing to run it from an elevated build (PATH-hijack guard)."
    }
    throw "dotnet.exe not found in Program Files. Install the .NET 10 SDK."
}
$Dotnet = Resolve-Dotnet
Write-Host "[BridgeBuild] Using dotnet: $Dotnet"

if ($Clean) {
    Write-Host "[BridgeBuild] Cleaning build output."
    Remove-Item (Join-Path $ProjectDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $ProjectDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
}

$PublishDir = Join-Path $Root "publish\BrokerSensorBridge"

# The broker/control services (and any launcher-/hand-started instance) run the bridge EXE
# FROM $PublishDir, so a live process locks the output and the publish fails. Tear everything
# down before republishing:
#   1. stop the SCM services cleanly and WAIT for Stopped (a clean stop won't trip recovery),
#   2. kill any STRAY bridge process from this repo (e.g. started by hand or by a launcher)
#      once the services are confirmed down -- this is the piece the old script missed,
#   3. wait until the published EXE is actually unlocked before deleting/republishing.
$servicesStoppedOk = $true
foreach ($svc in 'BrokerControl','SensorBroker') {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    if (-not $s) { continue }                       # not installed (e.g. launcher-only) -> nothing to stop
    if ($s.Status -ne 'Stopped') {
        Write-Host "[BridgeBuild] Stopping service '$svc' so the publish output isn't locked."
        try { Stop-Service -Name $svc -Force -ErrorAction Stop }
        catch { Write-Warning "[BridgeBuild] Stop-Service '$svc' failed: $($_.Exception.Message)" }
    }
    try { (Get-Service -Name $svc).WaitForStatus('Stopped', '00:00:20') } catch {}
    if ((Get-Service -Name $svc -ErrorAction SilentlyContinue).Status -ne 'Stopped') {
        $servicesStoppedOk = $false
        Write-Warning "[BridgeBuild] Service '$svc' did not reach Stopped; not force-killing it (would trip SCM recovery). Stop it manually if the publish locks."
    }
}

# Kill stray (non-service) bridge processes from this repo only AFTER the services are confirmed
# stopped -- so we never kill a live service process and trigger its auto-restart/recovery.
if ($servicesStoppedOk) {
    $RootFull = (Resolve-Path $Root).Path.TrimEnd('\')
    Get-Process -Name BrokerSensorBridge -ErrorAction SilentlyContinue | ForEach-Object {
        $proc = $_; $procPath = $null
        try { $procPath = $proc.Path } catch {}
        if ($procPath -and $procPath.StartsWith($RootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "[BridgeBuild] Killing stray BrokerSensorBridge.exe (PID $($proc.Id)) holding the publish output."
            try { $proc.Kill(); [void]$proc.WaitForExit(5000) } catch {}
        }
    }
}

# Confirm the published EXE is actually unlocked before we delete/republish (file handles can
# linger a moment after a process exits).
$ExeToReplace = Join-Path $PublishDir "BrokerSensorBridge.exe"
if (Test-Path $ExeToReplace) {
    $deadline = (Get-Date).AddSeconds(15); $unlocked = $false
    while ((Get-Date) -lt $deadline) {
        try { $fs = [System.IO.File]::Open($ExeToReplace, 'Open', 'ReadWrite', 'None'); $fs.Close(); $unlocked = $true; break }
        catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $unlocked) {
        Write-Warning "[BridgeBuild] '$ExeToReplace' is still locked after 15s. Stop the SensorBroker/BrokerControl services (and any consumer holding the exe), then retry."
    }
}

Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

$sc = if ($SelfContained) { "true" } else { "false" }
$dp = if ($DevProbes) { "true" } else { "false" }
Write-Host "[BridgeBuild] Publishing BrokerSensorBridge ($TargetFramework, self-contained=$sc, DevProbes=$dp)."
if ($DevProbes) { Write-Warning "DevProbes=true -- this build COMPILES IN the raw hardware probes and is NOT a deployment build." }

& $Dotnet publish $ProjectFile `
    -c Release `
    -r win-x64 `
    -p:TargetFramework=$TargetFramework `
    -p:SelfContained=$sc `
    -p:DevProbes=$dp `
    -p:PublishSingleFile=false `
    -o $PublishDir

# dotnet returns non-zero on a failed publish (e.g. a locked output file). Fail loudly rather
# than leaving a stale binary in place and reporting success.
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE). If a file was locked, stop the SensorBroker/BrokerControl services and retry."
}

$Exe = Join-Path $PublishDir "BrokerSensorBridge.exe"
if (-not (Test-Path $Exe)) {
    throw "Publish succeeded but exe was not found: $Exe"
}

Write-Host "[BridgeBuild] Built: $Exe"
Write-Host "[BridgeBuild] Verify after install (NON-admin): BrokerSensorBridge.exe --client --op=sensor.list"
