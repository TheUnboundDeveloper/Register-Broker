# Driver Attestation Signing — submission runbook

The **hands-on procedure** for getting `BrokerSmbus.sys` Microsoft-attestation-signed so it
loads on a stock Windows 10/11 x64 machine (Secure Boot on, HVCI on, test-signing off).

This is the command-by-command companion to [`SIGNING-AND-DEPLOYMENT.md`](SIGNING-AND-DEPLOYMENT.md),
which covers the *why*, the procurement lead time, and the broader deployment hardening. Read
that first for the strategy; use this for the actual submission once the cert + account exist.

> **One-line summary of the path:** EV cert → Partner Center hardware account → build release
> `.sys` → `Inf2Cat` → `MakeCab` → EV-sign the CAB → upload (attestation) → download the
> Microsoft-signed package → deploy via the INF.

---

## 0. Repo artifacts this runbook depends on

These already exist in the tree (added when the signing path was prepared):

| File | Purpose |
|---|---|
| [`BrokerSmbusDriver/BrokerSmbus.inf`](../BrokerSmbusDriver/BrokerSmbus.inf) | Non-PnP, x64, demand-start kernel service install. **Required** for the submission. |
| [`BrokerSmbusDriver/BrokerSmbus.rc`](../BrokerSmbusDriver/BrokerSmbus.rc) | `VERSIONINFO` stamped into the `.sys` (FileVersion/ProductVersion). |
| `Build-Driver-DirectLink.ps1` | Compiles the `.rc` and links the `.res` into the `.sys` automatically. |

**Keep these three in lockstep on every signed build:**

1. `BrokerSmbus.rc` → `VER_FILEVERSION` / `VER_PRODUCTVERSION_STR`
2. `BrokerSmbus.inf` → `DriverVer` (date + the same x.y.z.w version)
3. `BrokerSmbus.inf` → `KmdfLibraryVersion` **must equal the KMDF the `.sys` was linked
   against**. The build prints it: `[driver] KMDF : 1.35`. (It is **1.35** on the current
   dev box / WDK 10.0.26100 — bump the INF if a newer WDK changes it.)

A version/KMDF mismatch is the most common attestation rejection. There is no auto-stamp today;
treat the version bump as a manual release step (or wire `stampinf` into the build later).

---

## 1. Prerequisites (procure first — see SIGNING-AND-DEPLOYMENT §2)

- **EV code-signing certificate** on a FIPS token or cloud HSM (DigiCert/Sectigo/…).
- **Partner Center account** with the **Windows Hardware** program enabled, authenticated with
  that EV cert.
- WDK tools on PATH (`Inf2Cat.exe`, `InfVerif.exe`, `signtool.exe`, `makecab.exe`) — all ship
  with the WDK/SDK already installed on the dev box (`C:\Program Files (x86)\Windows Kits\10\bin\<sdk>\x64`).

Attestation signing requires **no HLK/lab testing** and is valid for **Windows 10 1607+ and
Windows 11 client x64**. (Windows **Server** is *not* covered by attestation — that needs full
WHQL; out of scope, the project is desktop x64.)

---

## 2. Build the release `.sys` to be submitted

