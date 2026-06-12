# Broker Roadmap — what's left (broker-only)

**Scope:** the broker(s) *only* — the custom auth / policy / marshalling layer that fronts
the kernel driver and serves non-admin clients. This is the project's central pillar: it's
where all the custom logic lives. Deliberately **out of scope here:** production driver
signing (EV cert + MS attestation), tracked in `BROKER-DESIGN.md` §8. (The former RGB-app /
Hardware Sync plugin items dropped out of scope entirely — the plugin was removed from the
repo 2026-06-11; Register Broker is standalone.)

Companion: `BROKER-DESIGN.md` (threat model, architecture, full phased plan).

---

## Where we are now (2026-06-12)

The broker (`BrokerSensorBridge`, shipped as the **Register Broker** services) brokers
**multiple** privileged surfaces, hardware-validated on the dev box, with **zero
third-party hardware libraries** (the in-proc hardware-monitoring library and the
USB-HID library were removed 2026-06-11; only NuGet dep:
`Microsoft.Extensions.Hosting.WindowsServices`):

- **Control plane** — fixed pipe `\\.\pipe\SensorBroker`, protocol v2. Auth = pipe DACL +
  peer-process identity + Authenticode signer-thumbprint pin (no secret). Scopes:
  `sensors:read`, `smbus:read`, `rgb:write` (control service only). Built, self-tested,
  and **live-verified** (signed client authorized by thumbprint; wrong pin rejected).
- **Named sensor sources on our own driver** — CPU temp + per-CCD via **SMU**
  (`SMU_READ`, `k10temp` decode → 56.6 °C, matched HWiNFO), board temps/fans/voltages
  via **Super-I/O** (NCT668x EC family — NCT6687D validated — plus the NCT6775
  bank-select family, built/unvalidated), and DIMM temps (JC42 over SMBus,
  presence-probed). Exposed by **named catalog** (`sensor.list` / `sensor.read` /
  `sensor.readall` — the batch op reads the whole catalog in ONE rate-limited op) —
  clients name a logical sensor, never an address. Stable raw ids + **data-driven board
  calibration** (labels/scales only, no addresses; `calibration.default.json` + optional
  user overlay; alias map for legacy ids; `--calibration` inspector).
  **No temperature on this box needs WinRing0 or any third-party library anymore.**
- **SMBus reads** — the `XFER` IOCTL, **hardware-validated** on AMD (DDR4 SPD read end to
  end; raw addressing only via compile-time-gated dev probes, never a broker client).
- **Policy hardening — done.** Per-session token-bucket **rate limit** (30/60; control
  service 120/240), bounded **session table** (32 max, 8 per identity), and a dedicated
  **audit log** — all unconditional. Verified live.
- **Non-admin RGB write — delivered, per-LED.** A separate write-only control service
  (`--control`, pipe `\\.\pipe\BrokerControl`, `rgb:write` scope) with a baked
  `RgbCatalog` (`ram0`/`ram1`, 5 LEDs each, ENE/Aura DRAM); ops `rgb.list` /
  `rgb.set {device, color|colors[]}`. A non-elevated client changed RAM color by *name*.
  Per-LED frames use one atomic 3-byte block write per LED with DIRECT/APPLY latched
  once per controller (2026-06-11 fix — the per-frame re-latch was the blink).
- **Windows-Service packaging — implemented AND live-validated (2026-06-09).** A
  non-admin `--client` (elevated=False) read the full catalog from the LocalSystem
  `SensorBroker` service ("Register Broker Sensor Service") over the SYSTEM-hosted pipe;
  clean stop/unload and the `BrokerControl` RGB service ("Register Broker RGB Control")
  validated too. See Option C below.

The catalog is the **only** sensor surface: the legacy bulk `sensors` op (the third-party
hardware-library payload) was removed 2026-06-11, after the HTTP `/data.json` feed was
removed 2026-06-09 — pipe + catalog only now.

---

## The template every remaining step follows

Each new privileged data source reuses the **same proven pattern** (the SMBus path is the
worked example):

1. **(driver)** one narrow IOCTL returning **raw register bytes** — no interpretation in
   Ring 0.
2. **(broker)** a capability **scope** + a control **op** that calls the IOCTL.
3. **(broker)** **user-mode decode** — the raw→units formula, *ported* from a proven source
   (Linux `k10temp`, `nct6683`, `nct6775`, JEDEC — recorded as register facts in
   `THIRD-PARTY-NOTICES.md`), never invented.
4. **(broker)** fold the typed reading into the **named sensor catalog** the client already
   consumes (raw channel id + calibration label/scale), so consumers don't care which
   backend a value came from.

