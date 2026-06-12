<#
  DEV-ONLY test signing for BrokerSmbus.sys.

  Generates a self-signed code-signing cert, trusts it on THIS machine, and
  signs the driver so it can load with test-signing enabled. This is for a
  lab/dev box only -- it is NOT a distributable signature. Production requires
  an EV certificate + Microsoft attestation signing (see BROKER-DESIGN.md).

  Elevation is only needed to import the cert into the LocalMachine trust
  stores, and test-signing mode loads the driver either way -- so when run
  non-elevated this script still SIGNS (the part that matters) and just skips
  the trust import with a warning. After signing you must enable test signing
  and reboot once:
      bcdedit /set testsigning on
  (Secure Boot must be off for test-signed drivers to load.)
#>
param(
    [string]$SysPath,
    [string]$CertName = "Broker SMBus Test Cert"
)
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $SysPath) { $SysPath = Join-Path $PSScriptRoot "..\x64\Release\BrokerSmbus.sys" }
if (-not (Test-Path $SysPath)) { throw "Driver .sys not found: $SysPath. Build it first (Build-Driver.ps1)." }
$SysPath = (Resolve-Path $SysPath).Path

# Reuse an existing test cert with this subject if present, else create one.
# (Previously this minted a NEW root every run and never cleaned up, piling up
#  trusted machine-wide root CAs. Reuse keeps exactly one. NonExportable so the
#  private key cannot be exported/stolen by same-user code -- signtool still uses
#  it in place.) Run scripts\Cleanup-TestCerts.ps1 to purge old duplicates.
$subject = "CN=$CertName"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host "[sign] Creating self-signed code-signing certificate '$CertName'."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -KeyExportPolicy NonExportable
} else {
    Write-Host "[sign] Reusing existing certificate '$CertName' (thumbprint $($cert.Thumbprint))."
}

# Trust it on THIS machine -- but only import if this exact thumbprint is not
# already present in each store (idempotent). Needs elevation; test-signing
# mode loads the driver without it, so non-elevated runs just skip this.
if ($admin) {
    $cer = Join-Path $env:TEMP "BrokerSmbusTest.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    foreach ($store in @("Cert:\LocalMachine\Root", "Cert:\LocalMachine\TrustedPublisher")) {
        $present = Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
        if ($present) {
            Write-Host "[sign] Already trusted in $store."
        } else {
            Write-Host "[sign] Trusting the test cert in $store."
            Import-Certificate -FilePath $cer -CertStoreLocation $store | Out-Null
        }
    }
    Remove-Item $cer -Force -ErrorAction SilentlyContinue
} else {
    Write-Warning "Not elevated: skipping the LocalMachine trust import (test-signing mode does not need it; run elevated once if you want the cert trusted)."
}

$signtool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK/WDK." }

Write-Host "[sign] Signing $SysPath"
& $signtool.FullName sign /v /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $SysPath
if ($LASTEXITCODE -ne 0) {
    # The timestamp server needs internet; for a dev test signature the timestamp
    # is optional (it only extends validity past cert expiry). Retry without it
    # rather than leaving the driver UNSIGNED -- an unsigned .sys at the service's
    # ImagePath is exactly the 2026-06-11 breakage.
    Write-Warning "signtool with timestamp failed ($LASTEXITCODE); retrying without a timestamp server."
    & $signtool.FullName sign /v /fd SHA256 /sha1 $cert.Thumbprint $SysPath
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)." }
}

# Verify the result so a chained caller (Build-Driver-DirectLink.ps1) can trust it.
$sig = Get-AuthenticodeSignature $SysPath
if (-not $sig.SignerCertificate) { throw "Post-sign verification failed: $SysPath has no signer." }
Write-Host "[sign] Verified: signer $($sig.SignerCertificate.Subject) (status: $($sig.Status))" -ForegroundColor Green

Write-Host ""
Write-Host "[sign] Done. To load a test-signed driver:" -ForegroundColor Cyan
Write-Host "       bcdedit /set testsigning on   (admin), then reboot. Secure Boot must be off."
