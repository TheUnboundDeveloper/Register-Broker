<#
  Test-Installer.ps1

  Static gates over a built installer, in the spirit of the broker's own
  --selftest: open the MSI's tables and assert that the package actually says
  what the authoring meant. Everything here is read-only - nothing is installed.

  These gates exist because the failure modes they cover are all SILENT:

    * A WiX fragment that nothing references is dropped at link time. The build
      succeeds and the custom actions are simply absent from the package.
    * CustomAction.Target is a 255-character column. MSI truncates a longer
      command line instead of erroring, producing a half-written command.
    * ConfigureBroker has exactly one correct window (after InstallServices,
      before StartServices). Outside it the package still installs; the RGB
      service just never gets enabled, or the sensor service reads
      appsettings.json before the selected backends were written to it.
    * The driver warning dialog only appears if its publish sorts ahead of the
      built-in CustomizeDlg -> VerifyReadyDlg publish.
    * An Add/Remove Programs row whose Publisher is a handle rather than the
      publisher name looks fine locally and fails Microsoft Store package
      validation as an "unrelated publisher" (docs\STORE-SUBMISSION.md).

  What these gates cannot cover is the behaviour of a real install - whether it
  is silent, what it leaves in Add/Remove Programs, whether it leaves exactly
  one entry. Test-StoreValidation.ps1 does that part, elevated, on a real
  install/uninstall cycle.

  Usage:
      .\scripts\Test-Installer.ps1
      .\scripts\Test-Installer.ps1 -Msi <path> -ExpectSignState production
      .\scripts\Test-Installer.ps1 -ExpectPublisher 'UNBOUNDED ENGINEERING LLC\'

  Windows PowerShell 5.1 compatible, ASCII only.
#>
[CmdletBinding()]
param(
    [string]$Msi = '',
    [string]$Exe = '',
    [ValidateSet('', 'production', 'test', 'unsigned', 'none')]
    [string]$ExpectSignState = '',

    # Assert the exact Publisher string the package advertises. For a Store
    # submission this must equal the publisher display name on the account.
    [string]$ExpectPublisher = ''
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Msi) {
    $Msi = Get-ChildItem (Join-Path $RepoRoot 'dist') -Filter 'RegisterBroker-*-x64.msi' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Msi -or -not (Test-Path $Msi)) { throw "No MSI found. Build one first: .\scripts\Build-Installer.ps1" }
$Msi = (Resolve-Path $Msi).Path

if (-not $Exe) {
    $Exe = Get-ChildItem (Join-Path $RepoRoot 'dist') -Filter 'RegisterBroker-*-x64.exe' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}

#--------------------------------------------------------------------------
# MSI table access. Uses the Windows Installer COM API rather than any WiX
# tooling, so this runs on a machine with no SDK installed.
#
# Rows come back as objects with named columns on purpose: MSI tables are
# read positionally, and PowerShell unrolls nested arrays at pipeline and
# assignment boundaries, so a single-row result silently turns into its own
# columns and every index then reads characters out of a string.
#--------------------------------------------------------------------------
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Msi, 0))

function Get-MsiRows([string]$Table, [string[]]$Columns) {
    $select = ($Columns | ForEach-Object { '`' + $_ + '`' }) -join ','
    $sql = "SELECT $select FROM ``$Table``"

    # A table that does not exist throws from OpenView. Report it as "no rows" so
    # the gate that cares fails with its own message instead of an MSI SQL error.
    try {
        $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    } catch {
        return , @()
    }
    # [void]: InvokeMember writes its return value to the output stream.
    [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

    $rows = New-Object System.Collections.ArrayList
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if (-not $rec) { break }
        $obj = New-Object psobject
        for ($c = 0; $c -lt $Columns.Count; $c++) {
            $value = $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, ($c + 1))
            Add-Member -InputObject $obj -NotePropertyName $Columns[$c] -NotePropertyValue $value
        }
        [void]$rows.Add($obj)
    }
    return $rows.ToArray()
}

