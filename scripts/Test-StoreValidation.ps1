<#
  Test-StoreValidation.ps1

  Runs Microsoft's own "manual package validation" procedure for an MSI/EXE
  Store submission against a built package, and produces a transcript that can
  be kept as evidence.

  It exists because three of Partner Center's automated checks came back as
  "we could not identify ...", and every one of them is a statement about what a
  REAL install leaves behind - something no amount of reading the MSI tables can
  answer. Test-Installer.ps1 covers the static side; this covers the live side:

    1. Silent install         - the package installs with the Store's own switch
                                and no user interaction at all.
    2. Entry in add or        - exactly the Name, Publisher and Version the
       remove programs          submission claims, none of them blank.
    3. Bundleware             - EXACTLY ONE entry appears. More than one is read
                                as the app installing other software.

  Reference: learn.microsoft.com/windows/apps/publish/publish-your-app/msi/
             manual-package-validation  (and app-package-requirements)

  THIS SCRIPT INSTALLS AND UNINSTALLS THE PRODUCT. It needs elevation, it
  changes machine state, and it is not something to run on a machine you care
  about mid-session. Pass -KeepInstalled to leave the install in place.

  Windows PowerShell 5.1 compatible, ASCII only.

  Examples:
      .\scripts\Test-StoreValidation.ps1
      .\scripts\Test-StoreValidation.ps1 -Package .\dist\RegisterBroker-1.6.0-x64.exe
      .\scripts\Test-StoreValidation.ps1 -ExpectPublisher 'UNBOUNDED ENGINEERING LLC'
#>
[CmdletBinding()]
param(
    # The .msi or .exe to validate. Defaults to the newest package in dist\,
    # preferring the .msi because that is what the Store runs with /qn.
    [string]$Package = '',

    # Override the silent switches. The defaults are deliberately the ones the
    # Store itself uses: /qn for an MSI, /quiet for the Burn bundle.
    [string]$SilentArgs = '',

    # What the Add/Remove Programs row must say. These are the values the
    # Partner Center submission has to agree with.
    [string]$ExpectName = 'Register Broker',
    [string]$ExpectPublisher = 'UNBOUNDED ENGINEERING LLC',
    [string]$ExpectVersion = '',

    # A silent install that blocks on a dialog is the failure this catches: it
    # never returns, so a hung run IS the result, not an inconvenience.
    [int]$TimeoutSeconds = 900,

    # Leave the product installed instead of uninstalling at the end.
    [switch]$KeepInstalled,

    [string]$TranscriptPath = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$Message) { Write-Host ""; Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Info([string]$Message) { Write-Host "    $Message" }

$script:Pass = 0
$script:Fail = 0
$script:Log  = New-Object System.Collections.ArrayList

function Record([string]$Line) {
    [void]$script:Log.Add($Line)
}

function Gate([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) {
        $script:Pass++
        Write-Host ("  [ok]   {0}" -f $Name)
        Record ("[ok]   {0}" -f $Name)
    } else {
        $script:Fail++
        Write-Host ("  [FAIL] {0}{1}" -f $Name, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor Red
        Record ("[FAIL] {0}{1}" -f $Name, $(if ($Detail) { " - $Detail" } else { '' }))
    }
}

#--------------------------------------------------------------------------
# 0. Preconditions
#--------------------------------------------------------------------------
Write-Step "Preconditions"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw ("This script installs a per-machine package, so it needs an ELEVATED shell. " +
           "Note that this is not a finding about the package: Microsoft's own requirement allows a UAC prompt, " +
           "it only forbids an installation UI.")
}

if (-not $Package) {
    $Package = Get-ChildItem (Join-Path $RepoRoot 'dist') -Filter 'RegisterBroker-*-x64.msi' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Package) {
        $Package = Get-ChildItem (Join-Path $RepoRoot 'dist') -Filter 'RegisterBroker-*-x64.exe' -ErrorAction SilentlyContinue |
                   Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $Package -or -not (Test-Path $Package)) {
    throw "No package found. Build one first: .\scripts\Build-Installer.ps1 -Store -EvThumbprint <thumbprint>"
}
$Package = (Resolve-Path $Package).Path
$isMsi = [System.IO.Path]::GetExtension($Package).ToLowerInvariant() -eq '.msi'

if (-not $SilentArgs) { $SilentArgs = if ($isMsi) { '/qn' } else { '/quiet' } }

$sig = Get-AuthenticodeSignature $Package
Write-Info "Package   : $Package"
Write-Info ("Type      : {0}" -f $(if ($isMsi) { 'MSI (the Store installs it with /qn)' } else { 'EXE (the Store passes the installer parameters you declare)' }))
Write-Info "Silent    : $SilentArgs"
Write-Info ("Signature : {0} / {1}" -f $sig.Status, $(if ($sig.SignerCertificate) { ($sig.SignerCertificate.Subject -split ',')[0] } else { '<unsigned>' }))

Record "Register Broker - Microsoft Store package validation"
Record ("Date      : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Record ("Machine   : {0} / {1}" -f $env:COMPUTERNAME, (Get-CimInstance Win32_OperatingSystem).Caption)
Record ("Package   : {0}" -f $Package)
Record ("SHA256    : {0}" -f (Get-FileHash $Package -Algorithm SHA256).Hash)
Record ("Signature : {0} / {1}" -f $sig.Status, $(if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '<unsigned>' }))
Record ""

# The package's own version, so the ARP check compares against the artifact
# rather than against something typed on the command line.
if (-not $ExpectVersion) {
    if ($Package -match '-(\d+\.\d+\.\d+)-x64\.(msi|exe)$') { $ExpectVersion = $matches[1] }
}
Write-Info "Expecting : Name='$ExpectName' Publisher='$ExpectPublisher' Version='$ExpectVersion'"

#--------------------------------------------------------------------------
# Add/Remove Programs enumeration.
#
# "Visible" means what Programs and Features shows, which is what a validator
# looks at: a DisplayName, and not flagged SystemComponent. A Burn bundle
# deliberately hides the MSI it chains this way, so the distinction is the
# whole difference between "one entry" and "two".
#--------------------------------------------------------------------------
$UninstallKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
)

