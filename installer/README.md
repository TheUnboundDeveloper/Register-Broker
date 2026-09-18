# Installer

Builds the redistributable Register Broker package.

| Artifact | What it is |
|---|---|
| `dist\RegisterBroker-<version>-x64.msi` | Per-machine MSI with a feature tree. GPO / Intune / `msiexec` friendly. |
| `dist\RegisterBroker-<version>-x64.exe` | A Burn bundle wrapping that same MSI — one file to hand someone, one file to sign. |

Both install identically: the bundle chains the MSI with
`bal:DisplayInternalUICondition="1"`, so the `.exe` shows the MSI's own dialogs
rather than a second, different UI. The `.exe` is packaging, not a second product.

```powershell
.\scripts\Build-Installer.ps1                      # build both
.\scripts\Build-Installer.ps1 -SkipPublish         # re-package the staged payload
.\scripts\Build-Installer.ps1 -EvThumbprint <t>    # EV-sign both artifacts
.\scripts\Test-Installer.ps1                       # 61 static gates over the built MSI
```

Signing the bundle is not just another signtool call: Burn's engine has to be
detached, signed, reattached, and only then is the bundle itself signed — skip
that and the engine stays unsigned, so the elevation prompt shows an unknown
publisher. `Build-Installer.ps1 -EvThumbprint` does the whole dance. It also
preflights `SM_CLIENT_CERT_FILE`, because a missing DigiCert KeyLocker
client-auth certificate surfaces only as `SignerSign() failed` (0x8009002d).

CI builds the installer and runs the gates on every push (`ci.yml`), and the
release workflow builds it on every release but attaches it **only** when the
packaged driver is production-signed.

Building needs no elevation and does not touch the running services or the
dev-box `publish\` tree. Installing the result does need elevation.

## What gets installed

```
C:\Program Files\Register Broker\
    bin\        BrokerSensorBridge, self-contained (its own .NET runtime)
    driver\     BrokerSmbus.sys
    console\    Reference Console, self-contained
    setup\      Setup-Driver.ps1 / Setup-Config.ps1 + their logs
