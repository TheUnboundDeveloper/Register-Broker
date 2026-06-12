# Register Broker — Sensor & Control Broker Design

Status: **implemented & hardware-validated** (first validated 2026-06-08; current as of
2026-06-12) — identity-authenticated control plane, a narrow KMDF driver (SMBus +
SMU + Super-I/O reads, brick-guarded RGB writes), Windows-Service packaging, and a
separate non-admin RGB write path are all working end-to-end on real hardware as the
**Register Broker** services. The remaining work is production signing plus hardware
validation of the unvalidated chip backends (see §8 / §10). Companion to
[`README.md`](../README.md), [`BROKER-ROADMAP.md`](BROKER-ROADMAP.md), and
[`CODE-REVIEW.md`](CODE-REVIEW.md).

This document scopes the evolution of `BrokerSensorBridge` into a hardened broker
that lets **non-admin** applications read PC sensors *and* drive SMBus-attached RGB
(RAM, motherboard) **without** loading a vulnerable kernel driver into every app. The
thesis is proven: a non-elevated client has read privileged Ryzen/board sensors and
changed RAM RGB color through the elevated broker, with no WinRing0. **Register Broker
is a standalone framework** — its former first consumer (an RGB-app hardware-sync
plugin) was removed from the repo 2026-06-11 (personal-use only, not distributed);
any non-admin client that speaks the pipe protocol can consume it.

---

## 1. Problem & hard constraints

- **Basic sensor reads** can be done entirely in user mode (WMI / PDH / NVAPI), but the
  interesting ones (CPU die temperature via SMU, Super-I/O board sensors, SMBus devices)
  cannot — they need Ring-0 register access.
- **RGB control over SMBus** (DRAM/SPD, motherboard controllers) is a **Ring-0**
  operation. There is no user-mode SMBus API in Windows — a kernel driver is mandatory.
- The classic driver, **WinRing0**, is on Microsoft's vulnerable-driver blocklist and
  is **blocked by HVCI / Memory Integrity** (originally confirmed on the dev machine
  with `VBS=running`, `HVCI=on`: WinRing0 not loaded → RGB tools detect **no RAM**).
  Elevation does **not** bypass this — the block is hypervisor-enforced. *(HVCI and
  Secure Boot are now switched **off** on the dev box so the test-signed `BrokerSmbus`
  driver can load during bring-up; the end goal is an attestation-signed driver that
  loads with HVCI back on.)*

**Conclusion:** we cannot make SMBus control "user-mode." We can only make the
Ring-0 surface *small, safe, signed, and brokered* — replacing WinRing0's
"any caller gets arbitrary physical memory / MSR / port I/O" footgun with a driver
that exposes **only scoped SMBus transactions**, reachable only through an
authenticated broker.

---

## 2. Threat model

Defend against:

| Adversary | Defense |
|---|---|
| **Passive port/service scanner** | No discoverable TCP surface for control; named-pipe/ALPC with ACL; unauthenticated probes get an indistinguishable, dataless response. |
| **Malicious local non-admin process** abusing the broker for arbitrary HW I/O | Narrow IOCTL (transactions only, never raw memory/ports); per-client auth; capability scopes; (bus,address) allowlist. |
| **Replay / spoofed client** | No secret to replay — auth is by **peer-process identity + Authenticode signer pin** resolved from the live pipe connection; short-lived session tokens; unauthorized peers dropped with no reply. |
| **The driver itself becoming the next "vulnerable driver"** | Minimal attack surface, strict input validation, bounded addresses, no physical-memory/MSR primitives, audited IOCTLs. |
| **Bricking hardware** (bad SMBus writes to SPD) | Address allow/deny lists, write guards on SPD/protected ranges, transaction serialization. |

Out of scope: remote attackers (loopback/pipe only), kernel exploits below the driver.

---

## 3. Architecture

