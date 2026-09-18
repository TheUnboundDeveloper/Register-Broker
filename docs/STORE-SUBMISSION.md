# Microsoft Store submission (MSI/EXE app)

What to put in Partner Center, what the automated package validation actually
checks, and how this repo proves each check locally before submitting.

Microsoft's own references:
[app package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements) ·
[upload app packages](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/upload-app-packages) ·
[package validation](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/package-validation-pre-check) ·
[manual package validation](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/manual-package-validation)

## The three checks that came back "could not identify"

A 2026-09 submission returned three inconclusive results:

| Check | What Partner Center said |
|---|---|
| Silent install check | "We could not identify if your app is installing silently." |
| Entry in add or remove programs | "We could not identify the app name and the publisher name that your app has added in the add or remove programs." |
| Bundleware check | (same message) |

Two of them share one message, and that is the clue: all three are decided by
running the installer and then **reading the Add/Remove Programs row it
created**. If the install does not complete, or the row does not identify the
app, none of the three can be answered. There were two causes, and both are
now fixed:

1. **The publisher was a handle.** The MSI's `Manufacturer` - the string Windows
   shows as "Publisher" - was `nvaitehiarna`, while the artifacts are EV-signed
   as `UNBOUNDED ENGINEERING LLC` and the Store account publishes under the
   company name. Microsoft's criterion is explicit: *"The entry for your product
   should not show a blank or unrelated Name or Publisher."* An unrecognizable
   publisher is an unidentifiable app.

2. **The installer parameters field held a documentation link.** That field is
   the literal command line the Store appends when it runs the installer, not a
   place to cite documentation. Measured on the dev box:
   `msiexec /i <package> /qn https://github.com/...` **never returns** - it hangs
   (killed after ~90 s), where the same command line without the URL comes back
   promptly. A Burn `.exe` given an unrecognized argument fares no better: it
   never goes silent and shows its full UI. Either way the install does not land,
   so no Add/Remove Programs row is written and all three checks go *unanswered*
   rather than *failed* - which is exactly the result Partner Center reported.

Nothing about the package's silent-install *mechanism* was broken. For the
record, both artifacts are structurally silent:

* `msiexec /i RegisterBroker-x.y.z-x64.msi /qn` - what the Store uses for MSI.
* `RegisterBroker-x.y.z-x64.exe /quiet` - Burn accepts `/q`, `/quiet`, `/s`,
  `/silent`. `wixstdba` only lets the chained MSI show its own dialogs when the
  bundle itself is displaying full or passive UI, so `bal:DisplayInternalUICondition`
  cannot leak a dialog into a silent install.

## Partner Center: Packages page

| Field | Value | Notes |
|---|---|---|
| Package URL | `https://.../RegisterBroker-<version>-x64.msi` | HTTPS, **versioned**, on hosting you control. The binary behind it must never change; publish a new URL for a new build. A GitHub release asset works - release assets are immutable per tag. |
| Architecture | `x64` | The driver, the broker and every hardware path are win-x64 only. |
| Languages | `en-us` | The UI is English-only today. |
| App type | `MSI` | Recommended. See below for what changes if you submit the `.exe`. |
| Installer parameters | **`/qn`** | Required by the form - it will not save empty. The Store already uses `/qn` for an MSI, and a doubled `/qn /qn` parses identically to a single one (both return 1619 against a missing package), so this value is safe whether the Store appends it to its own switch or replaces it. Do **not** tick "Installer runs in silent mode but does not require switches": if the Store's runner reads that as "invoke the installer bare", an MSI without `/qn` shows its full UI and fails the check again. |
| Installer handling | n/a for MSI | MSI return codes are standard, so the Store already understands them. |

### If you submit the `.exe` instead

The bundle is one signed file and leaves exactly one ARP entry, so it is a
legitimate choice - but EXE submissions require two extra fields.

| Field | Value |
|---|---|
| Installer parameters | `/quiet` |
| Installer handling | the return codes below |

Return codes, for the standard scenarios Partner Center lists. These are
Windows Installer's documented codes, which Burn passes through:

| Scenario | Code(s) |
|---|---|
| Installation successful | `0` |
| Reboot required | `3010` (restart required), `1641` (restart initiated) |
| Installation cancelled by user | `1602` |
| Application already exists | `1638` (another version is already installed) |
| Installation already in progress | `1618` |
| Disk space is full | `112`, `1632` (the Temp folder is full or inaccessible) |
| Package rejected during installation | `1625` (forbidden by system policy), `1260` (blocked by policy) |
| Network failure | *none* - this is a standalone offline installer; nothing is downloaded at install time |

Worth adding as custom codes with a documentation URL: `1603` (fatal error -
see the verbose log), `1639` (invalid command line - something was appended to
the installer parameters), `1620` (the package could not be opened).

**The documentation URL belongs in the Installer handling section**, attached to
a return code. It does not belong in Installer parameters.

## What each check requires, and how this package satisfies it

### 1. Silent install

> Initiating the install must not display an installation user interface (i.e.
> silent install is required), however a User Account Control (UAC) dialog is
> allowed.

Both artifacts install unattended with the switches above. The package is
**per-machine** - it installs two Windows services and, when a production-signed
driver is packaged, a kernel service - so it requires administrator rights.
Microsoft's requirements permit a UAC prompt; what they forbid is an
installation UI, and there is none.

One consequence is worth knowing, because it is the one thing the package cannot
fix: a per-machine MSI run with `/qn` from a **non-elevated** context cannot
show a UAC prompt, so it fails (exit `1603`; the verbose log records internal
error 1925, "insufficient privileges ... for all users of the machine"). If the
validator's environment is not elevated, no per-machine installer can pass, and
the documented route is the manual verification the failure message links to -
which is what `Test-StoreValidation.ps1` produces evidence for.

### 2. Entry in add or remove programs

> Verify the app name, publisher name and app version added by your app. The
> entry for your product should not show a blank or unrelated Name or Publisher.

Windows builds that row from three MSI properties, all of which are now gated:

| ARP column | Source | Value |
|---|---|---|
| Name | `Package/@Name` | `Register Broker` |
| Publisher | `Package/@Manufacturer` | `UNBOUNDED ENGINEERING LLC` |
| Version | `Package/@Version` | the release version, e.g. `1.6.0` |

Plus support metadata so the entry traces back to a real publisher:
`ARPCONTACT`, `ARPHELPLINK`, `ARPURLINFOABOUT`, `ARPURLUPDATEINFO`,
`ARPPRODUCTICON`.

**The Publisher string must equal the publisher display name on the Partner
Center account.** It is cross-checked in two places so it cannot drift:
`Build-Installer.ps1` refuses to build if `-Manufacturer` disagrees with the
signing certificate's subject organization, and `Test-Installer.ps1
-ExpectPublisher` asserts the value baked into the package (CI pins it).

### 3. Bundleware

> Your app should only add a single entry to the programs list. If your app has
> added multiple entries, it means that your app is installing bundleware.

The MSI adds exactly one entry. The `.exe` also adds exactly one: Burn registers
the bundle and the bundle compiler injects `ARPSYSTEMCOMPONENT=1` into the
chained MSI, which keeps the package's own row out of Programs and Features.
Everything in the payload - broker, console, client library, .NET runtime - is
part of this product; nothing third-party is installed alongside it.

`Test-StoreValidation.ps1` asserts the count directly rather than trusting that
reasoning.

## Verifying before you submit

```powershell
# 1. Build the submission artifact (EV-signed installer AND payload).
.\scripts\Build-Installer.ps1 -Store -EvThumbprint <thumbprint>

# 2. Static gates over the package tables (no install).
.\scripts\Test-Installer.ps1 -ExpectPublisher 'UNBOUNDED ENGINEERING LLC'