The signature is over one exact binary, so build the final bits first, signed for **dev** only
as an interim (the dev test-signature is replaced by Microsoft's).

```powershell
# ELEVATED. Stop the stack so the loaded .sys isn't link-locked (LNK1104).
.\scripts\Stop-BrokerServices.ps1
.\scripts\Build-All.ps1 -Bridge -Driver -Clean      # -Driver alone builds ONLY the driver
```

Confirm the version landed in the binary:

```powershell
(Get-Item .\BrokerSmbusDriver\x64\Release\BrokerSmbus.sys).VersionInfo |
    Format-List FileVersion, ProductVersion, FileDescription, LegalCopyright
# FileVersion 1.6.0.0 / ProductVersion 1.6.0 / "Register Broker SMBus Driver ..."
```

> Do **not** submit a `-p:DevProbes=true` build — that's broker-side, but treat "clean release
> only" as the rule for the whole package.

---

## 3. Stage a package directory

Put exactly the files the catalog will cover in one folder:

```powershell
$pkg = "$env:TEMP\BrokerSmbus-pkg"
New-Item -ItemType Directory -Force $pkg | Out-Null
Copy-Item .\BrokerSmbusDriver\BrokerSmbus.inf              $pkg
Copy-Item .\BrokerSmbusDriver\x64\Release\BrokerSmbus.sys  $pkg
```

---

## 4. Validate the INF

```powershell
$bin = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64"
& "$bin\InfVerif.exe" /v "$pkg\BrokerSmbus.inf"
```

- Attestation does **not** require full *universal/DCH* compliance, so a non-PnP
  `[DefaultInstall]` INF is acceptable. `Class=System` may emit an InfVerif note — that's
  expected for a low-level software driver; errors are not.
- Fix any **error** before continuing (warnings about the system class are tolerable).

---

## 5. Generate the catalog (`.cat`)

`Inf2Cat` creates the (unsigned) catalog hashing the package files. Microsoft re-signs it later.

```powershell
& "$bin\Inf2Cat.exe" /driver:"$pkg" /os:10_X64
# Add more targets as needed, comma-separated, e.g.:
#   /os:10_X64,10_NI_X64,10_VB_X64        (Win10 x64, Win11 21H2+, 22H2)
```

Produces `$pkg\BrokerSmbus.cat`. (`CatalogFile=BrokerSmbus.cat` in the INF must match — it does.)

---

## 6. Pack the CAB and EV-sign it

Partner Center attestation takes a **CAB** containing the package, **signed with your EV cert**.

`makecab` needs a directive file (`.ddf`):

```
.OPTION EXPLICIT
.Set CabinetNameTemplate=BrokerSmbus.cab
.Set DiskDirectoryTemplate=CDROM
.Set CompressionType=MSZIP
.Set Cabinet=on
.Set Compress=on
.Set DiskDirectory1=.
"<pkg>\BrokerSmbus.inf"
"<pkg>\BrokerSmbus.sys"
"<pkg>\BrokerSmbus.cat"
```

```powershell
# (write the .ddf above with the real $pkg path substituted, then:)
makecab /f BrokerSmbus.ddf                       # -> BrokerSmbus.cab

# Sign the CAB with the EV cert (token/HSM). /a auto-selects; or pin with /sha1 <thumb>.
& "$bin\signtool.exe" sign /v /fd SHA256 /a `
    /tr http://timestamp.digicert.com /td SHA256 BrokerSmbus.cab

& "$bin\signtool.exe" verify /v /pa BrokerSmbus.cab
```

> EV signing of the CAB is what authenticates the submission to your account. You do **not**
> need to embed-sign the `.sys` yourself for the production path — Microsoft's catalog signature
> is what the kernel trusts.

---

## 7. Submit for attestation

1. Partner Center → **Windows Hardware** → **Drivers** → **New shipping label** /
   **Submit new hardware**.
2. Upload `BrokerSmbus.cab`.
3. Choose **Attestation** signing (not WHQL).
4. Submit. Turnaround is typically minutes-to-hours.
5. Download the **Microsoft-signed** package — it contains the signed `BrokerSmbus.cat` (and the
   `.inf`/`.sys`). The `.sys` bytes are unchanged; the trust now lives in the signed catalog.

---

## 8. Verify the signed package

```powershell
# The MS-signed catalog should verify against the kernel-mode policy:
& "$bin\signtool.exe" verify /v /kp /c BrokerSmbus.cat BrokerSmbus.sys
```

Then the real acceptance test — on a **reference machine that is NOT a dev box**:

- Secure Boot **ON**, Memory Integrity (HVCI) **ON**, `bcdedit` test-signing **OFF**.
- Install (next section) and confirm the service starts and `\\.\BrokerSmbus` opens.

If it loads there, you've left the lab.

---

## 9. Deploy the signed driver

Install **via the INF**, not `sc create` — this copies the `.sys` into the Driver Store and
points the service at that stable path, which also **fixes the "ImagePath points at the build
tree" time-bomb** (Open items #6):

```powershell
# ELEVATED, on the target machine, from the folder holding the signed inf/sys/cat:
pnputil /add-driver BrokerSmbus.inf /install
# Verify:
sc.exe qc BrokerSmbus          # BINARY_PATH_NAME should be under \DriverStore\, StartType DEMAND
```

The broker/control services install unchanged (`Install-SensorBrokerService.ps1` — pass
`-SkipDriver` since the driver is now managed by the INF/Driver Store). The installer's
signature **preflight** still protects against a bad swap.

---

## 10. When you must re-submit (the freeze list)

The signature covers one `.sys`. **Any driver code change = a new build + a new submission.**
Decide these *before* the first submission to avoid burning a second cycle:

- **EC 12V RGB bring-up** — flipping `SuperioRgbImplemented = TRUE` + setting the real NCT6687
  register window is a driver change. Currently inert by design; ship it inert and re-sign later
  only if EC RGB is validated. (The IOCTL surface is already present, so this is a re-sign, not a
  protocol break.)
- **A kernel-side fix surfaced by validating** the HW-unvalidated paths (Intel i801, NCT6775
  family, NCT6683/6686 siblings).
- **Any new bounded IOCTL** or change to a brick-guard window.
- **A KMDF version change** from a WDK upgrade (update `KmdfLibraryVersion` in the INF + rebuild).

A pure **broker/calibration/RGB-catalog** change is *not* a driver change and never needs
re-signing — that separation is the whole point of keeping register maps in signed user-mode
code and the kernel surface class-wide and stable.

---

## Quick reference — full sequence

```powershell
.\scripts\Stop-BrokerServices.ps1
.\scripts\Build-All.ps1 -Bridge -Driver -Clean
# stage inf + sys into $pkg
& InfVerif  /v   $pkg\BrokerSmbus.inf
& Inf2Cat   /driver:$pkg /os:10_X64
makecab     /f   BrokerSmbus.ddf
& signtool  sign /v /fd SHA256 /a /tr <timestamp> /td SHA256 BrokerSmbus.cab
# upload BrokerSmbus.cab to Partner Center -> Attestation -> download signed package
& signtool  verify /v /kp /c BrokerSmbus.cat BrokerSmbus.sys
pnputil     /add-driver BrokerSmbus.inf /install     # on the target, elevated
```
