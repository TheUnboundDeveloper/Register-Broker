<#
  DEV-ONLY code-signing for BrokerSensorBridge.exe (and any client exe).

  The broker authorizes control-channel clients by Authenticode signer
  thumbprint (ClientAuthorization.AllowedClientSigners). This script signs the
  built exe with a self-signed test code-signing cert and prints the thumbprint
  to paste into appsettings.json.

  Unlike driver test-signing, this does NOT need elevation and does NOT install
  the cert into LocalMachine\Root: the broker pins the exact thumbprint and
  accepts an untrusted root (PeerSignature treats CERT_E_UNTRUSTEDROOT as
  "signature intact, pin by thumbprint"). It is NOT a distributable signature —
  production wants a real (EV) cert. See docs\SIGNING-AND-DEPLOYMENT.md.

  Usage:
    .\scripts\Sign-BrokerSensorBridge.ps1                       # signs the Release build
    .\scripts\Sign-BrokerSensorBridge.ps1 -ExePath C:\path\to\BrokerSensorBridge.exe
#>
param(
    [string]$ExePath,
    [string]$CertName = "Broker Test Cert"
)
$ErrorActionPreference = "Stop"

if (-not $ExePath) {
    # Look where the build actually puts the exe, in priority order: the canonical PUBLISH output
    # (Build-BrokerSensorBridge.ps1 -> publish\BrokerSensorBridge), then a Release build with the
    # win-x64 RuntimeIdentifier subfolder, then a plain Release build.
    $candidates = @(
        (Join-Path $PSScriptRoot "..\publish\BrokerSensorBridge\BrokerSensorBridge.exe"),
        (Join-Path $PSScriptRoot "..\BrokerSensorBridge\bin\Release\net10.0-windows\win-x64\BrokerSensorBridge.exe"),
        (Join-Path $PSScriptRoot "..\BrokerSensorBridge\bin\Release\net10.0-windows\BrokerSensorBridge.exe")
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "BrokerSensorBridge.exe not found. Build it first: .\scripts\Build-BrokerSensorBridge.ps1 " +
          "(publishes to publish\BrokerSensorBridge), or pass -ExePath C:\path\to\BrokerSensorBridge.exe."
}
$ExePath = (Resolve-Path $ExePath).Path

# Reuse an existing test cert with this subject if present, else create one.
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

$signtool = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK/WDK." }

Write-Host "[sign] Signing $ExePath"
& $signtool.FullName sign /v /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $ExePath
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)." }

Write-Host ""
Write-Host "[sign] Done. Authorize this binary by signer: add the thumbprint to" -ForegroundColor Cyan
Write-Host "       appsettings.json -> AllowedClientSigners, and set RequireAuthorizedClient: true" -ForegroundColor Cyan
Write-Host ""
Write-Host "  `"AllowedClientSigners`": [ `"$($cert.Thumbprint)`" ]" -ForegroundColor Green
Write-Host ""
Write-Host "Thumbprint: $($cert.Thumbprint)"