$script:Pass = 0
$script:Fail = 0
function Gate([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) {
        $script:Pass++
        Write-Host ("  [ok]   {0}" -f $Name)
    } else {
        $script:Fail++
        Write-Host ("  [FAIL] {0}{1}" -f $Name, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Register Broker installer selftest" -ForegroundColor Cyan
Write-Host "  MSI: $Msi"
if ($Exe) { Write-Host "  EXE: $Exe" }
Write-Host ""

#--------------------------------------------------------------------------
# Payload
#--------------------------------------------------------------------------
Write-Host "Payload"
$files = Get-MsiRows 'File' @('File', 'FileName', 'Component_')
# FileName is "short|long" when a short name was generated.
$names = $files | ForEach-Object { ($_.FileName -split '\|')[-1] }

Gate "file table is populated ($($files.Count) files)" ($files.Count -gt 400) "only $($files.Count) files - did staging run?"
Gate "broker executable present"         ($names -contains 'BrokerSensorBridge.exe')
Gate "console executable present"        ($names -contains 'ReferenceConsole.exe')
Gate "appsettings.json present"          ($names -contains 'appsettings.json')
Gate "calibration.default.json present"  ($names -contains 'calibration.default.json')
Gate "Setup-Driver.ps1 present"          ($names -contains 'Setup-Driver.ps1')
Gate "Setup-Config.ps1 present"          ($names -contains 'Setup-Config.ps1')
# Self-contained: the payload carries its own runtime, so there is no .NET
# prerequisite for the installer to chain or warn about.
Gate "self-contained runtime bundled" (($names -contains 'hostfxr.dll') -and ($names -contains 'coreclr.dll')) `
     "missing runtime - was --self-contained dropped?"
Gate "no debug symbols shipped" (-not ($names | Where-Object { $_ -like '*.pdb' }))

#--------------------------------------------------------------------------
# Features
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Features"
$features = @{}
foreach ($f in (Get-MsiRows 'Feature' @('Feature', 'Level'))) { $features[$f.Feature] = [int]$f.Level }

foreach ($id in @('FeatureCore', 'FeatureDriver', 'FeatureRgbControl', 'FeatureConsole',
                  'FeatureGpuSensors', 'FeatureAquaSensors', 'FeatureUpsSensors')) {
    Gate "feature $id exists" ($features.ContainsKey($id))
}
Gate "core feature is on by default" ($features['FeatureCore'] -eq 1)
# The reduced-assurance user-mode backends stay opt-in, matching the broker's
# own defaults (AllowGpuSensors / AllowAquaSensors / AllowUpsSensors are false).
Gate "gpu/aqua/ups sensors are opt-in" (
    $features['FeatureGpuSensors']  -ge 1000 -and
    $features['FeatureAquaSensors'] -ge 1000 -and
    $features['FeatureUpsSensors']  -ge 1000)

#--------------------------------------------------------------------------
# Add/Remove Programs identity
#
# Windows builds the ARP row from ProductName / Manufacturer / ProductVersion.
# Store package validation reads it back and rejects a blank or unrelated Name
# or Publisher, so these three are a shipping requirement, and the placeholder
# check exists because the default really was a GitHub handle once.
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Add/Remove Programs identity"
$props = @{}
foreach ($p in (Get-MsiRows 'Property' @('Property', 'Value'))) { $props[$p.Property] = $p.Value }

$productName = $props['ProductName']
$publisher   = $props['Manufacturer']
$placeholders = @('nvaitehiarna', 'Manufacturer', 'Publisher', 'Company', 'TODO')

Gate "ProductName is set"      ([bool]$productName) "ARP would show a blank Name"
Gate "Publisher is set"        ([bool]$publisher)   "ARP would show a blank Publisher"
Gate "ProductVersion is set"   ([bool]$props['ProductVersion'])
Gate "Publisher is not a placeholder or handle" (
    $publisher -and ($placeholders -notcontains $publisher) -and $publisher.Length -gt 3) `
    "Manufacturer='$publisher' - Store validation reads this as an unrelated publisher"
if ($ExpectPublisher) {
    Gate "Publisher is '$ExpectPublisher'" ($publisher -eq $ExpectPublisher) "got '$publisher'"
}
# Support links: an entry a person (or a validator) can trace to a publisher.
foreach ($arp in @('ARPURLINFOABOUT', 'ARPHELPLINK', 'ARPURLUPDATEINFO', 'ARPCONTACT')) {
    Gate "$arp is set" ([bool]$props[$arp])
}
Gate "ARPCONTACT matches the publisher" ($props['ARPCONTACT'] -eq $publisher) `
     "contact='$($props['ARPCONTACT'])' publisher='$publisher'"

#--------------------------------------------------------------------------
# Driver signature gate
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Driver signature gate"
$signState = $props['DRIVERSIGNSTATE']

Gate "DRIVERSIGNSTATE is stamped" (@('production', 'test', 'unsigned', 'none') -contains $signState) "got '$signState'"
if ($ExpectSignState) {
    Gate "DRIVERSIGNSTATE is '$ExpectSignState'" ($signState -eq $ExpectSignState) "got '$signState'"
}

# The state and the payload have to agree. "none" is the Store build, which
# leaves the driver out because a test-signed .sys cannot chain to a
# Microsoft-trusted root; anything else claims a binary and must carry it.
$hasSys = $names -contains 'BrokerSmbus.sys'
if ($signState -eq 'none') {
    Gate "no driver payload is packaged" (-not $hasSys) "DRIVERSIGNSTATE=none but BrokerSmbus.sys is in the package"
    Gate "driver feature is disabled and hidden" ($features['FeatureDriver'] -eq 0) `
         "Level=$($features['FeatureDriver']) - a feature with no payload must not be offered"
} else {
    Gate "the claimed driver is actually packaged" $hasSys `
         "DRIVERSIGNSTATE=$signState but no BrokerSmbus.sys in the File table"
    if ($signState -eq 'production') {
        Gate "production driver is selected by default" ($features['FeatureDriver'] -eq 1)
    } else {
        Gate "non-production driver is OFF by default" ($features['FeatureDriver'] -ge 1000) `
             "a $signState-signed driver must not install without an explicit opt-in"
    }
}

# ACCEPTTESTSIGNEDDRIVER must have no default value, or the acceptance checkbox
# renders pre-ticked (MSI shows a checkbox as checked for any non-empty value).
Gate "ACCEPTTESTSIGNEDDRIVER has no default" (-not $props.ContainsKey('ACCEPTTESTSIGNEDDRIVER'))

$accept = Get-MsiRows 'Condition' @('Feature_', 'Level', 'Condition') |
          Where-Object { $_.Feature_ -eq 'FeatureDriver' -and $_.Condition -match 'ACCEPTTESTSIGNEDDRIVER' }
Gate "accepting the warning re-enables the driver feature" ($accept -and ([int]$accept.Level) -eq 1)

#--------------------------------------------------------------------------
# Services
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Services"
$svcInstall = Get-MsiRows 'ServiceInstall' @('ServiceInstall', 'Name', 'StartType', 'Arguments', 'Component_')
$sensor  = $svcInstall | Where-Object { $_.Name -eq 'SensorBroker' }
$control = $svcInstall | Where-Object { $_.Name -eq 'BrokerControl' }

Gate "SensorBroker installed"  ([bool]$sensor)
Gate "BrokerControl installed" ([bool]$control)
if ($sensor) {
    Gate "SensorBroker starts automatically"  ($sensor.StartType -eq '2') "StartType=$($sensor.StartType)"
    Gate "SensorBroker runs with --service"   ($sensor.Arguments -eq '--service') "args=$($sensor.Arguments)"
}
if ($control) {
    Gate "BrokerControl runs with --control --service" ($control.Arguments -eq '--control --service') "args=$($control.Arguments)"
    # Feature-gated: created demand-start, then enabled by Setup-Config.ps1 only
    # when the RGB feature was selected.
    Gate "BrokerControl is demand-start (feature-gated)" ($control.StartType -eq '3') "StartType=$($control.StartType)"
}
if ($sensor -and $control) {
    Gate "both services share the broker component" ($sensor.Component_ -eq $control.Component_) `
         "MSI takes a service's image path from its component's key path"
}

$svcControl = Get-MsiRows 'ServiceControl' @('ServiceControl', 'Name')
Gate "SensorBroker has a ServiceControl row"  ([bool]($svcControl | Where-Object { $_.Name -eq 'SensorBroker' }))
Gate "BrokerControl has a ServiceControl row" ([bool]($svcControl | Where-Object { $_.Name -eq 'BrokerControl' }))

#--------------------------------------------------------------------------
# Uninstall cleanliness. MSI removes only what MSI created, and the setup
# scripts write logs and a DriverStatus value of their own - which otherwise
# survive an uninstall and keep the install directory and registry key alive.
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Uninstall cleanup"
$removeFiles = Get-MsiRows 'RemoveFile' @('FileKey', 'FileName', 'DirProperty')
foreach ($log in @('driver-setup.log', 'config-setup.log')) {
    Gate "uninstall removes $log" ([bool]($removeFiles | Where-Object { $_.FileName -like "*$log" })) `
         "a script-written file MSI does not track would keep the install folder behind"
}
# "Delete this key on uninstall" is a Registry row whose Name is "-", not a
# RemoveRegistry row (that table is for removals at INSTALL time).
$regRows = Get-MsiRows 'Registry' @('Registry', 'Root', 'Key', 'Name')
Gate "uninstall removes the HKLM key" ([bool]($regRows | Where-Object { $_.Key -match 'RegisterBroker' -and $_.Name -eq '-' })) `
     "the scripts write DriverStatus there, so the key outlives the values MSI created"

#--------------------------------------------------------------------------
# Custom actions
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Custom actions"
$cas = Get-MsiRows 'CustomAction' @('Action', 'Type', 'Source', 'Target')
$caIds = $cas | ForEach-Object { $_.Action }

foreach ($id in @('SetInstallDriver', 'InstallDriver', 'SetRemoveDriver', 'RemoveDriver',
                  'SetRollbackDriver', 'RollbackDriver', 'SetConfigureBroker', 'ConfigureBroker',
                  'ErrDriverNotAccepted')) {
    Gate "custom action $id linked" ($caIds -contains $id) `
         "unreferenced fragments are dropped at link time without a warning"
}

