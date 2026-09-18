<#
  Build-Installer.ps1

  Builds the redistributable installer:

      dist\RegisterBroker-<version>-x64.msi   feature tree, GPO/Intune-friendly
      dist\RegisterBroker-<version>-x64.exe   the same MSI in one signed file

  Pipeline:
      publish (self-contained)  ->  stage  ->  MSI  ->  bundle  ->  optional EV sign

  Both payloads publish SELF-CONTAINED, so the installer has no .NET
  prerequisite and works on a clean Windows box. That is what makes the package
  ~190 MB staged (cabinet compression brings the shipped artifacts well under
  that).

  DRIVER SIGNATURE. The package records what it actually contains. A .sys signed
  by Microsoft's attestation service is "production" and the kernel-driver
  feature is selected by default; anything else is "test" (or "unsigned") and
  the feature is listed but OFF by default, with an explicit acknowledgement
  required before it will install. This is the packaging gate expressed in the
  build rather than in a README: see docs\SIGNING-AND-DEPLOYMENT.md.

  PUBLISHER IDENTITY. -Manufacturer becomes the "Publisher" string in Add/Remove
  Programs, and it has to be the same publisher a validator can see elsewhere -
  the signing certificate's subject organization and the Microsoft Store
  publisher display name. When -EvThumbprint is given the two are cross-checked
  here, because a mismatch is invisible until a submission comes back saying the
  app name and publisher "could not be identified". See docs\STORE-SUBMISSION.md.

  Does NOT need elevation - it only builds. Installing the result does.
  Does NOT touch the running services or the dev-box publish\ tree.

  Windows PowerShell 5.1 compatible, ASCII only.

  Examples:
      .\scripts\Build-Installer.ps1
      .\scripts\Build-Installer.ps1 -SkipPublish            # re-package staged bits
      .\scripts\Build-Installer.ps1 -EvThumbprint <thumb>   # EV-sign both artifacts
      .\scripts\Build-Installer.ps1 -Store -EvThumbprint <thumb>   # Store submission build
#>
[CmdletBinding()]
param(
    # Defaults to <Version> in BrokerSensorBridge.csproj - the repo's single
    # source of truth for the release version.
    [string]$Version = '',

    [string]$Configuration = 'Release',

    # The driver binary to package. Default is the direct-link build output.
    [string]$SysPath = '',

    # production | test | unsigned | none. Detected from the .sys unless
    # overridden; "none" means the package carries no driver payload at all.
    [ValidateSet('', 'production', 'test', 'unsigned', 'none')]
    [string]$DriverSignState = '',

    # The publisher identity: the MSI's Manufacturer and the bundle's, which is
    # exactly what Add/Remove Programs shows as "Publisher". It must be the
    # legal publisher name rather than a handle - see the header note.
    [string]$Manufacturer = 'UNBOUNDED ENGINEERING LLC',

    [string]$OutDir = '',

    # Reuse whatever is already in installer\stage (fast iteration on the
    # packaging itself).
    [switch]$SkipPublish,

    # Build only the .msi.
    [switch]$NoBundle,

    # Sign every PE file in the staged payload that is not signed already (this
    # repo's own managed assemblies, Avalonia, NAudio). The Microsoft Store
    # requires the installer AND every PE it carries to chain to a trusted root;
    # a normal release only needs the two artifacts signed. Implied by -Store.
    [switch]$SignPayload,

    # Build the artifact for a Microsoft Store submission: EV-signed end to end
    # (installer plus payload) and carrying no kernel driver unless the .sys is
    # production-signed, because a test-signed binary cannot chain to a
    # Microsoft-trusted root. See docs\STORE-SUBMISSION.md.
    [switch]$Store,

    # EV code signing (DigiCert KeyLocker cloud HSM - see
    # docs\DRIVER-SIGNING-ATTESTATION.md for the smctl/KSP setup). Signs the
    # MSI, then the bundle via the detach/reattach dance Burn requires.
    [string]$EvThumbprint = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$InstallerDir = Join-Path $RepoRoot 'installer'
$StageDir    = Join-Path $InstallerDir 'stage'
$BundleDir   = Join-Path $InstallerDir 'bundle'
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }

function Write-Step([string]$Message) { Write-Host ""; Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Info([string]$Message) { Write-Host "    $Message" }

#--------------------------------------------------------------------------
# 0. Inputs
#--------------------------------------------------------------------------
Write-Step "Resolving inputs"

$BridgeProj  = Join-Path $RepoRoot 'BrokerSensorBridge\BrokerSensorBridge.csproj'
$ConsoleProj = Join-Path $RepoRoot 'Test_GUI\ReferenceConsole\ReferenceConsole\ReferenceConsole.csproj'
foreach ($p in @($BridgeProj, $ConsoleProj)) {
    if (-not (Test-Path $p)) { throw "Required project not found: $p" }
}

if (-not $Version) {
    $csproj = Get-Content $BridgeProj -Raw
    if ($csproj -match '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') {
        $Version = $matches[1]
    } else {
        throw "Could not read <Version> from $BridgeProj - pass -Version explicitly."
    }
}
# MSI ProductVersion is major.minor.build with hard field limits; a value that
# overflows silently truncates and breaks upgrade detection.
$v = [version]$Version
if ($v.Major -gt 255 -or $v.Minor -gt 255 -or $v.Build -gt 65535) {
    throw "Version '$Version' does not fit an MSI ProductVersion (max 255.255.65535)."
}
Write-Info "Version        : $Version"

if (-not $Manufacturer.Trim()) { throw "-Manufacturer cannot be empty: it is the Add/Remove Programs 'Publisher' string." }
Write-Info "Publisher      : $Manufacturer"

if ($Store) {
    # Every PE in a Store package has to be signed, so there is no such thing as
    # an unsigned Store build - fail now rather than after a five-minute publish.
    if (-not $EvThumbprint) {
        throw ("-Store requires -EvThumbprint: the Store requires the installer and every PE file it " +
               "carries to be signed by a certificate chaining to a Microsoft-trusted root.")
    }
    $SignPayload = $true
    Write-Info "Mode           : Microsoft Store submission build"
}

if (-not $SysPath) { $SysPath = Join-Path $RepoRoot 'BrokerSmbusDriver\x64\Release\BrokerSmbus.sys' }
$haveDriver = Test-Path $SysPath
if ($haveDriver) {
    $SysPath = (Resolve-Path $SysPath).Path
    Write-Info "Driver         : $SysPath"
} else {
    Write-Warning "Driver .sys not found at '$SysPath'. The package will be built WITHOUT the kernel driver feature payload."
}

# The console is a WinExe and locks its own build output while running.
$running = Get-Process -Name 'ReferenceConsole' -ErrorAction SilentlyContinue
if ($running) {
    Write-Info "Closing the running Reference Console so its build output is writable."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

#--------------------------------------------------------------------------
# 1. Driver signature state -> what the package is allowed to claim
#--------------------------------------------------------------------------
Write-Step "Determining driver signature state"

if ($DriverSignState) {
    Write-Warning "DriverSignState overridden on the command line: '$DriverSignState'. Make sure that is actually true of the binary being packaged."
    $signState = $DriverSignState
}
elseif (-not $haveDriver) {
    # "none", not "unsigned": there is no binary to describe. The distinction
    # matters because a package that claims a sign state must carry the .sys,
    # and Test-Installer.ps1 gates on exactly that.
    $signState = 'none'
    Write-Info "No driver staged -> 'none'."
}
else {
    $sig = Get-AuthenticodeSignature $SysPath
    $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '<none>' }
    Write-Info "Status : $($sig.Status)"
    Write-Info "Signer : $subject"

    if (-not $sig.SignerCertificate -or $sig.Status -in @('NotSigned', 'HashMismatch')) {
        $signState = 'unsigned'
    }
    elseif ($subject -match 'Microsoft Windows Hardware Compatibility Publisher') {
        # The only signer Windows accepts for a production kernel driver on a
        # normal machine: the attestation service's own certificate.
        $signState = 'production'
    }
    else {
        # Covers the dev test certificate. Note that its status reads Valid on
        # this box because the test root was imported locally - trusted HERE is
        # not the same as trusted on a user's machine, so signer identity
        # decides, not Status.
        $signState = 'test'
    }
}

# A Store package must not carry a driver Windows would refuse: the Store's own
# requirement is that every PE chains to a Microsoft-trusted root, and a
# test-signed .sys chains to a local test root. Leave it out rather than ship a
# feature that cannot work on a customer's machine.
if ($Store -and $signState -ne 'production') {
    if ($haveDriver) {
        Write-Warning ("Store build: the staged driver is $signState-signed, so it is being LEFT OUT of the package. " +
                       "The broker services and console install and run; the sensor catalog reports no hardware " +
                       "until an attestation-signed driver is packaged. See docs\SIGNING-AND-DEPLOYMENT.md.")
    }
    $haveDriver = $false
    $signState  = 'none'
}
if ($signState -eq 'none') { $haveDriver = $false }

# 1 = on by default (production only); 1000 = listed but off; 0 = disabled and
# hidden, which is the only honest level for a package with no driver payload.
$driverFeatureLevel = switch ($signState) {
    'production' { '1' }
    'none'       { '0' }
    default      { '1000' }
}
Write-Info "Sign state     : $signState"
Write-Info ("Driver feature : {0}" -f $(switch ($driverFeatureLevel) {
    '1'     { 'selected by default' }
    '0'     { 'disabled and hidden (no driver in this package)' }
    default { 'listed, OFF by default (explicit opt-in required)' }
}))

#--------------------------------------------------------------------------
# 1b. Signing preflight
#
# Resolved BEFORE anything is published or staged: a bad thumbprint, a stale
# KeyLocker client certificate or a publisher that disagrees with the
# certificate should cost a second, not a full rebuild. The payload signer needs
# these helpers too, and PowerShell binds functions at execution time, so they
# have to exist before the staging step runs.
#--------------------------------------------------------------------------
function Get-SignTool {
    $kit = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not (Test-Path $kit)) { throw "Windows Kits not found at $kit - signtool is part of the WDK/SDK." }
    $candidates = Get-ChildItem $kit -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^10\.' } |
                  Sort-Object Name -Descending
    foreach ($c in $candidates) {
        $st = Join-Path $c.FullName 'x64\signtool.exe'
        if (Test-Path $st) { return $st }
    }
    throw "signtool.exe not found under $kit."
}

