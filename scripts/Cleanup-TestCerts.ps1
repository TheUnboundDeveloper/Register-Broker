<#
  Cleans up the Broker DEV test code-signing certificates that the signing
  scripts create. Historically Sign-Driver-TestCert.ps1 minted a NEW self-signed
  root every run and imported it into LocalMachine\Root + LocalMachine\TrustedPublisher
  without ever removing the old ones, so trusted machine-wide root CAs piled up
  (each with an exportable private key in CurrentUser\My) -- a standing
  machine-wide trust-anchor exposure.

  Default behaviour: DEDUPE. Keep only the single newest cert per subject (so a
  test-signed driver still loads and the broker signer-pin still matches), and
  remove every older duplicate from CurrentUser\My, LocalMachine\Root, and
  LocalMachine\TrustedPublisher.

  -All: remove EVERY Broker test cert (current + legacy OpenRGB names) from all three stores
  (full reset). After
  -All you must re-run the signing scripts before the driver will load / the
  broker pin will match again:
      BrokerSmbusDriver\scripts\Sign-Driver-TestCert.ps1     (elevated)
      scripts\Sign-BrokerSensorBridge.ps1

  Run ELEVATED (it edits LocalMachine stores).
#>
param(
    [switch]$All,
    # Subject CNs treated as Broker dev test certs. Includes the LEGACY "OpenRGB *" names so a
    # full reset (-All) still purges any certs minted before the de-branding rename.
    [string[]]$Subjects = @(
        "CN=Broker SMBus Test Cert", "CN=Broker Test Cert",          # current names
        "CN=OpenRGB SMBus Test Cert", "CN=OpenRGB Broker Test Cert"  # legacy names (pre-rename)
    ),
    [switch]$WhatIf
)
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated PowerShell (it edits LocalMachine cert stores)." }

$stores = @("Cert:\CurrentUser\My", "Cert:\LocalMachine\Root", "Cert:\LocalMachine\TrustedPublisher")

# Determine which thumbprints to KEEP (newest per subject) unless -All.
$keep = @{}
if (-not $All) {
    foreach ($subject in $Subjects) {
        $newest = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $subject } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
        # Fall back to the newest copy anywhere if the private key store has none.
        if (-not $newest) {
            $newest = Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
                Where-Object { $_.Subject -eq $subject } |
                Sort-Object NotAfter -Descending | Select-Object -First 1
        }
        if ($newest) {
            $keep[$newest.Thumbprint] = $true
            Write-Host "[cleanup] Keeping newest '$subject' = $($newest.Thumbprint)" -ForegroundColor Green
        }
    }
}

$removed = 0
foreach ($store in $stores) {
    $certs = Get-ChildItem $store -ErrorAction SilentlyContinue |
        Where-Object { $Subjects -contains $_.Subject }
    foreach ($c in $certs) {
        if (-not $All -and $keep.ContainsKey($c.Thumbprint)) { continue }
        $where = "$store  $($c.Subject)  $($c.Thumbprint)"
        if ($WhatIf) {
            Write-Host "[cleanup] WOULD remove $where" -ForegroundColor Yellow
        } else {
            Remove-Item -Path $c.PSPath -Force -DeleteKey -ErrorAction SilentlyContinue
            Write-Host "[cleanup] Removed $where"
            $removed++
        }
    }
}

Write-Host ""
if ($WhatIf) {
    Write-Host "[cleanup] WhatIf only -- nothing removed." -ForegroundColor Cyan
} elseif ($All) {
    Write-Host "[cleanup] Removed $removed cert(s). FULL RESET -- re-run the signing scripts:" -ForegroundColor Cyan
    Write-Host "          BrokerSmbusDriver\scripts\Sign-Driver-TestCert.ps1   (elevated)"
    Write-Host "          scripts\Sign-BrokerSensorBridge.ps1"
} else {
    Write-Host "[cleanup] Removed $removed duplicate cert(s); kept the newest per subject." -ForegroundColor Cyan
    Write-Host "          For a full reset (then re-sign), run with -All."
}