# The 255-character trap: MSI truncates, it does not complain.
$tooLong = $cas | Where-Object { $_.Target -and $_.Target.Length -gt 255 }
Gate "no custom action command line exceeds 255 chars" (-not $tooLong) `
     (($tooLong | ForEach-Object { "$($_.Action)=$($_.Target.Length)" }) -join ', ')

# A backslash immediately before a closing quote escapes that quote under the
# standard command-line parser, and the argument then swallows the rest of the
# line. MSI directory properties always end in a backslash, so "[INSTALLFOLDER]"
# is exactly this bug - hence the trailing "." in Actions.wxs.
$badQuote = $cas | Where-Object { $_.Target -and $_.Target -match '\\"' }
Gate "no quoted argument ends in a backslash" (-not $badQuote) `
     (($badQuote | ForEach-Object { $_.Action }) -join ', ')

# Deferred system-context actions: bit 1024 (deferred) + 2048 (no impersonate).
foreach ($id in @('InstallDriver', 'RemoveDriver', 'ConfigureBroker')) {
    $row = $cas | Where-Object { $_.Action -eq $id }
    if ($row) {
        $type = [int]$row.Type
        Gate "$id is deferred, no-impersonate" ((($type -band 1024) -ne 0) -and (($type -band 2048) -ne 0)) "type=$type"
    }
}