```
  ┌─────────────────────────┐         ┌──────────────────────────────────────┐
  │ Non-admin clients        │        │ Broker service (one small process)    │
  │  • --client (built-in     │  auth  │  • holds the ONLY driver handle        │
  │    reference consumer)    │◀──────▶│  • handshake / session / scopes        │
  │  • any app speaking the   │  pipe  │  • named catalog (no addressing)       │
  │    pipe protocol          │        │  • rate limit + audit log              │
  └─────────────────────────┘         │  • board calibration (labels/scales)   │
                                        └───────────────┬───────────────────────┘
                                            scoped IOCTL │ (transactions only)
                                        ┌───────────────▼───────────────────────┐
                                        │ Minimal signed KMDF driver             │
                                        │  • SMBus transaction IOCTL ONLY        │
                                        │  • no phys-mem / no MSR / no raw ports  │
                                        └───────────────┬───────────────────────┘
                                                         │ SMBus host controller
                                                 ┌───────▼────────┐
                                                 │ RAM / mobo RGB  │
                                                 └────────────────┘
```

Privilege boundary: only the broker (and the driver it loads) is elevated. Every
consumer stays a normal user process. Sensors keep their existing user-mode path;
the driver is only on the **control** path.

---

## 4. Components

### 4.1 Kernel driver (the scoped Ring-0 surface)
- **KMDF** driver exposing a single device with a tiny IOCTL set:
  `IOCTL_SMBUS_XFER { bus_id, address, protocol(quick/byte/word/block), command, data[] }`.
