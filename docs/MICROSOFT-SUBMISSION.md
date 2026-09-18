# Microsoft Partner Center — submission fact sheet

Every value below was read out of the built artifacts in `dist\`, not from the authoring.
Re-run the checks at the bottom against the **final signed build** before submitting: the
`ProductCode` and both hashes change on every rebuild.

**Build described here:** Register Broker 1.6.0, built 2026-09-17.

---

## 1. Publisher and product identity

| Field | Value |
|---|---|
| Publisher (code-signing certificate subject) | `UNBOUNDED ENGINEERING LLC` |
| Certificate detail | Private Organization, Florida, US · SERIALNUMBER `L26000349775` |
| MSI `Manufacturer` property | `nvaitehiarna` ⚠️ **does not match the certificate — see §7** |
| Product name | Register Broker |
| Version | 1.6.0 |
| Support contact | TheUnboundDeveloper@outlook.com |
| Website / About URL | https://github.com/TheUnboundDeveloper/Register-Broker |
| Privacy policy URL | https://github.com/TheUnboundDeveloper/Register-Broker/blob/main/docs/PRIVACY.md |
| License | AGPL-3.0 with Commercial Exception |

## 2. Installer artifacts

Two artifacts, identical install behaviour. The `.exe` is a WiX Burn bundle wrapping the
same MSI and showing the MSI's own UI; it is packaging, not a second product.

| | MSI | EXE (bundle) |
|---|---|---|
| File | `RegisterBroker-1.6.0-x64.msi` | `RegisterBroker-1.6.0-x64.exe` |
| Size | 41,631,744 bytes (39.7 MB) | 42,138,120 bytes (40.2 MB) |
| SHA-256 | `C964A25AE998F67AA287A9B9A889094698BC89C4C737597A820E33C1ABF5A1E6` | `9D1F424600925ED300D4DA4B682E120967CF1FFC98F49AAE53817BFA8AB3F632` |
| Signature | Valid, EV (UNBOUNDED ENGINEERING LLC) | Valid, EV (UNBOUNDED ENGINEERING LLC) |
| Installer type | Windows Installer (MSI) | WiX Burn bootstrapper |
| Scope | Per-machine (`ALLUSERS=1`), elevation required | same |

## 3. MSI identity codes

| Field | Value |
|---|---|
| `ProductCode` | `{478095E0-CE4A-415B-B822-C75BEB9A07EF}` ⚠️ **regenerated on every build — re-read before submitting** |
| `UpgradeCode` | `{FEA52BA8-AF6C-4FC2-BFE8-A8C70A9E2FEF}` (stable across versions) |
| Bundle `UpgradeCode` | `{7B5B9CCA-A8F6-4398-9474-5F29654D4C93}` (stable) |
| `ProductLanguage` | 1033 (en-US) |
| `ProductVersion` | 1.6.0 |
| Upgrade behaviour | Major upgrade; same-version upgrade allowed; downgrade blocked with a message |

## 4. Installer parameters

### Silent install

```
msiexec /i RegisterBroker-1.6.0-x64.msi /qn
RegisterBroker-1.6.0-x64.exe /quiet
```

### Silent install with progress

```
msiexec /i RegisterBroker-1.6.0-x64.msi /qb!
RegisterBroker-1.6.0-x64.exe /passive
```

### Silent uninstall

```
msiexec /x {478095E0-CE4A-415B-B822-C75BEB9A07EF} /qn
RegisterBroker-1.6.0-x64.exe /uninstall /quiet
```

### Custom / optional parameters

| Property | Values | Meaning |
|---|---|---|
| `ACCEPTTESTSIGNEDDRIVER` | `1` | Required to install the kernel-driver feature when the packaged driver is **not** production-signed. Omitted, a silent install with that feature selected fails by design rather than registering an unloadable service. |
| `INSTALLFOLDER` | path | Install directory. Default `C:\Program Files\Register Broker` |
| `ADDLOCAL` | feature list | Feature selection — see §5 |
| `REMOVE` | `ALL` or feature list | Feature removal |

No other property needs to be set for a normal install.

## 5. Features (`ADDLOCAL` names)

| Feature | Default | Contents |
|---|---|---|
| `FeatureCore` | **Always** (cannot be deselected) | Broker + RGB control services, CLI client |
| `FeatureDriver` | On only when the packaged driver is production-signed | The `BrokerSmbus` kernel driver |
| `FeatureRgbControl` | On | Enables the RGB control service |
| `FeatureConsole` | On | Reference Console GUI (+ `FeatureDesktopShortcut`) |
| `FeatureGpuSensors` | Off | Optional read-only GPU telemetry |
| `FeatureAquaSensors` | Off | Optional read-only Aquacomputer telemetry |
| `FeatureUpsSensors` | Off | Optional read-only UPS telemetry |

Example: `msiexec /i RegisterBroker-1.6.0-x64.msi /qn ADDLOCAL=FeatureCore,FeatureConsole`

## 6. Return codes

Standard Windows Installer codes; the package defines no custom exit codes.

| Code | Meaning | Treat as |
|---|---|---|
| `0` | Success | Success |
| `3010` | Success, reboot required | Success |
| `1641` | Success, reboot initiated | Success |
| `1602` | User cancelled | Failure (user-initiated) |
| `1603` | Fatal error during installation | Failure |
| `1618` | Another install in progress | Failure, retryable |

One authored failure path worth naming: selecting the kernel-driver feature in a silent
install without `ACCEPTTESTSIGNEDDRIVER=1`, when the packaged driver is not
production-signed, terminates the install with an explanatory message (surfaces as a
standard MSI fatal-error return). This is intentional.

## 7. Platform and requirements

| Field | Value |
|---|---|
| Architecture | x64 only (enforced by a `VersionNT64` launch condition) |
| Minimum OS | Windows 10 version 1607 / Windows Server 2016 (the .NET 10 floor) |
| Prerequisites | **None.** Both payloads publish self-contained; no .NET runtime to chain |
| Languages | English (en-US) |
| Network use | None. No telemetry, no update check, no outbound connection (see privacy policy) |
| Elevation | Required (per-machine install; installs Windows services) |
| Installs services | `SensorBroker` (auto), `BrokerControl` (demand), `BrokerSmbus` (kernel, demand) |

## 8. Disclosures a reviewer will want

- **The product installs a kernel-mode driver.** `BrokerSmbus` is a narrow KMDF driver
  exposing bounded, validated, named-register IOCTLs for SMBus/Super-I/O sensor access.
  It does not read arbitrary physical memory, MSRs, or process memory. Writes are
  restricted in-kernel to an RGB address allow-list.
- **Purpose:** it replaces the per-application WinRing0 / run-as-admin model, letting
  non-admin applications read sensors and drive RGB through an authenticated, audited
  broker rather than each app shipping its own unrestricted driver.
- **Data:** nothing is collected or transmitted. See
  [PRIVACY.md](PRIVACY.md).
- **Source:** published under AGPL-3.0 with Commercial Exception; the complete source is
  public, so every claim above is independently checkable.

## 9. Resolve before submitting

1. **Publisher name mismatch.** The artifacts are signed by `UNBOUNDED ENGINEERING LLC`,
   but the MSI `Manufacturer`, the bundle manifest, and the assembly `Authors` /
   `Copyright` all say `nvaitehiarna`. Microsoft matches the publisher display name
   against the certificate. Decide which name is the publisher of record and make the
   package metadata, the privacy policy, and the Partner Center account agree.
2. **The packaged driver is test-signed.** This build stamps `DRIVERSIGNSTATE = test`,
   so the kernel feature is off by default and silent installs of it refuse. Ship the
   attestation-signed driver and rebuild; the gate then disappears on its own.
3. **Re-read the build-specific values** from the final signed artifacts.

### Re-reading the values

```powershell
# ProductCode / UpgradeCode / ProductVersion / Manufacturer
$wi = New-Object -ComObject WindowsInstaller.Installer
$db = $wi.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$wi,@($msiPath,0))
# ...query the Property table

# Hashes and signature
Get-FileHash .\dist\RegisterBroker-*-x64.* -Algorithm SHA256
Get-AuthenticodeSignature .\dist\RegisterBroker-*-x64.*
```
