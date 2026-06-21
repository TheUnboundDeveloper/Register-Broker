<#
  Assemble the Microsoft-attestation submission package for BrokerSmbus.sys.

  This is the scripted form of docs\DRIVER-SIGNING-ATTESTATION.md sections 3-6:
      stage (inf + sys)  ->  InfVerif (if present)  ->  Inf2Cat (.cat)
      ->  makecab (.cab)  ->  OPTIONAL EV-sign the .cab

  Two phases, split by the EV certificate:
    * Phase A (NO cert needed): everything up to and including the .cab. Run this
      now to prove the package assembles and the version triangle is in lockstep.
    * Phase B (EV cert provisioned): pass -EvThumbprint <thumb> to EV-sign the
      .cab. That signed .cab is what you upload to Partner Center -> Windows
      Hardware -> Attestation. Microsoft returns the signed .cat to deploy.

  The EV cert is a DigiCert KeyLocker CLOUD HSM cert (GoGetSSL EV Cloud Code
  Signing), not a USB token. Before signing: install the DigiCert KeyLocker
  client tools, authenticate (KeyLocker env vars / API key), and run
  `smctl windows certsync` once so the cert lands in Cert:\CurrentUser\My with
  its key reachable via the DigiCert KSP. signtool /sha1 then routes to the HSM.

  This script does NOT build the driver and does NOT touch the running services.
  Build a clean, version-stamped .sys first (the package is over one exact binary):
      .\scripts\Stop-BrokerServices.ps1
      .\scripts\Build-All.ps1 -Bridge -Driver -Clean

  The #1 attestation rejection cause is a version / KMDF mismatch, so this script
  HARD-FAILS if BrokerSmbus.rc, BrokerSmbus.inf (DriverVer) and the built .sys do
  not agree on the version. A blank .sys version means it was built before the .rc
  was linked in -- rebuild. Override only if you know why: -AllowVersionMismatch.

  Does NOT need elevation. PowerShell 5.1 compatible (ASCII).
#>
param(
    [string]$SysPath,
    [string]$InfPath,
    [string]$RcPath,
    [string]$OutDir       = (Join-Path $env:TEMP "BrokerSmbus-pkg"),
    [string]$OsTargets    = "10_X64",
    [string]$KitRoot      = "C:\Program Files (x86)\Windows Kits\10",
    [string]$EvThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$AllowVersionMismatch
)
$ErrorActionPreference = "Stop"

# --- Resolve the three inputs (default to the repo layout) ---------------------
if (-not $SysPath) { $SysPath = Join-Path $PSScriptRoot "..\x64\Release\BrokerSmbus.sys" }
if (-not $InfPath) { $InfPath = Join-Path $PSScriptRoot "..\BrokerSmbus.inf" }
if (-not $RcPath)  { $RcPath  = Join-Path $PSScriptRoot "..\BrokerSmbus.rc" }
foreach ($p in @($SysPath, $InfPath, $RcPath)) {
    if (-not (Test-Path $p)) { throw "Required input not found: $p" }
}
$SysPath = (Resolve-Path $SysPath).Path
$InfPath = (Resolve-Path $InfPath).Path
$RcPath  = (Resolve-Path $RcPath).Path

# --- Version triangle: .rc vs .inf DriverVer vs the built .sys ------------------
# All three must agree or attestation rejects the package. The .sys FileVersion is
# the authoritative built value; a blank one means the .rc was not linked in.
$rc  = Get-Content $RcPath  -Raw
$inf = Get-Content $InfPath -Raw

$rcFileVer = if ($rc  -match 'VER_FILEVERSION_STR\s+"([0-9.]+)')    { $matches[1] } else { $null }
$rcProdVer = if ($rc  -match 'VER_PRODUCTVERSION_STR\s+"([0-9.]+)') { $matches[1] } else { $null }
$infVer    = if ($inf -match 'DriverVer\s*=\s*[\d/]+\s*,\s*([0-9.]+)') { $matches[1] } else { $null }
$infKmdf   = if ($inf -match 'KmdfLibraryVersion\s*=\s*([0-9.]+)')  { $matches[1] } else { $null }
$catName   = if ($inf -match 'CatalogFile\s*=\s*(\S+)')             { $matches[1].Trim() } else { $null }
$sysVer    = (Get-Item $SysPath).VersionInfo.FileVersion
if ($sysVer) { $sysVer = $sysVer.Trim() }

Write-Host "[pkg] version triangle:"
Write-Host ("       .rc  FileVersion    : {0}" -f ($rcFileVer | ForEach-Object { $_ }))
Write-Host ("       .rc  ProductVersion : {0}" -f ($rcProdVer | ForEach-Object { $_ }))
Write-Host ("       .inf DriverVer      : {0}" -f ($infVer    | ForEach-Object { $_ }))
Write-Host ("       .sys FileVersion    : {0}" -f $(if ($sysVer) { $sysVer } else { "<BLANK>" }))

if (-not $catName) { throw "CatalogFile not found in $InfPath (Inf2Cat needs it)." }

function ConvertTo-Version([string]$v) { try { [version]$v } catch { $null } }
$vRc  = ConvertTo-Version $rcFileVer
$vInf = ConvertTo-Version $infVer
$vSys = ConvertTo-Version $sysVer

$versionsOk = $vRc -and $vInf -and $vSys -and ($vRc -eq $vInf) -and ($vRc -eq $vSys)
if (-not $versionsOk) {
    $msg = "Version triangle mismatch (.rc=$rcFileVer .inf=$infVer .sys=$(if($sysVer){$sysVer}else{'<BLANK>'}))."
    if (-not $sysVer) {
        $msg += " The built .sys carries NO version resource -- it predates BrokerSmbus.rc. Rebuild: .\scripts\Build-All.ps1 -Bridge -Driver -Clean"
    }
    if ($AllowVersionMismatch) { Write-Warning "$msg (continuing: -AllowVersionMismatch)" }
    else { throw "$msg  Fix the versions (or pass -AllowVersionMismatch if intentional)." }
} else {
    Write-Host "[pkg] version triangle OK ($rcFileVer)." -ForegroundColor Green
}

