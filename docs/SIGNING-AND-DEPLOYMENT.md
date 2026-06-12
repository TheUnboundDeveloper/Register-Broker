# Signing & Deployment — getting to a real user environment

The goal this guide works toward: **a properly-signed install that runs on an ordinary,
locked-down Windows machine** — Secure Boot on, Memory Integrity (HVCI) on, no test-signing,
the consumer non-admin — with the broker as a hardened service and only signed clients
allowed. This is the difference between "works on the dev box" and "shippable."

> **Where we are now:** the driver is **test-signed** (loads only with test-signing on / HVCI
> off — a lab machine), and client enforcement defaults to **audit-only**. Everything below is
> the path from there to a deployable product. Treat it as a checklist, not a one-afternoon
> task — the certificate + Microsoft onboarding has real lead time.

---

## 1. Why signing is the gatekeeper

A kernel-mode driver will not load on a stock Windows 10/11 x64 machine unless it is signed in
a way the OS trusts. The relevant facts:

- **Test-signing** (today) works only on machines with `bcdedit /set testsigning on` and
  Memory Integrity off — i.e. dev boxes. Not deployable.
- **Legacy cross-signing** (a normal code-signing cert chained to a Microsoft cross-cert) is
  **no longer accepted** for kernel drivers on Windows 10 1607+.
- The supported path is **Microsoft-signed via the Partner Center "Windows Hardware"
  program** — either **attestation signing** (no hardware lab testing; right for a small
  driver like this) or full **WHQL** (HLK testing; heavier, needed for the Windows logo /
  broad distribution). Attestation signing is the realistic target here.
- A Microsoft-attestation-signed driver loads with **HVCI on and Secure Boot on** — the end
  state we want.

So the critical-path dependency is: **EV code-signing certificate → Partner Center hardware
account → submit driver → Microsoft signs it.**

---

## 2. Prerequisites to procure (start these early — they gate everything)

1. **An organization / legal entity.** EV certs and Partner Center hardware accounts are
   issued to a verifiable organization, not an individual hobbyist in most cases. Decide the
   entity now.
2. **An EV (Extended Validation) code-signing certificate.** From a CA (DigiCert, Sectigo,
   etc.). Involves organization validation and ships on a **FIPS hardware token** or a **cloud
   HSM** (Azure Key Vault + a supported signing flow). Budget for cost + multi-week issuance.
   - The EV cert is used to **establish/authenticate the Partner Center account** and to
     **sign the driver submission**.
3. **A Microsoft Partner Center account** with the **Windows Hardware** program enabled,
   authenticated with the EV cert. (This is the "Hardware Dev Center" dashboard.)
4. *(Recommended)* **A standard code-signing certificate (OV or EV)** for the **user-mode**
   binaries (broker + client). This isn't the kernel path — it's so the broker's signer-pin
   can pin a real cert and so SmartScreen/AV don't flag the install.

---

## 3. Driver signing — the attestation path

Once the cert + account exist:

1. **Make a driver package.** Even though `BrokerSmbus` is a non-PnP driver loaded via
   `sc create type=kernel`, the Partner Center submission needs an **INF** + the **`.sys`** +
   symbols, packaged into a **CAB**. Author a minimal INF describing the non-PnP/legacy
   service install. Keep the architecture x64.
2. **Sign the CAB** with the EV cert (`signtool`, with a trusted timestamp).
3. **Submit to Partner Center** as a driver submission and request **attestation signing**.
   Microsoft validates and returns a **Microsoft-signed** driver package (`.cat` + the signed
   binaries).
4. **Ship the Microsoft-signed driver** (not your test-signed one). Verify it loads on a
   reference machine with **Secure Boot ON and Memory Integrity ON** and **test-signing OFF** —
   that's the acceptance test that we've left the lab.

> Re-submit when the `.sys` changes (the signature is over a specific binary). Build this into
> the release process. Keep the test-signed flow for dev only
> (`Build-Driver-DirectLink.ps1` auto-runs `Sign-Driver-TestCert.ps1` on every build unless
> `-NoSign`; the sign script needs elevation only for the one-time LocalMachine trust import).

---

## 4. User-mode binary signing

Sign `BrokerSensorBridge.exe` (broker + control) and any shipped client with the OV/EV
code-signing cert, timestamped (`signtool sign /fd sha256 /tr <rfc3161> /td sha256`). Then:

- **Pin the client's signer** in the deployed config: set `RequireAuthorizedClient=true` and
  put the client's signer SHA-1 thumbprint in `AllowedClientSigners` (the installer's
  `-RequireAuthorizedClient -AllowedClientSigners <thumb>` does this and validates the
  thumbprint format). Now only your signed client can use the broker; everything else is
  dropped at connect. (The existing `Sign-BrokerSensorBridge.ps1` prints the thumbprint to
  pin during dev; production swaps the dev cert for the real one — the pin logic is unchanged.)

---

## 5. Deployment hardening (flip the dev defaults)

A deployment build/install differs from the dev setup in these concrete ways:

| Aspect | Dev default | Deployment |
|---|---|---|
| Driver signing | test-signed + test-signing on | Microsoft attestation-signed; HVCI/Secure Boot **on** |
| Dev probes | sometimes `-p:DevProbes=true` | **never** — normal build only (no raw-addressing code present) |
| `RequireAuthorizedClient` | `false` (audit-only) | **`true`**, with the production client signer pinned |
| Broker account | LocalSystem | LocalSystem (needs the driver-device ACL) — but **consider a virtual service account** (`NT SERVICE\SensorBroker`) with an explicit DACL grant on `\\.\BrokerSmbus` for least privilege, especially for the **write-capable** control service |
| Install location | run in place from the user-writable `publish\` tree | **`-Deploy`**: the installer copies the bridge + driver to `%ProgramFiles%\SensorBroker` (admin-only writable) and registers the services from there — closes the local EoP where a non-admin could swap the exe/`.sys`/config a SYSTEM service loads |
| Config integrity | repo file | installer writes BOM-less JSON; with `-Deploy` the install dir + `appsettings.json` inherit the admin-only Program Files ACL |
| Logs/audit | `%LOCALAPPDATA%` (systemprofile) | consider `%ProgramData%\BrokerSensorBridge\` so the audit trail is discoverable |

The installer (`Install-SensorBrokerService.ps1`) already does driver→broker dependency,
auto-start, crash-recovery, signer/path validation, fail-closed config, the medium-IL
pipe DACL, the `-Deploy` Program Files hardening, and a **driver-signature preflight** (it
refuses to tear down the existing services if the new `.sys` is `NotSigned`/`HashMismatch`,
so a bad rebuild can't leave the machine with no services). The remaining packaging step is
wrapping it in a **signed installer** (MSI/MSIX or
a signed `.exe`) that lays down the Microsoft-signed driver, the signed broker, and the
hardened `appsettings.json`, then registers the services.

---

## 6. Production readiness checklist

**Blocking (must do before any real deployment):**

- [ ] EV code-signing cert procured; Partner Center Windows Hardware account live.
- [ ] Driver INF/CAB authored; submission → **Microsoft attestation-signed** `.sys`.
- [ ] Signed driver **loads with HVCI on + Secure Boot on + test-signing off** (acceptance test).
- [ ] Broker/client binaries Authenticode-signed (timestamped).
- [ ] Deployment config: `RequireAuthorizedClient=true` + production client signer pinned.
- [ ] Deployment build verified **without** `DevProbes` (no raw-addressing code; no DEV banner).
- [ ] Install dir + `appsettings.json` are admin-only writable (install with `-Deploy`).
- [ ] Signed installer (MSI/MSIX) that registers everything and applies the hardened config.

**Strongly recommended:**

- [ ] Security review of the IOCTL surface and the brick-guard before broadening RGB write
      support (the write path is the brick-risk surface).
- [ ] Decide LocalSystem vs a least-privilege virtual service account for each service
      (especially the write-capable control service).
- [ ] Intel **i801** SMBus path hardware-validated (today only AMD FCH is validated) — needed
      before claiming a universal tool. Likewise the NCT6775-family and NCT6683/NCT6686
      Super-I/O backends (built, hardware-unvalidated).
- [ ] Per-controller **register/command** allowlist in the brick-guard if writes expand beyond
      the curated RGB map (today the guard bounds the address, not the register).

**Out of scope / later:**

- [ ] WHQL certification (only if you want the Windows logo / driver distribution via Windows
      Update) — heavier HLK testing on top of attestation.

---

## 7. The reference: how this maps to the design

The whole point of the architecture is that **signing makes the security story credible, not
just functional.** A Microsoft-attestation-signed *narrow* driver that exposes only bounded,
validated transactions — loading under HVCI — is the antithesis of WinRing0 (an
arbitrary-memory driver on the block list). The phased plan and threat model behind this are
in [`BROKER-DESIGN.md`](BROKER-DESIGN.md) (see "Phase E — Production signing"); the
broker-side remaining work is in [`BROKER-ROADMAP.md`](BROKER-ROADMAP.md).