function Invoke-Sign([string]$Path, [string]$SignTool, [string]$Description) {
    Write-Info "sign: $(Split-Path $Path -Leaf)"
    & $SignTool sign /sha1 $EvThumbprint /fd sha256 /td sha256 /tr $TimestampUrl /d $Description $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $Path (exit $LASTEXITCODE)." }
}

# The subject organization of the signing certificate. An RDN value containing a
# comma would be quoted and would need a real parser; no code-signing subject
# here does, and a miss only skips the cross-check.
function Get-CertSubjectOrg([string]$Thumbprint) {
    foreach ($store in @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My')) {
        $cert = Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint }
        if ($cert) {
            foreach ($rdn in ($cert.Subject -split ',')) {
                if ($rdn.Trim() -match '^O=(.+)$') { return $matches[1].Trim().Trim('"') }
            }
        }
    }
    return ''
}

$signTool = $null
if ($EvThumbprint) {
    Write-Step "Signing preflight"

    # The DigiCert KeyLocker KSP reads SM_* from the environment, and if the
    # client-auth certificate is missing or stale, signtool fails with an opaque
    # SignerSign() 0x8009002d rather than anything that names the cause.
    $smCert = $env:SM_CLIENT_CERT_FILE
    if ($smCert -and -not (Test-Path $smCert)) {
        throw ("SM_CLIENT_CERT_FILE points at '$smCert', which does not exist. " +
               "The KeyLocker KSP cannot authenticate without it and signtool would fail with " +
               "'SignerSign() failed' (0x8009002d). Point SM_CLIENT_CERT_FILE at the client-auth .p12 and retry.")
    }
    if (-not $env:SM_API_KEY) {
        Write-Warning "SM_API_KEY is not set in this process. If the certificate's key lives in a cloud HSM, signing will fail."
    }

    $signTool = Get-SignTool
    Write-Info "signtool: $signTool"

    # Publisher vs. certificate. Add/Remove Programs showing a publisher that
    # does not match the signature is exactly what Store validation reports as
    # an unidentifiable app name and publisher.
    $certOrg = Get-CertSubjectOrg $EvThumbprint
    if (-not $certOrg) {
        Write-Warning ("Could not read a subject organization for $EvThumbprint from the local certificate stores, " +
                       "so the publisher cross-check was skipped. Confirm by hand that the certificate is issued to '$Manufacturer'.")
    } elseif ($certOrg -ne $Manufacturer) {
        throw ("Publisher mismatch: -Manufacturer is '$Manufacturer' but the signing certificate is issued to '$certOrg'. " +
               "Add/Remove Programs would then advertise a publisher the signature does not back, which fails Microsoft " +
               "Store package validation. Re-run with -Manufacturer '$certOrg', or sign with the matching certificate.")
    } else {
        Write-Info "Publisher matches the signing certificate: $certOrg"
    }
}