```

| Service | Start | Notes |
|---|---|---|
| `SensorBroker` | Automatic | `\\.\pipe\SensorBroker`, LocalSystem, restart-on-crash |
| `BrokerControl` | Set by feature | `\\.\pipe\BrokerControl`; enabled and started only if the RGB feature was selected, otherwise stopped and disabled |
| `BrokerSmbus` | Demand | Kernel service, created by custom action — see below |

Features in the tree: broker services (required), kernel driver, RGB control
service, Reference Console (+ desktop shortcut), and the three opt-in read-only
sensor backends (`gpu.*`, `aqua.*`, `ups.*`), which stay off by default to match
the broker's own `Allow*Sensors` defaults.

## The driver signature gate

The package records what it actually contains. `Build-Installer.ps1` inspects the
`.sys` and stamps `DRIVERSIGNSTATE`:

* **`production`** — signed by *Microsoft Windows Hardware Compatibility
  Publisher*, i.e. attestation-signed. The kernel feature is selected by default
  and installs with no warning.
* **`test`** / **`unsigned`** — anything else. The feature is still listed but is
  **off by default**, and selecting it puts up a dialog explaining that Windows
  will refuse to load the driver outside test-signing mode. Next stays disabled
  until the acknowledgement is ticked.

Signer identity decides this, not `Get-AuthenticodeSignature` status: the dev
test certificate reports `Valid` on the dev box because its root was imported
locally, which says nothing about any other machine.

A silent install has no dialog to acknowledge, so it refuses instead:

```powershell
msiexec /i RegisterBroker-1.6.0-x64.msi /qn                              # no driver
msiexec /i RegisterBroker-1.6.0-x64.msi /qn ADDLOCAL=ALL ACCEPTTESTSIGNEDDRIVER=1
RegisterBroker-1.6.0-x64.exe /quiet ACCEPTTESTSIGNEDDRIVER=1
RegisterBroker-1.6.0-x64.exe /uninstall /quiet
```

Registering the kernel service is not fatal if it will not start: the broker and
console are still worth installing, so `Setup-Driver.ps1` records the outcome in
`HKLM\SOFTWARE\RegisterBroker\DriverStatus` (`Running` / `Registered` / `Failed`)
and in `setup\driver-setup.log` rather than failing the transaction. Only a
failure to *create* the service rolls the install back.

This keeps the packaging gate intact — a test-signed driver never installs
silently or by accident — while letting the installer exist and be tested now.
When attestation lands, packaging the attested binary flips the sign state and
the warning path disappears on its own; no authoring changes.

## Files

| File | Role |
|---|---|
| `Package.wxs` | Product, directories, features, components, both Win32 services, UI wiring |
| `Payload.wxs` | `<Files>` harvesting of the staged trees |
| `Actions.wxs` | Custom actions: kernel driver install/remove/rollback, broker configuration, silent-install guard |
| `DriverWarn.wxs` | The signature warning dialog and its splice into WixUI_FeatureTree |
| `setup\Setup-Driver.ps1` | Creates/removes the kernel service (MSI cannot install kernel drivers) |
| `setup\Setup-Config.ps1` | Writes the `Allow*Sensors` flags and sets the control service's start type |
| `RegisterBroker.wixproj` / `bundle\Bundle.wixproj` | Build the MSI and the bundle |

`stage\`, `bin\`, `obj\`, `License.rtf` and `dist\` are build outputs and are
gitignored. `License.rtf` is generated from `LICENSE` on every build so the two
cannot drift.

## Things that will bite you

Each of these cost a build here, and each has a gate in `Test-Installer.ps1`.

* **WiX drops fragments nothing references.** Naming a property inside a
  condition string is *not* a reference. `Package.wxs` carries an explicit
  `<PropertyRef Id="DRIVERSIGNSTATE" />` purely to pull `Actions.wxs` in;
  without it the package builds clean and every custom action is missing.
* **`CustomAction.Target` is 255 characters** and MSI truncates rather than
  erroring. The custom actions pass an install root and let the scripts derive
  their own paths; the four backend selections travel as one `0101` flag string.
* **`<Files>` resolves relative to the project directory only.** An absolute
  `Include=` path harvests nothing, silently — which is why staging lives in
  `installer\stage\` and not in a temp directory.
* **`<Files>` has no `Exclude`.** The two executables are authored by hand (a
  service's image path must be its component's key path), so the build stages
  them into `stage\exe\` where the globs cannot see them.
* **A quoted argument must not end in a backslash.** MSI directory properties
  always do, so `-Root "[INSTALLFOLDER]"` expands to `...\Register Broker\"` —
  the backslash escapes the closing quote and the argument swallows the rest of
  the line. This cost a failed install here: `-Root` came through as
  `C:\Program Files\Register Broker" -SignState test`. Every quoted root ends
  with `.` for that reason.
* **Uninstall only removes what MSI created.** The setup scripts write their own
  logs and a `DriverStatus` value, so without explicit `RemoveFile` /
  `RemoveRegistryKey` entries an uninstall leaves the install folder and an empty
  `HKLM\SOFTWARE\RegisterBroker` behind.
* **Configuration has exactly one correct window:** after `InstallServices`
  (or `BrokerControl` does not exist yet and configuring it no-ops) and before
  `StartServices` (or the sensor service has already read `appsettings.json`).
* **The warning dialog only appears if it sorts first.** WixUI_FeatureTree
  publishes `CustomizeDlg/Next -> VerifyReadyDlg` at ordering 1; the warning is
  published at ordering 0, because MSI takes the first `NewDialog` whose
  condition is true.
* **WiX 5, not 6 or 7.** v6 introduced the Open Source Maintenance Fee, whose
  EULA must be accepted before the toolset builds. v5 is the last MIT-licensed
  line, which keeps a third-party build-time licensing obligation out of this
  repo. Nothing else is required: `dotnet build` restores `WixToolset.Sdk` on
  its own (the `wix` CLI tool is only needed to sign a bundle).