# 3. Microsoft's manual validation procedure, for real. ELEVATED - this
#    installs and uninstalls the product and writes a transcript to dist\.
.\scripts\Test-StoreValidation.ps1
```

Step 3 is the one that answers the three checks: it snapshots Add/Remove
Programs, installs with the Store's own switch under a timeout (a silent install
that waits on a dialog never returns, which is the failure), asserts a success
exit code, asserts **exactly one** new visible entry with the expected Name,
Publisher and Version, then uninstalls silently and asserts nothing is left
behind. The transcript it writes to `dist\validation\store-validation-<stamp>.txt`
records the machine, the package hash, the signature, the exact command lines and
every result - which is what to keep if Partner Center asks you to verify
manually. The verbose MSI install and uninstall logs land beside it, so `dist\`
itself holds only the two artifacts you link.

### Result of the last run

2026-09-18, against the EV-signed `1.6.0` Store MSI
(`0C558C5CDA0ACA0039A58A7548844D66CE7BFC9B2B021A48B24DEAA2623EDF75`) on Windows 11
Pro: **PASS, 12 checks**. Silent `/qn` install returned 0 in 6.5 s with no UI;
Add/Remove Programs showed exactly one visible entry reading `Register Broker` /
`UNBOUNDED ENGINEERING LLC` / `1.6.0`; the silent uninstall left no entry, no
services, no install directory and no registry key. Transcript in
`dist\validation\`.

## The other requirements

### Every PE file must be signed

> The binary and all of its Portable Executable (PE) files must be digitally
> signed with a code signing certificate that chains up to a certificate issued
> by a CA that is part of the Microsoft Trusted Root Program.

This covers the files *inside* the installer, not just the installer. A
self-contained publish is mostly Microsoft-signed .NET runtime files, but this
repo's own managed assemblies, Avalonia and NAudio ship unsigned - around three
dozen files.

`-SignPayload` (implied by `-Store`) signs every staged PE that carries no
signature, before the MSI is built, because the package stores file hashes.
Microsoft-signed files are deliberately left alone: re-signing them would
replace Microsoft's signature with ours. The step then re-scans and fails if
anything is still unsigned.

### Standalone, offline installer

Required, and satisfied: both payloads are published `--self-contained`, so
there is no .NET prerequisite and nothing is downloaded during setup.

### The kernel driver

This is the open blocker, and it is a deliberate one.

`BrokerSmbus.sys` is test-signed today; attestation signing is still gated on
Partner Center's hardware program (see `docs/DRIVER-SIGNING-ATTESTATION.md`). A
test-signed binary chains to a locally imported test root, so **packaging it
would violate the "all PE files chain to a Microsoft-trusted root" requirement**
outright - quite apart from the project's own rule that no distributable build
ships a test-signed driver.

So `-Store` leaves the driver out: the sign state is stamped `none`, the kernel
feature is set to Level 0 (disabled and hidden), and `Setup-Driver.ps1` records
`DriverStatus=Absent` instead of failing. The consequence has to be understood
before submitting:

**Without the driver, the broker installs and runs but reports no hardware.**
Sensors and RGB are empty. Store certification includes a functional review, and
an app whose entire function is unavailable is a reasonable thing for a reviewer
to reject.

The choices are therefore:

* **Wait for attestation**, then submit a package built around the
  Microsoft-signed `.sys`. `-Store` includes the driver automatically once the
  sign state comes out `production` - no authoring change.
* **Submit the driver-less package** knowing it demonstrates the framework's
  plumbing (services, pipes, auth, the console) but reads no hardware, and say
  so in the listing.

## Pre-submission checklist

- [ ] `-Store -EvThumbprint <t>` build, both artifacts EV-signed and timestamped
- [ ] `Test-Installer.ps1 -ExpectPublisher ...` passes
- [ ] `Test-StoreValidation.ps1` passes, transcript kept
- [ ] Publisher string equals the Partner Center publisher display name
- [ ] Package URL is HTTPS, versioned, and will never be overwritten
- [ ] Installer parameters: `/qn` for MSI, `/quiet` for EXE - **never a URL**
- [ ] Architecture `x64`, language `en-us`
- [ ] Driver decision made (attested, or knowingly absent)