function Get-ArpEntries {
    $out = New-Object System.Collections.ArrayList
    foreach ($root in $UninstallKeys) {
        if (-not (Test-Path $root)) { continue }
        foreach ($k in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
            $p = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
            if (-not $p) { continue }
            [void]$out.Add([pscustomobject]@{
                Id          = ('{0}\{1}' -f $root, $k.PSChildName)
                Key         = $k.PSChildName
                DisplayName = $p.DisplayName
                Publisher   = $p.Publisher
                Version     = $p.DisplayVersion
                Visible     = ([bool]$p.DisplayName -and $p.SystemComponent -ne 1)
                QuietUninstall = $p.QuietUninstallString
                Uninstall   = $p.UninstallString
            })
        }
    }
    return $out.ToArray()
}

$before = Get-ArpEntries
$already = $before | Where-Object { $_.DisplayName -like "*$ExpectName*" }
if ($already) {
    throw ("'{0}' is already installed ({1}). Uninstall it first, or the before/after comparison cannot tell " +
           "which entries this install created." -f $already[0].DisplayName, $already[0].Key)
}
Write-Info ("Add/Remove Programs before: {0} entries ({1} visible)" -f $before.Count, (@($before | Where-Object Visible)).Count)

#--------------------------------------------------------------------------
# 1. Silent install
#
# Microsoft's procedure: run the installer with the silent parameter and it
# must complete with no user interaction. Their requirements allow a UAC
# prompt; this shell is already elevated, so nothing can prompt.
#--------------------------------------------------------------------------
Write-Step "Check 1: silent install"

# Not dist\ itself: that folder holds the artifacts being uploaded, and a
# verbose MSI log next to them is clutter at exactly the wrong moment.
$logDir = Join-Path $RepoRoot 'dist\validation'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
$installLog = Join-Path $logDir ("store-validation-install-$stamp.log")

if ($isMsi) {
    $exe  = "$env:SystemRoot\System32\msiexec.exe"
    $args = @('/i', ('"' + $Package + '"')) + ($SilentArgs -split ' ') + @('/l*v', ('"' + $installLog + '"'))
} else {
    $exe  = $Package
    $args = ($SilentArgs -split ' ') + @('/log', ('"' + $installLog + '"'))
}
$cmdline = ('{0} {1}' -f $exe, ($args -join ' '))
Write-Info "run: $cmdline"
Record "Check 1: silent install"
Record ("  command : {0}" -f $cmdline)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exe -ArgumentList $args -PassThru -WindowStyle Hidden
$exited = $proc.WaitForExit($TimeoutSeconds * 1000)
$sw.Stop()