The broker work is steps **2–4**. Step 1 is the only driver dependency per source.

---

## Done since this roadmap was written

### Option A — Brokered CPU temperature  ✅ done (2026-06-08)
A non-admin client reads Ryzen Tctl/Tdie through the broker from *our* driver, WinRing0
out of that path. `SMU_READ` IOCTL (SMN/PCI indirect, ported from `k10temp`), the
`k10temp` decode broker-side, surfaced as catalog id `cpu.temp`. Validated:
`0x69BB0000` → 56.6 °C, matched HWiNFO. This was the headline "retire the vulnerable
driver" deliverable — the first temperature off WinRing0 onto our own driver.

### Option A′ — Super-I/O board sensors (NCT6687D)  ✅ done (2026-06-08)
Same template: `SUPERIO_READ` IOCTL via LPC, Linux `nct6683`-derived decode broker-side.
6 board temps matched HWiNFO labels; fans read RPM. Catalog adds board temps + fans. A
non-admin client read the VRM/system board temps through the gate. Combined
with A, **no temperature on this box needs WinRing0** (or, since 2026-06-11, any
third-party hardware library).

### Option B — Broker policy hardening  ✅ done (2026-06-08)
Per-session token-bucket **rate limiting** (`BrokerPolicy`/`RateLimiter`), a bounded
**session table** (`MaxSessions`, expired-pruned), and a dedicated **audit log** (every
connect + identity + op→result). All unconditional. Rate-limit self-tested; audit trail
verified live.
- **Deferred within B:** flipping `RequireAuthorizedClient` on *by default* still needs a
  shipped allowlist/signer policy (would lock out the dev flow) — pairs with the service
  installer below. Widening the pipe DACL beyond same-user belongs with the system-service
  work. PID-reuse TOCTOU remains a documented, accepted residual.

### Control / write broker (RGB over SMBus)  ✅ delivered (2026-06-08)
The full thesis. A **separate write-only service** (`--control`, pipe
`\\.\pipe\BrokerControl`, `rgb:write` scope) holds the brick-guarded write IOCTL; the
sensor broker never offers writes. `RgbCatalog` is the baked map (`ram0`→bus 0/`0x39`,
`ram1`→bus 0/`0x3A`); ops `rgb.list` / `rgb.set {device,color}`. **Verified live:** a
non-elevated client changed RAM color by name — never an address; can't scan or write
SPD. Hard rule held: **all addresses baked in; clients never supply or search them**
(memory `smbus-write-safety`). Kernel brick-guard + baked map + scope + rate-limit +
audit all enforce it.

### `sensor.readall` batch op  ✅ done (2026-06-09)
A consumer that wants the whole catalog each poll costs **1 op per cycle** against the
rate limiter instead of N — added for the (since-removed) plugin consumer, kept as the
recommended polling op for any client.

### Data-driven board calibration (Phases 1–2)  ✅ done (2026-06-10)
The catalog is assembled from **raw channels** (stable ids like `nct6687d.volt.0`,
`smu.cpu.temp`, `dimm.0`; decoders in `Sensors/SensorDecode.cs`) + **board calibration
DATA** (`calibration.default.json` + optional
`ProgramData\SensorBroker\calibration.user.json`, loader `Sensors/Calibration.cs`).
Calibration data is labels + scales only — **no addresses**; registers stay in trusted
code. Legacy semantic ids resolve via a built-in alias map; `--calibration` inspector;
regression-gated in `--selftest`.