- The driver performs the SMBus host-controller transaction internally (it owns the
  controller's port range derived from PCI config). **Clients never issue raw port
  I/O** — they describe a transaction; the driver executes a bounded one.
- Explicitly **absent**: physical-memory mapping, MSR read/write, arbitrary
  `IN`/`OUT`. This is what makes it not-WinRing0.
- Input validation on every field; reject unknown bus ids and out-of-range commands.
- **Signing reality:** to load under Secure Boot + HVCI it must be **attestation-
  signed by Microsoft** (Partner Center hardware account) with an **EV code-signing
  certificate**. Dev/PoC can use test-signing with Secure Boot off on a lab box.

### 4.2 Broker service (the policy layer)
- Owns the driver handle; no client gets a handle directly.
- Enforces: authentication, capability scopes, (bus,address) allowlist, brick-guard
  write rules, rate limiting, transaction serialization, and an audit log.
- Serves **sensor** reads over the named pipe (`sensor.list` / `sensor.read` /
  `sensor.readall` — the batch op reads the whole catalog in **one** rate-limited op).
  The earlier unauthenticated HTTP feed (`/data.json`, `/sensors.json`, `/health`) was
  **removed 2026-06-09**, and the legacy bulk `sensors` op (the third-party
  hardware-library payload) was **removed 2026-06-11** — everything now rides the
  authenticated pipe, catalog-only.
- The catalog is assembled from **raw channels** (stable ids like `smu.cpu.temp`,
  `nct6687d.volt.0`, `dimm.0`) plus **board calibration DATA** — labels and scales only,
  never addresses (`calibration.default.json` + optional
  `ProgramData\SensorBroker\calibration.user.json`; legacy semantic ids resolve via a
  built-in alias map; `--calibration` inspector).
- Elevation is scoped to this one process (or a thin driver-loader sub-service).

### 4.3 Client integration
- Any non-admin application can consume the broker by speaking the named-pipe protocol
  in [`CLIENT-PROTOCOL.md`](CLIENT-PROTOCOL.md) — sensors by catalog id, RGB by device
  name. The built-in `--client` mode is the reference consumer.
- *Historical:* the original first consumer was an RGB-app hardware-sync plugin that
  read the sensor catalog and drove per-LED RGB through the broker (live-validated
  2026-06-09). It was **removed from the repo 2026-06-11** — Register Broker is
  distributed standalone; the plugin remains personal-use only.

---

## 5. Control channel & handshake

- **Transport:** a **named pipe** with a restrictive security descriptor — no TCP at all.
  Benefits: OS ACLs, no remote reachability, and the broker can read the **peer process
  identity/signature**. Sensors *and* control both ride the pipe; the legacy sensor HTTP
  endpoint was removed 2026-06-09, so there is no TCP surface to defend.
- **Authentication (before any control op) — identity, not a secret:**
  1. Client connects to the **well-known** control pipe (`\\.\pipe\SensorBroker`).
     The OS **pipe DACL** keeps out other users.
  2. The broker resolves the **peer process identity** (PID → real image path) and,
     when configured, verifies the client's **Authenticode signer** (`WinVerifyTrust`)
     and pins on the **signer thumbprint**. An unauthorized peer is dropped with **no
     reply at all** — nothing to fingerprint.
  3. The authorized client sends a `hello` declaring the scopes it wants; the broker
     returns a short-lived **session token** carrying the granted **scopes**.
- **Why no shared secret:** a per-user secret file fits a per-user app, not a
  system-wide service (whose clients may not be the same user, and where a file on
  disk is just one more thing to leak/rotate). Binding to *who the binary is* —
  DACL + peer identity + signer pin — is the stronger, service-appropriate anchor.
  The signer pin treats `CERT_E_UNTRUSTEDROOT` as acceptable (it pins the exact cert
  by thumbprint) so a dev test cert needs **no** `LocalMachine\Root` install, while
  tamper/unsigned images are still rejected.
- **Capability scopes:** `sensors:read`, `smbus:read` (sensor broker), and `rgb:write`
  (the separate write-only control service — see §7c). A consumer is granted only what
  it needs; the broker refuses everything else, and the sensor broker never offers a
  write scope at all.

---

## 6. SMBus safety guardrails (control path)

- **(bus, address) allowlist** — only service controllers/addresses known to be RGB,
  configurable; refuse everything else by default.
- **Brick-guard** — deny writes to SPD EEPROM / write-protect ranges and any address
  not on the RGB allowlist; never expose SPD write-enable.
- **Serialize** all transactions (SMBus is single-master, slow; concurrent access
  corrupts state).
- **Rate-limit** per client/session.
- **Audit** every transaction: timestamp, client identity, bus, address, register,
  read/write, result.

---

## 7. Driver strategy — options

| Option | Pros | Cons / risk |
|---|---|---|
| **A. Custom minimal KMDF driver** (recommended) | Smallest, purpose-built attack surface; we control the IOCTL contract; defensible security story | Must write + maintain a kernel driver; **EV cert + Microsoft attestation signing** required for HVCI/Secure Boot distribution; WHQL-style process |
| **B. Rebuilt / re-signed WinRing0 fork** | Less new code | Broad surface remains; blocklist may match beyond a single hash; weak security story; likely still flagged |
| **C. Adopt an existing already-signed SMBus-capable driver** | No signing burden if one fits | Hard to find one with a usable, scoped SMBus transaction interface and acceptable licensing |

Recommendation: **A**, with a **dev-signed PoC first** (Secure Boot off on a lab
machine) to prove the transaction path end-to-end, then invest in production signing
once the contract is stable.

---

## 7a. Phase A — implemented (control channel, identity-authenticated)

Status: **done and self-tested** (`BrokerSensorBridge.exe --selftest` → SELFTEST PASS),
plus a live cross-process test (signed `--client` authorized purely by signer thumbprint;
a wrong-thumbprint pin rejects the same binary). Files: `BrokerProtocol.cs`,
`BrokerControlServer.cs`, `ClientAuthorization.cs`, `PeerIdentity.cs`, `PeerSignature.cs`,
wired into `Program.cs`. (The legacy HTTP sensor endpoints were removed 2026-06-09, and
the legacy bulk `sensors` op was removed 2026-06-11 — the pipe + named catalog is the
only serving surface.)

**Trust model (protocol v2).** There is **no shared secret and no descriptor file**.
The broker listens on a **fixed well-known pipe** `\\.\pipe\SensorBroker`. Three layers
authenticate a client, all bound to *who the binary is*:

1. **Pipe DACL** — the elevated broker sets an explicit same-user grant so its
   medium-integrity clients can open the pipe while other users cannot.
2. **Peer-process identity** — on connect, before anything is sent, the broker resolves
   the client's PID (`GetNamedPipeClientProcessId`) and real image path
   (`QueryFullProcessImageName`).
3. **Authenticode signer pin** — `PeerSignature` runs `WinVerifyTrust` on that image
   (rejecting unsigned/tampered) and extracts the signer's SHA-1 thumbprint. Untrusted
   *root* is accepted on purpose: we pin the **exact** thumbprint, so a dev test cert
   (or later an EV cert) authorizes without installing a self-signed root machine-wide.

**Policy (`appsettings.json`):**

- `RequireAuthorizedClient = false` (default): audit only — every connection is logged
  with `pid` / `image` / `signer` but allowed (preserves the dev setup).
- `RequireAuthorizedClient = true`: a peer must match **either** `AllowedClientPaths`
  (full image path — for self-built **unsigned** binaries) **or** `AllowedClientSigners`
  (signer thumbprints — the stronger pin, survives the binary moving on disk). Anything
  else is dropped at connect with no reply. Sign a client with
  `scripts\Sign-BrokerSensorBridge.ps1` (prints the thumbprint to paste in).

**Wire protocol** — named pipe, each message = 4-byte big-endian length + UTF-8 JSON:

| # | Direction | Message |
|---|---|---|
| (connect) | — | broker validates DACL + peer identity + signer; unauthorized → silent close |
| 1 | client → server | `{"type":"hello","protocol":2,"scopes":["sensors:read"]}` |
| 2 | server → client | `{"type":"ok","token":"<b64 32B>","scopes":[...]}` **or** close |
| 3 | client → server | `{"token":"<b64>","op":"sensor.list"}` (also `"ping"`, `"sensor.read"`, `"sensor.readall"`; on the control pipe `"rgb.list"` / `"rgb.set"`) |
| 4 | server → client | `{"type":"data","op":"sensor.list",…}` / `{"type":"pong"}` / `{"type":"deny"}` |

**Enforced now:** requested scopes are intersected with the server allowlist
(`sensors:read`, plus `smbus:read` only when a driver backs it — `smbus:write` is never
offered); session tokens expire after 10 min; every failure returns a uniform
`{"type":"deny"}` or a silent close; frames capped at 1 MB.

**Residual risks (documented, to harden later):**
- **PID-reuse TOCTOU** — tiny window between connect and identity/signature query;
  acceptable for local same-user IPC, tightened later if needed.
- **Self-signed dev cert.** The test cert is pinned by thumbprint (not chained to a
  trusted CA). That is the right primitive for a lab box; production swaps in an EV
  cert and the same thumbprint-pin logic applies unchanged.
- **System-service DACL.** ✅ Addressed in code (2026-06-09, Phase C′). When the broker
  runs as LocalSystem, `TryBuildPipeSecurity` widens the DACL to an Authenticated-Users
  grant, leaning on the signer pin as the real gate. No explicit integrity label is set: an
  unlabeled pipe is treated as medium integrity (writable by medium-IL clients), and an
  explicit label fails pipe creation because `SeSecurityPrivilege` is not enabled on the
  SYSTEM token (found+fixed in the first live install). The same-user DACL remains for the dev
  (non-SYSTEM) path. ✅ Live-validated 2026-06-09: a non-admin client connected to the
  SYSTEM-hosted pipe and read the sensor catalog.

## 7b. Phase B — implemented & hardware-validated (driver + sensor sources)

Status: **the narrow driver is hardware-validated on the dev box (AMD).**
Multiple privileged sources flow through it with **no WinRing0**. Intel i801 SMBus, the
NCT6775 bank-select family, and NCT6683/6686 are written but not yet hardware-validated;
SMBus writes outside the brick-guarded RGB windows are deliberately never implemented.

Driver surface — narrow IOCTLs only, **no memory/MSR/arbitrary-port primitives**
(`BrokerSmbusDriver/inc/SmbusBrokerProtocol.h`); IOCTL dispatch is sequential:
- `INFO` — version / bus-count / capability bits (`CAP_READ 0x1`, `CAP_SMU 0x2`,
  `CAP_SUPERIO 0x4`, `CAP_WRITE 0x8`) / detected Super-I/O chip id (`SuperioChipId`).
- `XFER` — one bounded, read-only SMBus transaction (read byte/word/block,
  `addr ≤ 0x7F`). **Validated:** read real DDR4 SPD off a DIMM
  (`--bus=0 --addr=0x50 --cmd=0x02 → 0C`). Detected 2 DDR4 UDIMMs at
  `0x50`/`0x51`. AMD KERNCZ path gated on device-id + revision (`SmbusAmdIsKerncz`:
  `790B` rev≥0x51 / `780B` rev≥0x59, mirroring `i2c-piix4`) so older FCHs fall through
  instead of mis-reading. *(Bug fixed
  during bring-up: `METHOD_BUFFERED` IOCTLs share one system buffer; `Driver.c` zeroed
  the response before reading the request, wiping `Version` → every XFER `BadRequest`.
  Now snapshots the request first. Never showed in `--selftest` — that uses
  `MockSmbusBackend`, not the kernel IOCTL.)*
- `SMU_READ` — **named** Ryzen sensors (Tctl plus per-CCD temps); the kernel bakes in
  the SMN addresses, returns the raw 32-bit value (SMN reported-temp via root-complex
  PCI cfg `0x60`/`0x64`), and the broker applies the `k10temp` decode. **Validated:**
  `0x69BB0000` → 56.6 °C, matched HWiNFO; per-CCD presence-gated on the valid bit.
  CPUID-gated (`CAP_SMU`). `SmuAmd.c`, ported from Linux `k10temp`/`zenpower`.
- `SUPERIO_READ` — named `{kind, index}` board sensors over LPC, never a raw EC
  address. Two Nuvoton backends, auto-detect-gated on the SIO chip id:
  - **NCT668x EC family** (`SuperioNct.c`, ported from Linux `nct6683` /
    `Fred78290/nct6687d`): NCT6683 (`0xC730`), NCT6686 (`0xD440`), NCT6687D (`0xD590`),
    masked `0xFFF0`. **NCT6687D hardware-validated** (6 board temps matched HWiNFO
    labels to the degree; fans read RPM; voltages mapped); 6683/6686 are
    register-identical but unvalidated.
  - **NCT6775 bank-select family** (`SuperioNct6775.c`, NEW 2026-06-11, ported from
    Linux `nct6775`): NCT6779/6791/6792/6793/6795/6796/6797/6798 (kernel mask `0xFFF8`),
    with the IO-space-lock clear on 6791+. `KIND_NCT6775 = 3`. **Built,
    hardware-unvalidated.**
  - *Archived 2026-06-11:* the ITE IT87xx backend (and the companion Gigabyte IT8297
    USB-HID RGB path) was moved to `_archive_gigabyte\` after a domain expert's
    corrections; `KIND_ITE = 2` stays reserved.
- `WRITE` — brick-guarded SMBus write (byte/word, plus **block** `op=5` writing 1..32
  bytes atomically, added 2026-06-11; the request struct gained an appended
  `Length` + `Block[32]`, and the 24-byte V1 prefix is still accepted for byte/word).
  **In-kernel brick-guard:** writes are allowed only to addresses `0x70–0x77` and
  `0x39–0x3A` — SPD and everything else is unreachable regardless of what the broker
  asks. Offered to the broker only via `CAP_WRITE`.

So **no temperature on this box needs WinRing0 or any third-party hardware library
anymore** (CPU via SMU, board via Super-I/O, DIMMs via JC42 over SMBus —
presence-probed). The kernel returns raw register bytes for *baked-in* locations only;
the broker applies every decode. Full catalog in [`SENSOR-MAP.md`](SENSOR-MAP.md).

Driver timing note: the SMBus poll loop uses 25 µs busy-wait stalls up to ~2 ms, then
250 µs sleeps up to ~48 ms. (The original pure-sleep loop rounded every wait up to the
OS timer tick, which made multi-write RGB frames crawl — fixed 2026-06-11.)

Broker side:
- The broker advertises `smbus:read` only when the driver is present and reports the
  capability. It exposes sources through a **named sensor catalog** (`SensorCatalog.cs` +
  `Sensors/RawChannel.cs` / `Sensors/SensorDecode.cs` / `Sensors/Calibration.cs`) —
  `sensor.list` / `sensor.read --id=<id>` / `sensor.readall`. Stable raw ids
  (`smu.cpu.temp`, `smu.ccd.0-7`, `nct6687d.temp.0-5` / `.fan.0-7` / `.volt.0-14`,
  `nct6775.temp.0-5` / `.fan.0-6` / `.volt.0-15`, `dimm.0-7`); board calibration data
  supplies labels/scales only; legacy semantic ids resolve via the alias map.
  **Clients name a logical sensor, never
  an address, and cannot scan hardware.** The raw `smbus.read` *client* op was removed;
  raw addressing survives only in compile-time-gated dev probes (`--smbus-read` /
  `--smu-read` / `--superio-read` / `--ene-read` / `--ene-set`, `-p:DevProbes=true`)
  that open `\\.\BrokerSmbus` directly and are excluded from normal builds.
- Bring-up checklists: [`BRINGUP-AMD-FCH.md`](../BrokerSmbusDriver/BRINGUP-AMD-FCH.md)
  (primary) and [`BRINGUP-i801.md`](../BrokerSmbusDriver/BRINGUP-i801.md) (Intel, pending).

## 7c. Phase C — implemented (policy hardening, service-grade)

Status: **done (2026-06-08).** Beyond the identity gate (still audit-only by default),
three guardrails are now **unconditional** (`BrokerPolicy` / `RateLimiter`):
- **Per-session token-bucket rate limit** (default 30 ops/s, burst 60; the RGB control
  service runs at 120/240 for per-LED frames) — an
  authorized-but-abusive client can't flood the bus. Self-tested; verified live
  (`pong=3/deny=5` flood throttle).
- **Bounded session table** (`MaxSessions` 32, max 8 per identity, expired-pruned).
- **Dedicated audit log** (`audit.log`) recording every connect (peer identity), auth
  decision, and op→result. `ClientAuthorization.Authorize` returns an `AuthDecision`
  carrying the identity for the trail.

Config knobs in `appsettings.json`. Widening the pipe DACL beyond same-user was
delivered with the Windows-Service work (Phase C′). Still deferred within C: flipping
`RequireAuthorizedClient` on by default (needs a shipped production-signer policy; the
installer already supports `-RequireAuthorizedClient -AllowedClientSigners` /
`-AllowedClientPaths`, with input validation).

## 7d. RGB write control — delivered (the full thesis)

Status: **done (2026-06-08; per-LED + block-write quality fixes 2026-06-11).** A
**separate, write-only control service**
(`BrokerSensorBridge.exe --control`) holds the driver's brick-guarded write IOCTL and
serves `\\.\pipe\BrokerControl` with the `rgb:write` scope (offered only when
`allowRgbWrite` + the driver advertises `CAP_WRITE`; **the sensor broker never
offers writes**). `RgbCatalog` is the baked device→hardware map (`ram0`→bus 0/`0x39`,
`ram1`→bus 0/`0x3A`, 5 LEDs each — ENE/Aura DRAM, a publicly documented hardware
protocol reproduced as register facts; see
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md)); ops `rgb.list` /
`rgb.set {device, color|colors[]}` (whole-device color or a per-LED frame).
`RgbRegistry` keeps the device layer transport-agnostic.

**Per-LED frame quality (2026-06-11):** each LED's RGB triple is written as **one
atomic 3-byte block write** (the driver's `WriteBlock` op), and the controller's
DIRECT/APPLY mode is **latched once per controller** instead of re-latched per frame —
the per-frame re-latch was the visible blink; the latch is cleared on failure so the
next frame re-establishes it.

**Verified live:** a non-elevated `--client --control --op=rgb.set --device=ram0
--color=00FF00` (elevated=False) changed the RAM color — naming a *device*, never an
address; it cannot scan or write SPD. Kernel brick-guard + baked map + scope +
rate-limit + audit all enforce the boundary — this is exactly what conventional RGB
tools still need admin (and WinRing0) to do. Files: `RgbCatalog.cs`,
`Smbus/EneController.cs`, `Rgb/RgbRegistry.cs`, `BrokerControlServer`
(`rgb.set`/`rgb.list`), `Program.RunControlServiceAsync`.

## 8. Phased roadmap

- **Phase A — Harden the sensor broker (no driver).** ✅ Done — named-pipe control
  channel, session tokens, `sensors:read` scope, and **identity authentication**:
  pipe DACL + peer-process identity + **Authenticode signer-thumbprint pin** (the
  shared-secret HMAC handshake was removed in protocol v2; see §7a). Later sub-step for
  a system-wide service: widen the pipe DACL beyond same-user and require the signer pin.
- **Phase B — Driver + sensor sources (dev-signed).** ✅ Done & hardware-validated.
  Narrow KMDF driver with `XFER` (AMD FCH SMBus, SPD-validated), `SMU_READ` (Ryzen Tctl
  + per-CCD), `SUPERIO_READ` (NCT668x EC family — NCT6687D validated — plus the new
  NCT6775 bank-select family), and the brick-guarded `WRITE` (byte/word/block), exposed
  by named catalog (§7b). Remaining hardware validation: **Intel i801** SMBus
  (universal-tool requirement), the **NCT6775 family**, and **NCT6683/6686**.
- **Phase C — Broker control plane.** ✅ Done. Driver handle ownership, scopes,
  rate-limit, bounded sessions, audit (§7c); plus the brick-guarded **`rgb:write`**
  control service and baked RGB map (§7d). The sensor catalog replaces the
  (bus,address) client allowlist — clients can't address hardware at all.
- **Phase D — first real consumer.** ✅ Done, then retired from the repo. An RGB-app
  hardware-sync plugin consumed the sensor catalog and drove per-LED RGB through the
  broker, live-validated 2026-06-09 — proving an unmodified non-admin desktop app can
  ride the broker. The plugin was **removed 2026-06-11** (personal-use only, not
  distributed); Register Broker ships standalone, and any pipe-protocol client fills
  this role.
- **Phase C′ — Windows-Service packaging.** ✅ Implemented and live-validated (2026-06-09):
  a non-admin client read the full sensor catalog from the LocalSystem `SensorBroker`
  service ("Register Broker Sensor Service"); clean stop/unload and the RGB control
  service ("Register Broker RGB Control") were also validated live. The broker/control
  bodies run under the SCM via
  `Microsoft.Extensions.Hosting.WindowsServices` (`ServiceHost.cs`; routed on `--service` /
  `WindowsServiceHelpers.IsWindowsService()`). One installer
  (`scripts\Install-SensorBrokerService.ps1`) registers the kernel-driver service + broker
  service (+ optional control service) with a driver→broker dependency, auto-start, and crash
  recovery; the matching uninstaller stops consumers first so the driver handle is released
  before the driver unloads. Running as LocalSystem, the broker widens the pipe DACL to
  Authenticated-Users (`TryBuildPipeSecurity`) so non-admin clients connect — no explicit
  integrity label (an unlabeled pipe is medium integrity, writable by medium-IL clients; an
  explicit label fails creation as `SeSecurityPrivilege` isn't enabled on the SYSTEM token,
  found+fixed in the first live install). Resolves the §7a "system-service DACL" residual. The
  installer can flip `RequireAuthorizedClient` on with a shipped signer/path policy, and now
  runs a **signature preflight** (refuses teardown if the built `.sys` is
  NotSigned/HashMismatch — lesson from a 2026-06-11 breakage). Note: the driver service's
  ImagePath points at the build output (`BrokerSmbusDriver\x64\Release\BrokerSmbus.sys`),
  and a loaded driver link-locks its image — rebuilds require stopping the stack
  (`scripts\Stop-BrokerServices.ps1`).
- **Phase E — Production signing & guardrail review.** ⬜ Pending. EV cert + Microsoft
  attestation signing; flipping `RequireAuthorizedClient` on with a pinned production
  signer; security review of the IOCTL surface and the baked maps; service
  distribution/packaging beyond the dev installer.

---

## 9. Decisions — resolved (kept for the record)

1. **Driver strategy** — ✅ custom minimal KMDF (Option A). Built, dev-signed, and
   hardware-validated on AMD; the narrow IOCTL contract is the defensible security story.
2. **Control transport** — ✅ named pipe (`\\.\pipe\SensorBroker` for sensors,
   `\\.\pipe\BrokerControl` for RGB write). Nothing rides TCP; the legacy HTTP sensor feed
   was removed 2026-06-09, and the legacy bulk `sensors` op 2026-06-11 — catalog ops only.
3. **Auth model** — ✅ no shared secret: pipe DACL + peer-process identity + Authenticode
   signer pin (protocol v2). See §5 / §7a.
4. **Integration depth** — ✅ resolved by going **standalone**. RGB **control** is
   delivered broker-side via a named-device client (`rgb.set`); the former RGB-app
   plugin consumer was removed from the repo 2026-06-11 (personal-use only). Register
   Broker ships as a framework; consumers integrate via `docs/CLIENT-PROTOCOL.md`.
5. **Dependencies** — ✅ zero third-party hardware libraries (2026-06-11). The in-proc
   hardware-monitoring library and the USB-HID library were both removed; the only
   NuGet dependency is `Microsoft.Extensions.Hosting.WindowsServices`. License: MIT +
   Commons Clause, with Linux driver references recorded as register facts in
   `THIRD-PARTY-NOTICES.md`.

Still open: **signing budget/timeline** — provisioning an EV cert + Microsoft hardware
dev account for a distributable driver (Phase E) vs staying dev-signed (lab-only) for now.

## 10. Open risks / unknowns

- Microsoft attestation signing turnaround and EV cert procurement lead time (Phase E).
- **Intel i801** SMBus path is written but not hardware-validated (no Intel test box);
  needed for a universal tool. Likewise the **NCT6775 bank-select family** and
  **NCT6683/6686** Super-I/O backends (built, register-faithful, unvalidated). Correct
  RGB address maps per DIMM/board vendor beyond the dev box's baked `RgbCatalog`.
- **SMU PM-table metrics** (CPU power PPT/TDC/EDC, IOD/L3/die-average temps) need the
  SMU mailbox protocol — a separate mechanism, deliberately not started.
- **Detector/backend registry** (Phase 3 of `CALIBRATION-AND-REGISTRY-PLAN.md`): adding
  a new chip is still a code change in the detect dispatch, not yet data-driven.
- Coexistence: ensure the broker's driver and any other Ring-0 sensor tool don't
  contend for the SMBus controller.
- **PID-reuse TOCTOU** on the identity check — accepted residual for local same-user IPC
  (see §7a); revisit for a multi-user system service.
- **SMBus writes outside the RGB windows** — deliberately never implemented (in-kernel
  brick-guard); not a roadmap item.