#--------------------------------------------------------------------------
# 2. Stage the payload
#--------------------------------------------------------------------------
if ($Clean -and (Test-Path $StageDir)) {
    Write-Step "Cleaning stage"
    Remove-Item $StageDir -Recurse -Force
}

if ($SkipPublish) {
    Write-Step "Skipping publish (-SkipPublish); using the existing stage"
    if (-not (Test-Path (Join-Path $StageDir 'exe\BrokerSensorBridge.exe'))) {
        throw "-SkipPublish was passed but installer\stage\exe\BrokerSensorBridge.exe is missing. Run once without it."
    }
} else {
    Write-Step "Publishing self-contained payloads"

    foreach ($t in @(
        @{ Name = 'app';     Proj = $BridgeProj },
        @{ Name = 'console'; Proj = $ConsoleProj }
    )) {
        $dst = Join-Path $StageDir $t.Name
        if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dst | Out-Null

        Write-Info "publish $($t.Name) ..."
        & dotnet publish $t.Proj -c $Configuration -r win-x64 --self-contained true --nologo -v quiet -o $dst
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($t.Proj) (exit $LASTEXITCODE)." }
    }

    # Debug symbols are a third of the staged weight (libSkiaSharp.pdb alone is
    # tens of MB) and ship no value to an end user.
    $pdbs = Get-ChildItem $StageDir -Recurse -Filter *.pdb -ErrorAction SilentlyContinue
    if ($pdbs) {
        $mb = [math]::Round((($pdbs | Measure-Object Length -Sum).Sum / 1MB), 1)
        $pdbs | Remove-Item -Force
        Write-Info "Removed $($pdbs.Count) .pdb files ($mb MB)."
    }

    # The two executables are authored by hand in the .wxs (a service image path
    # must be its component's key path; the console exe needs a stable File id
    # for the shortcuts). <Files> harvesting has no exclusion attribute, so move
    # them out of the harvested trees rather than trying to filter them out.
    $exeStage = Join-Path $StageDir 'exe'
    if (Test-Path $exeStage) { Remove-Item $exeStage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $exeStage | Out-Null
    foreach ($m in @(
        @{ From = 'app\BrokerSensorBridge.exe'; },
        @{ From = 'console\ReferenceConsole.exe'; }
    )) {
        $src = Join-Path $StageDir $m.From
        if (-not (Test-Path $src)) { throw "Expected published executable not found: $src" }
        Move-Item $src $exeStage -Force
    }
    Write-Info "Moved the two executables to stage\exe (they are authored explicitly)."
}

# Driver payload: the .sys plus an .inf/.cat when a production package exists.
$driverStage = Join-Path $StageDir 'driver'
if (Test-Path $driverStage) { Remove-Item $driverStage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $driverStage | Out-Null
if ($haveDriver) {
    Copy-Item $SysPath $driverStage -Force
    foreach ($ext in @('inf', 'cat')) {
        $extra = Join-Path (Split-Path -Parent $SysPath) ("BrokerSmbus.$ext")
        if (Test-Path $extra) { Copy-Item $extra $driverStage -Force; Write-Info "Staged BrokerSmbus.$ext" }
    }
} else {
    # <Files> harvesting on an empty directory yields an empty component group,
    # which links fine - the feature simply carries nothing.
    Write-Info "Driver stage left empty."
}

$stagedFiles = (Get-ChildItem $StageDir -Recurse -File).Count
$stagedMb    = [math]::Round(((Get-ChildItem $StageDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Info "Staged $stagedFiles files, $stagedMb MB."

#--------------------------------------------------------------------------
# 2b. Sign the payload
#
# "The binary and all of its Portable Executable (PE) files must be digitally
# signed with a code signing certificate that chains up to a certificate issued
# by a CA that is part of the Microsoft Trusted Root Program" - the Store's
# requirement covers what is INSIDE the installer, not just the installer.
#
# A self-contained publish is mostly Microsoft-signed runtime files, which are
# left exactly as they are: re-signing them would replace Microsoft's signature
# with ours. Only files that carry no signature at all are signed here, and they
# are signed BEFORE the MSI is built, because the package stores file hashes.
#--------------------------------------------------------------------------
if ($SignPayload) {
    if (-not $EvThumbprint) { throw "-SignPayload needs -EvThumbprint." }
    Write-Step "Signing the payload"

    $pe = @(Get-ChildItem $StageDir -Recurse -File -Include *.exe, *.dll)
    $unsigned = New-Object System.Collections.ArrayList
    $other    = New-Object System.Collections.ArrayList
    foreach ($f in $pe) {
        $status = (Get-AuthenticodeSignature $f.FullName).Status
        if ($status -eq 'NotSigned') { [void]$unsigned.Add($f.FullName) }
        elseif ($status -ne 'Valid') { [void]$other.Add(('{0} ({1})' -f $f.Name, $status)) }
    }
    Write-Info ("PE files: {0} total, {1} unsigned" -f $pe.Count, $unsigned.Count)
    if ($other.Count) {
        Write-Warning ("Left alone because they are signed but do not verify here: " + ($other -join ', '))
    }

    # Batched: signtool takes many files per invocation, and each invocation is a
    # separate authentication round trip to the cloud HSM.
    $batch = 40
    for ($i = 0; $i -lt $unsigned.Count; $i += $batch) {
        $slice = @($unsigned.GetRange($i, [math]::Min($batch, $unsigned.Count - $i)))
        Write-Info ("sign: {0} file(s) [{1}/{2}]" -f $slice.Count, ([math]::Min($i + $batch, $unsigned.Count)), $unsigned.Count)
        & $signTool sign /sha1 $EvThumbprint /fd sha256 /td sha256 /tr $TimestampUrl /d 'Register Broker' @slice | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool failed while signing the payload (exit $LASTEXITCODE)." }
    }

    $stillUnsigned = @(Get-ChildItem $StageDir -Recurse -File -Include *.exe, *.dll |
                       Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq 'NotSigned' })
    if ($stillUnsigned.Count) {
        throw ("{0} payload PE file(s) are still unsigned after signing, starting with {1}." -f
               $stillUnsigned.Count, $stillUnsigned[0].Name)
    }
    Write-Info "Every PE file in the payload carries a signature."
}

#--------------------------------------------------------------------------
# 3. License.rtf, generated from LICENSE so the two cannot drift
#--------------------------------------------------------------------------
Write-Step "Generating License.rtf from LICENSE"

$licenseSrc = Join-Path $RepoRoot 'LICENSE'
if (-not (Test-Path $licenseSrc)) { throw "LICENSE not found at $licenseSrc" }

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}')
[void]$sb.Append('\viewkind4\uc1\pard\f0\fs18 ')
foreach ($line in (Get-Content $licenseSrc)) {
    $esc = $line -replace '\\', '\\\\' -replace '\{', '\{' -replace '\}', '\}'
    # RTF is 7-bit; emit anything else as a Unicode escape (em dashes etc).
    $out = New-Object System.Text.StringBuilder
    foreach ($ch in $esc.ToCharArray()) {
        if ([int]$ch -gt 127) { [void]$out.Append('\u' + [int]$ch + '?') } else { [void]$out.Append($ch) }
    }
    [void]$sb.Append($out.ToString() + '\par ')
}
[void]$sb.Append('}')
[System.IO.File]::WriteAllText((Join-Path $InstallerDir 'License.rtf'), $sb.ToString(), (New-Object System.Text.ASCIIEncoding))
Write-Info "License.rtf written."

#--------------------------------------------------------------------------
# 4. Build the MSI
#--------------------------------------------------------------------------
Write-Step "Building the MSI"

$msiProj = Join-Path $InstallerDir 'RegisterBroker.wixproj'
$msiArgs = @(
    'build', $msiProj,
    '-c', $Configuration,
    '--nologo',
    "-p:ProductVersion=$Version",
    "-p:Manufacturer=$Manufacturer",
    "-p:DriverSignState=$signState",
    "-p:DriverFeatureLevel=$driverFeatureLevel"
)
if ($Clean) { $msiArgs += '-t:Rebuild' }
& dotnet @msiArgs
if ($LASTEXITCODE -ne 0) { throw "MSI build failed (exit $LASTEXITCODE)." }

# Search rather than assume: the WiX SDK drops a localized package into a
# culture subdirectory (bin\x64\Release\en-us\), and the package only became
# localized once the UI dialogs started using !(loc.*) strings.
$msiName = "RegisterBroker-$Version-x64.msi"
$msiPath = Get-ChildItem (Join-Path $InstallerDir "bin\x64\$Configuration") -Recurse -Filter $msiName -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $msiPath) { throw "MSI build reported success but no $msiName was produced under installer\bin\x64\$Configuration." }
Write-Info ("MSI: {0} ({1} MB)" -f $msiPath, [math]::Round((Get-Item $msiPath).Length / 1MB, 1))

#--------------------------------------------------------------------------
# 5. Sign the MSI (before bundling: Burn hashes the payload it embeds, so a
#    package signed after bundling would not match the bundle's manifest).
#    The signing helpers and credential preflight live in step 1b, because the
#    payload signer needs them before staging.
#--------------------------------------------------------------------------
if ($EvThumbprint) {
    Write-Step "Signing the MSI (EV)"
    Invoke-Sign -Path $msiPath -SignTool $signTool -Description 'Register Broker'
}

#--------------------------------------------------------------------------
# 6. Build the bundle (.exe)
#--------------------------------------------------------------------------
$exePath = $null
if (-not $NoBundle) {
    Write-Step "Building the bundle (.exe)"

    $bundleProj = Join-Path $BundleDir 'Bundle.wixproj'
    $bundleArgs = @(
        'build', $bundleProj,
        '-c', $Configuration,
        '--nologo',
        "-p:ProductVersion=$Version",
        "-p:Manufacturer=$Manufacturer",
        "-p:MsiPath=$msiPath"
    )
    if ($Clean) { $bundleArgs += '-t:Rebuild' }
    & dotnet @bundleArgs
    if ($LASTEXITCODE -ne 0) { throw "Bundle build failed (exit $LASTEXITCODE)." }

    $exeName = "RegisterBroker-$Version-x64.exe"
    $exePath = Get-ChildItem (Join-Path $BundleDir "bin\x64\$Configuration") -Recurse -Filter $exeName -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $exePath) { throw "Bundle build reported success but no $exeName was produced under installer\bundle\bin\x64\$Configuration." }
    Write-Info ("EXE: {0} ({1} MB)" -f $exePath, [math]::Round((Get-Item $exePath).Length / 1MB, 1))

    if ($EvThumbprint) {
        # A Burn bundle cannot simply be signed: the engine has to be detached,
        # signed, and reattached, then the resulting bundle signed. Signing the
        # bundle without that leaves the engine unsigned and Windows shows the
        # engine's publisher as unknown during the elevation prompt.
        Write-Step "Signing the bundle (detach / sign engine / reattach / sign)"

        $wix = Get-Command wix -ErrorAction SilentlyContinue
        if (-not $wix) {
            $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
            if (Test-Path $candidate) { $wix = Get-Item $candidate }
        }
        if (-not $wix) {
            throw "Signing a bundle needs the WiX CLI for burn detach/reattach. Install it with: dotnet tool install --global wix --version 5.0.2"
        }

        $engine = Join-Path ([System.IO.Path]::GetTempPath()) 'RegisterBroker-burnengine.exe'
        & $wix.Source burn detach $exePath -engine $engine | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "wix burn detach failed (exit $LASTEXITCODE)." }

        Invoke-Sign -Path $engine -SignTool $signTool -Description 'Register Broker Setup'

        & $wix.Source burn reattach $exePath -engine $engine -o $exePath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "wix burn reattach failed (exit $LASTEXITCODE)." }

        Invoke-Sign -Path $exePath -SignTool $signTool -Description 'Register Broker Setup'
        Remove-Item $engine -Force -ErrorAction SilentlyContinue
    }
}