# Bit 64 (msidbCustomActionTypeContinue) = "ignore the exit code". Which actions
# may fail the install is a deliberate split:
#
#   ConfigureBroker MUST be able to fail harmlessly. It writes the Allow*Sensors
#   flags and sets the control service's start type - recoverable state. When it
#   was fatal, a host running PowerShell in ConstrainedLanguage mode rolled the
#   entire install back, which presents as an installer that does nothing: no
#   files, no services, no Add/Remove Programs entry. A Microsoft Store
#   validator reported exactly that.
#
#   InstallDriver must STAY fatal. A kernel service that was half-created is not
#   something to shrug at, and the rollback action exists to undo it.
$cfgRow = $cas | Where-Object { $_.Action -eq 'ConfigureBroker' }
if ($cfgRow) {
    Gate "ConfigureBroker cannot fail the install" ((([int]$cfgRow.Type) -band 64) -ne 0) `
         "type=$($cfgRow.Type) - configuration is recoverable; a rolled-back install is not"
}
$drvRow = $cas | Where-Object { $_.Action -eq 'InstallDriver' }
if ($drvRow) {
    Gate "InstallDriver still fails the install" ((([int]$drvRow.Type) -band 64) -eq 0) `
         "type=$($drvRow.Type) - a failed kernel service registration must roll back"
}