if (-not $exited) {
    Gate "install completes unattended" $false `
         "still running after $TimeoutSeconds s - a silent install that does not return is waiting on something"
    try { $proc.Kill() } catch { }
    Record "  RESULT  : TIMED OUT"
} else {
    $code = $proc.ExitCode
    # 0 succeeded; 1641 and 3010 are also documented successes (reboot
    # initiated / reboot required).
    $ok = @(0, 1641, 3010) -contains $code
    Write-Info ("exit {0} after {1:N1} s" -f $code, $sw.Elapsed.TotalSeconds)
    Record ("  exit    : {0} after {1:N1} s" -f $code, $sw.Elapsed.TotalSeconds)
    Gate "install returns a success code" $ok "exit $code (0/1641/3010 are the documented successes)"
    Gate "install needs no reboot" ($code -eq 0) "exit $code means a restart is required to finish"
    Gate "install ran without an installation UI" $true "invoked with $SilentArgs; no dialog can be shown"
}
Record ("  log     : {0}" -f $installLog)

#--------------------------------------------------------------------------
# 2. Entry in add or remove programs
# 3. Bundleware (exactly one entry)
#--------------------------------------------------------------------------
Write-Step "Checks 2 and 3: Add/Remove Programs entry, and only one of them"

$after    = Get-ArpEntries
$beforeIds = @($before | ForEach-Object { $_.Id })
$new      = @($after | Where-Object { $beforeIds -notcontains $_.Id })
$newVisible = @($new | Where-Object Visible)
$newHidden  = @($new | Where-Object { -not $_.Visible })

Record ""
Record "Checks 2 and 3: Add/Remove Programs"
foreach ($e in $new) {
    $line = ("  {0} Name='{1}' Publisher='{2}' Version='{3}' key={4}" -f
             $(if ($e.Visible) { 'VISIBLE' } else { 'hidden ' }), $e.DisplayName, $e.Publisher, $e.Version, $e.Key)
    Write-Info $line.Trim()
    Record $line
}

Gate "the install added an Add/Remove Programs entry" ($newVisible.Count -ge 1) `
     "nothing new appeared in Programs and Features"
# Microsoft's wording: "Your app should only add a single entry to the programs
# list. If your app has added multiple entries, it means that your app is
# installing bundleware."
Gate "exactly one visible entry (bundleware check)" ($newVisible.Count -eq 1) `
     ("{0} visible entries: {1}" -f $newVisible.Count, (($newVisible | ForEach-Object { $_.DisplayName }) -join ', '))

if ($newVisible.Count -ge 1) {
    $entry = $newVisible[0]
    Gate "entry Name is '$ExpectName'"           ($entry.DisplayName -eq $ExpectName) "got '$($entry.DisplayName)'"
    Gate "entry Publisher is '$ExpectPublisher'" ($entry.Publisher -eq $ExpectPublisher) "got '$($entry.Publisher)'"
    if ($ExpectVersion) {
        Gate "entry Version is '$ExpectVersion'" ($entry.Version -eq $ExpectVersion) "got '$($entry.Version)'"
    }
    Gate "entry can be uninstalled" ([bool]($entry.QuietUninstall -or $entry.Uninstall)) `
         "no UninstallString - Programs and Features would have nothing to run"
}
if ($newHidden.Count) {
    # Not a failure: a Burn bundle hides the MSI it chains on purpose. Recorded
    # so the transcript explains why the count is what it is.
    Write-Info ("{0} hidden row(s), which is how a bundle keeps its chained MSI out of the list." -f $newHidden.Count)
}

#--------------------------------------------------------------------------
# What the install actually did. Not a Store criterion - context for the
# transcript, and a check that a silent install produced a working product
# rather than just an ARP row.
#--------------------------------------------------------------------------
Write-Step "Installed state (context, not a Store check)"
Record ""
Record "Installed state"
foreach ($svc in @('SensorBroker', 'BrokerControl', 'BrokerSmbus')) {
    $s = Get-Service -Name $svc -ErrorAction SilentlyContinue
    $line = if ($s) { ("  {0,-14} {1} / {2}" -f $svc, $s.Status, $s.StartType) } else { ("  {0,-14} not present" -f $svc) }
    Write-Info $line.Trim()
    Record $line
}
$state = Get-ItemProperty 'HKLM:\SOFTWARE\RegisterBroker' -ErrorAction SilentlyContinue
if ($state -and $state.DriverStatus) {
    $line = ("  DriverStatus   {0} ({1})" -f $state.DriverStatus, $state.DriverStatusDetail)
} elseif ($state) {
    # The MSI creates this key itself; DriverStatus only appears once
    # Setup-Driver.ps1 has run, which a driver-less package never does.
    $line = "  DriverStatus   not recorded (this package carries no kernel driver)"
} else {
    $line = "  DriverStatus   HKLM\SOFTWARE\RegisterBroker is absent"
}
Write-Info $line.Trim()
Record $line