# KMDF: the INF's KmdfLibraryVersion must equal the KMDF the .sys was linked
# against. We cannot read the link version from the .sys here, so compare against
# the newest installed KMDF as an informational guard (a WDK upgrade drifts this).
$instKmdf = Get-ChildItem (Join-Path $KitRoot "Include\wdf\kmdf") -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if ($instKmdf -and $infKmdf -and ($instKmdf -ne $infKmdf)) {
    Write-Warning "INF KmdfLibraryVersion=$infKmdf but newest installed KMDF=$instKmdf. If the .sys was linked against $instKmdf, update the INF before submitting."
} else {
    Write-Host "[pkg] KMDF: INF=$infKmdf installed=$instKmdf"
}

# --- Locate tools (Inf2Cat is x86-only; signtool prefer x64) -------------------
$bin = Join-Path $KitRoot "bin"
$inf2cat = Get-ChildItem $bin -Recurse -Filter Inf2Cat.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $inf2cat) { throw "Inf2Cat.exe not found under $bin. Install the WDK." }
$infverif = Get-ChildItem $bin -Recurse -Filter InfVerif.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
$signtool = Get-ChildItem $bin -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
if (-not $signtool) {
    $signtool = Get-ChildItem $bin -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
}
$makecab = (Get-Command makecab.exe -ErrorAction SilentlyContinue).Source
if (-not $makecab) { throw "makecab.exe not found on PATH." }

# --- Stage a clean package directory ------------------------------------------
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutDir | Out-Null
Copy-Item $InfPath (Join-Path $OutDir (Split-Path $InfPath -Leaf))
Copy-Item $SysPath (Join-Path $OutDir "BrokerSmbus.sys")
$pkgInf = Join-Path $OutDir (Split-Path $InfPath -Leaf)
Write-Host "[pkg] staged -> $OutDir"

# --- Validate the INF (optional; Inf2Cat also rejects a broken INF) -----------
if ($infverif) {
    Write-Host "[pkg] InfVerif..."
    & $infverif.FullName /v $pkgInf
    if ($LASTEXITCODE -ne 0) { Write-Warning "InfVerif returned $LASTEXITCODE (warnings on Class=System are expected; fix errors)." }
} else {
    Write-Host "[pkg] InfVerif not installed -- skipping (Inf2Cat still validates)."
}

# --- Generate the catalog ------------------------------------------------------
Write-Host "[pkg] Inf2Cat /os:$OsTargets ..."
& $inf2cat.FullName /driver:"$OutDir" /os:$OsTargets
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed ($LASTEXITCODE)." }
$cat = Join-Path $OutDir $catName
if (-not (Test-Path $cat)) { throw "Inf2Cat reported success but $cat is missing." }
Write-Host "[pkg] catalog: $cat" -ForegroundColor Green

# --- Pack the CAB (inf + sys + cat) -------------------------------------------
$ddf = Join-Path $OutDir "BrokerSmbus.ddf"
$cabName = "BrokerSmbus.cab"
@"
.OPTION EXPLICIT
.Set CabinetNameTemplate=$cabName
.Set DiskDirectoryTemplate=CDROM
.Set CompressionType=MSZIP
.Set Cabinet=on
.Set Compress=on
.Set DiskDirectory1=$OutDir
"$pkgInf"
"$(Join-Path $OutDir 'BrokerSmbus.sys')"
"$cat"
"@ | Set-Content -Path $ddf -Encoding Ascii

Write-Host "[pkg] makecab..."
& $makecab /f $ddf | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makecab failed ($LASTEXITCODE)." }
$cab = Join-Path $OutDir $cabName
if (-not (Test-Path $cab)) { throw "makecab reported success but $cab is missing." }
Write-Host "[pkg] cab: $cab ($((Get-Item $cab).Length) bytes)" -ForegroundColor Green

# --- Phase B: EV-sign the CAB (only if a thumbprint was supplied) --------------
if ($EvThumbprint) {
    # Cloud-HSM cert (DigiCert KeyLocker): run `smctl windows certsync` once first
    # so signtool can reach the cloud key via the DigiCert KSP. Auth is ambient
    # (KeyLocker env vars / API key) -- there is no interactive token PIN.
    Write-Host "[pkg] EV-signing the CAB (thumbprint $EvThumbprint) via the DigiCert KeyLocker cloud HSM..."
    & $signtool.FullName sign /v /fd SHA256 /sha1 $EvThumbprint /tr $TimestampUrl /td SHA256 $cab
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)." }
    & $signtool.FullName verify /v /pa $cab
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed ($LASTEXITCODE)." }
    Write-Host ""
    Write-Host "[pkg] SIGNED CAB ready to upload:" -ForegroundColor Cyan
    Write-Host "      $cab"
    Write-Host "      -> Partner Center > Windows Hardware > Drivers > New submission > Attestation"
} else {
    Write-Host ""
    Write-Host "[pkg] UNSIGNED package assembled (Phase A complete)." -ForegroundColor Cyan
    Write-Host "      When the EV cert is provisioned (DigiCert KeyLocker), EV-sign the CAB:"
    Write-Host "        .\BrokerSmbusDriver\scripts\Build-DriverPackage.ps1 -EvThumbprint <thumb>"
    Write-Host "      (KeyLocker: run 'smctl windows certsync' first, then find the thumbprint:"
    Write-Host "         Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert)"
}