#--------------------------------------------------------------------------
# The custom-action scripts must survive ConstrainedLanguage mode.
#
# WDAC or AppLocker in force runs PowerShell constrained, where creating or
# calling a .NET type throws "Only core types are supported in this language
# mode". Setup-Config.ps1 did exactly that to write BOM-less UTF-8; the script
# exited non-zero and took the whole install down with it. Both scripts are
# cmdlet-only now, and this is what keeps them that way.
#
# These are the same files Payload.wxs harvests into the package.
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Custom-action scripts (ConstrainedLanguage safety)"
$setupScripts = @(Get-ChildItem (Join-Path $RepoRoot 'installer\setup') -Filter *.ps1 -ErrorAction SilentlyContinue)
Gate "setup scripts found" ($setupScripts.Count -gt 0)
foreach ($s in $setupScripts) {
    $bad = New-Object System.Collections.ArrayList
    $n = 0
    $inBlock = $false
    foreach ($line in (Get-Content $s.FullName)) {
        $n++

        # Skip comments, or the scripts' own prose about what not to do trips
        # this gate - which it did on the first run. Block comments (<# .. #>)
        # need real tracking; after that, everything from the first '#' is a
        # line comment. Neither script has a '#' inside a string literal.
        if ($inBlock) {
            if ($line -match '#>') { $inBlock = $false }
            continue
        }
        if ($line -match '<#') {
            if ($line -notmatch '#>') { $inBlock = $true }
            continue
        }
        $hash = $line.IndexOf('#')
        $code = if ($hash -ge 0) { $line.Substring(0, $hash) } else { $line }

        if ($code -match '\]::' -or $code -match 'New-Object') { [void]$bad.Add("line $n") }
    }
    Gate "$($s.Name) is cmdlet-only" ($bad.Count -eq 0) `
         ("uses a .NET type at " + ($bad -join ', ') + " - throws in ConstrainedLanguage mode")
}

#--------------------------------------------------------------------------
# Sequencing
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Sequencing"
$seqRows = Get-MsiRows 'InstallExecuteSequence' @('Action', 'Sequence', 'Condition')
$seq = @{}
foreach ($s in $seqRows) { if ($s.Sequence) { $seq[$s.Action] = [int]$s.Sequence } }

function Test-Between([string]$Action, [string]$After, [string]$Before) {
    return ($seq.ContainsKey($Action) -and $seq.ContainsKey($After) -and $seq.ContainsKey($Before) -and
            $seq[$Action] -gt $seq[$After] -and $seq[$Action] -lt $seq[$Before])
}

Gate "driver is registered after its files land" (Test-Between 'InstallDriver' 'InstallFiles' 'InstallServices') `
     "InstallDriver=$($seq['InstallDriver']) InstallFiles=$($seq['InstallFiles'])"
Gate "driver is removed before its files go" (Test-Between 'RemoveDriver' 'InstallInitialize' 'RemoveFiles') `
     "RemoveDriver=$($seq['RemoveDriver']) RemoveFiles=$($seq['RemoveFiles'])"
Gate "rollback is scheduled ahead of the install it undoes" ($seq['RollbackDriver'] -lt $seq['InstallDriver'])
# The one correct window - see the header note.
Gate "ConfigureBroker runs between InstallServices and StartServices" (Test-Between 'ConfigureBroker' 'InstallServices' 'StartServices') `
     "ConfigureBroker=$($seq['ConfigureBroker']) InstallServices=$($seq['InstallServices']) StartServices=$($seq['StartServices'])"
Gate "silent-install guard runs early" ($seq.ContainsKey('ErrDriverNotAccepted') -and $seq['ErrDriverNotAccepted'] -lt $seq['InstallFiles'])

$guard = $seqRows | Where-Object { $_.Action -eq 'ErrDriverNotAccepted' }
Gate "silent-install guard only fires without full UI" ([bool]$guard -and $guard.Condition -match 'UILevel') `
     "the guard must not fire in the UI path, where the dialog handles acceptance"

#--------------------------------------------------------------------------
# UI splice
#--------------------------------------------------------------------------
Write-Host ""
Write-Host "Driver warning dialog"
$dialogs = Get-MsiRows 'Dialog' @('Dialog') | ForEach-Object { $_.Dialog }
Gate "DriverWarnDlg exists" ($dialogs -contains 'DriverWarnDlg')

$events  = Get-MsiRows 'ControlEvent' @('Dialog_', 'Control_', 'Event', 'Argument', 'Condition', 'Ordering')
$mine    = $events | Where-Object { $_.Dialog_ -eq 'CustomizeDlg' -and $_.Control_ -eq 'Next' -and $_.Argument -eq 'DriverWarnDlg' }
$builtin = $events | Where-Object { $_.Dialog_ -eq 'CustomizeDlg' -and $_.Control_ -eq 'Next' -and $_.Argument -eq 'VerifyReadyDlg' }

Gate "warning is published from CustomizeDlg" ([bool]$mine)
# MSI evaluates a control's events in ascending Ordering and takes the first
# NewDialog whose condition is true; ours must sort first or it never shows.
if ($mine -and $builtin) {
    Gate "warning sorts ahead of the built-in Next" ([int]$mine.Ordering -lt [int]$builtin.Ordering) `
         "ours=$($mine.Ordering) built-in=$($builtin.Ordering)"
}
if ($mine) {
    Gate "warning is conditional on driver + signature" (
        $mine.Condition -match 'FeatureDriver' -and $mine.Condition -match 'DRIVERSIGNSTATE') "condition=$($mine.Condition)"
}