### Third-party hardware libraries removed  ✅ done (2026-06-11)
The in-proc hardware-monitoring library (the old bulk feed) and the USB-HID library are
gone, along with the legacy bulk `sensors` op and the `lib\` folder. The broker's only
sensor source is its own driver + catalog; the only NuGet dependency is
`Microsoft.Extensions.Hosting.WindowsServices`.

### Chipset breadth — NCT668x family + NCT6775 family  ✅ built (2026-06-11)
`SuperioNct.c` now covers the EC family (NCT6683 `0xC730` / NCT6686 `0xD440` / NCT6687D
`0xD590`, masked `0xFFF0`; 6687D hardware-validated, the others register-identical but
unvalidated). New `SuperioNct6775.c` adds the bank-select family
(6779/6791/6792/6793/6795/6796/6797/6798, kernel mask `0xFFF8`, IO-space-lock clear on
6791+, `KIND_NCT6775=3`) — **built, hardware-unvalidated**. `--selftest` covers the
chipset gates (NCT6775 decode + EC/6775 mutual exclusion). The ITE IT87xx + Gigabyte
IT8297 USB-HID paths were **retired** the same day after a domain expert's corrections
(removed from the tree; design record `GIGABYTE-SUPPORT.md`; `KIND_ITE=2` reserved).

### Per-LED RGB quality — block write + latch fix  ✅ done (2026-06-11)
Driver `WRITE` gained an atomic **WriteBlock** op (1..32 bytes; appended `Length` +
`Block[32]`, V1 24-byte prefix still accepted for byte/word); each LED's triple is one
block write, and DIRECT/APPLY is latched once per controller (cleared on failure) — the
per-frame re-latch was the visible blink. The driver poll loop moved to 25 µs busy-wait
stalls (≤ ~2 ms) then 250 µs sleeps (≤ ~48 ms); the old pure-sleep loop rounded to the
OS timer tick and made frames crawl. `RgbRegistry` keeps the device layer
transport-agnostic.

### Rename to Register Broker  ✅ done (2026-06-11)
The project is **"Register Broker — Universal Low-Level Hardware Access Framework"**,
standalone. Service display names: "Register Broker Sensor Service" / "Register Broker
RGB Control" / "Register Broker SMBus Driver". License: MIT + Commons Clause;
`THIRD-PARTY-NOTICES.md` records the ENE/Aura protocol as publicly documented register
facts and the Linux driver references as facts.

---

## Candidate next moves — all picked & landed

### Option C — Broker as a Windows Service (own the lifecycle)  ✅ implemented + live-validated (2026-06-09)
**Goal:** the broker runs as an auto-start service that owns driver-load + serving;
`--client` / `--once` / `--selftest` / `--control` stay for dev.

**Done (code, `--selftest` green):**
- **Service host wrapper** — `ServiceHost.cs` hosts the broker/control body under
  `Microsoft.Extensions.Hosting.WindowsServices` (`AddWindowsService`). `Program.Main` routes
  to it on `--service` or `WindowsServiceHelpers.IsWindowsService()`; the same
  `RunBrokerAsync(args, token)` / `RunControlServiceAsync(args, token)` body backs both the
  service and the console path.
- **Graceful shutdown** — SCM stop cancels the hosted `stoppingToken`; the broker body
  returns and disposes the `SmbusDriverBackend` in its `finally`, releasing the
  `\\.\BrokerSmbus` handle so the kernel service can unload. 20 s shutdown window.
- **Pipe DACL beyond same-user** — `BrokerControlServer.TryBuildPipeSecurity` detects
  LocalSystem and switches from the same-user DACL to SYSTEM/Admins full + Authenticated-Users
  connect. No explicit integrity label: a pipe with no label is treated as medium integrity,
  which medium-IL (non-admin) clients can write to, so the DACL is the gate (an explicit label
  is both unnecessary and fails creation — `SeSecurityPrivilege` is not enabled on the SYSTEM
  token). The identity + signer pin is the real gate.
- **One installer** — `scripts\Install-SensorBrokerService.ps1` registers the kernel-driver
  service + broker service (+ optional `-WithRgbControl`), with a driver→broker dependency
  (start order), auto-start, and SCM crash-recovery. `-RequireAuthorizedClient` +
  `-AllowedClientSigners`/`-AllowedClientPaths` (with input validation) write the enforce
  policy into the published `appsettings.json`. The installer also runs a **signature
  preflight** — it refuses teardown if the built `.sys` is NotSigned/HashMismatch (lesson
  from the 2026-06-11 breakage; `Build-Driver-DirectLink.ps1` now auto-test-signs,
  `-NoSign` to opt out). `scripts\Uninstall-SensorBrokerService.ps1` tears down in
  dependency order (consumers first so the driver handle is released, then the driver);
  `scripts\Stop-BrokerServices.ps1` / `Start-BrokerServices.ps1` handle day-to-day
  lifecycle (the loaded driver link-locks its build-output image, so rebuilds require
  stopping the stack).

**Live-validated (2026-06-09):**
- ✅ A **non-admin** `--client --op=sensor.list` (elevated=False) reached the SYSTEM-hosted
  `\\.\pipe\SensorBroker`, passed the identity gate, and read the full 15-sensor catalog. The
  cross-integrity open works with no explicit IL label (the first attempt to stamp one failed —
  `SeSecurityPrivilege` not enabled on the SYSTEM token — and was removed).
- ✅ **Clean stop/unload:** after `Stop-Service SensorBroker`, the kernel driver service
  stopped to `STATE: STOPPED` with no hang — the broker releases the `\\.\BrokerSmbus` handle
  on shutdown so the driver can unload.
- ✅ **RGB write as a service:** with `-WithRgbControl` installed (all three services Running),
  a non-admin `--client --control --op=rgb.set --device=ram0 --color=00FF00` (elevated=False)
  drove `\\.\pipe\BrokerControl` → `rgb.set OK`. Sensor reads + RGB writes + clean lifecycle
  all proven as Windows services.

**Remaining (optional):**
- Decide whether to flip `RequireAuthorizedClient` on by default once a production signer
  thumbprint is pinned (currently audit-only).

### Companion: retire the third-party bulk feed source-by-source  ✅ done (2026-06-11)
CPU, board, and DIMM readings all moved onto our driver via the catalog template; the
in-proc hardware-monitoring library and its bulk `sensors` op were then **removed
entirely**. The broker has zero third-party hardware libraries.

---

## Open items (the NOT-done list, 2026-06-12)

- **Intel i801** SMBus hardware validation (written, untested — no Intel box).
- **NCT6775-family** Super-I/O hardware validation (built, untested — needs a board with
  one of the bank-select chips).
- **NCT6683/6686** hardware validation (register-identical to the validated 6687D,
  untested).
- **SMU PM-table metrics** (CPU power PPT/TDC/EDC, IOD/L3/die-avg temps) — needs the SMU
  mailbox protocol, a separate mechanism; deliberately not started.
- **Production code signing** (EV cert + Microsoft attestation, Phase E in
  `BROKER-DESIGN.md`) and **flipping `RequireAuthorizedClient` on** with a pinned
  production signer.
- **Phase 3 of `CALIBRATION-AND-REGISTRY-PLAN.md`** — the detector/backend registry —
  **DONE (2026-06-12)**: table-driven probe/dispatch descriptors in the driver
  (`g_SmbusBackends` / `g_SuperioBackends`), the `IOCTL_BROKER_ENUM_BACKENDS`
  enumeration op, and the broker-side `ChannelRegistry` + `DecoderRegistry` C# code
  tables (channel definitions stay in signed code, per the calibration plan's
  never-do), all selftest-gated and live-verified on the dev box. Adding a chip =
  one backend file + one kernel descriptor row + one channel-table entry + a decoder.
- **Community validation campaign** (after the GitHub release): recruit testers for
  the unvalidated backends via Overclock.net, r/overclocking / r/buildapc, and
  hardware Discords; pin a "Hardware Validation Campaign" tracking issue; testers
  follow `docs/TESTING.md` (build, `--calibration`, `sensor.readall` vs HWiNFO,
  report template). Message hook: "non-admin sensor reading, validated on AMD +
  NCT6687D — help us validate Intel i801 and the NCT6775 family on your board."
- **SMBus writes outside the RGB windows** — deliberately **never** (in-kernel
  brick-guard: writes only to `0x70–0x77` and `0x39–0x3A`).

---

## Deferred designs (post-1.0, documented only — no code exists)

### RGB sidecar (broader controller coverage without license contamination)

Today's RGB scope is ENE/Aura DRAM over SMBus, implemented clean-room from the
publicly documented protocol. Most other controller knowledge in the ecosystem lives
in GPL-2.0 codebases, which the AGPL-3.0 + commercial-exception broker cannot absorb.
The plan, when demand justifies it:

- A **separate sidecar process** hosts GPL-derived controller code under its own
  license; it is optional, separately distributed, and never linked into the broker
  or driver.
- The broker keeps the same client-facing ops (`rgb.list` / `rgb.set`) and forwards
  to the sidecar over local IPC when a device belongs to it; broker + driver remain
  license-clean and signable.
- Kernel guardrails do not move: any SMBus-side writes still go through the in-kernel
  allow-list; USB/HID-side controllers are the sidecar's own transport and never touch
  the driver.

### Effects / animation protocol

Effects stay **consumer-side** by design — the broker writes colors, it does not
animate. If a shared effects engine is ever wanted, the path is new protocol ops in
`CLIENT-PROTOCOL.md` dispatched to the sidecar (or `not-supported` without it), never
an engine inside the broker service.

---

## Suggested sequence

The broker stack is delivered: services live-validated, catalog-only serving, zero
third-party hardware libraries, per-LED RGB fixed. What's left is **validation breadth
and productization**: hardware-validate the written-but-untested backends (Intel i801,
NCT6775 family, NCT6683/6686) as test hardware becomes available; pursue production
signing and flip the enforce-by-default policy with a pinned production signer; and,
when chip count justifies it, do the Phase-3 detector/backend registry so new chips
become data instead of dispatch code.