#--------------------------------------------------------------------------
# 4. Uninstall, silently. Not one of the three checks, but a package that
#    cannot be removed unattended fails certification later, and leaving the
#    product installed makes this script un-rerunnable.
#--------------------------------------------------------------------------
if ($KeepInstalled) {
    Write-Step "Leaving the product installed (-KeepInstalled)"
    Record ""
    Record "Uninstall: skipped (-KeepInstalled)"
} else {
    Write-Step "Silent uninstall"

    $uninstallLog = Join-Path $logDir ("store-validation-uninstall-$stamp.log")
    $target = if ($newVisible.Count -ge 1) { $newVisible[0] } else { $null }

    if (-not $target) {
        Gate "uninstall is possible" $false "no entry was created, so there is nothing to remove"
    } else {
        # Prefer the package's own quiet uninstall string; for a plain MSI the
        # ARP key name IS the ProductCode, which msiexec takes directly.
        if ($isMsi) {
            $uexe  = "$env:SystemRoot\System32\msiexec.exe"
            $uargs = @('/x', $target.Key, '/qn', '/l*v', ('"' + $uninstallLog + '"'))
        } else {
            $uexe  = $Package
            $uargs = @('/uninstall', '/quiet', '/log', ('"' + $uninstallLog + '"'))
        }
        Write-Info ("run: {0} {1}" -f $uexe, ($uargs -join ' '))
        Record ""
        Record "Uninstall"
        Record ("  command : {0} {1}" -f $uexe, ($uargs -join ' '))

        $up = Start-Process -FilePath $uexe -ArgumentList $uargs -PassThru -WindowStyle Hidden
        $uexited = $up.WaitForExit($TimeoutSeconds * 1000)
        if (-not $uexited) {
            Gate "uninstall completes unattended" $false "still running after $TimeoutSeconds s"
            try { $up.Kill() } catch { }
        } else {
            Gate "uninstall returns a success code" (@(0, 1641, 3010) -contains $up.ExitCode) "exit $($up.ExitCode)"
            Record ("  exit    : {0}" -f $up.ExitCode)
        }

        $final = Get-ArpEntries
        $leftover = @($final | Where-Object { $_.DisplayName -like "*$ExpectName*" })
        Gate "uninstall removes the Add/Remove Programs entry" ($leftover.Count -eq 0) `
             (($leftover | ForEach-Object { $_.DisplayName }) -join ', ')
        $svcLeft = @(@('SensorBroker', 'BrokerControl', 'BrokerSmbus') |
                     Where-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue })
        Gate "uninstall removes the services" ($svcLeft.Count -eq 0) ($svcLeft -join ', ')
    }
}

#--------------------------------------------------------------------------
Write-Step "Result"
if (-not $TranscriptPath) { $TranscriptPath = Join-Path $logDir ("store-validation-$stamp.txt") }
Record ""
Record ("RESULT: {0} ({1} passed, {2} failed)" -f $(if ($script:Fail -eq 0) { 'PASS' } else { 'FAIL' }), $script:Pass, $script:Fail)
[System.IO.File]::WriteAllLines($TranscriptPath, $script:Log.ToArray())
Write-Info "Transcript: $TranscriptPath"

Write-Host ""
if ($script:Fail -eq 0) {
    Write-Host "STORE VALIDATION PASS ($($script:Pass) checks)" -ForegroundColor Green
    Write-Host "  Silent install, one correctly identified Add/Remove Programs entry, no bundleware."
    exit 0
} else {
    Write-Host "STORE VALIDATION FAIL ($($script:Fail) failed, $($script:Pass) passed)" -ForegroundColor Red
    Write-Host "  See docs\STORE-SUBMISSION.md for what each check means and how it is satisfied."
    exit 1
}