$checkbox = Get-MsiRows 'Control' @('Dialog_', 'Control', 'Property') |
            Where-Object { $_.Dialog_ -eq 'DriverWarnDlg' -and $_.Property -eq 'ACCEPTTESTSIGNEDDRIVER' }
Gate "acceptance checkbox is bound to ACCEPTTESTSIGNEDDRIVER" ([bool]$checkbox)

#--------------------------------------------------------------------------
# Bundle
#--------------------------------------------------------------------------
if ($Exe) {
    Write-Host ""
    Write-Host "Bundle"
    $info = (Get-Item $Exe).VersionInfo
    Gate "bundle carries a version"      ([bool]$info.FileVersion)
    Gate "bundle is larger than the MSI" ((Get-Item $Exe).Length -ge (Get-Item $Msi).Length) `
         "the MSI should be embedded in the bundle"
    # Burn stamps Bundle/@Manufacturer into CompanyName and into its own ARP
    # row, so a bundle built with a different Manufacturer than the MSI would
    # advertise two different publishers for one product.
    Gate "bundle publisher matches the MSI" ($info.CompanyName -eq $publisher) `
         "bundle='$($info.CompanyName)' msi='$publisher'"
    Gate "bundle product name matches the MSI" ($info.ProductName -eq $productName) `
         "bundle='$($info.ProductName)' msi='$productName'"
}

#--------------------------------------------------------------------------
Write-Host ""
if ($script:Fail -eq 0) {
    Write-Host "INSTALLER SELFTEST PASS ($($script:Pass) checks)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "INSTALLER SELFTEST FAIL ($($script:Fail) failed, $($script:Pass) passed)" -ForegroundColor Red
    exit 1
}