#--------------------------------------------------------------------------
# 7. Collect
#--------------------------------------------------------------------------
Write-Step "Collecting artifacts"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$artifacts = @($msiPath)
if ($exePath) { $artifacts += $exePath }

foreach ($a in $artifacts) { Copy-Item $a $OutDir -Force }

Write-Host ""
Write-Host "[done] $OutDir" -ForegroundColor Cyan
foreach ($a in $artifacts) {
    $final = Join-Path $OutDir (Split-Path $a -Leaf)
    $hash  = (Get-FileHash $final -Algorithm SHA256).Hash
    $size  = [math]::Round((Get-Item $final).Length / 1MB, 1)
    Write-Host ("  {0,-42} {1,7} MB" -f (Split-Path $final -Leaf), $size)
    Write-Host ("  {0,-42} sha256 {1}" -f '', $hash)
}

Write-Host ""
Write-Host "Publisher: $Manufacturer" -ForegroundColor Cyan
Write-Host "  Shown as the Add/Remove Programs 'Publisher'. It must match the Microsoft Store"
Write-Host "  publisher display name exactly - see docs\STORE-SUBMISSION.md."

Write-Host ""
if ($signState -eq 'none') {
    Write-Host "Driver: NOT included in this package." -ForegroundColor Yellow
    Write-Host "  The kernel-driver feature is disabled and hidden. Sensors and RGB report no"
    Write-Host "  hardware until a package built around a production-signed .sys is installed."
} else {
    Write-Host "Driver: $signState-signed." -ForegroundColor $(if ($signState -eq 'production') { 'Green' } else { 'Yellow' })
    if ($signState -ne 'production') {
        Write-Host "  The kernel driver feature is OFF by default and requires explicit acknowledgement."
        Write-Host "  Silent install with the driver: msiexec /i <msi> /qn ACCEPTTESTSIGNEDDRIVER=1 ADDLOCAL=ALL"
    }
}

if ($Store) {
    Write-Host ""
    Write-Host "Store submission build." -ForegroundColor Cyan
    Write-Host "  Verify it the way Microsoft does before submitting (ELEVATED, installs and uninstalls):"
    Write-Host "    .\scripts\Test-StoreValidation.ps1 -ExpectPublisher '$Manufacturer'"
    Write-Host "  Partner Center installer parameters: /qn for app type MSI, /quiet for EXE."
    Write-Host "  The field will not save empty, and a URL there hangs the install - see the doc."
}
if (-not $EvThumbprint) {
    Write-Host ""
    Write-Warning "Artifacts are UNSIGNED. Windows SmartScreen will warn on the .exe. Re-run with -EvThumbprint <thumbprint> to EV-sign."
}
